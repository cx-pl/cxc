using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CxCompiler.Tests;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void ReferencedGenericBodyClosesInternalHelperLayoutInConsumer()
    {
        var library = CxProject.CreateDefaultApplicationProject("collections");
        library.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            internal class LinkedListNode<T> {
                internal T Value;
                internal constructor(T value) { Value = value; }
            }
            public class LinkedList<T> {
                private LinkedListNode<T> first;
                public void AddFirst(T value) {
                    LinkedListNode<T> node = new LinkedListNode<T>(value);
                    first = node;
                }
            }
            """));
        new SemanticBinder().Bind(library);

        var app = CxProject.CreateDefaultApplicationProject("consumer");
        app.AddReferencedCompilationContext(library.CompilationContexts[0], "collections");
        app.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            public void Run() {
                LinkedList<int> values = new LinkedList<int>();
                values.AddFirst(7);
            }
            """));

        new SemanticBinder().Bind(app);
        var node = Assert.Single(app.GenericTypeInstances, instance =>
            instance.Type.Name == "LinkedListNode");
        Assert.Equal("System.Int", node.Type.TypeArguments.Single().FullName.ToString());
        var generated = NativeInteropTests.Generate(app);
        var nodeType = node.Type.ConstructedIdentity!.CIdentifier;
        Assert.Contains($"struct {nodeType} {{", generated.Header);
        Assert.Contains("cx_int Value;", generated.Header);
        Assert.Contains("cx_memory_alloc_for_owner((const void*)(__this)", generated.Source);
    }

    [Fact]
    public void ProjectReferenceClosesNestedGenericTypeFromGenericContainingClass()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-nested-generic-owner-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), """
                name: library
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class Outer<T> {
                    public class Nested<U> {
                        public T OuterValue;
                        public U InnerValue;
                        public T ReadOuter() { return OuterValue; }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public int Run() {
                    Outer<int>.Nested<string> value =
                        new Outer<int>.Nested<string>();
                    value.OuterValue = 47;
                    value.InnerValue = "binary api";
                    return value.ReadOuter();
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("\"OuterValue\"", appSource, StringComparison.Ordinal);
            Assert.Contains("\"InnerValue\"", appSource, StringComparison.Ordinal);
            Assert.Contains("APP___generic_type_0_arg_1", appSource,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BinaryApiReferenceClosesNestedGenericTypeFromGenericContainingClass()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-binary-nested-generic-owner-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), """
                name: library
                version: 1.0
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class Outer<T> {
                    public class Nested<U> {
                        public T OuterValue;
                        public U InnerValue;
                    }
                }
                """);
            new Compiler().Compile([Path.Combine(library, "library.cxproj")]);

            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                module_api_references:
                - module_name: cxcore
                  source: core.cxapi
                  synthetic_core: true
                - module_name: library
                  source: ../library/.obj/library.cxapi.json
                  version: 1.0
                """);
            File.WriteAllText(Path.Combine(app, "core.cxapi"), string.Empty);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public int Run() {
                    Outer<int>.Nested<string> value =
                        new Outer<int>.Nested<string>();
                    value.OuterValue = 47;
                    value.InnerValue = "binary api";
                    return value.OuterValue;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("\"OuterValue\"", appSource, StringComparison.Ordinal);
            Assert.Contains("\"InnerValue\"", appSource, StringComparison.Ordinal);
            Assert.Contains("APP___generic_type_0_arg_1", appSource,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompilerLoadsEffectMetadataForConfiguredBinaryApiStub()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-module-effects-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var binary = Path.Combine(root, "binary");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(binary);
        try
        {
            var exported = CxProject.CreateDefaultApplicationProject("binary_module");
            exported.AddCompilationContext(CompilerTestHelper.Parse("""
                import System.Interop;
                public class Payload {}
                public interface IReader {
                    @NoEscape void Read(Payload value);
                    @NoEscape Payload Echo(Payload value);
                }
                public class Reader {
                    @NoEscape public virtual void Read(Payload value) {}
                    @NoEscape public virtual Payload Echo(Payload value) { return value; }
                }
                public static class BinaryApi {
                    @NoEscape public static void Read(Payload value) {}
                    @NoEscape public static extern Payload Echo(Payload value);
                    @NoEscape public static extern void ReadGeneric<T>(T value);
                    @NoEscape public static extern void Visit(delegate() => void callback);
                }
                """));
            new SemanticBinder().Bind(exported);
            CCodeOutputGenerator.GenerateOutput(exported,
                Path.Combine(binary, "binary_module.cx"));
            var exportedEffects = File.ReadAllText(
                Path.Combine(binary, "binary_module.cxeffects.json"));
            Assert.Contains("IReader.Read", exportedEffects, StringComparison.Ordinal);
            Assert.Contains("Reader.Read", exportedEffects, StringComparison.Ordinal);
            Assert.Contains("BinaryApi.ReadGeneric", exportedEffects,
                StringComparison.Ordinal);
            Assert.Contains("BinaryApi.Visit", exportedEffects,
                StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                module_api_references:
                - module_name: binary_module
                  source: ../binary/binary.cxapi
                  version: 1.0
                - module_name: cxcore
                  source: core.cxapi
                  synthetic_core: true
                effect_metadata_references:
                - ../binary/binary_module.cxeffects.json
                """);
            File.WriteAllText(Path.Combine(binary, "binary.cxapi"), """
                public class Payload {}
                public interface IReader {
                    void Read(Payload value);
                    Payload Echo(Payload value);
                }
                public class Reader {
                    public extern virtual void Read(Payload value);
                    public extern virtual Payload Echo(Payload value);
                }
                public static class BinaryApi {
                    public static extern void Read(Payload value);
                    public static extern Payload Echo(Payload value);
                    public static extern void ReadGeneric<T>(T value);
                    public static extern void Visit(delegate() => void callback);
                }
                """);
            File.WriteAllText(Path.Combine(app, "core.cxapi"), """
                namespace System;
                public static class Memory {
                    public extern static void WithArena(delegate() => void action);
                }
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import System;
                public void Consume(Payload value, IReader interfaceReader,
                    Reader virtualReader) {
                    Memory.WithArena(() => {
                        BinaryApi.Read(value);
                        BinaryApi.ReadGeneric(value);
                        BinaryApi.Visit(() => { Payload captured = value; });
                        interfaceReader.Read(value);
                        virtualReader.Read(value);
                        interfaceReader.Echo(value);
                        virtualReader.Echo(value);
                    });
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            Assert.True(File.Exists(Path.Combine(app, ".obj", "app.c")));

            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import System;
                public static class Cache { public static Payload Value; }
                public void Consume(Payload value) {
                    Memory.WithArena(() => { Cache.Value = BinaryApi.Echo(value); });
                }
                """);
            var escapeError = Assert.Throws<CompilationErrorException>(() =>
                new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("possibly arena-scoped reference", escapeError.Message);

            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import System;
                public static class Cache { public static Payload Value; }
                public void Consume(Payload value, IReader reader) {
                    Memory.WithArena(() => { Cache.Value = reader.Echo(value); });
                }
                """);
            var interfaceEscapeError = Assert.Throws<CompilationErrorException>(() =>
                new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("possibly arena-scoped reference", interfaceEscapeError.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompilerLoadsSiblingEffectsForExplicitLooseBinaryApiMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-loose-binary-effects-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var binary = Path.Combine(root, "binary");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(binary);
        try
        {
            File.WriteAllText(Path.Combine(binary, "BinaryModule.cxproj"), """
                name: BinaryModule
                version: 1.0
                type: Library
                """);
            File.WriteAllText(Path.Combine(binary, "api.cx"), """
                import System.Interop;
                public class Payload {}
                public static class BinaryApi {
                    @NoEscape public static void Read(Payload value) {}
                }
                """);
            new Compiler().Compile([Path.Combine(binary, "BinaryModule.cxproj")]);

            var apiPath = Path.Combine(binary, ".obj", "BinaryModule.cxapi.json");
            var effectsPath = Path.Combine(binary, ".obj", "BinaryModule.cxeffects.json");
            Assert.True(File.Exists(apiPath));
            Assert.True(File.Exists(effectsPath));

            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                module_api_references:
                - module_name: cxcore
                  source: core.cxapi
                  synthetic_core: true
                - module_name: BinaryModule
                  source: ../binary/.obj/BinaryModule.cxapi.json
                  version: 1.0
                """);
            File.WriteAllText(Path.Combine(app, "core.cxapi"), """
                namespace System;
                public static class Memory {
                    public extern static void WithArena(delegate() => void action);
                }
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import System;
                public void Consume(Payload value) {
                    Memory.WithArena(() => BinaryApi.Read(value));
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);
            Assert.True(File.Exists(Path.Combine(app, ".obj", "app.c")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompilerDiscoversStagedLooseBinaryApisAndEffectsWithoutApiReference()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-loose-binary-discovery-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var binary = Path.Combine(root, "binary");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(binary);
        try
        {
            File.WriteAllText(Path.Combine(binary, "BinaryModule.cxproj"), """
                name: BinaryModule
                version: 1.0
                type: Library
                """);
            File.WriteAllText(Path.Combine(binary, "api.cx"), """
                import System.Interop;
                public class Payload {}
                public static class BinaryApi {
                    @NoEscape public static void Read(Payload value) {}
                }
                """);
            new Compiler().Compile([Path.Combine(binary, "BinaryModule.cxproj")]);

            var apiPath = Path.Combine(binary, ".obj", "BinaryModule.cxapi.json");
            var effectsPath = Path.Combine(binary, ".obj", "BinaryModule.cxeffects.json");
            Assert.True(File.Exists(apiPath));
            Assert.True(File.Exists(effectsPath));
            var stagedBinaryDirectory = Path.Combine(app, "bin");
            Directory.CreateDirectory(stagedBinaryDirectory);
            File.Copy(apiPath, Path.Combine(stagedBinaryDirectory,
                Path.GetFileName(apiPath)));
            File.Copy(effectsPath, Path.Combine(stagedBinaryDirectory,
                Path.GetFileName(effectsPath)));

            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                module_api_references:
                - module_name: cxcore
                  source: core.cxapi
                  synthetic_core: true
                """);
            File.WriteAllText(Path.Combine(app, "core.cxapi"), """
                namespace System;
                public static class Memory {
                    public extern static void WithArena(delegate() => void action);
                }
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import System;
                import BinaryModule;
                public void Consume(Payload value) {
                    Memory.WithArena(() => BinaryApi.Read(value));
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);
            Assert.True(File.Exists(Path.Combine(app, ".obj", "app.c")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompilerRejectsConflictingLooseApisFromSearchPaths()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-loose-binary-conflict-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            foreach (var (directory, typeName) in new[]
                { (first, "FirstPayload"), (second, "SecondPayload") })
            {
                File.WriteAllText(Path.Combine(directory, "BinaryModule.cxproj"), """
                    name: BinaryModule
                    version: 1.0
                    type: Library
                    """);
                File.WriteAllText(Path.Combine(directory, "api.cx"),
                    $"public class {typeName} {{}}");
                new Compiler().Compile([Path.Combine(directory, "BinaryModule.cxproj")]);
            }

            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                module_api_search_paths:
                - ../first/.obj
                - ../second/.obj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), "public void Use() {}");

            var error = Assert.Throws<CompilationErrorException>(() =>
                new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("provide conflicting API metadata", error.Message,
                StringComparison.Ordinal);
            Assert.Contains("BinaryModule", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompilerLoadsGeneratedBinaryApiMetadataWithoutSourceStub()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-generated-module-api-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var directApp = Path.Combine(root, "direct-app");
        var binary = Path.Combine(root, "binary");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(directApp);
        Directory.CreateDirectory(binary);
        try
        {
            File.WriteAllText(Path.Combine(binary, "BinaryModule.cxproj"), """
                name: BinaryModule
                version: 1.0
                type: Library
                """);
            File.WriteAllText(Path.Combine(binary, "api.cx"), """
                import System.Interop;
                public class Payload {
                    public int Value;
                    public int Computed {
                        get { int getterImplementationOnly = Value; return getterImplementationOnly; }
                        set(value) { int setterImplementationOnly = value; Value = setterImplementationOnly; }
                    }
                    public constructor() { int constructorImplementationOnly = 1; Value = constructorImplementationOnly; }
                }
                public interface IReader { @NoEscape void Read(Payload value); }
                public static class BinaryApi {
                    @NoEscape public static void Read(Payload value) {
                        int implementationOnly = value.Value;
                    }
                }
                """);

            new Compiler().Compile([Path.Combine(binary, "BinaryModule.cxproj")]);

            var apiMetadataPath = Path.Combine(binary, ".obj", "BinaryModule.cxapi.json");
            Assert.True(File.Exists(apiMetadataPath));
            var apiDocument = CxModuleApiMetadataLoader.Read(apiMetadataPath);
            Assert.True(apiDocument.Sources.Single().Source.Contains(
                "public static extern void Read(Payload value) ;"),
                apiDocument.Sources.Single().Source);
            Assert.Contains("public interface IReader", apiDocument.Sources.Single().Source,
                StringComparison.Ordinal);
            Assert.Contains("extern void Read(Payload value)",
                apiDocument.Sources.Single().Source, StringComparison.Ordinal);
            Assert.DoesNotContain("implementationOnly", apiDocument.Sources.Single().Source);
            Assert.DoesNotContain("getterImplementationOnly", apiDocument.Sources.Single().Source);
            Assert.DoesNotContain("setterImplementationOnly", apiDocument.Sources.Single().Source);
            Assert.DoesNotContain("constructorImplementationOnly", apiDocument.Sources.Single().Source);
            Assert.Contains("extern get", apiDocument.Sources.Single().Source);
            Assert.Contains("extern set", apiDocument.Sources.Single().Source);
            Assert.Contains("extern constructor", apiDocument.Sources.Single().Source);

            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                module_api_references:
                - module_name: cxcore
                  source: core.cxapi
                  synthetic_core: true
                effect_metadata_references:
                - ../binary/.obj/BinaryModule.cxeffects.json
                """);
            File.WriteAllText(Path.Combine(app, "core.cxapi"), """
                namespace System;
                public static class Memory {
                    public extern static void WithArena(delegate() => void action);
                }
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import System;
                public void Consume(Payload value) {
                    Memory.WithArena(() => BinaryApi.Read(value));
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            Assert.True(File.Exists(Path.Combine(app, ".obj", "app.c")));

            var directProject = """
                name: direct
                type: Executable
                module_api_references:
                - module_name: cxcore
                  source: core.cxapi
                  synthetic_core: true
                - module_name: BinaryModule
                  source: ../binary/.obj/BinaryModule.cxapi.json
                  version: 2.0
                """;
            File.WriteAllText(Path.Combine(directApp, "direct.cxproj"), directProject);
            File.WriteAllText(Path.Combine(directApp, "core.cxapi"), """
                namespace System;
                public static class Memory {
                    public extern static void WithArena(delegate() => void action);
                }
                """);
            File.WriteAllText(Path.Combine(directApp, "main.cx"), """
                public void Consume(Payload value) {
                    BinaryApi.Read(value);
                }
                """);

            var versionError = Assert.Throws<CompilationErrorException>(() =>
                new Compiler().Compile([Path.Combine(directApp, "direct.cxproj")]));
            Assert.Contains("must pin version '1.0'", versionError.Message,
                StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(directApp, "direct.cxproj"),
                directProject.Replace("version: 2.0", "version: 1.0",
                    StringComparison.Ordinal));
            new Compiler().Compile([Path.Combine(directApp, "direct.cxproj")]);

            Assert.True(File.Exists(Path.Combine(directApp, ".obj", "direct.c")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GeneratedBinaryApiKeepsVisibilityAcrossDeclarationShapes()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-generated-module-api-visibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var provider = CxProject.CreateDefaultApplicationProject("BinaryModule");
            provider.Version = "1.0";
            const string providerSource = """
                import System.Interop;
                namespace BinaryModule;
                public class PublicPayload {
                    private int HiddenField;
                    protected int ProtectedField;
                    internal int InternalField;
                    private int HiddenProperty { get { return HiddenField; } }
                    protected int ProtectedProperty { get { return ProtectedField; } }
                    internal int InternalProperty { get { return InternalField; } }
                    public int VisibleField;
                    public int VisibleProperty { get { return VisibleField; } set(value) { VisibleField = value; } }
                    private constructor(int hidden) { HiddenField = hidden; }
                    protected constructor(string hidden) {}
                    internal constructor(bool hidden) {}
                    public constructor() {}
                    private void HiddenMethod() { int privateImplementationOnly = 1; }
                    protected void ProtectedMethod() {}
                    internal void InternalMethod() {}
                    public void VisibleMethod() {}
                    public class VisibleNested {}
                    private class HiddenNested {}
                    protected class ProtectedNested {}
                    internal class InternalNested {}
                }
                public class GenericPayload<T> {
                    private T HiddenGenericMethod(T value) { return value; }
                    protected T ProtectedGenericMethod(T value) { return value; }
                    internal T InternalGenericMethod(T value) { return value; }
                    public T Echo(T value) { return value; }
                }
                public interface IPublicContract<T> {
                    T Echo(T value);
                }
                public class PublicContract : IPublicContract<int> {
                    public int Echo(int value) { return value; }
                }
                public struct PublicValue<T> {
                    public T Value;
                }
                internal struct InternalValue<T> {
                    public T Value;
                }
                public class NestedGenericContainer {
                    public class PublicNestedGeneric<T> {
                        private T HiddenField;
                        protected T ProtectedField;
                        internal T InternalField;
                        public T VisibleField;
                        private T HiddenProperty { get; set; }
                        protected T ProtectedProperty { get; set; }
                        internal T InternalProperty { get; set; }
                        public T VisibleProperty { get; set; }
                        private constructor(string hidden) {}
                        protected constructor(bool hidden) {}
                        internal constructor(char hidden) {}
                        public constructor() {}
                        private T HiddenMethod(T value) { return value; }
                        protected T ProtectedMethod(T value) { return value; }
                        internal T InternalMethod(T value) { return value; }
                        public T Echo(T value) { return value; }
                    }
                    private class PrivateNestedGeneric<T> {
                        public T Echo(T value) { return value; }
                    }
                    protected class ProtectedNestedGeneric<T> {
                        public T Echo(T value) { return value; }
                    }
                    internal class InternalNestedGeneric<T> {
                        public T Echo(T value) { return value; }
                    }
                }
                public class GenericOuter<T> {
                    public class PublicNestedGeneric<U> {
                        private static int HiddenNestedStaticField;
                        protected static int ProtectedNestedStaticField;
                        internal static int InternalNestedStaticField;
                        public static int VisibleNestedStaticField;
                        private T HiddenOuterField;
                        protected T ProtectedOuterField;
                        internal T InternalOuterField;
                        public T VisibleOuterField;
                        private U HiddenInnerField;
                        protected U ProtectedInnerField;
                        internal U InternalInnerField;
                        public U VisibleInnerField;
                        private T HiddenOuterProperty { get; set; }
                        protected T ProtectedOuterProperty { get; set; }
                        internal T InternalOuterProperty { get; set; }
                        public T VisibleOuterProperty { get; set; }
                        private U HiddenInnerProperty { get; set; }
                        protected U ProtectedInnerProperty { get; set; }
                        internal U InternalInnerProperty { get; set; }
                        public U VisibleInnerProperty { get; set; }
                        private constructor(string hidden) {}
                        protected constructor(bool hidden) {}
                        internal constructor(char hidden) {}
                        public constructor() {}
                        private T HiddenOuterMethod() { return VisibleOuterField; }
                        protected T ProtectedOuterMethod() { return VisibleOuterField; }
                        internal T InternalOuterMethod() { return VisibleOuterField; }
                        public T ReadOuter() { return VisibleOuterField; }
                        private U HiddenInnerMethod(U value) { return value; }
                        protected U ProtectedInnerMethod(U value) { return value; }
                        internal U InternalInnerMethod(U value) { return value; }
                        public U EchoInner(U value) { return value; }
                    }
                }
                class HiddenContainer {
                    public class PublicNested {
                        @NoEscape public void HiddenByPrivateAncestor() {}
                    }
                    public class PublicNestedGeneric<T> {
                        @NoEscape public T HiddenGenericByPrivateAncestor(T value) {
                            return value;
                        }
                    }
                }
                public static class BinaryApi {
                    private static void HiddenStaticMethod() {}
                    protected static void ProtectedStaticMethod() {}
                    internal static void InternalStaticMethod() {}
                    public static void VisibleStaticMethod() {}
                }
                public class StaticFieldContainer {
                    private static int PrivateStaticField;
                    protected static int ProtectedStaticField;
                    internal static int InternalStaticField;
                    public static int VisibleStaticField;
                }
                void HiddenTopLevelFunction() {}
                internal void InternalTopLevelFunction() {}
                public void VisibleTopLevelFunction() {}
                class HiddenType {}
                internal class InternalType {}
                enum HiddenEnum { Value }
                delegate void HiddenDelegate();
                internal delegate void InternalDelegate();
                public delegate void VisibleDelegate();
                alias PayloadAlias = PublicPayload;
                concept HiddenConcept { int Read() const; }
                public concept VisibleConcept { int Read() const; }
                extension HiddenExtensions : PublicPayload {
                    public void HiddenExtensionMethod() {}
                }
                public extension VisibleExtensions : PublicPayload {
                    public void VisibleExtensionMethod() {}
                }
                """;
            provider.AddCompilationContext(CompilerTestHelper.Parse(providerSource));
            provider.AddModuleApiSource("api.cx", providerSource);
            new SemanticBinder().Bind(provider);

            CCodeOutputGenerator.GenerateOutput(provider,
                Path.Combine(root, "BinaryModule.cx"));
            var generatedHeader = File.ReadAllText(Path.Combine(root, "BinaryModule.h"));
            var generatedC = File.ReadAllLines(Path.Combine(root, "BinaryModule.c"));
            Assert.DoesNotContain("HiddenByPrivateAncestor(", generatedHeader);
            Assert.DoesNotContain("HiddenGenericByPrivateAncestor(", generatedHeader);
            Assert.DoesNotContain("PrivateStaticField", generatedHeader);
            Assert.DoesNotContain("ProtectedStaticField", generatedHeader);
            Assert.DoesNotContain("InternalStaticField", generatedHeader);
            Assert.Contains("VisibleStaticField", generatedHeader);
            Assert.DoesNotContain("HiddenNestedStaticField", generatedHeader);
            Assert.DoesNotContain("ProtectedNestedStaticField", generatedHeader);
            Assert.DoesNotContain("InternalNestedStaticField", generatedHeader);
            Assert.Contains("VisibleNestedStaticField", generatedHeader);
            var hiddenFunctionLines = generatedC.Where(line =>
                line.Contains("HiddenByPrivateAncestor(", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(hiddenFunctionLines);
            Assert.All(hiddenFunctionLines, line =>
                Assert.DoesNotContain("CX_BINARYMODULE_API", line));
            var apiPath = Path.Combine(root, "BinaryModule.cxapi.json");
            var apiSource = CxModuleApiMetadataLoader.Read(apiPath)
                .Sources.Single().Source;
            using var effectDocument = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(root, "BinaryModule.cxeffects.json")));
            var exportedEffects = effectDocument.RootElement.GetProperty("functions")
                .EnumerateArray().Select(function =>
                    function.GetProperty("name").GetString()).ToArray();
            Assert.DoesNotContain("HiddenContainer.PublicNested.HiddenByPrivateAncestor",
                exportedEffects);
            Assert.DoesNotContain(
                "HiddenContainer.PublicNestedGeneric.HiddenGenericByPrivateAncestor",
                exportedEffects);
            Assert.Contains("private int HiddenField", apiSource);
            Assert.Contains("private static extern void HiddenStaticMethod", apiSource);
            Assert.Contains("protected int ProtectedField", apiSource);
            Assert.Contains("internal int InternalField", apiSource);
            Assert.Contains("protected extern void ProtectedMethod()", apiSource);
            Assert.Contains("internal extern void InternalMethod()", apiSource);
            Assert.Contains("protected extern constructor(string hidden)", apiSource);
            Assert.Contains("internal extern constructor(bool hidden)", apiSource);
            Assert.Contains("protected class ProtectedNested", apiSource);
            Assert.Contains("internal class InternalNested", apiSource);
            Assert.Contains("protected class ProtectedNestedGeneric<T>", apiSource);
            Assert.Contains("internal class InternalNestedGeneric<T>", apiSource);
            Assert.Contains("private T HiddenField", apiSource);
            Assert.Contains("protected T ProtectedField", apiSource);
            Assert.Contains("internal T InternalField", apiSource);
            Assert.Contains("private T HiddenProperty", apiSource);
            Assert.Contains("protected T ProtectedProperty", apiSource);
            Assert.Contains("internal T InternalProperty", apiSource);
            Assert.Contains("private extern T HiddenMethod(T value)", apiSource);
            Assert.Contains("protected extern T ProtectedMethod(T value)", apiSource);
            Assert.Contains("internal extern T InternalMethod(T value)", apiSource);
            Assert.Contains("private extern constructor(string hidden)", apiSource);
            Assert.Contains("protected extern constructor(bool hidden)", apiSource);
            Assert.Contains("internal extern constructor(char hidden)", apiSource);
            Assert.Contains("protected extern T ProtectedGenericMethod(T value)", apiSource);
            Assert.Contains("internal extern T InternalGenericMethod(T value)", apiSource);
            Assert.Contains("protected static extern void ProtectedStaticMethod", apiSource);
            Assert.Contains("internal static extern void InternalStaticMethod", apiSource);
            Assert.Contains("protected static int ProtectedStaticField", apiSource);
            Assert.Contains("internal static int InternalStaticField", apiSource);
            Assert.Contains("internal extern void InternalTopLevelFunction()", apiSource);
            Assert.Contains("internal class InternalType", apiSource);
            Assert.Contains("internal delegate void InternalDelegate()", apiSource);
            Assert.Contains("public interface IPublicContract<T>", apiSource);
            Assert.Contains("internal struct InternalValue<T>", apiSource);
            Assert.Contains("private T HiddenOuterField", apiSource);
            Assert.Contains("private static int HiddenNestedStaticField", apiSource);
            Assert.Contains("protected static int ProtectedNestedStaticField", apiSource);
            Assert.Contains("internal static int InternalNestedStaticField", apiSource);
            Assert.Contains("public static int VisibleNestedStaticField", apiSource);
            Assert.Contains("protected T ProtectedOuterField", apiSource);
            Assert.Contains("internal T InternalOuterField", apiSource);
            Assert.Contains("public T VisibleOuterField", apiSource);
            Assert.Contains("private U HiddenInnerField", apiSource);
            Assert.Contains("protected U ProtectedInnerField", apiSource);
            Assert.Contains("internal U InternalInnerField", apiSource);
            Assert.Contains("public U VisibleInnerField", apiSource);
            Assert.Contains("private T HiddenOuterProperty", apiSource);
            Assert.Contains("protected T ProtectedOuterProperty", apiSource);
            Assert.Contains("internal T InternalOuterProperty", apiSource);
            Assert.Contains("private U HiddenInnerProperty", apiSource);
            Assert.Contains("protected U ProtectedInnerProperty", apiSource);
            Assert.Contains("internal U InternalInnerProperty", apiSource);
            Assert.Contains("private extern T HiddenOuterMethod()", apiSource);
            Assert.Contains("protected extern T ProtectedOuterMethod()", apiSource);
            Assert.Contains("internal extern T InternalOuterMethod()", apiSource);
            Assert.Contains("private extern U HiddenInnerMethod(U value)", apiSource);
            Assert.Contains("protected extern U ProtectedInnerMethod(U value)", apiSource);
            Assert.Contains("internal extern U InternalInnerMethod(U value)", apiSource);
            Assert.Contains("private extern constructor(string hidden)", apiSource);
            Assert.Contains("protected extern constructor(bool hidden)", apiSource);
            Assert.Contains("internal extern constructor(char hidden)", apiSource);
            Assert.DoesNotContain("privateImplementationOnly", apiSource);

            var apiContext = CompilerTestHelper.Parse(apiSource);
            Assert.Equal(Visibility.Internal, Assert.Single(
                apiContext.DeclarationScope.Declarations.OfType<ClassDeclaration>(),
                declaration => declaration.Name == "InternalValue").Visibility);
            void AssertRejected(string body, string hiddenName)
            {
                var consumer = CxProject.CreateDefaultApplicationProject("consumer");
                consumer.AddReferencedCompilationContext(apiContext, "BinaryModule");
                consumer.AddCompilationContext(CompilerTestHelper.Parse($$"""
                    import BinaryModule;
                    public void Use(PublicPayload value) { {{body}} }
                    """));
                var error = Assert.Throws<CompilationErrorException>(
                    () => new SemanticBinder().Bind(consumer));
                Assert.Contains(hiddenName, error.Message, StringComparison.Ordinal);
            }

            AssertRejected("int fieldValue = value.HiddenField;", "HiddenField");
            AssertRejected("int fieldValue = value.ProtectedField;", "ProtectedField");
            AssertRejected("int fieldValue = value.InternalField;", "InternalField");
            AssertRejected("int propertyValue = value.HiddenProperty;", "HiddenProperty");
            AssertRejected("int propertyValue = value.ProtectedProperty;", "ProtectedProperty");
            AssertRejected("int propertyValue = value.InternalProperty;", "InternalProperty");
            AssertRejected("value.HiddenMethod();", "HiddenMethod");
            AssertRejected("value.ProtectedMethod();", "ProtectedMethod");
            AssertRejected("value.InternalMethod();", "InternalMethod");
            AssertRejected("BinaryApi.HiddenStaticMethod();", "HiddenStaticMethod");
            AssertRejected("BinaryApi.ProtectedStaticMethod();", "ProtectedStaticMethod");
            AssertRejected("BinaryApi.InternalStaticMethod();", "InternalStaticMethod");
            AssertRejected("HiddenTopLevelFunction();", "HiddenTopLevelFunction");
            AssertRejected("InternalTopLevelFunction();", "InternalTopLevelFunction");
            AssertRejected("PublicPayload hidden = new PublicPayload(1);", "constructor");
            AssertRejected("PublicPayload hidden = new PublicPayload(\"hidden\");", "constructor");
            AssertRejected("PublicPayload hidden = new PublicPayload(true);", "constructor");
            AssertRejected("PublicPayload.HiddenNested hidden = new PublicPayload.HiddenNested();", "HiddenNested");
            AssertRejected("PublicPayload.ProtectedNested hidden = new PublicPayload.ProtectedNested();", "ProtectedNested");
            AssertRejected("PublicPayload.InternalNested hidden = new PublicPayload.InternalNested();", "InternalNested");
            AssertRejected(
                "NestedGenericContainer.PrivateNestedGeneric<int> hidden = new NestedGenericContainer.PrivateNestedGeneric<int>();",
                "PrivateNestedGeneric");
            AssertRejected(
                "NestedGenericContainer.ProtectedNestedGeneric<int> hidden = new NestedGenericContainer.ProtectedNestedGeneric<int>();",
                "ProtectedNestedGeneric");
            AssertRejected(
                "NestedGenericContainer.InternalNestedGeneric<int> hidden = new NestedGenericContainer.InternalNestedGeneric<int>();",
                "InternalNestedGeneric");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); int hidden = nested.HiddenField;",
                "HiddenField");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); int hidden = nested.ProtectedField;",
                "ProtectedField");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); int hidden = nested.InternalField;",
                "InternalField");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); int hidden = nested.HiddenProperty;",
                "HiddenProperty");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); int hidden = nested.ProtectedProperty;",
                "ProtectedProperty");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); int hidden = nested.InternalProperty;",
                "InternalProperty");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); nested.HiddenMethod(1);",
                "HiddenMethod");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); nested.ProtectedMethod(1);",
                "ProtectedMethod");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(); nested.InternalMethod(1);",
                "InternalMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); int hidden = nested.HiddenOuterField;",
                "HiddenOuterField");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); int hidden = nested.ProtectedOuterField;",
                "ProtectedOuterField");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); int hidden = nested.InternalOuterField;",
                "InternalOuterField");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); string hidden = nested.HiddenInnerField;",
                "HiddenInnerField");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); string hidden = nested.ProtectedInnerField;",
                "ProtectedInnerField");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); string hidden = nested.InternalInnerField;",
                "InternalInnerField");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); int hidden = nested.HiddenOuterProperty;",
                "HiddenOuterProperty");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); int hidden = nested.ProtectedOuterProperty;",
                "ProtectedOuterProperty");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); int hidden = nested.InternalOuterProperty;",
                "InternalOuterProperty");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); string hidden = nested.HiddenInnerProperty;",
                "HiddenInnerProperty");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); string hidden = nested.ProtectedInnerProperty;",
                "ProtectedInnerProperty");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); string hidden = nested.InternalInnerProperty;",
                "InternalInnerProperty");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); nested.HiddenOuterMethod();",
                "HiddenOuterMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); nested.ProtectedOuterMethod();",
                "ProtectedOuterMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); nested.InternalOuterMethod();",
                "InternalOuterMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); nested.HiddenInnerMethod(\"x\");",
                "HiddenInnerMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); nested.ProtectedInnerMethod(\"x\");",
                "ProtectedInnerMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(); nested.InternalInnerMethod(\"x\");",
                "InternalInnerMethod");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(\"hidden\");",
                "constructor");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>(true);",
                "constructor");
            AssertRejected(
                "GenericOuter<int>.PublicNestedGeneric<string> nested = new GenericOuter<int>.PublicNestedGeneric<string>('x');",
                "constructor");
            AssertRejected(
                "int hidden = GenericOuter<int>.PublicNestedGeneric<string>.HiddenNestedStaticField;",
                "HiddenNestedStaticField");
            AssertRejected(
                "int hidden = GenericOuter<int>.PublicNestedGeneric<string>.ProtectedNestedStaticField;",
                "ProtectedNestedStaticField");
            AssertRejected(
                "int hidden = GenericOuter<int>.PublicNestedGeneric<string>.InternalNestedStaticField;",
                "InternalNestedStaticField");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(\"hidden\");",
                "constructor");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>(true);",
                "constructor");
            AssertRejected(
                "NestedGenericContainer.PublicNestedGeneric<int> nested = new NestedGenericContainer.PublicNestedGeneric<int>('x');",
                "constructor");
            AssertRejected(
                "HiddenContainer.PublicNested hidden = new HiddenContainer.PublicNested();",
                "HiddenContainer");
            AssertRejected(
                "HiddenContainer.PublicNestedGeneric<int> hidden = new HiddenContainer.PublicNestedGeneric<int>();",
                "HiddenContainer");
            AssertRejected("GenericPayload<int> generic = new GenericPayload<int>(); generic.HiddenGenericMethod(1);", "HiddenGenericMethod");
            AssertRejected("GenericPayload<int> generic = new GenericPayload<int>(); generic.ProtectedGenericMethod(1);", "ProtectedGenericMethod");
            AssertRejected("GenericPayload<int> generic = new GenericPayload<int>(); generic.InternalGenericMethod(1);", "InternalGenericMethod");
            AssertRejected("HiddenType hidden = new HiddenType();", "HiddenType");
            AssertRejected("InternalType hidden = new InternalType();", "InternalType");
            AssertRejected("InternalValue<int> hidden = new InternalValue<int>();", "InternalValue");
            AssertRejected("HiddenEnum hidden = HiddenEnum.Value;", "HiddenEnum");
            AssertRejected("HiddenDelegate hidden = () => {}; hidden();", "HiddenDelegate");
            AssertRejected("InternalDelegate hidden = () => {}; hidden();", "InternalDelegate");
            AssertRejected("value.HiddenExtensionMethod();", "HiddenExtensionMethod");
            AssertRejected(
                "int hidden = StaticFieldContainer.PrivateStaticField;",
                "PrivateStaticField");
            AssertRejected(
                "int hidden = StaticFieldContainer.ProtectedStaticField;",
                "ProtectedStaticField");
            AssertRejected(
                "int hidden = StaticFieldContainer.InternalStaticField;",
                "InternalStaticField");

            var hiddenConceptConsumer = CxProject.CreateDefaultApplicationProject("hidden_concept_consumer");
            hiddenConceptConsumer.AddReferencedCompilationContext(apiContext, "BinaryModule");
            hiddenConceptConsumer.AddCompilationContext(CompilerTestHelper.Parse("""
                import BinaryModule;
                public void Use<T>(T value) : T = HiddenConcept { value.Read(); }
                """));
            var hiddenConceptError = Assert.Throws<CompilationErrorException>(
                () => new SemanticBinder().Bind(hiddenConceptConsumer));
            Assert.Contains("HiddenConcept", hiddenConceptError.Message, StringComparison.Ordinal);

            var visibleConsumer = CxProject.CreateDefaultApplicationProject("visible_consumer");
            visibleConsumer.AddReferencedCompilationContext(apiContext, "BinaryModule");
            visibleConsumer.AddCompilationContext(CompilerTestHelper.Parse("""
                import BinaryModule;
                public void Use() {
                    PayloadAlias aliasValue = new PayloadAlias();
                    PublicPayload value = aliasValue;
                    value.VisibleField = 1;
                    value.VisibleProperty = 2;
                    value.VisibleMethod();
                    PublicPayload.VisibleNested nested = new PublicPayload.VisibleNested();
                    GenericPayload<int> generic = new GenericPayload<int>();
                    int echoed = generic.Echo(1);
                    IPublicContract<int> contract = new PublicContract();
                    int contractEchoed = contract.Echo(2);
                    PublicValue<int> valueStruct = default;
                    valueStruct.Value = 5;
                    NestedGenericContainer.PublicNestedGeneric<int> nestedGeneric =
                        new NestedGenericContainer.PublicNestedGeneric<int>();
                    nestedGeneric.VisibleField = 4;
                    nestedGeneric.VisibleProperty = 5;
                    int nestedEchoed = nestedGeneric.Echo(3);
                    GenericOuter<int>.PublicNestedGeneric<string> closedNested =
                        new GenericOuter<int>.PublicNestedGeneric<string>();
                    GenericOuter<int>.PublicNestedGeneric<string>.VisibleNestedStaticField = 7;
                    closedNested.VisibleOuterField = 6;
                    closedNested.VisibleInnerField = "visible";
                    closedNested.VisibleOuterProperty = 7;
                    closedNested.VisibleInnerProperty = "property";
                    int outerValue = closedNested.ReadOuter();
                    string innerValue = closedNested.EchoInner("ok");
                    value.VisibleExtensionMethod();
                    VisibleDelegate callback = () => {};
                    callback();
                    BinaryApi.VisibleStaticMethod();
                    StaticFieldContainer.VisibleStaticField = 4;
                    VisibleTopLevelFunction();
                }
                """));
            visibleConsumer.AddCompilationContext(CompilerTestHelper.Parse("""
                import BinaryModule;
                public void UseConcept<T>(T value) : T = VisibleConcept { value.Read(); }
                """));
            new SemanticBinder().Bind(visibleConsumer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EffectMetadataMustMatchPinnedBinaryApiVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-module-effects-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var binary = CxProject.CreateDefaultApplicationProject("binary_module");
            binary.AddCompilationContext(CompilerTestHelper.Parse("""
                import System.Interop;
                public class Payload {}
                public static class BinaryApi {
                    @NoEscape public static void Read(Payload value) {}
                }
                """));
            new SemanticBinder().Bind(binary);
            CCodeOutputGenerator.GenerateOutput(binary,
                Path.Combine(root, "binary_module.cx"));

            var app = CxProject.CreateDefaultApplicationProject("app");
            app.ModuleApiReferences.Add(new CxModuleApiReference
            {
                ModuleName = "binary_module",
                Source = "binary.cxapi",
                Version = "2.0",
            });
            app.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
                public class Payload {}
                public static class BinaryApi {
                    public static extern void Read(Payload value);
                }
                """), "binary_module");

            var error = Assert.Throws<CompilationErrorException>(() =>
                CxEffectMetadataLoader.Apply(app,
                    [Path.Combine(root, "binary_module.cxeffects.json")]));
            Assert.Contains("pins '2.0'", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EffectMetadataRejectsMalformedEntriesAndMismatchedContracts()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-module-effects-entries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var binary = CxProject.CreateDefaultApplicationProject("binary_module");
            binary.AddCompilationContext(CompilerTestHelper.Parse("""
                public static class BinaryApi { public static void Read() {} }
                """));
            new SemanticBinder().Bind(binary);
            var generatedPath = Path.Combine(root, "generated.cx");
            CCodeOutputGenerator.GenerateOutput(binary, generatedPath);
            var generatedEffects = JsonSerializer.Deserialize<CxEffectMetadataDocument>(
                File.ReadAllText(Path.Combine(root, "binary_module.cxeffects.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var validEffect = generatedEffects.Functions.Single();

            var malformedCases = new[]
            {
                (new CxFunctionEffectMetadata[] { null! }, "null function entry at index 0"),
                (new[] { validEffect with { Symbol = " " } }, "incomplete function entry"),
                (new[] { validEffect, validEffect }, "duplicate function symbol"),
                (new[] { validEffect with
                    { BorrowedReturnParameterIndices = new[] { 0 } } },
                    "invalid borrowed return parameter index"),
                (new[] { validEffect with { Signature = "()->int" } },
                    "found 0 matching declarations"),
                (new[] { validEffect with { Deferred = true } },
                    "disagrees with deferred-call status"),
            };
            for (var index = 0; index < malformedCases.Length; index++)
            {
                var app = CxProject.CreateDefaultApplicationProject("app");
                app.ModuleApiReferences.Add(new CxModuleApiReference
                {
                    ModuleName = binary.Name,
                    Source = "binary.cxapi",
                    Version = binary.Version,
                });
                app.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
                    public static class BinaryApi { public static extern void Read(); }
                    """), binary.Name);

                var metadataPath = Path.Combine(root, $"effects-{index}.json");
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(
                    generatedEffects with { Functions = malformedCases[index].Item1 }));
                var error = Assert.Throws<CompilationErrorException>(() =>
                    CxEffectMetadataLoader.Apply(app, [metadataPath]));
                Assert.Contains(malformedCases[index].Item2, error.Message,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ArenaEscapeThroughReferencedFunctionBodyIsDiagnosed()
    {
        var library = CxProject.CreateDefaultApplicationProject("library");
        library.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Lib;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Store(Payload value) { Cache.Value = value; }
            """));
        new SemanticBinder().Bind(library);

        var app = NativeInteropTests.Project("""
            import System;
            import Lib;
            namespace App;
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Store(scoped);
                });
            }
            """);
        app.AddReferencedCompilationContext(library.CompilationContexts[0], "library");
        app.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public static class Memory {
                public extern static void WithArena(delegate() => void action);
                public extern static void WithDurable(delegate() => void action);
            }
            """), "cxcore", syntheticCore: true);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(app));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ReferencedNonRetainingBorrowAcceptsArenaArgument()
    {
        var library = CxProject.CreateDefaultApplicationProject("library");
        library.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Lib;
            public class Payload {}
            public int Read(Payload value) { return 42; }
            """));
        new SemanticBinder().Bind(library);

        var app = NativeInteropTests.Project("""
            import System;
            import Lib;
            namespace App;
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Read(scoped);
                });
            }
            """);
        app.AddReferencedCompilationContext(library.CompilationContexts[0], "library");
        app.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public static class Memory {
                public extern static void WithArena(delegate() => void action);
                public extern static void WithDurable(delegate() => void action);
            }
            """), "cxcore", syntheticCore: true);

        new SemanticBinder().Bind(app);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("string")]
    [InlineData("Payload")]
    public void ImportedValueTaskApiDoesNotDuplicateClosedGenericDefinitions(string resultType)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-valuetask-ownership-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), $$"""
                import System.Threading.Tasks;
                namespace Lib;
                public struct Payload { public int Number; public string Label; }
                public ValueTask<{{resultType}}> Forward(ValueTask<{{resultType}}> source) { return source; }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), $$"""
                import System.Threading.Tasks;
                import Lib;
                namespace App;
                public async Task<{{resultType}}> AwaitForward(ValueTask<{{resultType}}> source) {
                    var result = await Forward(source);
                    return result;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "library.c"));
            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var functionPattern = @"^\S[^\r\n]*\b(cx_generic_[a-f0-9]+)\([^;\r\n]*\)\r?\n\{";
            var libraryFunctions = Regex.Matches(librarySource, functionPattern, RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value).ToHashSet();
            var appFunctions = Regex.Matches(appSource, functionPattern, RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value).ToHashSet();
            Assert.NotEmpty(libraryFunctions);
            Assert.Empty(libraryFunctions.Intersect(appFunctions));

            var metadataPattern = @"CX_TYPEINFO_NAME\((cx_generic_[a-f0-9]+)\) = \{";
            var libraryTypes = Regex.Matches(librarySource, metadataPattern)
                .Select(match => match.Groups[1].Value).ToHashSet();
            var appTypes = Regex.Matches(appSource, metadataPattern)
                .Select(match => match.Groups[1].Value).ToHashSet();
            Assert.NotEmpty(libraryTypes);
            Assert.Empty(libraryTypes.Intersect(appTypes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ProjectReferenceBindsPublicSymbolsToTheirOwningModuleAndGeneratesSeparateUnits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-refs-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "math");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "math.cxproj"), "name: math\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Math; public struct Value { public int number; } public interface IValue { int Get(); } public int AddOne(int value) { return value + 1; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\ntype: Executable\nproject_references:\n- ../math/math.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "import Math; namespace App; public class Implementation : IValue { public constructor() {} public int Get() { return 41; } } public int Run() { Implementation value = new Implementation(); IValue reference = value; Value number; number.number = 41; return AddOne(reference.Get() + number.number - 41); }");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var appHeader = File.ReadAllText(Path.Combine(app, ".obj", "app.internal.h"));
            var libraryHeader = File.ReadAllText(Path.Combine(library, ".obj", "math.h"));
            var cmake = File.ReadAllText(Path.Combine(app, ".obj", "CMakeLists.txt"));
            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "math.c"));
            Assert.Contains("math_Math_AddOne(", appSource);
            Assert.Contains("math_Math_Value", appSource);
            Assert.Contains("cx_iface_ref", appSource);
            Assert.Contains("#include \"../../math/.obj/math.h\"", appHeader);
            Assert.Contains("target_link_libraries(app PRIVATE math)", cmake);
            Assert.Contains("copy_if_different \"$<TARGET_FILE:math>\"", cmake);
            Assert.Contains("math_Math_AddOne", librarySource);
            Assert.DoesNotContain("math_Math_AddOne", appSource.Split("Run", 2)[0]);
            Assert.Contains("option(CX_STATIC_LINK", cmake);
            Assert.Contains("__cx_module_init_APP();", appSource);
            Assert.Contains("__cx_module_init_MATH();", appSource);
            Assert.Contains("(struct cx_interface_impl)", appSource);
            Assert.Contains("CX_MATH_DATA_API CX_IMPORT", libraryHeader);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCanConsumeGenericCountInterfaceImplementation()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-collection-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"),
                "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public interface ICollection<T> { uint Count { const get; } }
                public class Bag<T> : ICollection<T> {
                    public uint Count { const get { return 3u; } }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public uint GetCount() {
                    ICollection<int> items = new Bag<int>();
                    return items.Count;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("cx_iface_ref", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ImportedGenericMethodAllocatesCellsForCapturedParameters()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-capture-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"),
                "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class Source<T> {
                    public void Register(delegate() => void callback) {
                        delegate() => void wrapped = () => callback();
                        wrapped();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public void Run(delegate() => void callback) {
                    Source<int> source = new Source<int>();
                    source.Register(callback);
                    Source<string> textSource = new Source<string>();
                    textSource.Register(callback);
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("__cx_generated_cell_1", appSource);
            var capturedCellStores = appSource.Split('\n').Where(line =>
                line.Contains("_store_", StringComparison.Ordinal) &&
                line.Contains("__cx_generated_cell_1", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(capturedCellStores);
            Assert.Contains(capturedCellStores,
                line => line.Contains("callback", StringComparison.Ordinal));
            Assert.Matches(
                @"struct __cx_lambda_[A-F0-9]+_\d+_\d+_[A-F0-9]{8}_environment\s*\{",
                appSource);
            Assert.Matches(
                @"static void app___cx_lambda_[A-F0-9]+_\d+_\d+_[A-F0-9]{8}\(void \*__cx_environment\) \{",
                appSource);
            Assert.Equal(2, Regex.Matches(
                appSource,
                @"static void app___cx_lambda_[A-F0-9]+_\d+_\d+_[A-F0-9]{8}\(void \*__cx_environment\) \{").Cast<Match>().Count());
            Assert.Contains("__cx_function_reflection_", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReferencedGenericOwnerFieldInitializerCanConstructConcreteClass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-generic-field-init-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"),
                "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class Seed {
                    public int Value = 17;
                    public constructor() {}
                }
                public class Holder<T> {
                    public Seed SeedValue = new Seed();
                    public constructor(Seed seed) { SeedValue = seed; }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public Holder<int> Create(Seed seed) { return new Holder<int>(seed); }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("__this->SeedValue", appSource);
            Assert.Contains("library_Lib_Seed", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCyclesAreReportedBeforeCodeGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-cycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cxproj"), "name: a\nproject_references:\n- b.cxproj\n");
            File.WriteAllText(Path.Combine(root, "b.cxproj"), "name: b\nproject_references:\n- a.cxproj\n");
            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(root, "a.cxproj")]));
            Assert.Contains("Project reference cycle", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".obj")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferencePropagatesPointerRetentionSummary()
    {
        WithPointerProjectReference(
            """
            namespace Lib;
            public class Holder { public ptr<int> Value; }
            public unsafe void Forward(Holder holder, ptr<int> value) {
                Retain(holder, value);
            }
            public unsafe void Retain(Holder holder, ptr<int> value) {
                holder.Value = value;
            }
            """,
            """
            import Lib;
            namespace App;
            public unsafe void Bad(Holder holder) {
                int local = 42;
                Forward(holder, &local);
            }
            """,
            appProject =>
            {
                var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                    () => new Compiler().Compile([appProject]));
                Assert.Contains("function that may retain it", error.Message,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void ProjectReferencePropagatesPointerReturnSummary()
    {
        WithPointerProjectReference(
            """
            namespace Lib;
            public class Holder { public ptr<int> Value; }
            public unsafe ptr<int> Identity(ptr<int> value) { return value; }
            """,
            """
            import Lib;
            namespace App;
            public unsafe void Bad(Holder holder) {
                int local = 42;
                holder.Value = Identity(&local);
            }
            """,
            appProject =>
            {
                var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                    () => new Compiler().Compile([appProject]));
                Assert.Contains("Cannot store a pointer borrowing local stack storage",
                    error.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void ProjectReferenceAcceptsProvenNonRetainingPointerCall()
    {
        WithPointerProjectReference(
            """
            namespace Lib;
            public unsafe int Read(ptr<int> value) { return value[0]; }
            """,
            """
            import Lib;
            namespace App;
            public unsafe int Good() {
                int local = 42;
                return Read(&local);
            }
            """,
            appProject => new Compiler().Compile([appProject]));
    }

    private static void WithPointerProjectReference(
        string librarySource,
        string appSource,
        Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-pointer-summary-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"),
                """
                name: library
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), librarySource);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), appSource);
            action(Path.Combine(app, "app.cxproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCanSpecializeAnImportedGenericFunction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-generic-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"), "name: generic\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Lib; public T Identity<T>(T value) { return value; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\ntype: Executable\nproject_references:\n- ../generic/generic.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "import Lib; namespace App; public int Run() { return Identity(42); }");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("return value;", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCallsPublicGenericSpecializationInOwningModule()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-specialization-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"),
                """
                name: generic
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public T Identity<T>(T value) { return value; }
                public int Identity <int>(int value) { return value + 1; }
                public class Counter {
                    public int Value = 10;
                    public T Adjust<T>(T value) { return value; }
                    public int Adjust <int>(int value) { return Value + value; }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                """
                name: app
                type: Executable
                project_references:
                - ../generic/generic.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public int Run() {
                    Counter counter = new Counter();
                    return Identity(42) + counter.Adjust(5);
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "generic.c"));
            var libraryHeader = File.ReadAllText(Path.Combine(library, ".obj", "generic.h"));
            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var symbols = Regex.Matches(
                libraryHeader, @"(?m)^extern .* (cx_generic_[a-f0-9]+)\(");
            Assert.Equal(2, symbols.Count);
            foreach (Match symbol in symbols)
            {
                var specializationName = symbol.Groups[1].Value;
                Assert.Contains($"{specializationName}(", librarySource);
                Assert.Contains($"{specializationName}(", appSource);
            }
            Assert.Contains("return (value + 1);", librarySource);
            Assert.DoesNotContain($"return (value + 1);", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ProjectReferenceRegistersConstrainedGenericClassOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-virtual-constraints-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), """
                name: library
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public interface IMarker { }
                public interface IAuditable { }
                public interface IStamped { }
                public interface IClassInterfaceRouter {
                    T Transform<T>(T value) where T : class, IMarker;
                }
                public interface IStructInterfaceRouter {
                    T TransformStruct<T>(T value) where T : struct, IMarker;
                }
                public interface ITripleClassInterfaceRouter {
                    T TransformTripleClass<T>(T value)
                        where T : class, IMarker, IAuditable, IStamped;
                }
                public interface IGenericVirtualBaseRouter {
                    T Transform<T>(T value);
                }
                public interface IGenericVirtualChildRouter :
                    IGenericVirtualBaseRouter {
                    T Transform<T>(T value, int offset);
                }
                public interface IGenericVirtualDiamondLeftRouter :
                    IGenericVirtualBaseRouter {
                    T Transform<T>(T value);
                }
                public interface IGenericVirtualDiamondRightRouter :
                    IGenericVirtualBaseRouter {
                    T Transform<T>(T value);
                }
                public interface IGenericVirtualDiamondRouter :
                    IGenericVirtualDiamondLeftRouter,
                    IGenericVirtualDiamondRightRouter { }
                public interface ITripleStructInterfaceRouter {
                    T TransformTripleStruct<T>(T value)
                        where T : struct, IMarker, IAuditable, IStamped;
                }
                public T ForwardClassInterface<T>(IClassInterfaceRouter value,
                    T item) where T : class, IMarker {
                    return value.Transform(item);
                }
                public T ForwardStructInterface<T>(IStructInterfaceRouter value,
                    T item) where T : struct, IMarker {
                    return value.TransformStruct(item);
                }
                public T ForwardTripleClassInterface<T>(
                    ITripleClassInterfaceRouter value, T item)
                    where T : class, IMarker, IAuditable, IStamped {
                    return value.TransformTripleClass(item);
                }
                public T ForwardInheritedTransform<T>(
                    IGenericVirtualChildRouter value, T item) {
                    return value.Transform(item);
                }
                public T ForwardAddedTransform<T>(
                    IGenericVirtualChildRouter value, T item, int offset) {
                    return value.Transform(item, offset);
                }
                public T ForwardDiamondTransform<T>(
                    IGenericVirtualDiamondRouter value, T item) {
                    return value.Transform(item);
                }
                public T ForwardChildAlias<T>(
                    IGenericVirtualChildRouter value, T item) {
                    T forwarded = item;
                    return value.Transform(forwarded);
                }
                public T ForwardChildAliasConditional<T>(
                    IGenericVirtualChildRouter value, T item, bool dispatch) {
                    T forwarded = item;
                    return dispatch ? value.Transform(forwarded) : forwarded;
                }
                public T ForwardChildAliasIf<T>(
                    IGenericVirtualChildRouter value, T item, bool dispatch) {
                    T forwarded = item;
                    if (dispatch) return value.Transform(forwarded);
                    return forwarded;
                }
                public T ForwardChildAliasIfElse<T>(
                    IGenericVirtualChildRouter value, T item, bool dispatch) {
                    T forwarded = item;
                    bool shouldDispatch = dispatch;
                    if (shouldDispatch) return forwarded;
                    else return value.Transform(forwarded);
                }
                public T ForwardChildAliasAfterBool<T>(
                    IGenericVirtualChildRouter value, T item, bool dispatch) {
                    bool shouldDispatch = dispatch;
                    T forwarded = item;
                    if (shouldDispatch) return value.Transform(forwarded);
                    return forwarded;
                }
                public T ForwardChildAliasChain<T>(
                    IGenericVirtualChildRouter value, T item, bool dispatch) {
                    T first = item;
                    T forwarded = first;
                    if (dispatch) return value.Transform(forwarded);
                    return forwarded;
                }
                public T ForwardChildInferredAlias<T>(
                    IGenericVirtualChildRouter value, T item, bool dispatch) {
                    var forwarded = item;
                    if (dispatch) return value.Transform(forwarded);
                    return forwarded;
                }
                public T ForwardTripleStructInterface<T>(
                    ITripleStructInterfaceRouter value, T item)
                    where T : struct, IMarker, IAuditable, IStamped {
                    return value.TransformTripleStruct(item);
                }
                public class VirtualBase {
                    public virtual T Transform<T>(T value)
                        where T : class, IMarker { return value; }
                    public T Forward<T>(T value)
                        where T : class, IMarker { return this.Transform(value); }
                }
                public class StructVirtualBase {
                    public virtual T Transform<T>(T value)
                        where T : struct, IMarker { return value; }
                    public T Forward<T>(T value)
                        where T : struct, IMarker { return this.Transform(value); }
                }
                public class MultiInterfaceVirtualBase {
                    public virtual T Transform<T>(T value)
                        where T : class, IMarker, IAuditable { return value; }
                    public T Forward<T>(T value)
                        where T : class, IMarker, IAuditable {
                        return this.Transform(value);
                    }
                }
                public class TripleInterfaceVirtualBase {
                    public virtual T Transform<T>(T value)
                        where T : class, IMarker, IAuditable, IStamped { return value; }
                    public T Forward<T>(T value)
                        where T : class, IMarker, IAuditable, IStamped {
                        return this.Transform(value);
                    }
                }
                public class TripleStructInterfaceVirtualBase {
                    public virtual T Transform<T>(T value)
                        where T : struct, IMarker, IAuditable, IStamped { return value; }
                    public T Forward<T>(T value)
                        where T : struct, IMarker, IAuditable, IStamped {
                        return this.Transform(value);
                    }
                }
                public class MultiInterfaceStructVirtualBase {
                    public virtual T Transform<T>(T value)
                        where T : struct, IMarker, IAuditable { return value; }
                    public T Forward<T>(T value)
                        where T : struct, IMarker, IAuditable {
                        return this.Transform(value);
                    }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public class Marker : IMarker { public int Value; }
                public class FallbackMarker : IMarker { public int Value = 7; }
                public class ClassInterfaceRouter : IClassInterfaceRouter {
                    public T Transform<T>(T value) where T : class, IMarker {
                        return value;
                    }
                    public Marker Transform<Marker>(Marker value) {
                        value.Value = 54;
                        return value;
                    }
                }
                public struct ExactStruct : IMarker { public int Value; }
                public struct RouterFallbackStruct : IMarker { public int Value; }
                public class StructInterfaceRouter : IStructInterfaceRouter {
                    public T TransformStruct<T>(T value)
                        where T : struct, IMarker { return value; }
                    public ExactStruct TransformStruct<ExactStruct>(ExactStruct value) {
                        value.Value = 58;
                        return value;
                    }
                }
                public class TripleMarker : IMarker, IAuditable, IStamped {
                    public int Value;
                }
                public class TripleFallback : IMarker, IAuditable, IStamped {
                    public int Value = 14;
                }
                public class TripleClassRouter : ITripleClassInterfaceRouter {
                    public T TransformTripleClass<T>(T value)
                        where T : class, IMarker, IAuditable, IStamped {
                        return value;
                    }
                    public TripleMarker TransformTripleClass<TripleMarker>(
                        TripleMarker value) {
                        value.Value = 59;
                        return value;
                    }
                }
                public class GenericVirtualChildRouter :
                    IGenericVirtualChildRouter {
                    public T Transform<T>(T value) { return value; }
                    public int Transform<int>(int value) { return value + 55; }
                    public T Transform<T>(T value, int offset) { return value; }
                    public int Transform<int>(int value, int offset) {
                        return value + offset + 100;
                    }
                }
                public class GenericVirtualDiamondRouterImpl :
                    IGenericVirtualDiamondRouter {
                    public T Transform<T>(T value) { return value; }
                    public int Transform<int>(int value) { return value + 75; }
                }
                public struct TripleStructMarker : IMarker, IAuditable, IStamped {
                    public int Value;
                }
                public struct TripleStructFallback : IMarker, IAuditable, IStamped {
                    public int Value;
                }
                public class TripleStructRouter : ITripleStructInterfaceRouter {
                    public T TransformTripleStruct<T>(T value)
                        where T : struct, IMarker, IAuditable, IStamped {
                        return value;
                    }
                    public TripleStructMarker TransformTripleStruct<TripleStructMarker>(
                        TripleStructMarker value) {
                        value.Value = 61;
                        return value;
                    }
                }
                public class VirtualDerived : VirtualBase {
                    public override U Transform<U>(U value)
                        where U : class, IMarker { return value; }
                    public override Marker Transform<Marker>(Marker value) {
                        value.Value = 42;
                        return value;
                    }
                }
                public struct MarkerStruct : IMarker { public int Value; }
                public struct FallbackStruct : IMarker { public int Value; }
                public class StructVirtualDerived : StructVirtualBase {
                    public override U Transform<U>(U value)
                        where U : struct, IMarker { return value; }
                    public override MarkerStruct Transform<MarkerStruct>(
                        MarkerStruct value) {
                        value.Value = 51;
                        return value;
                    }
                }
                public class MultiMarker : IMarker, IAuditable { public int Value; }
                public class MultiFallback : IMarker, IAuditable { public int Value; }
                public class MultiVirtualDerived : MultiInterfaceVirtualBase {
                    public override U Transform<U>(U value)
                        where U : class, IMarker, IAuditable { return value; }
                    public override MultiMarker Transform<MultiMarker>(
                        MultiMarker value) {
                        value.Value = 52;
                        return value;
                    }
                }
                public class TripleVirtualMarker : IMarker, IAuditable, IStamped {
                    public int Value;
                }
                public class TripleVirtualFallback : IMarker, IAuditable, IStamped {
                    public int Value = 17;
                }
                public class TripleVirtualDerived : TripleInterfaceVirtualBase {
                    public override U Transform<U>(U value)
                        where U : class, IMarker, IAuditable, IStamped { return value; }
                    public override TripleVirtualMarker Transform<TripleVirtualMarker>(
                        TripleVirtualMarker value) {
                        value.Value = 63;
                        return value;
                    }
                }
                public struct TripleStructVirtualMarker : IMarker, IAuditable, IStamped {
                    public int Value;
                }
                public struct TripleStructVirtualFallback : IMarker, IAuditable, IStamped {
                    public int Value;
                }
                public class TripleStructVirtualDerived : TripleStructInterfaceVirtualBase {
                    public override U Transform<U>(U value)
                        where U : struct, IMarker, IAuditable, IStamped { return value; }
                    public override TripleStructVirtualMarker Transform<TripleStructVirtualMarker>(
                        TripleStructVirtualMarker value) {
                        value.Value = 64;
                        return value;
                    }
                }
                public struct MultiMarkerStruct : IMarker, IAuditable {
                    public int Value;
                }
                public struct MultiFallbackStruct : IMarker, IAuditable {
                    public int Value;
                }
                public class MultiStructVirtualDerived : MultiInterfaceStructVirtualBase {
                    public override U Transform<U>(U value)
                        where U : struct, IMarker, IAuditable { return value; }
                    public override MultiMarkerStruct Transform<MultiMarkerStruct>(
                        MultiMarkerStruct value) {
                        value.Value = 53;
                        return value;
                    }
                }
                public int Run() {
                    IClassInterfaceRouter classInterface =
                        new ClassInterfaceRouter();
                    Marker exactClassInterface = new Marker();
                    FallbackMarker fallbackClassInterface =
                        new FallbackMarker();
                    IStructInterfaceRouter structInterface =
                        new StructInterfaceRouter();
                    ExactStruct exactStructInterface = default;
                    RouterFallbackStruct fallbackStructInterface = default;
                    fallbackStructInterface.Value = 13;
                    ITripleClassInterfaceRouter tripleClassInterface =
                        new TripleClassRouter();
                    TripleMarker exactTripleClassInterface = new TripleMarker();
                    TripleFallback fallbackTripleClassInterface =
                        new TripleFallback();
                    IGenericVirtualChildRouter childRouter =
                        new GenericVirtualChildRouter();
                    IGenericVirtualDiamondRouter diamondRouter =
                        new GenericVirtualDiamondRouterImpl();
                    ITripleStructInterfaceRouter tripleStructInterface =
                        new TripleStructRouter();
                    TripleStructMarker exactTripleStructInterface = default;
                    TripleStructFallback fallbackTripleStructInterface = default;
                    fallbackTripleStructInterface.Value = 15;
                    VirtualBase value = new VirtualDerived();
                    Marker marker = new Marker();
                    StructVirtualBase structValue = new StructVirtualDerived();
                    MarkerStruct exact = default;
                    FallbackStruct fallback = default;
                    MultiInterfaceVirtualBase multiValue = new MultiVirtualDerived();
                    MultiMarker multiExact = new MultiMarker();
                    MultiFallback multiFallback = new MultiFallback();
                    TripleInterfaceVirtualBase tripleValue = new TripleVirtualDerived();
                    TripleVirtualMarker tripleExact = new TripleVirtualMarker();
                    TripleVirtualFallback tripleFallback = new TripleVirtualFallback();
                    TripleStructInterfaceVirtualBase tripleStructValue =
                        new TripleStructVirtualDerived();
                    TripleStructVirtualMarker tripleStructExact = default;
                    TripleStructVirtualFallback tripleStructFallback = default;
                    tripleStructFallback.Value = 18;
                    MultiInterfaceStructVirtualBase multiStructValue =
                        new MultiStructVirtualDerived();
                    MultiMarkerStruct multiStructExact = default;
                    MultiFallbackStruct multiStructFallback = default;
                    return ForwardClassInterface(classInterface,
                            exactClassInterface).Value +
                        ForwardClassInterface(classInterface,
                            fallbackClassInterface).Value +
                        ForwardStructInterface(structInterface,
                            exactStructInterface).Value +
                        ForwardStructInterface(structInterface,
                            fallbackStructInterface).Value +
                        ForwardTripleClassInterface(tripleClassInterface,
                            exactTripleClassInterface).Value +
                        ForwardTripleClassInterface(tripleClassInterface,
                            fallbackTripleClassInterface).Value +
                        (tripleValue.Forward(tripleExact).Value == 63 ? 1 : 0) +
                        (tripleValue.Forward(tripleFallback).Value == 17 ? 1 : 0) +
                        (tripleStructValue.Forward(tripleStructExact).Value == 64 ? 1 : 0) +
                        (tripleStructValue.Forward(tripleStructFallback).Value == 18 ? 1 : 0) +
                        (ForwardInheritedTransform(childRouter, 2) == -35 ? 1 : 0) +
                        (ForwardInheritedTransform(childRouter, 2L) == 2L ? 1 : 0) +
                        (ForwardDiamondTransform(diamondRouter, 2) == -55 ? 1 : 0) +
                        (ForwardDiamondTransform(diamondRouter, 2L) == 2L ? 1 : 0) +
                        (ForwardAddedTransform(childRouter, 2, 3) == 70 ? 1 : 0) +
                        (ForwardAddedTransform(childRouter, 2L, 3) == 2L ? 1 : 0) +
                        (ForwardChildAlias(childRouter, 2) == -35 ? 1 : 0) +
                        (ForwardChildAlias(childRouter, 2L) == 2L ? 1 : 0) +
                        (ForwardChildAliasConditional(childRouter, 2, true) == -35 ? 1 : 0) +
                        (ForwardChildAliasConditional(childRouter, 2, false) == 2 ? 1 : 0) +
                        (ForwardChildAliasIf(childRouter, 2, true) == -35 ? 1 : 0) +
                        (ForwardChildAliasIf(childRouter, 2, false) == 2 ? 1 : 0) +
                        (ForwardChildAliasIfElse(childRouter, 2, false) == -35 ? 1 : 0) +
                        (ForwardChildAliasIfElse(childRouter, 2, true) == 2 ? 1 : 0) +
                        (ForwardChildAliasAfterBool(childRouter, 2, true) == -35 ? 1 : 0) +
                        (ForwardChildAliasAfterBool(childRouter, 2, false) == 2 ? 1 : 0) +
                        (ForwardChildAliasChain(childRouter, 2, true) == -35 ? 1 : 0) +
                        (ForwardChildAliasChain(childRouter, 2, false) == 2 ? 1 : 0) +
                        (ForwardChildInferredAlias(childRouter, 2, true) == -35 ? 1 : 0) +
                        (ForwardChildInferredAlias(childRouter, 2L, false) == 2L ? 1 : 0) +
                        (ForwardTripleStructInterface(tripleStructInterface,
                            exactTripleStructInterface).Value == 61 ? 1 : 0) +
                        (ForwardTripleStructInterface(tripleStructInterface,
                            fallbackTripleStructInterface).Value == 15 ? 1 : 0) +
                        value.Forward<Marker>(marker).Value +
                        structValue.Forward<MarkerStruct>(exact).Value +
                        structValue.Forward<FallbackStruct>(fallback).Value +
                        multiValue.Forward<MultiMarker>(multiExact).Value +
                        multiValue.Forward<MultiFallback>(multiFallback).Value +
                        multiStructValue.Forward<MultiMarkerStruct>(multiStructExact).Value +
                        multiStructValue.Forward<MultiFallbackStruct>(multiStructFallback).Value;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "library.c"));
            Assert.Contains("cx_require_generic_virtual_method", appSource);
            Assert.Contains("Lib.IClassInterfaceRouter.Transform`1(!T)->!T", appSource);
            Assert.Contains("App.ClassInterfaceRouter.Transform#replacement", appSource);
            Assert.Contains("Lib.IStructInterfaceRouter.TransformStruct`1(!T)->!T", appSource);
            Assert.Contains("App.StructInterfaceRouter.TransformStruct#replacement", appSource);
            Assert.Contains("Lib.ITripleClassInterfaceRouter.TransformTripleClass`1(!T)->!T", appSource);
            Assert.Contains("App.TripleClassRouter.TransformTripleClass#replacement", appSource);
            Assert.Contains("IGenericVirtualBaseRouter.Transform", appSource);
            Assert.Contains("IGenericVirtualChildRouter.Transform", appSource);
            Assert.Contains("App.GenericVirtualChildRouter.Transform#replacement", appSource);
            Assert.Contains("Lib.ITripleStructInterfaceRouter.TransformTripleStruct", appSource);
            Assert.Contains("App.TripleStructRouter.TransformTripleStruct#replacement", appSource);
            Assert.Contains("Lib.VirtualBase.Transform`1(!T)->!T", appSource);
            Assert.Contains("cx_register_generic_virtual_method", appSource);
            Assert.Contains("App.VirtualDerived.Transform#replacement", appSource);
            Assert.Contains("App.StructVirtualDerived.Transform#replacement", appSource);
            Assert.Contains("Lib.StructVirtualBase.Transform`1(!T)->!T", appSource);
            Assert.Contains("App.MultiVirtualDerived.Transform#replacement", appSource);
            Assert.Contains("App.MultiStructVirtualDerived.Transform#replacement", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConsumingProjectCanReplaceImportedGenericFunctionSpecializations()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-specialization-override-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        var plainApp = Path.Combine(root, "plain-app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(plainApp);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"), """
                name: generic
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public T Identity<T>(T value) { return value; }
                public int Identity<int>(int value) { return value + 1; }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../generic/generic.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "override.cx"), """
                namespace Lib;
                public int Identity<int>(int value) { return value + 100; }
                public long Identity<long>(long value) { return value + 200L; }
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public int Run() { return Identity(41); }
                public long RunLong() { return Identity(10L); }
                public string RunFallback() { return Identity("fallback"); }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "generic.c"));
            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("return (value + 1);", librarySource);
            Assert.Contains("return (value + 100);", appSource);
            Assert.Contains("return (value + 200L);", appSource);
            Assert.Contains("return value;", appSource);
            Assert.DoesNotContain("return (value + 100);", librarySource);

            File.WriteAllText(Path.Combine(plainApp, "plain.cxproj"), """
                name: plain
                type: Executable
                project_references:
                - ../generic/generic.cxproj
                """);
            File.WriteAllText(Path.Combine(plainApp, "main.cx"), """
                import Lib;
                namespace Plain;
                public int Run() { return Identity(41); }
                """);
            new Compiler().Compile([Path.Combine(plainApp, "plain.cxproj")]);
            var plainSource = File.ReadAllText(Path.Combine(plainApp, ".obj", "plain.c"));
            Assert.DoesNotContain("return (value + 100);", plainSource);
            Assert.DoesNotContain("return (value + 200L);", plainSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCanUseAnImportedDerivedConcept()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-concept-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"),
                "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public concept CReadable { int Read() const; }
                public concept CValueWritable { int Value { set; } }
                public concept CWritableReadable : CReadable, CValueWritable { }
                public void Set<T>(T value, int next) : T = CWritableReadable {
                    value.Value = next;
                }
                public int Read<T>(T value) : T = CWritableReadable {
                    return value.Read();
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../library/library.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public class Item {
                    public int Value { const get; set; }
                    public int Read() const { return Value; }
                }
                public int Run() {
                    Item item = new Item();
                    Set(item, 42);
                    return Read(item);
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("Item", appSource);
            Assert.DoesNotContain("_unknowntype_", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCanCloseAnImportedGenericClass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-generic-type-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"), "name: generic\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"),
                "namespace Lib; public class Box<T> { public T Value; public constructor() {} } public class Crate<T> { public T Value; public constructor() {} } public int CreateBox() { Box<int> box = new Box<int>(); box.Value = 41; return box.Value; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../generic/generic.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"),
                "import Lib; namespace App; public int Run() { Box<int> box = new Box<int>(); Crate<int> crate = new Crate<int>(); box.Value = 42; crate.Value = box.Value; return crate.Value + CreateBox(); }");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var appHeader = File.ReadAllText(Path.Combine(app, ".obj", "app.internal.h"));
            var genericHeader = File.ReadAllText(Path.Combine(library, ".obj", "generic.h"));
            var genericSource = File.ReadAllText(Path.Combine(library, ".obj", "generic.c"));
            var dependencyBoxIdentity = Regex.Match(genericHeader,
                @"struct (cx_generic_[0-9a-f]+) \{").Groups[1].Value;
            Assert.NotEmpty(dependencyBoxIdentity);
            Assert.Contains("cx_generic_", appHeader);
            Assert.DoesNotContain($"CX_BEGIN_VTABLE_DEF({dependencyBoxIdentity})", appSource);
            Assert.Contains("CX_BEGIN_VTABLE_DEF(cx_generic_", appSource);
            Assert.Contains("CX_BEGIN_VTABLE_DEF(cx_generic_", genericSource);
            Assert.Contains("CX_STRING_DEF(cx_generic_", appSource);
            Assert.Contains("cx_generic_", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceDoesNotExposePrivateFunctions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-private-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Lib; struct Internal {} int Hidden(Internal value) { return 1; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\nproject_references:\n- ../library/library.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "import Lib; namespace App; public int Run() { return Hidden(); }");

            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("Cannot resolve function 'Hidden'", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsTypeNameCollisionsAcrossProjectBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-collision-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Shared; public class Token {}");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\nproject_references:\n- ../library/library.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "namespace Shared; public class Token {}");

            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("declared by multiple CX projects", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceSpecializesGenericContainingMethodUsingOwningTypeLayout()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-container-specialization-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"),
                "name: generic\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class Router<TState> {
                    public U Route<U>(TState state, U value) { return value; }
                    public int Route<int>(TState state, int value) { return value + 1; }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../generic/generic.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public int Run() {
                    Router<string> router = new Router<string>();
                    if (router.Route<int>("state", 2) != 3) return 1;
                    return router.Route<long>("state", 4L) == 4L ? 42 : 2;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("generic_Lib_Router", appSource);
            Assert.Contains("return (value + 1);", appSource);
            Assert.Contains("return value;", appSource);
            Assert.DoesNotContain("app_Lib_Router", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceSpecializesStaticGenericMethodThroughClosedGenericOwner()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-static-generic-owner-specialization-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"),
                "name: generic\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class StaticRouter<TState> {
                    public static U Route<U>(TState state, U value) { return value; }
                    public static int Route<int>(TState state, int value) { return value + 20; }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../generic/generic.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public int Run() {
                    return StaticRouter<string>.Route<int>("state", 3);
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "generic.c"));
            Assert.Contains("return (value + 20);", appSource);
            Assert.DoesNotContain("return (value + 20);", librarySource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReferencedClosedGenericOwnerRegistersVirtualSpecializations()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"cxc-project-generic-virtual-owner-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), """
                name: library
                type: Library
                """);
            File.WriteAllText(Path.Combine(library, "api.cx"), """
                namespace Lib;
                public class GenericVirtualOwner<TState> {
                    public constructor() { }
                    public virtual U Transform<U>(U value) { return value; }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"), """
                name: app
                type: Executable
                project_references:
                - ../library/library.cxproj
                """);
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Lib;
                namespace App;
                public class Derived<TState> : GenericVirtualOwner<TState> {
                    public constructor() : base() { }
                    public override U Transform<U>(U value) { return value; }
                    public override int Transform<int>(int value) {
                        return value + 90;
                    }
                }
                public int Run() {
                    GenericVirtualOwner<string> owner = new Derived<string>();
                    if (owner.Transform(2) != 92) return 1;
                    if (owner.Transform(11L) != 11L) return 2;
                    return 42;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("cx_register_generic_virtual_method", appSource);
            Assert.Contains("Lib.GenericVirtualOwner.Transform`1(!U)->!U", appSource);
            Assert.Contains("App.Derived.Transform#replacement", appSource);
            Assert.Contains("cx_generic_", appSource);
            Assert.Equal(4, Regex.Matches(appSource,
                "methodIdentity = \\\"Lib\\.GenericVirtualOwner\\.Transform").Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCMakeOrderIsDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-order-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var alpha = Path.Combine(root, "alpha");
        var zeta = Path.Combine(root, "zeta");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(zeta);
        try
        {
            File.WriteAllText(Path.Combine(alpha, "alpha.cxproj"), "name: alpha\ntype: Library\n");
            File.WriteAllText(Path.Combine(zeta, "zeta.cxproj"), "name: zeta\ntype: Library\n");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\ntype: Executable\nproject_references:\n- ../zeta/zeta.cxproj\n- ../alpha/alpha.cxproj\n");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var cmake = File.ReadAllText(Path.Combine(app, ".obj", "CMakeLists.txt"));
            Assert.True(cmake.IndexOf("../alpha/.obj", StringComparison.Ordinal) <
                cmake.IndexOf("../zeta/.obj", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
