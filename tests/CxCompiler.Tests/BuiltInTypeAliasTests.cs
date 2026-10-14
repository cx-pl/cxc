using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class BuiltInTypeAliasTests
{
    public static TheoryData<string, string> NumericAliases => new()
    {
        { "int8", "sbyte" },
        { "int16", "short" },
        { "int32", "int" },
        { "int64", "long" },
        { "uint8", "byte" },
        { "uint16", "ushort" },
        { "uint32", "uint" },
        { "uint64", "ulong" },
        { "float32", "float" },
        { "float64", "double" },
    };

    [Theory]
    [MemberData(nameof(NumericAliases))]
    public void AliasAndCanonicalSpellingShareTypeIdentityAndAcceptEachOthersValues(
        string alias, string canonical)
    {
        var project = NativeInteropTests.Project($$"""
            public {{canonical}} Echo({{alias}} value) { return value; }
            public {{alias}} Run({{canonical}} value) { return Echo(value); }
            """);
        new SemanticBinder().Bind(project);
        var functions = project.CompilationContexts[0].DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();

        Assert.Same(functions[0].ReturnType, functions[0].Parameters[0].ParameterType);
        Assert.Same(functions[0].ReturnType, functions[1].ReturnType);
        Assert.Same(functions[0].ReturnType, functions[1].Parameters[0].ParameterType);
    }

    [Theory]
    [MemberData(nameof(NumericAliases))]
    public void AliasProducesCanonicalCAbiAndMetadataInCompoundTypes(
        string alias, string canonical)
    {
        var actual = Generate(alias);
        var expected = Generate(canonical);

        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(expected.Source, actual.Source);
    }

    private static (string Source, string Header, string CMake) Generate(string type)
    {
        var project = NativeInteropTests.Project($$"""
            public struct Holder { public {{type}} Value; public {{type}}[] Values; }
            public delegate {{type}} Callback({{type}} value);
            public {{type}} Echo({{type}} value) { return value; }
            public unsafe {{type}} Read(ptr<const {{type}}> value) { return *value; }
            """);
        new SemanticBinder().Bind(project);
        return NativeInteropTests.Generate(project);
    }
}
