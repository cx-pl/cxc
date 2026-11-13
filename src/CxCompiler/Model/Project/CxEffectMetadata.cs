using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using System.Text.Json;

namespace CxCompiler.Model.Project;

public sealed record CxEffectMetadataDocument(
    int SchemaVersion,
    int ModuleMetadataFormatVersion,
    string ModuleName,
    string ModuleVersion,
    IReadOnlyList<CxFunctionEffectMetadata> Functions);

public sealed record CxFunctionEffectMetadata(
    string Symbol,
    string Name,
    string? Owner,
    string Signature,
    bool NoEscape,
    bool Deferred,
    bool PointerBorrowSummaryComplete,
    IReadOnlyList<int> BorrowedReturnParameterIndices,
    IReadOnlyList<int> RetainedPointerParameterIndices);

public static class CxEffectMetadataLoader
{
    public const int CurrentSchemaVersion = 1;

    public static void Apply(CxProject project, IEnumerable<string> metadataPaths)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(metadataPaths);

        foreach (var path in metadataPaths)
        {
            CxEffectMetadataDocument document;
            try
            {
                var json = File.ReadAllText(path);
                document = JsonSerializer.Deserialize<CxEffectMetadataDocument>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        MaxDepth = 32,
                    }) ?? throw new JsonException("The effects document is empty.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new CompilationErrorException(
                    $"Cannot read CX effect metadata '{path}': {error.Message}");
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' uses schema version " +
                    $"{document.SchemaVersion}; version {CurrentSchemaVersion} is required.");
            }
            if (document.ModuleMetadataFormatVersion !=
                CCodeOutputGenerator.CurrentModuleMetadataFormatVersion)
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' requires module metadata format " +
                    $"{document.ModuleMetadataFormatVersion}; format " +
                    $"{CCodeOutputGenerator.CurrentModuleMetadataFormatVersion} is required.");
            }
            if (string.IsNullOrWhiteSpace(document.ModuleName) ||
                string.IsNullOrWhiteSpace(document.ModuleVersion) ||
                document.Functions is null)
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' is missing its module name, module version, or function list.");
            }

            var symbols = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < document.Functions.Count; index++)
            {
                var effect = document.Functions[index];
                if (effect is null)
                {
                    throw new CompilationErrorException(
                        $"CX effect metadata '{path}' contains a null function entry at index {index}.");
                }
                if (string.IsNullOrWhiteSpace(effect.Symbol) ||
                    string.IsNullOrWhiteSpace(effect.Name) ||
                    string.IsNullOrWhiteSpace(effect.Signature) ||
                    effect.BorrowedReturnParameterIndices is null ||
                    effect.RetainedPointerParameterIndices is null)
                {
                    throw new CompilationErrorException(
                        $"CX effect metadata '{path}' contains an incomplete function entry at index {index}.");
                }
                if (!symbols.Add(effect.Symbol))
                {
                    throw new CompilationErrorException(
                        $"CX effect metadata '{path}' contains duplicate function symbol '{effect.Symbol}'.");
                }
            }

            var apiReferences = project.ModuleApiReferences
                .Where(reference => reference.ModuleName == document.ModuleName)
                .ToArray();
            if (apiReferences.Length != 1 ||
                string.IsNullOrWhiteSpace(apiReferences[0].Version))
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' requires exactly one module API reference " +
                    $"for '{document.ModuleName}' with an explicit version.");
            }
            if (!string.Equals(apiReferences[0].Version, document.ModuleVersion,
                StringComparison.Ordinal))
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' describes module version " +
                    $"'{document.ModuleVersion}', but the module API reference pins " +
                    $"'{apiReferences[0].Version}'.");
            }

            var contexts = project.CompilationContexts
                .Where(context => context.ProjectName == document.ModuleName)
                .ToArray();
            if (contexts.Length == 0)
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' names module '{document.ModuleName}', " +
                    "but no matching source declaration context or API stub is loaded. " +
                    $"Loaded module contexts: {string.Join(", ", project.CompilationContexts
                        .Select(context => context.ProjectName ?? "<unnamed>")
                        .Distinct(StringComparer.Ordinal))}.");
            }

            foreach (var effect in document.Functions)
                ApplyFunctionEffect(project, contexts, document.ModuleName, effect, path);
        }
    }

    private static void ApplyFunctionEffect(
        CxProject project,
        IReadOnlyList<CompilationContext> contexts,
        string moduleName,
        CxFunctionEffectMetadata effect,
        string path)
    {
        var matches = contexts
            .SelectMany(context => EnumerateFunctions(context.DeclarationScope.Declarations))
            .Where(function => function.FullName.ToString() == effect.Name &&
                function.ParentClassDeclaration?.FullName.ToString() == effect.Owner &&
                CCodeOutputGenerator.ToCFunctionName(function, moduleName) == effect.Symbol &&
                CCodeOutputGenerator.GetEffectSignature(function) == effect.Signature)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new CompilationErrorException(
                $"CX effect metadata '{path}' identifies '{effect.Name}' with " +
                $"signature '{effect.Signature}', but found {matches.Length} matching declarations.");
        }

        var function = matches[0];
        // Effect metadata is applied before source annotations are resolved. Recognize the
        // source spelling as well, so the metadata does not append a second @NoEscape that
        // would later fail the annotation repeatability check.
        var hasNoEscape = function.Annotations.Any(annotation =>
            annotation.ResolvedTypeName == "cxcore.System.Interop.NoEscape" ||
            annotation.Name.ToString() is "NoEscape" or "System.Interop.NoEscape" or
                "cxcore.System.Interop.NoEscape");
        if (hasNoEscape && !effect.NoEscape)
        {
            throw new CompilationErrorException(
                $"CX effect metadata '{path}' disagrees with the source NoEscape contract " +
                $"on '{effect.Name}'.");
        }
        if (effect.NoEscape && !hasNoEscape)
        {
            if (function.NativeImport is { NoEscape: false })
            {
                throw new CompilationErrorException(
                    $"CX effect metadata '{path}' conflicts with the native import contract " +
                    $"on '{effect.Name}'.");
            }
            var annotation = new AnnotationApplication(
                new QualifiedIdentifier("System", "Interop", "NoEscape"), null, []);
            annotation.ResolvedTypeName = "cxcore.System.Interop.NoEscape";
            function.Annotations = [.. function.Annotations, annotation];
        }

        var deferred = function.IsAsync || function.IsIteratorFactory;
        if (deferred != effect.Deferred)
        {
            throw new CompilationErrorException(
                $"CX effect metadata '{path}' disagrees with deferred-call status " +
                $"on '{effect.Name}'.");
        }

        ValidateParameterIndices(effect.BorrowedReturnParameterIndices,
            function.Parameters.Count, "borrowed return", path, effect.Name);
        ValidateParameterIndices(effect.RetainedPointerParameterIndices,
            function.Parameters.Count, "retained pointer", path, effect.Name);

        if (function.PointerBorrowSummaryComplete && effect.PointerBorrowSummaryComplete &&
            (!function.BorrowedReturnParameterIndices.SetEquals(
                 effect.BorrowedReturnParameterIndices) ||
             !function.RetainedPointerParameterIndices.SetEquals(
                 effect.RetainedPointerParameterIndices)))
        {
            throw new CompilationErrorException(
                $"CX effect metadata '{path}' disagrees with the source pointer-borrow " +
                $"summary on '{effect.Name}'.");
        }

        foreach (var index in effect.BorrowedReturnParameterIndices)
            function.MarkBorrowedReturnParameter(index);
        foreach (var index in effect.RetainedPointerParameterIndices)
            function.MarkRetainedPointerParameter(index);
        if (effect.PointerBorrowSummaryComplete)
            function.MarkPointerBorrowSummaryComplete();
    }

    private static void ValidateParameterIndices(
        IReadOnlyList<int> indices,
        int parameterCount,
        string effectName,
        string path,
        string functionName)
    {
        if (indices is null || indices.Any(index => index < 0 || index >= parameterCount) ||
            indices.Distinct().Count() != indices.Count)
        {
            throw new CompilationErrorException(
                $"CX effect metadata '{path}' has an invalid {effectName} parameter index " +
                $"for '{functionName}'.");
        }
    }

    private static IEnumerable<FunctionDeclaration> EnumerateFunctions(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is FunctionDeclaration function)
                yield return function;
            else if (declaration is ClassDeclaration classDeclaration)
                foreach (var nested in EnumerateFunctions(
                    classDeclaration.MemberDeclarations.Declarations))
                    yield return nested;
            else if (declaration is ExtensionDeclaration extensionDeclaration)
                foreach (var nested in EnumerateFunctions(
                    extensionDeclaration.Members.MemberDeclarations.Declarations))
                    yield return nested;
        }
    }
}
