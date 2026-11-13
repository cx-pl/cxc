using CxCompiler.Model.Common;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private const int CurrentEffectMetadataSchemaVersion = 1;

    private static void WriteModuleEffectMetadata(
        CxProject project,
        string outputPath,
        IReadOnlyCollection<DeclarationBase> declarations)
    {
        var functions = EnumerateFunctions(declarations)
            .Where(IsPublicModuleFunction)
            .Select(function => new CxFunctionEffectMetadata(
                ToCFunctionName(function, project.Name),
                function.FullName.ToString(),
                function.ParentClassDeclaration?.FullName.ToString(),
                GetEffectSignature(function),
                HasNoEscapeFunctionContract(function) ||
                    function.NativeImport?.NoEscape == true,
                function.IsAsync || function.IsIteratorFactory,
                function.PointerBorrowSummaryComplete,
                function.BorrowedReturnParameterIndices.Order().ToArray(),
                function.RetainedPointerParameterIndices.Order().ToArray()))
            .OrderBy(function => function.Symbol, StringComparer.Ordinal)
            .ToArray();

        var document = new CxEffectMetadataDocument(
            CurrentEffectMetadataSchemaVersion,
            CurrentModuleMetadataFormatVersion,
            project.Name,
            project.Version,
            functions);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });
        File.WriteAllText(outputPath, json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool IsPublicModuleFunction(FunctionDeclaration function)
    {
        if (function.LocalCName is not null ||
            function.Name.StartsWith("__", StringComparison.Ordinal) ||
            function.ParentClassDeclaration is { } parent &&
                !IsPubliclyAccessibleClass(parent))
        {
            return false;
        }

        if (function.MemberModifiers.Contains(MemberModifier.Public))
        {
            return true;
        }

        // Interface members are public by default. They still need effect
        // records because consumers may dispatch through the interface slot.
        return function.ParentClassDeclaration?.ClassType == ClassType.Interface &&
            !function.MemberModifiers.Contains(MemberModifier.Private) &&
            !function.MemberModifiers.Contains(MemberModifier.Protected) &&
            !function.MemberModifiers.Contains(MemberModifier.Internal);
    }

    internal static string GetEffectSignature(FunctionDeclaration function)
    {
        var parameters = string.Join(",", function.Parameters.Select(parameter =>
            GetEffectTypeSignature(parameter.ParameterType)));
        return $"({parameters})->{GetEffectTypeSignature(function.ReturnType)}";
    }

    internal static string GetEffectTypeSignature(TypeBase type) => type switch
    {
        ConstType constant => $"const {GetEffectTypeSignature(constant.UnderlyingType)}",
        NullableType nullable => $"{GetEffectTypeSignature(nullable.UnderlyingType)}?",
        ArrayType array => $"{GetEffectTypeSignature(array.ElementType)}[]",
        FixedArrayType array => $"{GetEffectTypeSignature(array.ElementType)}[{array.Length}]",
        PtrType pointer => pointer.Pointee is null
            ? "ptr"
            : $"ptr<{GetEffectTypeSignature(pointer.Pointee)}>",
        NativeFunctionType function =>
            $"nativefn({string.Join(",", function.ParameterTypes.Select(GetEffectTypeSignature))})" +
            $"->{GetEffectTypeSignature(function.ReturnType)}@{function.CallingConvention}",
        FunctionType function =>
            $"fn({string.Join(",", function.ParameterTypes.Select(GetEffectTypeSignature))})" +
            $"->{GetEffectTypeSignature(function.ReturnType)}",
        _ => type.Name,
    };

}
