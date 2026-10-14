using CxCompiler.Model.Common;
using CxCompiler.Model.Project;
using CxCompiler.Semantics;
using System.Text.RegularExpressions;

namespace CxCompiler.Tests;

public sealed class ProjectReferenceTests
{
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
            Assert.Contains("CX_ID_3(math, Math, AddOne)(", appSource);
            Assert.Contains("CX_ID_3(math, Math, Value)", appSource);
            Assert.Contains("cx_iface_ref", appSource);
            Assert.Contains("#include \"math.h\"", appHeader);
            Assert.Contains("target_link_libraries(app PRIVATE math)", cmake);
            Assert.Contains("copy_if_different \"$<TARGET_FILE:math>\"", cmake);
            Assert.Contains("CX_ID_3(math, Math, AddOne)", librarySource);
            Assert.DoesNotContain("CX_ID_3(math, Math, AddOne)", appSource.Split("Run", 2)[0]);
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
            Assert.Contains("CX_ID_3(library, Lib, Seed)", appSource);
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
            Assert.Contains("CX_STRING_DEF(CX_ID_2(cx_generic_", appSource);
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
            Assert.Contains("CX_ID_3(generic, Lib, Router)", appSource);
            Assert.Contains("return (value + 1);", appSource);
            Assert.Contains("return value;", appSource);
            Assert.DoesNotContain("CX_ID_3(app, Lib, Router)", appSource);
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
            Assert.Contains("CX_ID_2(cx_generic_", appSource);
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
