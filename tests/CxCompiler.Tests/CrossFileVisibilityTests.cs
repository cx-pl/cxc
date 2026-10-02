using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Semantics;
using CxCompiler.OutputGenerators;

namespace CxCompiler.Tests;

public sealed class CrossFileVisibilityTests
{
    [Fact]
    public void BindsPublicDeclarationsAcrossImportedSourceContexts()
    {
        var project = CxProject.CreateDefaultApplicationProject("cross_file_visibility");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Lib;
            public struct Token { public int value; }
            public int Read(Token token) { return token.value; }
            """));
        var consumer = CompilerTestHelper.Parse("""
            namespace App;
            public int Run(Token token) { return Read(token); }
            """);
        consumer.AddImport(new QualifiedIdentifier("Lib"));
        project.AddCompilationContext(consumer);

        new SemanticBinder().Bind(project);

        var run = consumer.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Equal("Lib.Read", call.TargetSymbol!.FullName.ToString());
    }

    [Fact]
    public void EmitsCrossFilePublicDeclarationsInOneModuleHeader()
    {
        var project = CxProject.CreateDefaultApplicationProject("cross_file_visibility");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Lib;
            public struct Token { public int value; }
            public int Read(Token token) { return token.value; }
            """));
        var consumer = CompilerTestHelper.Parse("""
            namespace App;
            public int Run(Token token) { return Read(token); }
            """);
        consumer.AddImport(new QualifiedIdentifier("Lib"));
        project.AddCompilationContext(consumer);
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-cross-file-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "cross_file_visibility.h"));
            var source = File.ReadAllText(Path.Combine(directory, "cross_file_visibility.c"));
            Assert.Contains("extern CX_CROSS_FILE_VISIBILITY_API cx_int CX_ID_3(cross_file_visibility, Lib, Read)", header);
            Assert.Contains("extern CX_CROSS_FILE_VISIBILITY_API cx_int CX_ID_3(cross_file_visibility, App, Run)", header);
            Assert.Contains("CX_ID_3(cross_file_visibility, Lib, Read)(token)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportedGenericCallIsSpecializedOnceByTheOwningProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_cross_file");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Lib;
            public T Identity<T>(T value) { return value; }
            """));
        var consumer = CompilerTestHelper.Parse("""
            import Lib;
            namespace App;
            public int Run() { return Identity(37); }
            """);
        project.AddCompilationContext(consumer);

        new SemanticBinder().Bind(project);
        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Equal("generic_cross_file", instance.ModuleName);
        Assert.Equal("Lib.Identity", instance.FullName.ToString());
        var run = consumer.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Equal(instance.SpecializationName, call.TargetSymbol!.SpecializationName);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-cross-file-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_cross_file.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_cross_file.c"));
            Assert.Contains(instance.SpecializationName!, header);
            Assert.Equal(2, source.Split(instance.SpecializationName!).Length - 1);
            Assert.Contains($"return {instance.SpecializationName}(37);", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
