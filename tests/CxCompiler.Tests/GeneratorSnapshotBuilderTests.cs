using CxCompiler.Generators;
using CxCompiler.Model.Project;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GeneratorSnapshotBuilderTests
{
    [Fact]
    public void GeneratorSnapshotBindingStopsBeforeUserFunctionBodies()
    {
        const string source = """
            namespace Example;
            public class DataRecord {
                public int Id;
                public int InvalidBody() { return "not an integer"; }
            }
            """;
        var project = CxProject.CreateDefaultApplicationProject("SnapshotFixture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source, "src/data-record.cx"));

        new SemanticBinder().BindForGeneratorSnapshot(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."),
            new Dictionary<string, string> { ["src/data-record.cx"] = source });
        var record = Assert.Single(snapshot.Types, type => type.MetadataName == "Example.DataRecord");
        Assert.Contains(record.Members, member => member.Name == "InvalidBody" &&
            member.TypeIdentity == "System.Int");
    }

    [Fact]
    public void PreservesUnresolvedConstructedTypeSourceNamesInDeclarationSnapshots()
    {
        const string source = """
            public class UsesList {
                public System.Collections.List<int> Values;
            }
            """;
        var project = CxProject.CreateDefaultApplicationProject("SnapshotFixture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source, "src/uses-list.cx"));

        new SemanticBinder().BindForGeneratorSnapshot(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."));
        var member = Assert.Single(Assert.Single(snapshot.Types,
            type => type.MetadataName == "UsesList").Members,
            item => item.Name == "Values");
        Assert.Equal("System.Collections.List<System.Int>", member.TypeIdentity);
    }

    [Fact]
    public void CreatesStableTypeAndMemberSnapshotsFromBoundDeclarations()
    {
        const string source = """
            namespace Example;
            public interface IHasName { }
            public class Box<T> where T : class : IHasName {
                public T Value;
                public string Name { get; set; }
                public int Read(int offset) { return offset; }
            }
            public class UsesBox {
                public int Read(Box<string> box) { return box.Read(1); }
            }
            public final class FinalBox { }
            public abstract class AbstractBox { }
            """;
        var project = CxProject.CreateDefaultApplicationProject("SnapshotFixture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source, "src/models.cx"));
        new SemanticBinder().Bind(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."),
            new Dictionary<string, string> { ["src/models.cx"] = source });

        var box = Assert.Single(snapshot.Types, type => type.MetadataName == "Example.Box`1");
        Assert.Equal("SnapshotFixture.Example.Box`1", box.Identity);
        Assert.Equal("SnapshotFixture", box.ModuleName);
        Assert.Equal("Example", box.NamespaceName);
        Assert.True(box.IsGenericDefinition);
        Assert.False(box.IsFinal);
        Assert.False(box.IsAbstract);
        Assert.Equal(["T"], box.GenericParameters);
        Assert.Contains("SnapshotFixture.Example.IHasName", box.BaseTypeIdentities);
        Assert.Equal("ReferenceType", Assert.Single(box.GenericConstraints).Kind);
        Assert.Equal("T", Assert.Single(box.GenericConstraints).ParameterName);
        Assert.Equal("src/models.cx", box.Location.Path);
        Assert.Contains(box.Members, member => member.Name == "Value" &&
            member.Kind == "Field" && member.TypeIdentity == "!T");
        Assert.Contains(box.Members, member => member.Name == "Name" &&
            member.Kind == "Property" && member.TypeIdentity == "System.String");
        Assert.Contains(box.Members, member => member.Identity ==
            "SnapshotFixture.Example.Box`1.Read(System.Int)");

        Assert.Contains(snapshot.ClosedGenericRequests, request =>
            request.DefinitionIdentity == "SnapshotFixture.Example.Box`1" &&
            request.TypeArguments.SequenceEqual(["System.String"]));
        Assert.Equal(snapshot.Types.Select(type => type.Identity),
            snapshot.Types.Select(type => type.Identity).Order(StringComparer.Ordinal));
        Assert.Equal(snapshot.SyntaxCandidates.Select(item => item.Identity),
            snapshot.SyntaxCandidates.Select(item => item.Identity).Order(StringComparer.Ordinal));
        Assert.Contains(snapshot.SyntaxCandidates, item => item.Text.Contains("class Box<T>",
            StringComparison.Ordinal));
        Assert.True(Assert.Single(snapshot.Types, type => type.MetadataName == "Example.FinalBox").IsFinal);
        Assert.True(Assert.Single(snapshot.Types, type => type.MetadataName == "Example.AbstractBox").IsAbstract);
        var mutableView = Assert.IsAssignableFrom<IList<GeneratorTypeSnapshot>>(snapshot.Types);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
    }

    [Fact]
    public void PreservesNullableReferenceArgumentsOnClosedGenericRequests()
    {
        const string source = """
            namespace Example;
            public final class Child { }
            public class Box<T> {
                public T Value;
                public constructor() {}
            }
            public class Holder {
                public Box<Child?> Value;
            }
            """;
        var project = CxProject.CreateDefaultApplicationProject("SnapshotFixture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source, "src/nullable-generic.cx"));
        new SemanticBinder().BindForGeneratorSnapshot(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."));

        Assert.Contains(snapshot.ClosedGenericRequests, request =>
            request.DefinitionIdentity == "SnapshotFixture.Example.Box`1" &&
            request.TypeArguments.SequenceEqual(["SnapshotFixture.Example.Child?"]));
    }

    [Fact]
    public void CapturesResolvedAnnotationArgumentsAndDeclarationKinds()
    {
        const string source = """
            import System.Interop;
            @NativeFunction(callingConvention = NativeCallingConvention.Cdecl)
            public delegate int Callback(int value);
            public enum Mode : int { First, Second }
            """;
        var project = CxProject.CreateDefaultApplicationProject("SnapshotFixture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source, "src/contracts.cx"));
        new SemanticBinder().Bind(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."));

        var callback = Assert.Single(snapshot.Types, type => type.MetadataName == "Callback");
        Assert.StartsWith("Delegate:", callback.Kind, StringComparison.Ordinal);
        var annotation = Assert.Single(snapshot.Annotations, item =>
            item.MetadataName == "cxcore.System.Interop.NativeFunction");
        Assert.Equal("SnapshotFixture.Callback", annotation.TargetIdentity);
        Assert.Equal(["callingConvention", "contextParameter", "failureResult", "symbolOnly"],
            annotation.ArgumentNames);
        Assert.Contains("NativeCallingConvention.Cdecl", annotation.ArgumentValues);
        var mode = Assert.Single(snapshot.Types, type => type.MetadataName == "Mode");
        Assert.Equal("Enum", mode.Kind);
        Assert.Equal(["First", "Second"], mode.Members.Select(member => member.Name));
        Assert.Equal(["0", "1"], mode.Members.Select(member => member.ConstantValue));
    }

    [Fact]
    public void QualifiesMemberAndParameterAnnotationsWithTheirContainingSymbols()
    {
        const string source = """
            import System;
            @AnnotationUsage(targets = "parameter")
            public final class DataName : Annotation {
                public constructor(string value) {}
            }
            public final class Key {
                public constructor(@DataName("user-key") string value) {}
            }
            """;
        var project = CxProject.CreateDefaultApplicationProject("SnapshotFixture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source, "src/key.cx"));
        new SemanticBinder().Bind(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."),
            new Dictionary<string, string> { ["src/key.cx"] = source });
        var dataName = Assert.Single(snapshot.Annotations, item =>
            item.MetadataName.EndsWith(".DataName", StringComparison.Ordinal));

        Assert.Equal("SnapshotFixture.Key..ctor(System.String).parameter:value",
            dataName.TargetIdentity);
        Assert.Contains(snapshot.Types.Single(type => type.MetadataName == "Key").Members,
            member => member.Identity == "SnapshotFixture.Key..ctor(System.String)");
    }

    [Fact]
    public void ReferencedProjectsExposeOnlyPublicContractsAndNoSourceBodies()
    {
        var project = CxProject.CreateDefaultApplicationProject("Consumer");
        var referenced = CompilerTestHelper.Parse("""
            namespace Models;
            public class PublicRecord {
                private string Secret;
                public string Name;
                private void Hidden() {}
                public void Read(int index) {}
            }
            class InternalRecord { public string Value; }
            """, "../library/src/models.cx");
        project.AddReferencedCompilationContext(referenced, "Library");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace App;
            public class LocalRecord { public int Id; }
            """, "src/local.cx"));
        new SemanticBinder().Bind(project);

        var snapshot = GeneratorSnapshotBuilder.Create(project, Path.GetFullPath("."),
            new Dictionary<string, string>
            {
                ["../library/src/models.cx"] = "private implementation body data",
                ["src/local.cx"] = "namespace App; public class LocalRecord { public int Id; }",
            });

        var referencedTypes = snapshot.Types.Where(type => type.ModuleName == "Library").ToArray();
        var publicRecord = Assert.Single(referencedTypes);
        Assert.Equal("Models.PublicRecord", publicRecord.MetadataName);
        Assert.Equal([".ctor", "Name", "Read"], publicRecord.Members.Select(member => member.Name));
        Assert.DoesNotContain(publicRecord.Members, member => member.Name is "Secret" or "Hidden");
        Assert.DoesNotContain(snapshot.SyntaxCandidates, candidate =>
            candidate.Identity.StartsWith("Library.", StringComparison.Ordinal));
    }
}
