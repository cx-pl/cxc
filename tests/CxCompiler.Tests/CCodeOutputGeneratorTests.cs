using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace CxCompiler.Tests;

public sealed class CCodeOutputGeneratorTests
{
    [Fact]
    public void WritesVersionedModuleEffectMetadata()
    {
        var project = CxProject.CreateDefaultApplicationProject("effect_export");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Interop;
            public class Payload {}
            public class Contract {
                @NoEscape public void Read(Payload value) {}
                public void Store(Payload value) {}
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-effects-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(directory, "input.cx"));
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(directory, "effect_export.cxeffects.json")));
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("moduleMetadataFormatVersion").GetInt32());
            Assert.Equal("effect_export", root.GetProperty("moduleName").GetString());
            var functions = root.GetProperty("functions").EnumerateArray().ToArray();
            var read = Assert.Single(functions, function =>
                function.GetProperty("name").GetString() == "Contract.Read");
            var store = Assert.Single(functions, function =>
                function.GetProperty("name").GetString() == "Contract.Store");
            Assert.True(read.GetProperty("noEscape").GetBoolean());
            Assert.True(read.GetProperty("pointerBorrowSummaryComplete").GetBoolean());
            Assert.False(store.GetProperty("noEscape").GetBoolean());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ImportsNoEscapeContractFromModuleEffectMetadata()
    {
        var exported = CxProject.CreateDefaultApplicationProject("binary_module");
        exported.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Interop;
            public class Payload {}
            public class OtherPayload {}
            public static class BinaryApi {
                @NoEscape public static void Read(Payload value) {}
                public static void Read(OtherPayload value) {}
            }
            """));
        new SemanticBinder().Bind(exported);

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-effects-import-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(exported,
                Path.Combine(directory, "export.cx"));
            var metadataPath = Path.Combine(directory, "binary_module.cxeffects.json");

            CxProject CreateConsumer(bool useOtherPayload = false)
            {
                var project = CxProject.CreateDefaultApplicationProject("binary_module");
                project.ModuleApiReferences.Add(new CxModuleApiReference
                {
                    ModuleName = "binary_module",
                    Source = "binary.cxapi",
                    Version = exported.Version,
                });
                var inputType = useOtherPayload ? "OtherPayload" : "Payload";
                project.AddCompilationContext(CompilerTestHelper.Parse($$"""
                    import System;
                    public class Payload {}
                    public class OtherPayload {}
                    public static class BinaryApi {
                        public static extern void Read(Payload value);
                        public static extern void Read(OtherPayload value);
                    }
                    public void Consume({{inputType}} value) {
                        Memory.WithArena(() => BinaryApi.Read(value));
                    }
                    """));
                project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
                    namespace System;
                    public static class Memory {
                        public extern static void WithArena(delegate() => void action);
                    }
                    """), "cxcore", syntheticCore: true);
                return project;
            }

            var withoutMetadata = CreateConsumer();
            var error = Assert.Throws<CompilationErrorException>(() =>
                new SemanticBinder().Bind(withoutMetadata));
            Assert.True(error.Message.Contains("possibly arena-scoped"), error.Message);

            var withMetadata = CreateConsumer();
            CxEffectMetadataLoader.Apply(withMetadata, [metadataPath]);
            new SemanticBinder().Bind(withMetadata);

            var sameArityDifferentType = CreateConsumer(useOtherPayload: true);
            CxEffectMetadataLoader.Apply(sameArityDifferentType, [metadataPath]);
            var otherError = Assert.Throws<CompilationErrorException>(() =>
                new SemanticBinder().Bind(sameArityDifferentType));
            Assert.Contains("possibly arena-scoped", otherError.Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RejectsModuleEffectMetadataWithMismatchedExportedSymbol()
    {
        var exported = CxProject.CreateDefaultApplicationProject("binary_module");
        exported.AddCompilationContext(CompilerTestHelper.Parse("""
            public static class BinaryApi {
                public static void Read(int value) {}
            }
            """));
        new SemanticBinder().Bind(exported);

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-effects-symbol-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(exported,
                Path.Combine(directory, "export.cx"));
            var metadataPath = Path.Combine(directory, "binary_module.cxeffects.json");
            var metadata = JsonSerializer.Deserialize<CxEffectMetadataDocument>(
                File.ReadAllText(metadataPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })!;
            var tamperedMetadata = metadata with
            {
                Functions = metadata.Functions.Select(effect => effect with
                {
                    Symbol = "stale_or_misdirected_symbol",
                }).ToArray(),
            };
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(tamperedMetadata));

            var consumer = CxProject.CreateDefaultApplicationProject("binary_module");
            consumer.ModuleApiReferences.Add(new CxModuleApiReference
            {
                ModuleName = "binary_module",
                Source = "binary.cxapi",
                Version = exported.Version,
            });
            consumer.AddCompilationContext(CompilerTestHelper.Parse("""
                public static class BinaryApi {
                    public static extern void Read(int value);
                }
                """));

            var error = Assert.Throws<CompilationErrorException>(() =>
                CxEffectMetadataLoader.Apply(consumer, [metadataPath]));
            Assert.Contains("found 0 matching declarations", error.Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RejectsModuleEffectMetadataWithIncompatibleRuntimeFormat()
    {
        var project = CxProject.CreateDefaultApplicationProject("binary_module");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public static class BinaryApi {
                public static extern void Read();
            }
            """));
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-effects-version-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "binary_module.cxeffects.json");
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "moduleMetadataFormatVersion": 4,
                  "moduleName": "binary_module",
                  "moduleVersion": "1.0",
                  "functions": []
                }
                """);

            var error = Assert.Throws<CompilationErrorException>(() =>
                CxEffectMetadataLoader.Apply(project, [path]));
            Assert.Contains("module metadata format 4", error.Message);
            Assert.Contains("format 1 is required", error.Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ImportsPointerBorrowSummaryFromModuleEffectMetadata()
    {
        var exported = CxProject.CreateDefaultApplicationProject("pointer_module");
        exported.AddCompilationContext(CompilerTestHelper.Parse("""
            public unsafe ptr<int> Identity(ptr<int> value) { return value; }
            public static class PointerApi {
                public static ptr<int> Value;
                public static unsafe void Retain(ptr<int> value) { Value = value; }
            }
            """));
        new SemanticBinder().Bind(exported);
        var producerFunction = exported.CompilationContexts[0].DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Identity");
        Assert.True(producerFunction.PointerBorrowSummaryComplete);
        Assert.Contains(0, producerFunction.BorrowedReturnParameterIndices);
        var producerRetain = exported.CompilationContexts[0].DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single().MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Retain");
        Assert.True(producerRetain.PointerBorrowSummaryComplete);
        Assert.Contains(0, producerRetain.RetainedPointerParameterIndices);

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-effects-borrow-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(exported,
                Path.Combine(directory, "pointer_module.cx"));

            var consumer = CxProject.CreateDefaultApplicationProject("pointer_module");
            consumer.ModuleApiReferences.Add(new CxModuleApiReference
            {
                ModuleName = "pointer_module",
                Source = "pointer.cxapi",
                Version = exported.Version,
            });
            consumer.AddCompilationContext(CompilerTestHelper.Parse("""
                public unsafe extern ptr<int> Identity(ptr<int> value);
                public static class PointerApi {
                    public static ptr<int> Value;
                    public static unsafe extern void Retain(ptr<int> value);
                }
                """));
            CxEffectMetadataLoader.Apply(consumer,
                [Path.Combine(directory, "pointer_module.cxeffects.json")]);

            var importedFunction = consumer.CompilationContexts[0].DeclarationScope
                .Declarations.OfType<FunctionDeclaration>().Single();
            Assert.True(importedFunction.PointerBorrowSummaryComplete);
            Assert.Contains(0, importedFunction.BorrowedReturnParameterIndices);
            var importedRetain = consumer.CompilationContexts[0].DeclarationScope.Declarations
                .OfType<ClassDeclaration>().Single().MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>().Single(function => function.Name == "Retain");
            Assert.True(importedRetain.PointerBorrowSummaryComplete);
            Assert.Contains(0, importedRetain.RetainedPointerParameterIndices);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CheckedPointerCompoundAssignmentUsesNativePointerBarrier()
    {
        var pointerType = new PtrType(
            CxCompiler.Model.Common.QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Int);
        var generated = CCodeOutputGenerator.ToCCheckedCompoundAssignment(
            "owner->pointer", pointerType, "+=", "offset");

        Assert.Contains("cx_gc_store_native_pointer", generated);
        Assert.Contains("owner->pointer + offset", generated);
        Assert.Equal("value += step",
            CCodeOutputGenerator.ToCCheckedCompoundAssignment(
                "value", BuiltInSystemTypes.Int, "+=", "step"));
    }

    [Fact]
    public void CollectionEnumeratorsAllocateForTheirOwner()
    {
        var generatedSource = GenerateSource("""
            namespace System.Collections;
            public class ArrayEnumerator<T> { }
            public class DictionaryEnumerator<TKey, TValue> { }
            public class LinkedListEnumerator<T> { }
            public class Array<T> {
                public constructor(uint length) { }
                public ArrayEnumerator<T> GetEnumerator() const {
                    return new ArrayEnumerator<T>();
                }
            }
            public class Dictionary<TKey, TValue> {
                public DictionaryEnumerator<TKey, TValue> GetEnumerator() const {
                    return new DictionaryEnumerator<TKey, TValue>();
                }
            }
            public class LinkedList<T> {
                public LinkedListEnumerator<T> GetEnumerator() const {
                    return new LinkedListEnumerator<T>();
                }
            }
            public int Main() {
                Array<int> array = new Array<int>(1u);
                Dictionary<string, int> dictionary =
                    new Dictionary<string, int>();
                LinkedList<int> linkedList = new LinkedList<int>();
                array.GetEnumerator();
                dictionary.GetEnumerator();
                linkedList.GetEnumerator();
                return 0;
            }
            """);

        Assert.True(3 == generatedSource.Split(
            "cx_memory_alloc_for_owner((const void*)(__this)",
            StringSplitOptions.None).Length - 1, generatedSource);
    }

    [Fact]
    public void EmitsQualifiedNamesDirectlyAndPreservesUnderscores()
    {
        var generatedSource = GenerateSource("""
            namespace Company.Product;
            public class _Service {
                public static int _Value = 42;
                public constructor() { }
                public int Execute() { return _Value; }
                public int Execute(int value) { return value; }
            }
            """);

        Assert.Contains("unnamed_Company_Product__Service__Value = 42", generatedSource);
        Assert.Contains("unnamed_Company_Product__Service___constructor(", generatedSource);
        Assert.Contains("unnamed_Company_Product__Service_Execute(", generatedSource);
        Assert.Contains("unnamed_Company_Product__Service_Execute__2(", generatedSource);
    }

    [Fact]
    public void RoutesReferenceAssignmentsThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class Node {
                public Node Next;
                public constructor() { }
            }
            public void Link(Node owner, Node value) {
                owner.Next = value;
            }
            """);

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_reference(", StringComparison.Ordinal) &&
            line.Contains("Next", StringComparison.Ordinal));
        Assert.Contains("Next", checkedStore);
        Assert.Contains("value", checkedStore);
    }

    [Fact]
    public void RoutesReferenceLocalInitializersThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public interface INode { int Get(); }
            public class Node : INode {
                public constructor() { }
                public int Get() { return 1; }
            }
            public Node Copy(Node value) {
                Node local = value;
                INode interfaceLocal = value;
                return local;
            }
            """);

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_reference(", StringComparison.Ordinal) &&
            line.Contains("local", StringComparison.Ordinal));
        Assert.Contains("(cx_ptr*)&(local)", checkedStore);
        Assert.Contains("value", checkedStore);
        Assert.Contains("cx_gc_store_interface_reference(", generatedSource);
        Assert.Contains("interfaceLocal", generatedSource);
    }

    [Fact]
    public void RoutesReferenceBearingAggregateLocalInitializersThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Pair { public Node Reference; public int Count; }
            public Pair Copy(Pair value) {
                Pair local = value;
                return local;
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&(local)", generatedSource);
        Assert.Contains("&unnamed_Pair___typeinfo", generatedSource);
    }

    [Fact]
    public void RoutesGenericTypedMemoryCopyThroughReflectedValueCopy()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public class Node { public constructor() { } }
            public struct Pair { public Node Reference; public int Count; }
            public static class Memory {
                public static unsafe extern void CopyValue<T>(
                    ptr destination, ptr source) where T : struct;
            }
            public unsafe void CopyPair(ptr destination, ptr source) {
                Memory.CopyValue<Pair>(destination, source);
            }
            """));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-typed-copy-{Guid.NewGuid():N}");

        string generatedSource;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "TypedCopy.cx"));
            generatedSource = File.ReadAllText(Path.Combine(outputDirectory, "cxcore.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        Assert.Contains("cx_gc_copy_value((cx_ptr)(destination), (cx_ptr)(source)", generatedSource);
        Assert.Contains("&cxcore_System_Pair___typeinfo", generatedSource);
    }

    [Fact]
    public void ClosesTypedMemoryCopyLayoutAtGenericCallSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public class Node { public constructor() { } }
            public struct Pair { public Node Reference; public int Count; }
            public static class Memory {
                public static unsafe extern void CopyValue<T>(
                    ptr destination, ptr source) where T : struct;
            }
            public class Copier<T> where T : struct {
                public unsafe void Copy(ptr destination, ptr source) {
                Memory.CopyValue<T>(destination, source);
                }
            }
            public unsafe void CopyPair(Copier<Pair> copier, ptr destination, ptr source) {
                copier.Copy(destination, source);
            }
            """));
        var copier = project.CompilationContexts[0].DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(declaration => declaration.Name == "Copier");
        Assert.Contains(copier.GenericConstraints, constraint =>
            constraint.ParameterName == "T" &&
            constraint.Kind == GenericConstraintKind.ValueType);
        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-generic-typed-copy-{Guid.NewGuid():N}");
        string generatedSource;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "GenericTypedCopy.cx"));
            generatedSource = File.ReadAllText(Path.Combine(outputDirectory, "cxcore.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        var typedCopyCall = Assert.Single(generatedSource.Split('\n'), line =>
            line.Contains(
                "cx_gc_copy_value((cx_ptr)(destination), (cx_ptr)(source)",
                StringComparison.Ordinal));
        Assert.Contains("&cxcore_System_Pair___typeinfo", typedCopyCall);
        Assert.DoesNotContain("Generic_T___typeinfo", generatedSource);
    }

    [Fact]
    public void RequiresStructConstraintWhenGenericOwnerCallsTypedMemoryCopy()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public static class Memory {
                public static unsafe extern void CopyValue<T>(
                    ptr destination, ptr source) where T : struct;
            }
            public class Copier<T> {
                public unsafe void Copy(ptr destination, ptr source) {
                    Memory.CopyValue<T>(destination, source);
                }
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));
        Assert.Contains("does not satisfy the value-type constraint", error.Message);
    }

    [Fact]
    public void RoutesDynamicBaseTypeMetadataThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class BaseType { }
            public class DerivedType : BaseType { }
            """);

        Assert.Contains("cx_gc_store_reference((cx_ptr*)&", generatedSource);
        Assert.Contains("BaseType._obj", generatedSource);
        Assert.DoesNotContain("memcpy((void *)&", generatedSource);
    }

    [Fact]
    public void RoutesArrayReferenceAssignmentsThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public void Store(Node[] values, Node value) {
                values[0] = value;
            }
            """);

        Assert.Contains("cx_gc_store_reference(", generatedSource);
        Assert.Contains("cx_array_at_checked(", generatedSource);
    }

    [Fact]
    public void RoutesArraySetReferenceAssignmentsThroughRuntimeLifetimeCheck()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {
                public void Set(uint index, T value) { }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Node { public constructor() { } }
            public void Store(System.Collections.Array<Node> values, Node value) {
                values.Set(0u, value);
            }
            """));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-array-set-{Guid.NewGuid():N}");

        string generatedSource;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ArraySet.cx"));
            generatedSource = File.ReadAllText(Path.Combine(outputDirectory, "cxcore.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_reference(", StringComparison.Ordinal) &&
            line.Contains("cx_array_at_checked(", StringComparison.Ordinal));
        Assert.Contains("value", checkedStore);
    }

    [Fact]
    public void RoutesErasedGenericArrayStoresThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class GenericArrayBox<T> {
                public T[] Values;
                public void Set(uint index, T value) { Values[index] = value; }
            }
            public void Store(GenericArrayBox<int> box, int value) {
                box.Set(0u, value);
            }
            """);

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_reference(", StringComparison.Ordinal) &&
            line.Contains("cx_array_at_checked(", StringComparison.Ordinal));
        Assert.Contains("value", checkedStore);
    }

    [Fact]
    public void RoutesGenericMethodArrayStoresThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public void Store<T>(T[] values, T value) { values[0] = value; }
            public void Run(Node[] values, Node value) { Store<Node>(values, value); }
            """);

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_reference(", StringComparison.Ordinal) &&
            line.Contains("cx_array_at_checked(", StringComparison.Ordinal));
        Assert.Contains("value", checkedStore);
    }

    [Fact]
    public void RoutesGenericMethodFieldStoresThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public class Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run(Box<Node> box, Node value) { Store<Node>(box, value); }
            """);

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_reference(", StringComparison.Ordinal) &&
            line.Contains("Value", StringComparison.Ordinal));
        Assert.Contains("value", checkedStore);
    }

    [Fact]
    public void RoutesGenericMethodByValueStructFieldStoresThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Box<T> {
                public T Value;
                public constructor() { }
            }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run(Box<Node> box, Node value) { Store<Node>(box, value); }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_store_reference", StringComparison.Ordinal) &&
            line.Contains("box", StringComparison.Ordinal));
    }

    [Fact]
    public void RoutesGenericMethodReferenceBearingStructFieldStoresThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Envelope { public Node Reference; public int Count; }
            public class Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run(Box<Envelope> box, Envelope value) {
                Store<Envelope>(box, value);
            }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_copy_value", StringComparison.Ordinal) &&
            line.Contains("Value", StringComparison.Ordinal));
        Assert.DoesNotContain("Value = value;", generatedSource);
    }

    [Fact]
    public void RoutesInterfaceAssignmentsThroughRuntimeLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public interface INode { int Get(); }
            public class Node : INode { public int Get() { return 1; } }
            public class Holder {
                public INode Value;
                public void Set(INode value) { Value = value; }
            }
            void Store(Holder holder, INode value) { holder.Set(value); }
            """);

        Assert.Contains("cx_gc_store_interface_reference(", generatedSource);
        Assert.Contains("&(__this->Value)", generatedSource);
    }

    [Fact]
    public void RoutesNativePointerFieldAssignmentsThroughNativePointerLifetimeCheck()
    {
        var generatedSource = GenerateSource("""
            public class PointerHolder {
                public ptr<int> Value;
                public unsafe void Set(ptr<int> value) { Value = value; }
            }
            """);

        var checkedStore = generatedSource.Split('\n').Single(line =>
            line.Contains("cx_gc_store_native_pointer(", StringComparison.Ordinal));
        Assert.Contains("__this->Value", checkedStore);
        Assert.Contains("(cx_ptr)(value)", checkedStore);
    }

    [Fact]
    public void RoutesStructAssignmentsThroughReflectedTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Pair { public Node First; public int Count; }
            public void Copy(Pair destination, Pair source) {
                destination = source;
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&(destination)", generatedSource);
        Assert.Contains("&((struct unnamed_Pair[]){ source })[0]", generatedSource);
        Assert.Contains("&unnamed_Pair___typeinfo", generatedSource);
    }

    [Fact]
    public void RoutesConstructorAggregateFieldInitializationThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Pair { public Node Reference; public int Count; }
            public class Holder {
                public Pair Value;
                public constructor(Pair value) { Value = value; }
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&(__this->Value)", generatedSource);
        Assert.DoesNotContain("__this->Value = value;", generatedSource);
    }

    [Fact]
    public void RoutesClosedGenericStructFieldSetterThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public class Box<T> {
                public T Value;
                public void Set(T value) { Value = value; }
            }
            public void Assign(Box<Payload> box, Payload value) {
                box.Set(value);
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&(", generatedSource);
        Assert.DoesNotContain("__this->Value = value;", generatedSource);
    }

    [Fact]
    public void RoutesClosedGenericClassFieldStoreThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public class Box<T> { public T Value; }
            public void Assign(Box<Payload> box, Payload value) {
                box.Value = value;
            }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_copy_value", StringComparison.Ordinal) &&
            line.Contains("Value", StringComparison.Ordinal));
        Assert.DoesNotContain("box->Value = value;", generatedSource);
    }

    [Fact]
    public void RoutesNullableReferenceFieldStoreThroughReferenceBarrier()
    {
        var generatedSource = GenerateSource("""
            public class NullableHolder {
                private string? value;
                public constructor(string assigned) { value = assigned; }
            }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_store_reference", StringComparison.Ordinal) &&
            line.Contains("value)._obj", StringComparison.Ordinal));
        Assert.DoesNotContain("__this->value =", generatedSource);
    }

    [Fact]
    public void RoutesOpenGenericFieldStoreThroughReferenceBarrier()
    {
        var generatedSource = GenerateSource("""
            public class GenericHolder<T> {
                public T value;
                public constructor(T assigned) { value = assigned; }
            }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_store_reference", StringComparison.Ordinal) &&
            line.Contains("__this->value", StringComparison.Ordinal));
        Assert.DoesNotContain("__this->value = assigned;", generatedSource);
    }

    [Fact]
    public void RoutesClosedGenericPropertyBackingFieldStoreThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public class Box<T> {
                public T stored;
                public T Value {
                    get { return stored; }
                    set { stored = value; }
                }
            }
            public void Assign(Box<Payload> box, Payload value) {
                box.Value = value;
            }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_copy_value", StringComparison.Ordinal) &&
            line.Contains("stored", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "unnamed_Box_Value___set(((struct unnamed_Box*)", generatedSource);
    }

    [Fact]
    public void RoutesClosedGenericInterfacePropertySetterThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public interface IValue<T> {
                T Value { get; set; }
            }
            public class Box<T> : IValue<T> {
                public T stored;
                public T Value {
                    get { return stored; }
                    set { stored = value; }
                }
            }
            public void Assign(Box<Payload> box, Payload value) {
                IValue<Payload> view = box;
                view.Value = value;
            }
            """);

        Assert.Contains(generatedSource.Split('\n'), line =>
            line.Contains("cx_gc_copy_value", StringComparison.Ordinal) &&
            line.Contains("stored", StringComparison.Ordinal));
        Assert.DoesNotContain("__root)->stored =", generatedSource);
    }

    [Fact]
    public void RoutesClosedGenericStructValueFieldThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public struct Wrapper<T> {
                public T Value;
                public constructor() { }
            }
            public void Assign(Wrapper<Payload> wrapper, Payload value) {
                wrapper.Value = value;
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&((wrapper).Value)", generatedSource);
        Assert.DoesNotContain("(wrapper).Value = value;", generatedSource);
    }

    [Fact]
    public void RoutesForDeclarationStructInitializerThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public void Iterate(Payload value) {
                for (Payload local = value; local.Count < 1; local.Count += 1) { }
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&(local)", generatedSource);
        Assert.DoesNotContain("struct unnamed_Payload local = value;", generatedSource);
    }

    [Fact]
    public void RoutesClosedGenericBranchStructFieldAssignmentsThroughTypedCopy()
    {
        var generatedSource = GenerateSource("""
            public class Node { public constructor() { } }
            public struct Payload { public Node Reference; public int Count; }
            public class Box<T> {
                public T Value;
                public void Set(bool condition, T first, T second) {
                    if (condition) { Value = first; }
                    else { Value = second; }
                }
            }
            public void Assign(Box<Payload> box, Payload first, Payload second) {
                box.Set(true, first, second);
            }
            """);

        Assert.Contains("cx_gc_copy_value((cx_ptr)&(__this->Value)", generatedSource);
        Assert.DoesNotContain("__this->Value = first;", generatedSource);
        Assert.DoesNotContain("__this->Value = second;", generatedSource);
    }

    [Theory]
    [InlineData("while (condition) { Reference = value; }")]
    [InlineData("do { Reference = value; } while (condition);")]
    [InlineData("for (Reference = value; condition; Reference = value) { Reference = value; }")]
    public void RoutesClosedGenericStructLoopReferenceStoresThroughBarrier(string loop)
    {
        var generatedSource = GenerateSource($$"""
            public class Node { public constructor() { } }
            public struct Wrapper<T> {
                public T Item;
                public Node Reference;
                public constructor() { }
                public void Update(bool condition, Node value) { {{loop}} }
            }
            public void Run(Wrapper<int> wrapper, Node value) {
                wrapper.Update(false, value);
            }
            """);

        Assert.Contains("cx_gc_store_reference((cx_ptr*)&(__this->Reference)", generatedSource);
        Assert.DoesNotContain("__this->Reference = value;", generatedSource);
    }

    [Fact]
    public void GenericClassObjectPropertyUsesDeclaringClassReceiver()
    {
        var generatedSource = GenerateSource("""
            public class Box<T> {
                public TypeInfo ReadType() { return this.Type; }
            }
            public TypeInfo Read(Box<int> box) { return box.ReadType(); }
            """);

        var getterCall = generatedSource.Split('\n').Single(line =>
            line.Contains("Type___const_get(", StringComparison.Ordinal));
        Assert.DoesNotContain("__base", getterCall);
    }

    [Fact]
    public void EmitsQualifiedNamesWithMoreThan32Parts()
    {
        var parts = Enumerable.Range(0, 40).Select(index => $"Namespace{index}").ToArray();
        var generatedSource = GenerateSource($$"""
            namespace {{string.Join('.', parts)}};
            public struct Payload { public int Value; }
            public int Read(Payload value) { return value.Value; }
            """);

        var prefix = "unnamed_" + string.Join('_', parts);
        Assert.Contains($"cx_int {prefix}_Read(", generatedSource);
        Assert.Contains($"struct {prefix}_Payload value", generatedSource);
    }

    [Fact]
    public void EmitsClosedGenericStructFieldAccessWithoutStructCast()
    {
        var generatedSource = GenerateSource("""
            public struct ZInner<T> {
                public T Value;
                public constructor(T initial) { Value = initial; }
            }
            public struct AOuter<T> {
                public ZInner<T> Inner;
                public constructor(ZInner<T> initial) { Inner = initial; }
            }
            public int ReadNested(AOuter<int> value) {
                return value.Inner.Value;
            }
            """);

        var functionStart = generatedSource.IndexOf(
            "unnamed_ReadNested(", StringComparison.Ordinal);
        Assert.True(functionStart >= 0);
        var functionEnd = generatedSource.IndexOf("\n}", functionStart,
            StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart);
        var function = generatedSource[functionStart..functionEnd];
        Assert.True(!function.Contains("((struct cx_generic_", StringComparison.Ordinal),
            function);
    }

    [Fact]
    public void EmitsGenericVirtualSlotsWithoutTypedFunctionPointers()
    {
        var generatedSource = GenerateSource("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base {
                public override U Echo<U>(U value) { return value; }
            }
            public int Run() {
                Base value = new Derived();
                return value.Echo(42);
            }
            """);

        Assert.Contains("cx_register_generic_virtual_method", generatedSource);
        Assert.Contains("cx_require_generic_virtual_method", generatedSource);
        Assert.Contains("__cx_generic_virtual_thunk_", generatedSource);
        Assert.Contains("CX_BEGIN_VTABLE_DEF(", generatedSource);
        Assert.True(generatedSource.Split(
            "{ .function = (cx_vtable_function)0 },",
            StringSplitOptions.None).Length >= 3);
    }

    [Fact]
    public void EmitsHelloWorldFunctionBody()
    {
        const string source = """
            import System;

            public void Main() {
                Console.WriteLine("Hello world!");
            }
            """;
        var generatedSource = GenerateSource(source);

        Assert.Contains(
            "CX_STRING_DEF(unnamed___string_",
            generatedSource);
        Assert.Contains("\"Hello world!\");", generatedSource);
        Assert.Contains("void unnamed_Main()", generatedSource);
        Assert.Contains(
            "cxcore_System_Console_WriteLine(&unnamed___string_",
            generatedSource);
    }

    [Fact]
    public void EmitsAllInterfaceReferenceFields()
    {
        var generatedSource = GenerateSource("""
            public interface IValue {}
            public class Value : IValue {}

            public IValue Create() { return new Value(); }
            """);

        Assert.Contains("(struct cx_iface_ref){", generatedSource);
        Assert.Contains(", CX_NULL }", generatedSource);
    }

    [Fact]
    public void EmitsCxCoreObjectBeforeClosedGenericValueLayouts()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public class Object {}
            public struct InlineValue<T> {
                private T _value;
                public constructor(T value) { _value = value; }
            }
            public int Main() {
                InlineValue<bool> value = new InlineValue<bool>(true);
                return 0;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-cxcore-closed-layout-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var header = File.ReadAllText(Path.Combine(directory, "cxcore.internal.h"));
            var objectDefinition = header.IndexOf(
                "CX_TYPE_DEF(cxcore_System_Object) {", StringComparison.Ordinal);
            var closedValueDefinition = header.IndexOf(
                "struct cx_generic_", objectDefinition + 1, StringComparison.Ordinal);

            Assert.True(objectDefinition >= 0);
            Assert.True(closedValueDefinition > objectDefinition);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsObjectPrefixInOpenGenericStructPlaceholders()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public class Object {}
            public struct InlineValue<T> {
                private T _value;
                public constructor(T value) { _value = value; }
                public T GetValue() { return _value; }
            }
            public int Main() {
                InlineValue<bool> value = new InlineValue<bool>(true);
                return value.GetValue() ? 1 : 0;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-cxcore-open-generic-layout-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var header = File.ReadAllText(Path.Combine(directory, "cxcore.internal.h"));
            var valueStart = header.IndexOf(
                "CX_TYPE_DEF(cxcore_System_InlineValue) {", StringComparison.Ordinal);
            var valueEnd = header.IndexOf("};", valueStart, StringComparison.Ordinal);
            var objectStart = header.IndexOf(
                "CX_TYPE_DEF(cxcore_System_Object) {", StringComparison.Ordinal);

            Assert.True(objectStart >= 0 && objectStart < valueStart);
            Assert.True(valueStart >= 0);
            Assert.True(valueEnd > valueStart);
            Assert.Contains("struct cxcore_System_Object __base;",
                header[valueStart..valueEnd]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsClosedGenericStructReturnsThroughInterfaceDispatchByValue()
    {
        var generatedSource = GenerateSource("""
            public struct ValueTaskLike<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public interface ISequence<T> {
                ValueTaskLike<bool> MoveNextAsync();
            }
            public class Sequence : ISequence<int> {
                public ValueTaskLike<bool> MoveNextAsync() {
                    return new ValueTaskLike<bool>(true);
                }
            }
            public bool Read(ISequence<int> sequence) {
                return sequence.MoveNextAsync().Value;
            }
            """);

        var dispatchLines = string.Join(Environment.NewLine,
            generatedSource.Split('\n').Where(line =>
                line.Contains("slot_1", StringComparison.Ordinal) ||
                line.Contains("MoveNextAsync", StringComparison.Ordinal)));
        var thunkDeclaration = generatedSource.Split('\n').First(line =>
            line.Contains("static ", StringComparison.Ordinal) &&
            line.Contains("slot_1", StringComparison.Ordinal));
        Assert.StartsWith("static struct cx_generic_", thunkDeclaration.Trim());
        Assert.False(thunkDeclaration.TrimStart().StartsWith(
            "static void", StringComparison.Ordinal));
        Assert.Matches(
            "struct cx_generic_[0-9a-f]+ \\(\\*\\)\\(cx_ptr\\)", generatedSource);
    }

    [Fact]
    public void DoesNotEmitUnusedLambdaEnvironmentAlias()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            int Main() {
                int offset = 1;
                Transform transform = value => value + offset;
                return transform(2);
            }
            """);

        Assert.Contains("__cx_lambda_", generatedSource);
        Assert.DoesNotContain("__cx_closure =", generatedSource);
    }

    [Fact]
    public void EmitsDirectReturnAwaitCallbackWithCapturedLocals()
    {
        var generatedSource = GenerateSource("""
            import System.Threading.Tasks;
            async Task<int> Relay(Task<int> source) {
                return await source;
            }
            """);

        Assert.Contains("Relay", generatedSource);
        Assert.Contains("__cx_lambda_", generatedSource);
        Assert.Contains("__cx_environment", generatedSource);
    }

    [Fact]
    public void CastsClosedTaskCompletionSourceToOpenPropertyAbi()
    {
        var generatedSource = GenerateSource("""
            import System.Threading.Tasks;
            Task<int> Read() {
                TaskCompletionSource<int> completion = new TaskCompletionSource<int>();
                return completion.Task;
            }
            """);
        Assert.Contains(
            "TaskCompletionSource_Task___const_get(((struct cxcore_System_Threading_Tasks_TaskCompletionSource*)(&(completion)))",
            generatedSource);
    }

    [Fact]
    public void EmitsSwitchSectionTerminatorForStrictCCompilers()
    {
        var generatedSource = GenerateSource("""
            int Choose(int value) {
                switch (value) {
                    case 0: return 1;
                    default: return 2;
                }
            }
            """);

        Assert.Contains("switch (value)", generatedSource);
        Assert.Contains("break;", generatedSource);
    }

    [Fact]
    [Trait("Area", "Lowering")]
    public void EmitsNativeMainWrapperForCxStringArrayArguments()
    {
        var generatedSource = GenerateSource("""
            public uint Count(string[] args) {
                return args.Length;
            }

            public int Main(string[] args) {
                return 0;
            }
            """);

        Assert.Contains("int wmain(int argc, wchar_t **argv)", generatedSource);
        Assert.Contains("int main(int argc, char **argv)", generatedSource);
        Assert.Contains("cx_array_new(__cx_argument_count", generatedSource);
        Assert.Contains("cxcore_System_String___constructor__2", generatedSource);
        Assert.Contains(
            "cx_gc_store_reference((cx_ptr*)cx_array_at(__cx_arguments, (cx_uint)(__cx_index - 1), (cx_uint)sizeof(struct cxcore_System_String*)), (cx_ptr)__cx_argument)",
            generatedSource);
        Assert.DoesNotContain(
            "*(struct cxcore_System_String**)cx_array_at(__cx_arguments",
            generatedSource);
        Assert.Contains("cxcore_System_Collections_Array_Length___const_get(args)", generatedSource);
        Assert.Contains("unnamed_Main(__cx_arguments)", generatedSource);
        Assert.Contains("int __cx_exit_code = (int)unnamed_Main(__cx_arguments);", generatedSource);
        Assert.Contains("cx_thread_wait_all();", generatedSource);
        Assert.Contains("return __cx_exit_code;", generatedSource);
    }

    [Fact]
    [Trait("Area", "NegativeDiagnostics")]
    public void RejectsUnsupportedNativeMainSignature()
    {
        var exception = Assert.Throws<CompilationErrorException>(
            () => GenerateSource("public int Main(string argument) { return 0; }"));

        Assert.Contains("must be a non-generic top-level function", exception.Message);
    }

    [Fact]
    [Trait("Area", "NegativeDiagnostics")]
    public void RejectsAsyncNativeMainSignature()
    {
        var exception = Assert.Throws<CompilationErrorException>(() => GenerateSource("""
            import System.Threading.Tasks;
            async Task Main() {}
            """));

        Assert.Contains("must be a non-generic top-level function", exception.Message);
    }

    [Fact]
    public void EmitsSelectedOverloadAndReturnStatement()
    {
        const string source = """
            void Print(int value) {}
            void Print(string value) {}

            string Echo(string value) {
                return value;
            }

            void Main() {
                Print("text");
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains("unnamed_Print__2(", generatedSource);
        Assert.Contains("return value;", generatedSource);
    }

    [Fact]
    public void EmitsMulticastDelegateCombinationRemovalAndInvocation()
    {
        var generatedSource = GenerateSource("""
            int Main() {
                delegate(int) => int first = value => value + 1;
                delegate(int) => int second = value => value + 2;
                first += second;
                first -= second;
                return first(10);
            }
            """);

        Assert.Contains("__invocation_list", generatedSource);
        Assert.Contains("__cx_delegate_combine_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_remove_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_equal_CX_FN_", generatedSource);
        Assert.Contains(
            "cx_gc_store_reference((cx_ptr*)&(*tail)->next, (cx_ptr)copy)",
            generatedSource);
        Assert.Contains(
            "cx_gc_store_reference((cx_ptr*)&copy->next, CX_NULL)",
            generatedSource);
        Assert.DoesNotContain("copy->next = NULL;", generatedSource);
    }

    [Fact]
    public void EmitsAndInvokesNamedDelegateTypes()
    {
        const string source = """
            public delegate int Transform(int value);

            int Main() {
                Transform first = value => value + 1;
                Transform second = value => value + 2;
                first += second;
                bool same = first == second;
                bool isNull = first == null;
                return first(10);
            }
            """;
        var generatedHeader = GenerateOutput(source, "unnamed.h");
        var generatedSource = GenerateSource(source);

        Assert.Contains("typedef CX_FN_", generatedHeader);
        Assert.Contains("unnamed_Transform", generatedHeader);
        Assert.Contains("__cx_delegate_combine_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_equal_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_is_null_CX_FN_", generatedSource);
    }

    [Fact]
    public void EmitsClosedDelegateAbiForGenericTaskReturningFields()
    {
        const string source = """
            import System.Threading.Tasks;
            public class AsyncCallbackHolder<T, TResult> {
                private delegate(T) => Task<TResult> _callback;
                public Task<TResult> Invoke(T value) {
                    return _callback(value);
                }
            }
            public Task<int> Use(AsyncCallbackHolder<int, int> holder) {
                return holder.Invoke(4);
            }
            """;

        var generatedHeader = GenerateOutput(source, "unnamed.h");
        var generatedSource = GenerateSource(source);
        var invocationTypes = Regex.Matches(
                generatedSource,
                @"__cx_delegate_invoke_(CX_FN_[A-F0-9]+)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(invocationTypes);
        Assert.All(invocationTypes, functionType =>
            Assert.Contains($"typedef struct {functionType}", generatedHeader));
    }

    [Fact]
    public void RoutesDelegateFieldAssignmentsThroughReferenceStoreHelper()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            class Holder {
                Transform Callback;
            }

            int Main() {
                Holder holder = new Holder();
                Transform callback = value => value + 1;
                holder.Callback = callback;
                holder.Callback += callback;
                return 0;
            }
            """);

        Assert.Contains("__cx_delegate_store_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_combine_CX_FN_", generatedSource);
        Assert.Contains("cx_gc_store_reference((cx_ptr*)&destination->__environment", generatedSource);
        Assert.Contains("cx_gc_store_reference((cx_ptr*)&destination->__target", generatedSource);
        Assert.Contains("cx_gc_store_reference((cx_ptr*)&destination->__invocation_list", generatedSource);
    }

    [Fact]
    public void KeepsNamedDelegateTypesNominal()
    {
        var exception = Assert.Throws<CompilationErrorException>(() => GenerateSource("""
            delegate int First(int value);
            delegate int Second(int value);

            void Main() {
                First first = value => value;
                Second second = first;
            }
            """));

        Assert.Contains("Cannot initialize local", exception.Message);
    }

    [Fact]
    public void EmitsTypedDelegateInvocationList()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            int Main() {
                Transform first = value => value + 1;
                Transform second = value => value + 2;
                first += second;
                Transform[] handlers = first.GetInvocationList();
                Transform firstHandler = handlers[0];
                Transform secondHandler = handlers[1];
                Transform empty = null;
                Transform[] emptyHandlers = empty.GetInvocationList();
                return handlers.Length == 2 &&
                    firstHandler(10) == 11 && secondHandler(10) == 12 &&
                    handlers[0](10) == 11 && handlers[1](10) == 12 &&
                    emptyHandlers.Length == 0u ? 0 : 1;
            }
            """);

        Assert.Contains("__cx_delegate_get_invocation_list_CX_FN_", generatedSource);
        Assert.Contains("cx_array_new_for_owner((cx_ptr)(__cx_owner)", generatedSource);
        Assert.Contains("cx_array_at", generatedSource);
    }

    [Fact]
    public void EmitsCheckedStoreForReferenceReturningDelegateInvocationList()
    {
        var generatedSource = GenerateSource("""
            public class Payload {}
            public delegate Payload Transform(Payload value);

            public Payload InvokeAll(Payload value, Transform first, Transform second) {
                first += second;
                return first(value);
            }
            """);

        Assert.Contains("cx_gc_store_reference((cx_ptr*)&(result)", generatedSource);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
    }

    [Fact]
    public void EmitsTypedInvocationListForAnonymousFunctionValues()
    {
        var generatedSource = GenerateSource("""
            int Main() {
                delegate(int) => int first = value => 11;
                delegate(int) => int second = value => 12;
                first += second;
                var handlers = first.GetInvocationList();
                delegate(int) => int empty = default;
                var emptyHandlers = empty.GetInvocationList();
                return handlers.Length == 2u &&
                    handlers[0](10) == 11 && handlers[1](10) == 12 &&
                    emptyHandlers.Length == 0u ? 0 : 1;
            }
            """);

        Assert.Contains("__cx_delegate_get_invocation_list_CX_FN_", generatedSource);
        Assert.Contains("cx_array_at", generatedSource);
    }

    [Fact]
    public void EmitsClosedElementLayoutForGenericArrayGetReturn()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {
                public T Get(uint index) const { return default; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            public T First<T>(Array<T> values) { return values.Get(0u); }

            int Main() {
                long[] values = new long[1];
                return First(values) == 9000000000L ? 0 : 1;
            }
            """));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-tests-{Guid.NewGuid():N}");
        string generatedSource;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "GenericArray.cx"));
            var sourceFile = Directory.GetFiles(outputDirectory, "*.c", SearchOption.AllDirectories)
                .Single(path => !Path.GetFileName(path).EndsWith(".metadata.c", StringComparison.Ordinal));
            generatedSource = File.ReadAllText(sourceFile);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }

        Assert.Contains("cx_long*)cx_array_at_checked(values, (cx_uint)(0u), (cx_uint)sizeof(cx_long)", generatedSource);
    }

    [Fact]
    public void EmitsTypedInvocationListsForStaticAndInstanceMethodValues()
    {
        var generatedSource = GenerateSource("""
            public delegate int Transform(int value);
            public int AddOne(int value) { return value + 1; }
            public class Counter {
                public int AddTwo(int value) { return value + 2; }
            }
            public interface ICounter {
                int AddThree(int value);
            }
            public class InterfaceCounter : ICounter {
                public int AddThree(int value) { return value + 3; }
            }
            public class Marker { }
            public struct ValueCounter {
                public Marker Keep;
                public int AddTwo(int value) { return value + 2; }
            }

            int Main() {
                Counter counter = new Counter();
                ICounter interfaceCounter = new InterfaceCounter();
                ValueCounter valueCounter = default;
                Transform staticMethod = AddOne;
                Transform instanceMethod = counter.AddTwo;
                Transform interfaceMethod = interfaceCounter.AddThree;
                Transform valueMethod = valueCounter.AddTwo;
                FunctionInfo topLevelInfo = staticMethod.Method;
                FunctionInfo methodInfo = instanceMethod.Method;
                int offset = 3;
                Transform closure = value => value + offset;
                FunctionInfo closureInfo = closure.Method;
                Transform[] staticList = staticMethod.GetInvocationList();
                Transform[] instanceList = instanceMethod.GetInvocationList();
                Transform[] interfaceList = interfaceMethod.GetInvocationList();
                return staticList.Length == 1u && instanceList.Length == 1u &&
                    interfaceList.Length == 1u &&
                    staticList[0](40) == 41 && instanceList[0](40) == 42 &&
                    interfaceList[0](40) == 43 &&
                    valueMethod(40) == 42 &&
                    staticMethod.Target == null && instanceMethod.Target == (object)counter &&
                    closure.Target != null ? 0 : 1;
            }
            """);

        Assert.Equal(4, generatedSource.Split(
            "__cx_delegate_get_invocation_list_CX_FN_", StringSplitOptions.None).Length - 1);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
        Assert.Contains(".__target = (cx_ptr)__cx_method_value_", generatedSource);
        Assert.Contains(".__target = CX_NULL", generatedSource);
        Assert.Contains("__cx_delegate_get_target_CX_FN_", generatedSource);
        Assert.Contains(".__method = __cx_function_reflection_", generatedSource);
        Assert.Contains("static struct cx_reflection_function __cx_function_reflection_", generatedSource);
        Assert.Contains(".__method = __cx_function_reflection_", generatedSource);
        Assert.Contains("cx_gc_store_reference(", generatedSource);
        Assert.Contains("cx_memory_alloc_for_owner((const void*)__cx_receiver", generatedSource);
        Assert.Contains("cx_memory_alloc_for_owner(__cx_receiver.owner", generatedSource);
        Assert.Contains("cx_gc_store_reference((cx_ptr*)&__cx_result->receiver", generatedSource);
        Assert.Contains("cx_gc_copy_value(&__cx_result->receiver", generatedSource);
        Assert.Contains("_get()", generatedSource);
        Assert.Contains("cx_reflection_get_function_info", generatedSource);
    }

    [Fact]
    public void EmitsDynamicInvokeForBoxedArgumentsAndVoidDelegates()
    {
        var generatedSource = GenerateSource("""
            public delegate int Transform(int value);
            public delegate void Setter(int value);
            public class Target {
                public int Value;
                public void SetValue(int value) { Value = value; }
            }

            int Main() {
                Transform transform = value => value + 2;
                object[] arguments = new object[1];
                arguments[0] = 40;
                object result = transform.DynamicInvoke(arguments);
                Target target = new Target();
                Setter setter = target.SetValue;
                object[] setterArguments = new object[1];
                setterArguments[0] = 41;
                object voidResult = setter.DynamicInvoke(setterArguments);
                object[] wrongArguments = new object[1];
                wrongArguments[0] = "wrong type";
                return (int)result == 42 && voidResult == null &&
                    setter.DynamicInvoke(wrongArguments) == null && target.Value == 41 ? 0 : 1;
            }
            """);

        Assert.Contains("__cx_delegate_dynamic_invoke_CX_FN_", generatedSource);
        Assert.Contains("cx_unbox_value", generatedSource);
        Assert.Contains("cx_box_value", generatedSource);
        Assert.Contains("cx_boxed_value_type", generatedSource);
        Assert.Contains("#include \"cxruntime.h\"", generatedSource);
        Assert.Contains("Array_Length___const_get(arguments)", generatedSource);
        Assert.DoesNotContain("arguments->_length", generatedSource);
    }

    [Fact]
    public void EmitsTypedNullForAnonymousFunctionValueStorage()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            int Main() {
                delegate(int) => int empty = null;
                empty = null;
                Transform namedEmpty = null;
                return empty.GetInvocationList().Length == 0u &&
                    namedEmpty.GetInvocationList().Length == 0u ? 0 : 1;
            }
            """);

        var typedNullStores = generatedSource.Split('\n').Where(line =>
            line.Contains("__cx_delegate_store_CX_FN_", StringComparison.Ordinal) &&
            line.Contains("(CX_FN_", StringComparison.Ordinal) &&
            line.Contains("{ 0 }", StringComparison.Ordinal));
        Assert.Equal(2, typedNullStores.Count());
    }

    [Fact]
    public void EmitsCompleteNullInitializerForInterfaceOwnerField()
    {
        var generatedSource = GenerateSource("""
            public interface IMarker { void Touch(); }

            int Main() {
                IMarker marker = null;
                return marker == null ? 0 : 1;
            }
            """);

        Assert.Contains(
            "(struct cx_iface_ref){ CX_NULL, CX_NULL, CX_NULL }",
            generatedSource);
        Assert.DoesNotContain(
            "(struct cx_iface_ref){ CX_NULL, CX_NULL }",
            generatedSource);
    }

    [Fact]
    public void RejectsInvokingAnIndexedNonFunctionValue()
    {
        var exception = Assert.Throws<CompilationErrorException>(() => GenerateSource("""
            int Main() {
                int[] values = new int[1];
                return values[0](1);
            }
            """));

        Assert.Contains("ArrayAccessExpression' cannot be invoked", exception.Message);
    }

    [Fact]
    public void EmitsGenericFunctionParametersAndReturnStorageAsPointers()
    {
        const string source = """
            public struct Optional<T> {
                public static extern Optional<T> Box(T value);
            }
            """;

        var generatedHeader = GenerateOutput(source, "unnamed.h");

        Assert.Contains(
            "extern CX_UNNAMED_API void unnamed_Optional_Box(",
            generatedHeader);
        Assert.Contains("void* value,", generatedHeader);
        Assert.Contains("void* __returnValue", generatedHeader);
        Assert.DoesNotContain("struct void*", generatedHeader);
    }

    [Fact]
    public void SeparatesGeneratedModuleImportsFromItsExports()
    {
        var project = CxProject.CreateDefaultApplicationProject("api_split");
        project.AddCompilationContext(CompilerTestHelper.Parse("public int Run() { return 1; }"));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-api-split-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "api_split.h"));
            var cmake = File.ReadAllText(Path.Combine(directory, "CMakeLists.txt"));
            var source = File.ReadAllText(Path.Combine(directory, "api_split.c"));

            Assert.Contains("#if defined(CX_API_SPLIT_BUILD)", header);
            Assert.Contains("#define CX_API_SPLIT_API CX_EXPORT", header);
            Assert.Contains("#define CX_API_SPLIT_API CX_IMPORT", header);
            Assert.Contains("extern CX_API_SPLIT_API cx_int api_split_Run()", header);
            Assert.Contains("CXCORE_ABI_VERSION EQUAL 1", cmake);
            Assert.Contains(
                "if(CX_STATIC_LINK AND CMAKE_SYSTEM_NAME STREQUAL \"Linux\")",
                cmake);
            Assert.Contains("set(CMAKE_POSITION_INDEPENDENT_CODE ON)", cmake);
            Assert.Contains(
                "set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\")",
                cmake);
            Assert.Contains("CX_RUNTIME_ABI_VERSION != 1", source);
            Assert.Contains("#pragma GCC diagnostic ignored \"-Wunused-parameter\"", source);
            Assert.Contains("#pragma GCC diagnostic ignored \"-Wunused-value\"", source);
            Assert.Contains("#pragma GCC diagnostic ignored \"-Wmissing-braces\"", source);
            Assert.DoesNotContain(
                "__declspec(dllimport) __declspec(noreturn) void __cdecl abort(void);",
                source);
            Assert.Contains("target_compile_definitions(api_split PRIVATE CX_API_SPLIT_BUILD)", cmake);
            Assert.True(cmake.IndexOf("add_executable(api_split", StringComparison.Ordinal) <
                cmake.IndexOf("target_compile_definitions(api_split", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsStringLiteralsUsedByConstructorInitializers()
    {
        var project = CxProject.CreateDefaultApplicationProject("constructor_literals");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public constructor(string message) { }
            }
            public class Derived : Base {
                public constructor() : base("constructor initializer literal") { }
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-constructor-literals-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "constructor_literals.c"));
            Assert.Contains("\"constructor initializer literal\"", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExportsInternalHelpersUsedByPublicTaskGenerics()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Threading.Tasks;
            internal final class ReusableValueTaskContinuationState {
                public extern void Clear();
                public extern constructor();
            }
            public final class ReusableValueTaskSource<T> {
                private ReusableValueTaskContinuationState _state;
                public void Reset() {
                    if (_state != null) { _state.Clear(); }
                }
                public void Initialize() {
                    _state = new ReusableValueTaskContinuationState();
                }
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(
            Path.GetTempPath(), $"cxc-task-generic-abi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "cxcore.h"));

            Assert.Contains(
                "extern CX_CXCORE_API void cxcore_System_Threading_Tasks_ReusableValueTaskContinuationState_Clear(",
                header);
            Assert.Contains(
                "extern CX_CXCORE_API void cxcore_System_Threading_Tasks_ReusableValueTaskContinuationState___constructor(",
                header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsBaseClassLayoutsBeforeDerivedClassLayouts()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_layout_order");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Derived : Base {}
            public class Base {}
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(
            Path.GetTempPath(), $"cxc-class-layout-order-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "class_layout_order.h"));

            Assert.True(header.IndexOf(
                    "CX_TYPE_DEF(class_layout_order_Base)",
                    StringComparison.Ordinal) <
                header.IndexOf(
                    "CX_TYPE_DEF(class_layout_order_Derived)",
                    StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IncludesNativeProjectHeadersAndSelectsPlatformSources()
    {
        var project = CxProject.CreateDefaultApplicationProject("native_platforms");
        project.AddCompilationContext(CompilerTestHelper.Parse("public void Run() {}"));
        var directory = Path.Combine(
            Path.GetTempPath(), $"cxc-native-platforms-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "include"));
            Directory.CreateDirectory(Path.Combine(directory, "platform", "common"));
            Directory.CreateDirectory(Path.Combine(directory, "platform", "linux"));
            Directory.CreateDirectory(Path.Combine(directory, "platform", "win"));
            Directory.CreateDirectory(Path.Combine(directory, "tests"));
            File.WriteAllText(Path.Combine(directory, "platform", "common", "Shared.c"), "");
            File.WriteAllText(Path.Combine(directory, "platform", "linux", "Linux.c"), "");
            File.WriteAllText(Path.Combine(directory, "platform", "win", "Windows.c"), "");
            File.WriteAllText(Path.Combine(directory, "tests", "NativeTests.c"), "");
            var outputDirectory = Path.Combine(directory, ".obj");
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"), directory);

            var cmake = File.ReadAllText(Path.Combine(outputDirectory, "CMakeLists.txt"));
            Assert.Contains("target_include_directories(native_platforms PRIVATE", cmake);
            Assert.Contains("platform/common", cmake);
            Assert.Contains("include", cmake);
            Assert.Contains("if(WIN32)", cmake);
            Assert.Contains("if(CMAKE_SYSTEM_NAME STREQUAL \"Linux\")", cmake);
            Assert.Contains("platform/linux/Linux.c", cmake);
            Assert.Contains("platform/win/Windows.c", cmake);
            Assert.DoesNotContain("tests/NativeTests.c", cmake);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildsCxcoreThroughItsExistingRuntimeTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("public void Run() {}"));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-cxcore-cmake-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var cmake = File.ReadAllText(Path.Combine(directory, "CMakeLists.txt"));

            Assert.Contains("if(NOT TARGET cxcore)", cmake);
            Assert.DoesNotContain("add_library(cxcore", cmake);
            Assert.DoesNotContain("target_sources(cxcore", cmake);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CoreHeaderUsesSharedRuntimeDeclarationsWithoutSocketApi()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("public void Run() {}"));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-cxcore-socket-abi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var header = File.ReadAllText(Path.Combine(directory, "cxcore.h"));
            Assert.Contains("#include <cx.h>", header);
            Assert.DoesNotContain("socket", header, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SeparatesPublicApiHeaderFromInternalCompilationHeader()
    {
        var project = CxProject.CreateDefaultApplicationProject("public_api_filter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            class ApiDependency {}
            class PrivateType {}
            void HiddenFunction() {}
            public int Exported(ApiDependency dependency) { return 1; }
            public class PublicType {
                private int HiddenMethod() { return 2; }
                public int VisibleMethod() { return 3; }
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-public-api-filter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var publicHeader = File.ReadAllText(Path.Combine(directory, "public_api_filter.h"));
            var internalHeader = File.ReadAllText(
                Path.Combine(directory, "public_api_filter.internal.h"));
            var source = File.ReadAllText(Path.Combine(directory, "public_api_filter.c"));

            Assert.Contains("public_api_filter_Exported", publicHeader);
            Assert.Contains("public_api_filter_ApiDependency", publicHeader);
            Assert.Contains("public_api_filter_PublicType_VisibleMethod", publicHeader);
            Assert.Contains("#include \"public_api_filter.internal.h\"", publicHeader);
            Assert.DoesNotContain("public_api_filter_HiddenFunction", publicHeader);
            Assert.DoesNotContain("public_api_filter_PrivateType", publicHeader);
            Assert.DoesNotContain("public_api_filter_PublicType_HiddenMethod", publicHeader);

            Assert.Contains("public_api_filter_HiddenFunction", internalHeader);
            Assert.Contains("public_api_filter_PrivateType", internalHeader);
            Assert.Contains("public_api_filter_PublicType_HiddenMethod", internalHeader);
            Assert.Contains("#include \"public_api_filter.internal.h\"", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string GenerateSource(string source)
    {
        return GenerateOutput(source, "unnamed.c");
    }

    private static string GenerateOutput(string source, string outputFileName)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "HelloWorld.cx"));

            return File.ReadAllText(
                Path.Combine(outputDirectory, outputFileName));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
