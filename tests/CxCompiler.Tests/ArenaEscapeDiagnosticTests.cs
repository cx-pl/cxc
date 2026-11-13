using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ArenaEscapeDiagnosticTests
{
    [Fact]
    public void OpaqueEnumReturnIsNotTreatedAsAnArenaReference()
    {
        var project = Project("""
            import System;
            public enum Status { Ready }
            public static class Cache { public static Status Value; }
            public extern Status GetStatus();
            public void Run() {
                Memory.WithArena(() => { Cache.Value = GetStatus(); });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DirectStaticFieldEscapeFromArenaIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => { Cache.Value = new Payload(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("Memory.WithDurable", error.Message);
    }

    [Fact]
    public void DirectCaptureEscapeFromArenaIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public void Run() {
                Payload escaped = null;
                Memory.WithArena(() => { escaped = new Payload(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("captured", error.Message);
    }

    [Fact]
    public void ImplicitInstanceFieldStoreToDurableOwnerIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
            }
            public void Run() {
                Holder holder = new Holder();
                Memory.WithArena(() => { holder.Set(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ImplicitInstanceFieldStoreToOuterArenaOwnerIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Set(new Payload()); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ImplicitInstanceFieldStoreToSameArenaOwnerIsAllowed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Set(new Payload());
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void UnqualifiedFieldReadPropagatesArenaRegionThroughMethodReturn()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Holder {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
                public Payload Get() { return _value; }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Set(new Payload());
                    Memory.WithDurable(() => { Cache.Value = holder.Get(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void NestedImplicitFieldPathPropagatesRegionAndOwnerLifetime()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Child {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
                public Payload Get() { return _value; }
            }
            public class Parent {
                private Child _child;
                public constructor() { _child = new Child(); }
                public void Set(Payload value) { _child.Set(value); }
                public Payload Get() { return _child.Get(); }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Parent parent = new Parent();
                    parent.Set(new Payload());
                    Memory.WithDurable(() => { Cache.Value = parent.Get(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void TwoLevelNestedImplicitFieldPathPropagatesRegionAndOwnerLifetime()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Leaf {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
                public Payload Get() { return _value; }
            }
            public class Child {
                private Leaf _leaf;
                public constructor() { _leaf = new Leaf(); }
                public void Set(Payload value) { _leaf.Set(value); }
                public Payload Get() { return _leaf.Get(); }
            }
            public class Parent {
                private Child _child;
                public constructor() { _child = new Child(); }
                public void Set(Payload value) { _child.Set(value); }
                public Payload Get() { return _child.Get(); }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Parent parent = new Parent();
                    parent.Set(new Payload());
                    Memory.WithDurable(() => { Cache.Value = parent.Get(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void NestedImplicitFieldPathsAreTrackedOrFailClosedAtDepthLimit(int nestedEdges)
    {
        var declarations = new System.Text.StringBuilder();
        for (var index = 0; index < nestedEdges; index++)
        {
            declarations.AppendLine($$"""
                public class Owner{{index}} {
                    private Owner{{index + 1}} _next;
                    public constructor() { _next = new Owner{{index + 1}}(); }
                    public void Set(Payload value) { _next.Set(value); }
                    public Payload Get() { return _next.Get(); }
                }
                """);
        }
        declarations.AppendLine($$"""
            public class Owner{{nestedEdges}} {
                private Payload _value;
                public void Set(Payload value) { _value = value; }
                public Payload Get() { return _value; }
            }
            """);
        var project = Project($$"""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            {{declarations}}
            public void Run() {
                Memory.WithArena(() => {
                    Owner0 owner = new Owner0();
                    owner.Set(new Payload());
                    Memory.WithDurable(() => { Cache.Value = owner.Get(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        if (nestedEdges <= 32)
            Assert.Contains("arena-scoped reference", error.Message);
        else
            Assert.Contains("supported depth", error.Message);
    }

    [Fact]
    public void NestedFieldPropertyArrayPathPropagatesArenaLifetime()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Child {
                private Payload[] _items;
                public constructor() { _items = new Payload[1]; }
                public Payload[] Items { get { return _items; } }
                public Payload Get() { return Items[0]; }
            }
            public class Parent {
                private Child _child;
                public constructor() { _child = new Child(); }
                public void Set(Payload value) { _child.Items[0] = value; }
                public Payload Get() { return _child.Get(); }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Parent parent = new Parent();
                    parent.Set(new Payload());
                    Memory.WithDurable(() => { Cache.Value = parent.Get(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void WithArenaRejectsPublishingCapturedPossiblyScopedParameter()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run(Payload value) {
                Memory.WithArena(() => { Cache.Value = value; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void WithArenaRejectsPublishingCapturedAliasOfPossiblyScopedParameter()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run(Payload value) {
                Payload copyOfValue = value;
                Memory.WithArena(() => { Cache.Value = copyOfValue; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void WithArenaRejectsPublishingCapturedInstanceReceiver()
    {
        var project = Project("""
            import System;
            public static class Cache { public static Publisher Value; }
            public class Publisher {
                public void Publish() {
                    Memory.WithArena(() => { Cache.Value = this; });
                }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void WithArenaAllowsPublishingCapturedScalarParameter()
    {
        var project = Project("""
            import System;
            public static class Cache { public static int Value; }
            public void Run(int value) {
                Memory.WithArena(() => { Cache.Value = value; });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WideStringCodeUnitReadAllowsPossiblyScopedReceiver()
    {
        var project = Project("""
            import System;
            public char Read(WideString value, uint index) {
                char result = (char)0;
                Memory.WithArena(() => { result = (char)value.CodeUnitAt(index); });
                return result;
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaEscapeThroughForwardDeclaredFunctionCallsIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Forward(scoped);
                });
            }
            public void Forward(Payload value) { Store(value); }
            public void Store(Payload value) { Cache.Value = value; }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughInstanceReceiverIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Publisher { public void Publish() { Cache.Value = this; } }
            public static class Cache { public static Publisher Value; }
            public void Run() {
                Memory.WithArena(() => { new Publisher().Publish(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaReturnThroughForwardDeclaredHelperChainIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => { Cache.Value = CreateOuter(); });
            }
            public Payload CreateOuter() { return CreateInner(); }
            public Payload CreateInner() { return new Payload(); }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void DurableObjectReturnedFromHelperMayBePublished()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public static class Factory {
                public static Payload Create() {
                    Payload value = null;
                    Memory.WithDurable(() => { value = new Payload(); });
                    return value;
                }
            }
            public void Run() {
                Memory.WithArena(() => { Cache.Value = Factory.Create(); });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaReturnOfParameterPreservesProvenance()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public Payload Echo(Payload value) { return value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Cache.Value = Echo(scoped);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaConstructorCannotPublishThisToStaticStorage()
    {
        var project = Project("""
            import System;
            public class Publisher {
                public constructor() { Cache.Value = this; }
            }
            public static class Cache { public static Publisher Value; }
            public void Run() {
                Memory.WithArena(() => { Publisher value = new Publisher(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void GenericDirectCallPreservesArenaReturnProvenance()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public T Echo<T>(T value) { return value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Cache.Value = Echo(scoped);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void GenericMethodCannotStoreArenaValueIntoDurableArray()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public void Store<T>(T[] values, T value) { values[0] = value; }
            public void Run() {
                var values = new Payload[1];
                Memory.WithArena(() => { Store<Payload>(values, new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void GenericMethodCanStoreArenaValueIntoArenaOwnedArray()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public void Store<T>(T[] values, T value) { values[0] = value; }
            public void Run() {
                Memory.WithArena(() => {
                    var values = new Payload[1];
                    Store<Payload>(values, new Payload());
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericMethodCannotStoreArenaValueIntoDurableGenericField()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run() {
                Box<Payload> box = new Box<Payload>();
                Memory.WithArena(() => { Store<Payload>(box, new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void GenericMethodCanStoreArenaValueIntoArenaOwnedGenericField()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run() {
                Memory.WithArena(() => {
                    Box<Payload> box = new Box<Payload>();
                    Store<Payload>(box, new Payload());
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericMethodMayStoreArenaValueIntoByValueStructReceiver()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run() {
                Memory.WithArena(() => {
                    Box<Payload> box;
                    Store<Payload>(box, new Payload());
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericMethodPropertyAssignmentRemainsFailClosed()
    {
        var project = Project("""
            public class Box<T> {
                private T stored;
                public T Value {
                    get { return stored; }
                    set { stored = value; }
                }
            }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run(Box<int> box, int value) { Store<int>(box, value); }
            """);

        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void GenericMethodCannotStoreArenaReferenceBearingStructIntoDurableGenericField()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Envelope { public Payload Value; }
            public class Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run() {
                Box<Envelope> box = new Box<Envelope>();
                Memory.WithArena(() => {
                    Envelope value;
                    value.Value = new Payload();
                    Store<Envelope>(box, value);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void GenericMethodCanStoreArenaReferenceBearingStructIntoArenaOwnedGenericField()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Envelope { public Payload Value; }
            public class Box<T> { public T Value; }
            public void Store<T>(Box<T> box, T value) { box.Value = value; }
            public void Run() {
                Memory.WithArena(() => {
                    Box<Envelope> box = new Box<Envelope>();
                    Envelope value;
                    value.Value = new Payload();
                    Store<Envelope>(box, value);
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void VirtualOverrideEscapeIsDiagnosedAcrossKnownTargets()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Publisher {
                @NoEscape public virtual void Publish(Payload value) {}
            }
            public class RetainingPublisher : Publisher {
                public override void Publish(Payload value) { Cache.Value = value; }
            }
            public void Run() {
                Publisher publisher = new RetainingPublisher();
                Memory.WithArena(() => { publisher.Publish(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void InterfaceImplementationEscapeIsDiagnosed()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public interface IPublisher { @NoEscape void Publish(Payload value); }
            public class RetainingPublisher : IPublisher {
                public void Publish(Payload value) { Cache.Value = value; }
            }
            public void Run() {
                IPublisher publisher = new RetainingPublisher();
                Memory.WithArena(() => { publisher.Publish(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void NonRetainingInterfaceBorrowAcceptsArenaArgument()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            public interface IReader { @NoEscape void Read(Payload value); }
            public class Reader : IReader {
                public void Read(Payload value) {}
            }
            public void Run() {
                IReader reader = new Reader();
                Memory.WithArena(() => { reader.Read(new Payload()); });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void NonRetainingVirtualBorrowAcceptsArenaArgument()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            public class Reader {
                @NoEscape public virtual void Read(Payload value) {}
            }
            public class DerivedReader : Reader {
                public override void Read(Payload value) {}
            }
            public void Run() {
                Reader reader = new DerivedReader();
                Memory.WithArena(() => { reader.Read(new Payload()); });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DerivedVirtualCallInheritsNoEscapeSlotContract()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            public class Reader { @NoEscape public virtual void Read(Payload value) {} }
            public class DerivedReader : Reader { public override void Read(Payload value) {} }
            public void Run() {
                DerivedReader reader = new DerivedReader();
                Memory.WithArena(() => { reader.Read(new Payload()); });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void UncontractedInterfaceDispatchRejectsArenaArgument()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public interface IReader { void Read(Payload value); }
            public class Reader : IReader { public void Read(Payload value) {} }
            public void Run() {
                IReader reader = new Reader();
                Memory.WithArena(() => { reader.Read(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void UncontractedVirtualDispatchRejectsArenaArgument()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Reader { public virtual void Read(Payload value) {} }
            public class DerivedReader : Reader { public override void Read(Payload value) {} }
            public void Run() {
                Reader reader = new DerivedReader();
                Memory.WithArena(() => { reader.Read(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void OpenVirtualDispatchReturnMayBeArenaScoped()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Publisher {
                public virtual Payload Create() {
                    Payload value = null;
                    Memory.WithDurable(() => { value = new Payload(); });
                    return value;
                }
            }
            public class DerivedPublisher : Publisher {
                public override Payload Create() {
                    Payload value = null;
                    Memory.WithDurable(() => { value = new Payload(); });
                    return value;
                }
            }
            public void Run() {
                Publisher publisher = new DerivedPublisher();
                Memory.WithArena(() => { Cache.Value = publisher.Create(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void AsyncCallRejectsArenaArgumentEvenWithNoEscapeAnnotation()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            @NoEscape public async System.Threading.Tasks.Task<int> Echo(Payload value) {
                return 1;
            }
            public void Run() {
                Memory.WithArena(() => { Echo(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void AsyncReferenceResultCannotEscapeSynchronousArena()
    {
        var project = Project("""
            import System;
            public static class Cache {
                public static System.Threading.Tasks.Task<int> Value;
            }
            public async System.Threading.Tasks.Task<int> Create() { return 1; }
            public void Run() {
                Memory.WithArena(() => { Cache.Value = Create(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot start inside Memory.WithArena", error.Message);
    }

    [Fact]
    public void AsyncCallWithoutScopedArgumentsIsRejectedInsideArena()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task<int> Create() { return 1; }
            public void Run() {
                Memory.WithArena(() => { Create(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot start inside Memory.WithArena", error.Message);
    }

    [Fact]
    public void AsyncCallCanStartInsideDurableOverrideWithinArena()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task<int> Create() { return 1; }
            public void Run() {
                Memory.WithArena(() => {
                    Memory.WithDurable(() => { Create(); });
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void AsyncCallCanUseArenaArgumentsInsideWithArenaAsync()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task ReadAfter(
                Payload value, System.Threading.Tasks.Task gate) {
                await gate;
                int result = value.Value;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() =>
                    ReadAfter(new Payload(), new System.Threading.Tasks.Task()));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncChecksAsyncBodiesForDurablePublication()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public static class Cache { public static Payload Value; }
            public async System.Threading.Tasks.Task LeakAfter(
                Payload value, System.Threading.Tasks.Task gate) {
                await gate;
                Cache.Value = value;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() =>
                    LeakAfter(new Payload(), new System.Threading.Tasks.Task()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot be stored", error.Message);
    }

    [Fact]
    public void WithArenaAsyncBindsNestedReferenceValueResultForRuntimeBarrier()
    {
        var project = Project("""
            import System;
            public class PayloadNode {}
            public struct PayloadLeaf { public PayloadNode Reference; }
            public struct PayloadValue { public PayloadLeaf Nested; }
            public async System.Threading.Tasks.Task<PayloadValue> CreateValue() {
                PayloadValue value = default;
                value.Nested.Reference = new PayloadNode();
                return value;
            }
            public void Run() {
                Memory.WithArenaAsync<PayloadValue>(() => CreateValue());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsDetachedAsyncChild()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Child() { await new System.Threading.Tasks.Task(); }
            public async System.Threading.Tasks.Task Parent() {
                Child();
                await new System.Threading.Tasks.Task();
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsReturnedChildOperation()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Child() { await new System.Threading.Tasks.Task(); }
            public System.Threading.Tasks.Task Parent() { return Child(); }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsAwaitedChildOperation()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Child() { await new System.Threading.Tasks.Task(); }
            public async System.Threading.Tasks.Task Parent() { await Child(); }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsAwaitedLocalChildOperation()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Parent() {
                System.Threading.Tasks.Task child =
                    System.Threading.Tasks.TaskHandle.Delay(1u);
                await child;
                int afterJoin = 1;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsTwoLocalChildrenJoinedByWhenAll()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value) {
                Task first = Child(value);
                Task second = Child(value);
                await TaskHandle.WhenAll(first, second);
                value.Value += 1;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsCoreDelayFactoryJoinedByWhenAll()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, CancellationToken token) {
                Task canceled = TaskHandle.Delay(1u, token);
                Task sibling = Child(value);
                await TaskHandle.WhenAll(canceled, sibling);
                value.Value += 1;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() =>
                    Parent(new Payload(), CancellationToken.None));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsCoreDelayFactorySeparatedFromJoin()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public async Task Parent(CancellationToken token) {
                Task child = TaskHandle.Delay(1u, token);
                MayThrow();
                await TaskHandle.WhenAll(child);
            }
            public void MayThrow() { throw new Exception(); }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(CancellationToken.None));
            }
            """);

        Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void WithArenaAsyncDoesNotTrustShadowedDelayFactoryName()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public static class TaskHandle {
                public static Task Delay(Payload value) {
                    TaskScheduler.Post(() => value.Value += 1);
                    return new Task();
                }
            }
            public async Task Parent(Payload value) {
                Task child = TaskHandle.Delay(value);
                await TaskHandle.WhenAll(child);
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void WithArenaAsyncRejectsChildBeforePotentiallyThrowingStatementAndJoin()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public void MayThrow() { throw new Exception(); }
            public async Task Parent(Payload value) {
                Task child = Child(value);
                Task sibling = Child(value);
                MayThrow();
                await TaskHandle.WhenAll(child, sibling);
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsChildBeforePotentiallyThrowingChildArgument()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public Payload MayThrow() { throw new Exception(); }
            public async Task Parent(Payload value) {
                Task first = Child(value);
                Task second = Child(MayThrow());
                await TaskHandle.WhenAll(first, second);
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsVariableSizeChildJoinThroughInitializedArray()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value) {
                Task first = Child(value);
                Task second = Child(value);
                Task third = Child(value);
                Task[] children = new Task[3u];
                children[0u] = first;
                children[1u] = second;
                children[2u] = third;
                await TaskHandle.WhenAll(children);
                value.Value += 1;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsSharedChildArrayJoinedInBothConditionalBranches()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                Task first = Child(value);
                Task second = Child(value);
                Task[] children = new Task[2u];
                children[0u] = first;
                children[1u] = second;
                if (firstBranch) {
                    await TaskHandle.WhenAll(children);
                } else {
                    await TaskHandle.WhenAll(children);
                }
                value.Value += 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsSharedChildArrayNotJoinedOnEveryBranch()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                Task first = Child(value);
                Task second = Child(value);
                Task[] children = new Task[2u];
                children[0u] = first;
                children[1u] = second;
                Task[] firstOnly = new Task[1u];
                firstOnly[0u] = first;
                if (firstBranch) {
                    await TaskHandle.WhenAll(children);
                } else {
                    await TaskHandle.WhenAll(firstOnly);
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsBranchLocalChildrenJoinedThroughTaskLocals()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    Task first = Child(value);
                    Task second = Child(value);
                    await TaskHandle.WhenAll(first, second);
                } else {
                    Task left = Child(value);
                    Task right = Child(value);
                    await TaskHandle.WhenAll(left, right);
                }
                value.Value += 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsBranchLocalThreeChildArrayJoinedImmediately()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    Task[] children = new Task[3u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    children[2u] = Child(value);
                    await TaskHandle.WhenAll(children);
                } else {
                    Task[] children = new Task[3u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    children[2u] = Child(value);
                    await TaskHandle.WhenAll(children);
                }
                value.Value += 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsBranchLocalThreeChildArrayWithMissingSlot()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    Task[] children = new Task[3u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    children[2u] = Child(value);
                    await TaskHandle.WhenAll(children);
                } else {
                    Task[] children = new Task[3u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    children[1u] = Child(value);
                    await TaskHandle.WhenAll(children);
                }
                value.Value += 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("initial await", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsBranchLocalChildSetMismatch()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    Task first = Child(value);
                    Task second = Child(value);
                    await TaskHandle.WhenAll(first, second);
                } else {
                    Task left = Child(value);
                    Task right = Child(value);
                    await TaskHandle.WhenAll(left, left);
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("initial await", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsBranchLocalChildArraysJoinedImmediately()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    Task[] children = new Task[2u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    await TaskHandle.WhenAll(children);
                } else {
                    Task[] children = new Task[2u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    await TaskHandle.WhenAll(children);
                }
                value.Value += 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsBranchLocalChildArrayWithMissingSlot()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    Task[] children = new Task[2u];
                    children[0u] = Child(value);
                    children[1u] = Child(value);
                    await TaskHandle.WhenAll(children);
                } else {
                    Task[] children = new Task[2u];
                    children[0u] = Child(value);
                    children[0u] = Child(value);
                    await TaskHandle.WhenAll(children);
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("initial await", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsInlineChildrenJoinedInBothConditionalBranches()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    await TaskHandle.WhenAll(Child(value), Child(value));
                } else {
                    await TaskHandle.WhenAll(Child(value), Child(value));
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsInlineJoinWithPotentiallyThrowingChildArgument()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { public int Value; }
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
                value.Value += 1;
            }
            public Payload MayThrow() { throw new Exception(); }
            public async Task Parent(Payload value, bool firstBranch) {
                if (firstBranch) {
                    await TaskHandle.WhenAll(Child(value), Child(MayThrow()));
                } else {
                    await TaskHandle.WhenAll(Child(value), Child(value));
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsVariableSizeJoinWithMissingArrayEntry()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload {}
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
            }
            public async Task Parent(Payload value) {
                Task first = Child(value);
                Task detached = Child(value);
                Task[] children = new Task[2u];
                children[0u] = first;
                await TaskHandle.WhenAll(children);
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsVariableSizeJoinAfterArrayMutatorCall()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload {}
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
            }
            public void ReplaceFirstWithSecond(Task[] children) {
                children[0u] = children[1u];
            }
            public async Task Parent(Payload value) {
                Task first = Child(value);
                Task second = Child(value);
                Task[] children = new Task[2u];
                children[0u] = first;
                children[1u] = second;
                ReplaceFirstWithSecond(children);
                await TaskHandle.WhenAll(children);
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsChildOmittedFromWhenAll()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload {}
            public async Task Child(Payload value) {
                await TaskHandle.Delay(1u);
            }
            public async Task Parent(Payload value) {
                Task first = Child(value);
                Task detached = Child(value);
                await TaskHandle.WhenAll(first, TaskHandle.Delay(2u));
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncDoesNotTrustShadowedWhenAllContinuationHelper()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            namespace System.Threading.Tasks;
            public class TaskHandleWhenAllState {
                public void Start(Task task, Payload value) {
                    task.OnCompleted(() => { int observed = value.Value; });
                }
            }
            public class Payload { public int Value; }
            public async Task Parent(Payload value) {
                TaskHandleWhenAllState state = new TaskHandleWhenAllState();
                state.Start(TaskHandle.Delay(1u), value);
                await TaskHandle.Delay(2u);
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot cross an opaque call boundary", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsLiteralLocalPrefixBeforeJoiningChild()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Parent() {
                System.Threading.Tasks.Task child =
                    System.Threading.Tasks.TaskHandle.Delay(1u);
                int prefix = 41;
                bool include = true;
                await child;
                if (include) prefix += 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsPureScalarPrefixBeforeJoiningChild()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                int result = value.Value;
            }
            public async System.Threading.Tasks.Task Parent(Payload value) {
                System.Threading.Tasks.Task child = Child(value);
                uint adjustedDelay = 1u + 2u;
                await child;
                if (adjustedDelay > 0u) value.Value = 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsBoundScalarLocalPrefixBeforeJoiningChild()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                int result = value.Value;
            }
            public async System.Threading.Tasks.Task Parent(Payload value, uint delayMilliseconds) {
                System.Threading.Tasks.Task child = Child(value);
                uint initialDelay = delayMilliseconds + 1u;
                uint adjustedDelay = initialDelay + 2u;
                await child;
                if (adjustedDelay > 0u) value.Value = 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), 1u));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsFieldReadPrefixBeforeJoiningChild()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                int result = value.Value;
            }
            public async System.Threading.Tasks.Task Parent(Payload value) {
                System.Threading.Tasks.Task child = Child(value);
                int prefix = value.Value;
                await child;
                value.Value = prefix;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncDoesNotTreatPotentiallyThrowingPrefixAsChildJoin()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                int result = value.Value;
            }
            public async System.Threading.Tasks.Task Parent(Payload value, int divisor) {
                System.Threading.Tasks.Task child = Child(value);
                int quotient = 1 / divisor;
                await child;
                value.Value = quotient;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), 1));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsChildTaskJoinedInsideLoopBody()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Child() {
                await new System.Threading.Tasks.Task();
            }
            public async System.Threading.Tasks.Task Parent(uint count, uint delayMilliseconds) {
                uint delay = delayMilliseconds;
                while (count > 0u) {
                    System.Threading.Tasks.Task child =
                        System.Threading.Tasks.TaskHandle.Delay(delay + count);
                    await child;
                    count -= 1u;
                }
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent(2u, 1u));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsJoinedChildInsideForAndDoWhileLoops()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value, uint delay) {
                await System.Threading.Tasks.TaskHandle.Delay(delay);
                value.Value += 1;
            }
            public async System.Threading.Tasks.Task ParentFor(Payload value) {
                for (uint index = 0u; index < 2u; index += 1u) {
                    System.Threading.Tasks.Task child = Child(value, index + 1u);
                    await child;
                }
            }
            public async System.Threading.Tasks.Task ParentDoWhile(Payload value) {
                uint index = 0u;
                do {
                    System.Threading.Tasks.Task child = Child(value, index + 1u);
                    await child;
                    index += 1u;
                } while (index < 2u);
            }
            public void Run() {
                Payload value = new Payload();
                Memory.WithArenaAsync(() => ParentFor(value));
                Memory.WithArenaAsync(() => ParentDoWhile(value));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsPotentiallyThrowingPrefixBeforeJoiningChild()
    {
        var project = Project("""
            import System;
            public int ReadPrefix() { return 41; }
            public async System.Threading.Tasks.Task Parent() {
                System.Threading.Tasks.Task child =
                    System.Threading.Tasks.TaskHandle.Delay(1u);
                int prefix = ReadPrefix();
                await child;
                int afterJoin = prefix;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsLocalChildAwaitedOnBothBranches()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Child() {
                await new System.Threading.Tasks.Task();
            }
            public async System.Threading.Tasks.Task Parent(bool chooseFirst) {
                System.Threading.Tasks.Task child = Child();
                if (chooseFirst) await child;
                else await child;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent(true));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsLocalChildJoinedInEverySwitchSection()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                value.Value = 91;
            }
            public async System.Threading.Tasks.Task Parent(Payload value, int selector) {
                System.Threading.Tasks.Task child = Child(value);
                switch (selector) {
                    case 0: {
                        await child;
                        break;
                    }
                    default: {
                        await child;
                        break;
                    }
                }
                int afterJoin = value.Value;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), 0));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncDoesNotTreatSwitchWithoutDefaultAsAChildJoin()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                value.Value = 91;
            }
            public async System.Threading.Tasks.Task Parent(Payload value, int selector) {
                System.Threading.Tasks.Task child = Child(value);
                switch (selector) {
                    case 0: {
                        await child;
                        break;
                    }
                }
                int afterPossibleNonJoin = value.Value;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), 0));
            }
            """);

        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void WithArenaAsyncDoesNotTreatPropertySwitchSelectorAsANonThrowingJoin()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public static class Selector { public static int Value { get { return 0; } } }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                value.Value = 91;
            }
            public async System.Threading.Tasks.Task Parent(Payload value) {
                System.Threading.Tasks.Task child = Child(value);
                switch (Selector.Value) {
                    case 0: {
                        await child;
                        break;
                    }
                    default: {
                        await child;
                        break;
                    }
                }
                int afterPossibleDetachedChild = value.Value;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsBranchedChildJoinAfterScalarSetup()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                int result = value.Value;
            }
            public async System.Threading.Tasks.Task Parent(Payload value, uint delayMilliseconds) {
                System.Threading.Tasks.Task child = Child(value);
                uint adjustedDelay = delayMilliseconds + 1u;
                if (delayMilliseconds > 0u && adjustedDelay < 100u) {
                    uint thenDelay = adjustedDelay + 1u;
                    await child;
                    int afterJoin = value.Value;
                } else {
                    bool useFallback = delayMilliseconds == 0u;
                    await child;
                }
                if (adjustedDelay > 0u) value.Value = 1;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), 1u));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncAllowsJoinedChildAfterScalarLocalAssignment()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
                value.Value = 91;
            }
            public async System.Threading.Tasks.Task Parent(Payload value, uint initial) {
                uint adjusted = initial;
                System.Threading.Tasks.Task child = Child(value);
                adjusted = adjusted + 1u;
                await child;
                value.Value += (int)adjusted;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), 1u));
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void WithArenaAsyncRejectsFieldWriteBeforeJoiningChild()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await System.Threading.Tasks.TaskHandle.Delay(1u);
            }
            public async System.Threading.Tasks.Task Parent(Payload value) {
                System.Threading.Tasks.Task child = Child(value);
                value.Value = 92;
                await child;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncDoesNotTreatPropertyReadAsNonThrowingJoinCondition()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Worker {
                public static bool Choose { get { return true; } }
                public static async System.Threading.Tasks.Task Child(Payload value) {
                    await System.Threading.Tasks.TaskHandle.Delay(1u);
                }
                public static async System.Threading.Tasks.Task Parent(Payload value) {
                    System.Threading.Tasks.Task child = Child(value);
                    if (Choose) await child;
                    else await child;
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Worker.Parent(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncDoesNotTreatDivisionAsNonThrowingJoinCondition()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Worker {
                public static async System.Threading.Tasks.Task Child(Payload value) {
                    await System.Threading.Tasks.TaskHandle.Delay(1u);
                }
                public static async System.Threading.Tasks.Task Parent(Payload value, uint divisor) {
                    System.Threading.Tasks.Task child = Child(value);
                    if (10u / divisor > 0u) await child;
                    else await child;
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Worker.Parent(new Payload(), 1u));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncDoesNotTreatFieldReadAsSafeBranchPrefixBeforeJoin()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public static class Worker {
                public static async System.Threading.Tasks.Task Child(Payload value) {
                    await System.Threading.Tasks.TaskHandle.Delay(1u);
                }
                public static async System.Threading.Tasks.Task Parent(Payload value, bool chooseFirst) {
                    System.Threading.Tasks.Task child = Child(value);
                    if (chooseFirst) {
                        int beforeJoin = value.Value;
                        await child;
                    } else await child;
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Worker.Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsBranchPrefixLocalUsedAfterItsJoin()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public static class Worker {
                public static async System.Threading.Tasks.Task Child(Payload value) {
                    await System.Threading.Tasks.TaskHandle.Delay(1u);
                }
                public static async System.Threading.Tasks.Task Parent(Payload value, bool chooseFirst) {
                    System.Threading.Tasks.Task child = Child(value);
                    if (chooseFirst) {
                        int branchValue = 1;
                        await child;
                        value.Value = branchValue;
                    } else await child;
                }
            }
            public void Run() {
                Memory.WithArenaAsync(() => Worker.Parent(new Payload(), true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncAllowsJoinedTaskResultChild()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task<int> ReadValue() {
                return 42;
            }
            public async System.Threading.Tasks.Task Parent() {
                System.Threading.Tasks.Task<int> child = ReadValue();
                await child;
                int value = child.GetResult();
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void NestedWithArenaAsyncAllowsJoinedChildToBorrowAncestorValue()
    {
        var project = Project("""
            import System;
            public class Payload { public int Value; }
            public async System.Threading.Tasks.Task Child(Payload value) {
                await new System.Threading.Tasks.Task();
                int observed = value.Value;
            }
            public async System.Threading.Tasks.Task Parent() {
                Payload value = new Payload();
                var child = Memory.WithArenaAsync(() => Child(value));
                await child;
                int observed = value.Value;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void NestedWithArenaAsyncRejectsPublishingInnerValueIntoAncestorOwner()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder { public Payload Value; }
            public async System.Threading.Tasks.Task Child(Holder holder) {
                Payload inner = new Payload();
                holder.Value = inner;
                await new System.Threading.Tasks.Task();
            }
            public async System.Threading.Tasks.Task Parent() {
                Holder holder = new Holder();
                var child = Memory.WithArenaAsync(() => Child(holder));
                await child;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent());
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsLocalChildAwaitedOnOnlyOneBranch()
    {
        var project = Project("""
            import System;
            public async System.Threading.Tasks.Task Child() {
                await new System.Threading.Tasks.Task();
            }
            public async System.Threading.Tasks.Task Parent(bool chooseFirst) {
                System.Threading.Tasks.Task child = Child();
                if (chooseFirst) await child;
                await new System.Threading.Tasks.Task();
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Parent(true));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("cannot prove this async operation is joined", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsExplicitDetachedContinuation()
    {
        var project = Project("""
            import System;
            public void Run() {
                var operation = Memory.WithArenaAsync(() => {
                    Payload value = new Payload();
                    var gate = new System.Threading.Tasks.Task();
                    gate.OnCompleted(() => { value.ToString(); });
                    return gate;
                });
            }
            public class Payload {}
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsDirectTaskRuntimeContinuation()
    {
        var project = Project("""
            import System;
            import System.Threading.Tasks;
            public class Payload { }
            public static class Cache { public static Payload Value; }
            public async Task Parent(Payload payload, Task<int> gate) {
                TaskRuntime.OnCompleted(gate, () => { Cache.Value = payload; });
                await gate;
            }
            public void Run() {
                Memory.WithArenaAsync(() => Parent(new Payload(), new Task<int>(1)));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsDetachedSchedulerPost()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public void Run() {
                var operation = Memory.WithArenaAsync(() => {
                    Payload value = new Payload();
                    System.Threading.Tasks.TaskScheduler.Post(() => { value.ToString(); });
                    return new System.Threading.Tasks.Task();
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsContinuationCaptureInsideAsyncBody()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public async System.Threading.Tasks.Task Work(Payload value) {
                var gate = new System.Threading.Tasks.Task();
                gate.OnCompleted(() => { value.ToString(); });
                await gate;
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Work(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void WithArenaAsyncRejectsDetachedSchedulerPostInsideAsyncBody()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public async System.Threading.Tasks.Task Work(Payload value) {
                System.Threading.Tasks.TaskScheduler.Post(() => { value.ToString(); });
                await new System.Threading.Tasks.Task();
            }
            public void Run() {
                var operation = Memory.WithArenaAsync(() => Work(new Payload()));
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void OpaqueCallRejectsArenaReferenceInsideValueStruct()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Holder { public Payload Value; }
            public extern void Observe(Holder value);
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder;
                    holder.Value = new Payload();
                    Observe(holder);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void DirectCallPropagatesArenaReferenceInsideValueStruct()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Holder { public Payload Value; }
            public static class Cache { public static Holder Value; }
            public void Store(Holder value) { Cache.Value = value; }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder;
                    holder.Value = new Payload();
                    Store(holder);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void OpaqueCallRejectsArenaReferenceInNestedValueStruct()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Inner { public Payload Value; }
            public struct Outer { public Inner InnerValue; }
            public extern void Observe(Outer value);
            public void Run() {
                Memory.WithArena(() => {
                    Outer holder;
                    holder.InnerValue.Value = new Payload();
                    Observe(holder);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void OpaqueCallRejectsArenaReferenceInsideClosedGenericValueStruct()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Holder<T> { public T Value; }
            public extern void Observe(Holder<Payload> value);
            public void Run() {
                Memory.WithArena(() => {
                    Holder<Payload> holder;
                    holder.Value = new Payload();
                    Observe(holder);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void DurableValueStructMayBePublished()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public struct Holder { public Payload Value; }
            public static class Cache { public static Holder Value; }
            public void Run() {
                Holder holder;
                Memory.WithArena(() => {
                    Memory.WithDurable(() => {
                        holder.Value = new Payload();
                        Cache.Value = holder;
                    });
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DynamicInvokeRejectsArenaReferencesInArgumentArray()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public delegate void Handler(object value);
            public void Run(Handler handler) {
                Memory.WithArena(() => {
                    object[] arguments = new object[1];
                    arguments[0] = new Payload();
                    handler.DynamicInvoke(arguments);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("reflective invocation", error.Message);
    }

    [Fact]
    public void DelegateInvocationRejectsArenaReferenceArgumentWithoutContract()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public delegate void Handler(Payload value);
            public void Run(Handler handler) {
                Memory.WithArena(() => { handler(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void CapturingLocalFunctionDelegateCannotEscapeArenaScope()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public delegate Payload Reader();
            public static class Cache { public static Reader Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = new Payload();
                    Payload Read() { return value; }
                    Cache.Value = Read;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void BoundMethodDelegateCannotEscapeArenaScope()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Read() { return this; } }
            public delegate Payload Reader();
            public static class Cache { public static Reader Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = new Payload();
                    Cache.Value = value.Read;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void OuterArenaMethodDelegateCanBeStoredInSameOuterArenaDuringNestedScope()
    {
        var project = Project("""
            import System;
            public delegate int Transform(int value);
            public class Target { public int Add(int value) { return value + 1; } }
            public class Holder { public Transform Callback; }
            public void Run() {
                Memory.WithArena(() => {
                    Target target = new Target();
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Callback = target.Add; });
                    Transform callback = holder.Callback;
                    int result = callback(41);
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void InnerArenaMethodDelegateCannotBeStoredInOuterArenaDuringNestedScope()
    {
        var project = Project("""
            import System;
            public delegate int Transform(int value);
            public class Target { public int Add(int value) { return value + 1; } }
            public class Holder { public Transform Callback; }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => {
                        Target target = new Target();
                        holder.Callback = target.Add;
                    });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void InnerArenaObjectCannotBeStoredInOuterArenaDuringNestedScope()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder { public Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => {
                        Payload value = new Payload();
                        holder.Value = value;
                    });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void UnknownReturnCannotBeStoredIntoOuterArenaOwnerDuringNestedScope()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder { public Payload Value; }
            public static class Factory { public static extern Payload Unknown(); }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Value = Factory.Unknown(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void UnknownReturnCanBeStoredIntoSameArenaOwnerDuringNestedScope()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder { public Payload Value; }
            public static class Factory { public static extern Payload Unknown(); }
            public void Run() {
                Memory.WithArena(() => {
                    Memory.WithArena(() => {
                        Holder holder = new Holder();
                        holder.Value = Factory.Unknown();
                    });
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void UnknownReturnCannotBeStoredThroughOuterArenaPropertyDuringNestedScope()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder { public Payload Value { get; set; } }
            public static class Factory { public static extern Payload Unknown(); }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Value = Factory.Unknown(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void InnerArenaObjectStoredInFieldCannotBeRepublishedToOuterArena()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Box { public Payload Value; }
            public class Holder { public Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => {
                        Box box = new Box();
                        box.Value = new Payload();
                        holder.Value = box.Value;
                    });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void KnownFunctionReturnKeepsInnerArenaAllocationDepth()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Factory { public Payload Create() { return new Payload(); } }
            public class Holder { public Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Factory factory = new Factory();
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Value = factory.Create(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void KnownFunctionReturnPreservesOuterArenaOwner()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Factory { public Payload Echo(Payload value) { return value; } }
            public class Holder { public Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Factory factory = new Factory();
                    Payload value = new Payload();
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Value = factory.Echo(value); });
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void KnownPropertyGetterKeepsInnerArenaAllocationDepth()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Factory {
                public static Payload Create { get { return new Payload(); } }
            }
            public class Holder { public Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Memory.WithArena(() => { holder.Value = Factory.Create; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped", error.Message);
    }

    [Fact]
    public void UnqualifiedBoundMethodDelegateCannotEscapeArenaScope()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public delegate Payload Reader();
            public static class Cache { public static Reader Value; }
            public class Owner {
                public Payload Read() { return null; }
                public void Publish() {
                    Memory.WithArena(() => { Cache.Value = Read; });
                }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void DynamicInvokeAcceptsKnownEmptyArenaArgumentArray()
    {
        var project = Project("""
            import System;
            public delegate void Handler();
            public void Run(Handler handler) {
                Memory.WithArena(() => {
                    object[] arguments = new object[0];
                    handler.DynamicInvoke(arguments);
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DynamicInvokeRejectsUnknownArgumentArrayContents()
    {
        var project = Project("""
            import System;
            public delegate void Handler();
            public void Run(Handler handler, object[] arguments) {
                Memory.WithArena(() => { handler.DynamicInvoke(arguments); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("reflective invocation", error.Message);
    }

    [Fact]
    public void DynamicInvokeResultCannotEscapeArena()
    {
        var project = Project("""
            import System;
            public delegate object Handler();
            public static class Cache { public static object Value; }
            public void Run(Handler handler) {
                Memory.WithArena(() => {
                    object[] arguments = new object[0];
                    Cache.Value = handler.DynamicInvoke(arguments);
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped reference", error.Message);
    }

    [Fact]
    public void OpaqueDelegateResultCannotEscapeArena()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public delegate Payload Handler();
            public static class Cache { public static Payload Value; }
            public void Run(Handler handler) {
                Memory.WithArena(() => { Cache.Value = handler(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped reference", error.Message);
    }

    [Fact]
    public void TypeInfoConvertValueAcceptsArenaScopedBorrow()
    {
        var project = Project("""
            import System;
            import System.Reflection;
            public class Payload {}
            public void Run(TypeInfo type) {
                Memory.WithArena(() => {
                    object value = new Payload();
                    type.ConvertValue(value);
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void TypeInfoConvertValueResultCannotEscapeArena()
    {
        var project = Project("""
            import System;
            import System.Reflection;
            public class Payload {}
            public static class Cache { public static object Value; }
            public void Run(TypeInfo type) {
                Memory.WithArena(() => {
                    Cache.Value = type.ConvertValue(new Payload());
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData("module.GetReference(0u)")]
    [InlineData("module.GetAnnotation(0u)")]
    [InlineData("module.GetGenericArguments(owner)")]
    [InlineData("module.CanReadFunctionArray(owner, 0u)")]
    [InlineData("module.GetFunctionArrayElementType(owner, 0u)")]
    [InlineData("module.ReadFunctionArray(owner, 0u, new Payload(), 8u)")]
    public void ModuleInfoSnapshotCallsAcceptArenaScopedInputs(string invocation)
    {
        var project = Project($$"""
            import System;
            import System.Reflection;
            public class Payload {}
            public void Run(ModuleInfo module, TypeInfo owner) {
                Memory.WithArena(() => {
                    {{invocation}};
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Theory]
    [InlineData("Module", "module.GetReference(0u)")]
    [InlineData("Annotation", "module.GetAnnotation(0u)")]
    [InlineData("Arguments", "module.GetGenericArguments(owner)")]
    [InlineData("ElementType", "module.GetFunctionArrayElementType(owner, 0u)")]
    [InlineData("Values", "module.ReadFunctionArray(owner, 0u, new Payload(), 8u)")]
    public void ModuleInfoSnapshotResultsCannotEscapeArena(string destination, string invocation)
    {
        var project = Project($$"""
            import System;
            import System.Reflection;
            public class Payload {}
            public static class Cache {
                public static ModuleInfo Module;
                public static AnnotationInfo Annotation;
                public static TypeInfo[] Arguments;
                public static TypeInfo ElementType;
                public static object[] Values;
            }
            public void Run(ModuleInfo module, TypeInfo owner) {
                Memory.WithArena(() => {
                    Cache.{{destination}} = {{invocation}};
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData("type.GetModule()")]
    [InlineData("type.IsAssignableFrom(other)")]
    [InlineData("type.IsSameType(other)")]
    [InlineData("type.IsInBaseTypeChain(other)")]
    [InlineData("type.HasRuntimeInterface(0ul)")]
    [InlineData("type.CreateInstance()")]
    [InlineData("type.GetFunction(0u)")]
    [InlineData("type.CanInvokeFunction(0u)")]
    [InlineData("type.GetFunctionCount()")]
    [InlineData("type.GetInterfaces()")]
    [InlineData("type.GetFunctionFlags(0u)")]
    [InlineData("type.GetFunctionParameterCount(0u)")]
    [InlineData("type.GetFunctionParameterType(0u, 0u)")]
    [InlineData("type.GetEnumerableElementType()")]
    [InlineData("type.IsServiceSequence(value)")]
    public void TypeInfoReadOnlyCallsAcceptArenaScopedInputs(string invocation)
    {
        var project = Project($$"""
            import System;
            import System.Reflection;
            public void Run(TypeInfo type, TypeInfo other, object value) {
                Memory.WithArena(() => {
                    {{invocation}};
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Theory]
    [InlineData("Module", "type.GetModule()")]
    [InlineData("Function", "type.GetFunction(0u)")]
    [InlineData("Instance", "type.CreateInstance()")]
    [InlineData("Instance", "type.Invoke(null, \"Create\", new object[0])")]
    [InlineData("Instance", "type.InvokeFunction(null, 0u, new object[0])")]
    [InlineData("Instance", "type.InvokeConstructor(0u, new object[0])")]
    [InlineData("Instance", "type.CreateCollectionFromSequence(new object[0])")]
    [InlineData("Interfaces", "type.GetInterfaces()")]
    [InlineData("ParameterType", "type.GetFunctionParameterType(0u, 0u)")]
    [InlineData("ElementType", "type.GetEnumerableElementType()")]
    public void TypeInfoReadOnlyResultsCannotEscapeArena(string destination, string invocation)
    {
        var project = Project($$"""
            import System;
            import System.Reflection;
            public static class Cache {
                public static ModuleInfo Module;
                public static FunctionInfo Function;
                public static object Instance;
                public static TypeInfo[] Interfaces;
                public static TypeInfo ParameterType;
                public static TypeInfo ElementType;
            }
            public void Run(TypeInfo type) {
                Memory.WithArena(() => {
                    Cache.{{destination}} = {{invocation}};
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("possibly arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData("type.CreateInstance(arguments)")]
    [InlineData("type.Invoke(target, name, arguments)")]
    [InlineData("type.InvokeFunction(target, index, arguments)")]
    [InlineData("type.InvokeConstructor(index, arguments)")]
    [InlineData("type.CreateCollectionFromSequence(arguments)")]
    public void TypeInfoReflectionRejectsScopedInputs(string invocation)
    {
        var project = Project($$"""
            import System;
            import System.Reflection;
            public class Payload {}
            public void Run(TypeInfo type, object target, uint index, string name) {
                Memory.WithArena(() => {
                    object[] arguments = new object[1];
                    arguments[0] = new Payload();
                    {{invocation}};
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("reflective invocation", error.Message);
    }

    [Fact]
    public void ArenaListAcceptsArenaElement()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> values = new List<Payload>();
                    values.Add(new Payload());
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaOwnedImplicitCollectionFieldAcceptsArenaKeyAndDurableValue()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public class CloneContext {
                private Dictionary<Payload, Payload> _copies;
                public constructor() { _copies = new Dictionary<Payload, Payload>(); }
                public void Register(Payload source, Payload copy) {
                    _copies.Set(source, copy);
                }
            }
            public void Run() {
                Memory.WithArena(() => {
                    CloneContext context = new CloneContext();
                    Payload source = new Payload();
                    Memory.WithDurable(() => {
                        Payload copy = new Payload();
                        context.Register(source, copy);
                    });
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DurableImplicitCollectionFieldRejectsArenaElement()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public class Holder {
                private List<Payload> _values;
                public constructor() { _values = new List<Payload>(); }
                public void Add(Payload value) { _values.Add(value); }
            }
            public void Run() {
                Holder holder = null;
                Memory.WithDurable(() => { holder = new Holder(); });
                Memory.WithArena(() => { holder.Add(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData("Array<Payload>", "new Array<Payload>(1u)", "values.Set(0u, new Payload())")]
    [InlineData("List<Payload>", "new List<Payload>()", "values.Add(new Payload())")]
    [InlineData("Dictionary<Payload, Payload>", "new Dictionary<Payload, Payload>()", "values.Set(new Payload(), new Payload())")]
    [InlineData("Queue<Payload>", "new Queue<Payload>()", "values.Enqueue(new Payload())")]
    [InlineData("Stack<Payload>", "new Stack<Payload>()", "values.Push(new Payload())")]
    [InlineData("LinkedList<Payload>", "new LinkedList<Payload>()", "values.AddLast(new Payload())")]
    public void ArenaOwnedCollectionsAcceptArenaElements(
        string collectionType,
        string initializer,
        string storeCall)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run() {
                Memory.WithArena(() => {
                    {{collectionType}} values = {{initializer}};
                    {{storeCall}};
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Theory]
    [InlineData("source.CopyTo(arrayDestination, 0u)")]
    [InlineData("source.TryCopyTo(arrayDestination, 0u)")]
    [InlineData("destination.AddRange(source)")]
    public void ArenaOwnedCollectionCopiesAcceptArenaContents(string copyCall)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> source = new List<Payload>();
                    List<Payload> destination = new List<Payload>();
                    Array<Payload> arrayDestination = new Array<Payload>(1u);
                    source.Add(new Payload());
                    {{copyCall}};
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Theory]
    [InlineData("values.Add(new Payload())")]
    [InlineData("values.Set(0u, new Payload())")]
    [InlineData("values.Insert(0u, new Payload())")]
    [InlineData("values.TrySet(0u, new Payload())")]
    [InlineData("values.TryInsert(0u, new Payload())")]
    public void UnknownListOwnerRejectsArenaElement(string storeCall)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(List<Payload> values) {
                Memory.WithArena(() => { {{storeCall}}; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData("values.Set(0u, new Payload())")]
    [InlineData("values.TrySet(0u, new Payload())")]
    [InlineData("values.Fill(new Payload())")]
    public void UnknownArrayOwnerRejectsArenaElement(string storeCall)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(Array<Payload> values) {
                Memory.WithArena(() => { {{storeCall}}; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void UnknownDictionaryOwnerRejectsArenaKeyAndValue()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(Dictionary<Payload, Payload> values) {
                Memory.WithArena(() => { values.Set(new Payload(), new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void UnknownDictionaryOwnerRejectsArenaKeyAndValueInTryAdd()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(Dictionary<Payload, Payload> values) {
                Memory.WithArena(() => { values.TryAdd(new Payload(), new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void UnknownCollectionInterfaceOwnerRejectsArenaElement()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(ICollection<Payload> values) {
                Memory.WithArena(() => { values.Add(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void OpaqueCallRejectsArenaReferenceArgument()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public extern void Retain(Payload value);
            public void Run() {
                Memory.WithArena(() => { Retain(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void OpaqueCallResultCannotBePublished()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public extern Payload GetPayload();
            public void Run() {
                Memory.WithArena(() => { Cache.Value = GetPayload(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void OpaqueProjectReferenceRejectsArenaReferenceArgument()
    {
        var project = Project("""
            import System;
            import External;
            public class Payload {}
            public void Run(Gate gate) {
                Memory.WithArena(() => { gate.Retain(new Payload()); });
            }
            """);
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            namespace External;
            public class Gate { public extern void Retain(object value); }
            """), "external-module");

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque call boundary", error.Message);
    }

    [Fact]
    public void NoEscapeContractAllowsOpaqueBorrowArgument()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            @NoEscape public extern void Observe(Payload value);
            public void Run() {
                Memory.WithArena(() => { Observe(new Payload()); });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void NoEscapeContractDoesNotMakeOpaqueReturnDurable()
    {
        var project = Project("""
            import System;
            import System.Interop;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            @NoEscape public extern Payload Get(Payload value);
            public void Run() {
                Memory.WithArena(() => { Cache.Value = Get(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void NoEscapeContractAllowsOpaqueProjectReferenceBorrow()
    {
        var project = Project("""
            import System;
            import External;
            public class Payload {}
            public void Run(Gate gate) {
                Memory.WithArena(() => { gate.Observe(new Payload()); });
            }
            """);
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Interop;
            namespace External;
            public class Gate { @NoEscape public extern void Observe(object value); }
            """), "external-module");

        new SemanticBinder().Bind(project);
    }

    [Theory]
    [InlineData("Queue<Payload>", "values.Enqueue(new Payload())")]
    [InlineData("Stack<Payload>", "values.Push(new Payload())")]
    [InlineData("LinkedList<Payload>", "values.AddFirst(new Payload())")]
    [InlineData("LinkedList<Payload>", "values.AddLast(new Payload())")]
    public void QueueStackAndLinkedListRejectArenaItems(string collectionType, string storeCall)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run({{collectionType}} values) {
                Memory.WithArena(() => { {{storeCall}}; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void DurableLinkedListCanAllocateNodesUnderArena()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public void Run() {
                LinkedList<int> values = new LinkedList<int>();
                Memory.WithArena(() => {
                    values.AddFirst(1);
                    values.AddLast(2);
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ListAddRangeRejectsUnknownScopedContents()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(List<Payload> destination, List<Payload> source) {
                Memory.WithArena(() => { destination.AddRange(source); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Theory]
    [InlineData("source.CopyTo(destination, 0u)")]
    [InlineData("source.TryCopyTo(destination, 0u)")]
    public void ListCopyToRejectsUnknownScopedContents(string copyCall)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(List<Payload> source, Array<Payload> destination) {
                Memory.WithArena(() => { {{copyCall}}; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ArrayTryCopyToRejectsUnknownScopedContents()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(Array<Payload> source, Array<Payload> destination) {
                Memory.WithArena(() => { source.TryCopyTo(destination, 0u); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ListToArrayRejectsScopedContentsUnderDurableOverride()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> source = new List<Payload>();
                    source.Add(new Payload());
                    Memory.WithDurable(() => { Array<Payload> durable = source.ToArray(); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ListToArrayRejectsUnknownContentsWhenOwnerMayBeDurable()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(List<Payload> source) {
                Memory.WithArena(() => { Array<Payload> result = source.ToArray(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ListToArrayAllowsArenaOwnedContentsToStayWithOwner()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> source = new List<Payload>();
                    source.Add(new Payload());
                    Array<Payload> result = source.ToArray();
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Theory]
    [InlineData("Array<Payload> values = new Array<Payload>(1u); values.Set(0u, new Payload());", "values.Get(0u)")]
    [InlineData("List<Payload> values = new List<Payload>(); values.Add(new Payload());", "values.Get(0u)")]
    [InlineData("List<Payload> values = new List<Payload>(); values.Add(new Payload());", "values.GetOrDefault(0u)")]
    [InlineData("Dictionary<Payload, Payload> values = new Dictionary<Payload, Payload>(); Payload key = new Payload(); values.Set(key, new Payload());", "values.GetValueOrDefault(key)")]
    [InlineData("Queue<Payload> values = new Queue<Payload>(); values.Enqueue(new Payload());", "values.Peek()")]
    [InlineData("Queue<Payload> values = new Queue<Payload>(); values.Enqueue(new Payload());", "values.Dequeue()")]
    [InlineData("Stack<Payload> values = new Stack<Payload>(); values.Push(new Payload());", "values.Peek()")]
    [InlineData("Stack<Payload> values = new Stack<Payload>(); values.Push(new Payload());", "values.Pop()")]
    [InlineData("LinkedList<Payload> values = new LinkedList<Payload>(); values.AddFirst(new Payload());", "values.RemoveFirst()")]
    [InlineData("LinkedList<Payload> values = new LinkedList<Payload>(); values.AddLast(new Payload());", "values.RemoveLast()")]
    public void ArenaCollectionReadResultsCannotBePublishedDurably(
        string collectionSetup,
        string readExpression)
    {
        var project = Project($$"""
            import System;
            import System.Collections;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    {{collectionSetup}}
                    Payload selected = {{readExpression}};
                    Memory.WithDurable(() => { Cache.Value = selected; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void DurableListElementsCanBePublishedAfterEnumeration()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> values = new List<Payload>();
                    Memory.WithDurable(() => { values.Add(new Payload()); });
                    foreach (Payload value in values) {
                        Cache.Value = value;
                    }
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaListElementsCannotBePublishedAfterEnumeration()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> values = new List<Payload>();
                    values.Add(new Payload());
                    foreach (Payload value in values) {
                        Cache.Value = value;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ClearingArenaListRemovesItsElementProvenance()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> values = new List<Payload>();
                    values.Add(new Payload());
                    values.Clear();
                    foreach (Payload value in values) {
                        Cache.Value = value;
                    }
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaCollectionEnumeratorCannotBePublishedDurably()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public static class Cache { public static ArrayEnumerator<Payload> Value; }
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> values = new List<Payload>();
                    values.Add(new Payload());
                    ArrayEnumerator<Payload> enumerator = values.GetEnumerator();
                    Memory.WithDurable(() => { Cache.Value = enumerator; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ArenaEnumeratorCurrentCannotBePublishedDurably()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    List<Payload> values = new List<Payload>();
                    values.Add(new Payload());
                    ArrayEnumerator<Payload> enumerator = values.GetEnumerator();
                    if (enumerator.MoveNext()) {
                        Payload selected = enumerator.Current;
                        Memory.WithDurable(() => { Cache.Value = selected; });
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void UnknownInstanceFieldCollectionOwnerRejectsArenaElement()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public class Holder { public List<Payload> Values; }
            public void Run(Holder holder) {
                Memory.WithArena(() => { holder.Values.Add(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void CollectionLoadedFromUnknownArrayOwnerRejectsArenaElement()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(Array<List<Payload>> values) {
                Memory.WithArena(() => {
                    List<Payload> selected = values[0u];
                    selected.Add(new Payload());
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped reference", error.Message);
    }

    [Fact]
    public void ArenaOwnedInstanceFieldCollectionAcceptsArenaElement()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public class Holder {
                public List<Payload> Values;
                public constructor() { Values = new List<Payload>(); }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Values.Add(new Payload());
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughCapturedDelegateEnvironment()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public delegate Payload Getter();
            public static class Cache { public static Getter Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Getter getter = () => scoped;
                    Cache.Value = getter;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughLocalAliases()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public void Run() {
                Payload escaped = null;
                Memory.WithArena(() => {
                    Payload first = new Payload();
                    Payload aliasCopy = first;
                    escaped = aliasCopy;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaOriginIsPreservedAcrossConditionalBranches()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public void Run() {
                Payload escaped = null;
                Memory.WithArena(() => {
                    Payload candidate = null;
                    if (true) candidate = new Payload();
                    escaped = candidate;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void DurableObjectCannotRetainExistingArenaObject()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Child; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Memory.WithDurable(() => {
                        Payload durable = new Payload();
                        durable.Child = scoped;
                    });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("durable destination", error.Message);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughLocalObjectFields()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Child; }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload holder = new Payload();
                    holder.Child = new Payload();
                    Memory.WithDurable(() => { Cache.Value = holder.Child; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughNestedObjectFields()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Child; }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload holder = new Payload();
                    holder.Child = new Payload();
                    holder.Child.Child = new Payload();
                    Memory.WithDurable(() => { Cache.Value = holder.Child.Child; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughLocalProperties()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Child { get; set; } }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload holder = new Payload();
                    holder.Child = new Payload();
                    Memory.WithDurable(() => { Cache.Value = holder.Child; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughAliasesToNestedObjectFields()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Child; }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload holder = new Payload();
                    holder.Child = new Payload();
                    Payload nested = holder.Child;
                    nested.Child = new Payload();
                    Memory.WithDurable(() => { Cache.Value = nested.Child; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaOriginPropagatesThroughLocalArrayElements()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload[] values = new Payload[1];
                    values[0] = new Payload();
                    Memory.WithDurable(() => { Cache.Value = values[0]; });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void CollectionMutationThroughUnknownArrayElementReceiverIsRejected()
    {
        var project = Project("""
            import System;
            import System.Collections;
            public class Payload {}
            public void Run(List<Payload>[] lists) {
                Memory.WithArena(() => { lists[0].Add(new Payload()); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void StackAllocationCountExpressionIsAnalyzedForArenaEscapes()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public unsafe int PublishAndReturnCount(Payload value) {
                Cache.Value = value;
                return 1;
            }
            public unsafe void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    ptr<int> scratch = stackalloc<int>(PublishAndReturnCount(scoped));
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void NestedArenaOverridesDurableAllocationContext()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Memory.WithDurable(() => {
                        Memory.WithArena(() => { Cache.Value = new Payload(); });
                    });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void DurableConstructionMayPublishToStaticFieldInsideArena()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Child; }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    scoped.Child = new Payload();
                    Payload[] scopedBuffer = new Payload[1];
                    Memory.WithDurable(() => {
                        Payload durable = new Payload();
                        scopedBuffer[0] = durable;
                        Cache.Value = scopedBuffer[0];
                    });
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ScopedRawAllocationCannotCrossARetainingNativeBoundary()
    {
        var project = NativeInteropTests.Project("""
            import System;
            import System.Interop;
            @NativeImport("fixture") public extern void Retain(ptr<int> value);
            public unsafe void Run() {
                Memory.WithArena(() => {
                    ptr<int> scoped = (ptr<int>)Memory.Alloc(16u);
                    scoped = (ptr<int>)Memory.Realloc((ptr)scoped, 32u);
                    Retain(scoped);
                });
            }
            """);
        AddMemoryContext(project);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("native boundary", error.Message);
        Assert.Contains("noEscape", error.Message);
    }

    [Fact]
    public void ScopedRawAllocationMayCrossTrustedNoEscapeNativeBoundary()
    {
        var project = NativeInteropTests.Project("""
            import System;
            import System.Interop;
            @NativeImport("fixture", noEscape = true) public extern void Consume(ptr<int> value);
            public unsafe void Run() {
                Memory.WithArena(() => {
                    ptr<int> scoped = (ptr<int>)Memory.Alloc(16u);
                    Consume(scoped);
                });
            }
            """);
        AddMemoryContext(project);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaEscapeThroughPropertyGetterIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload { public Payload Value { get { return this; } } }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Cache.Value = scoped.Value;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughUnqualifiedPropertyGetterIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {
                public Payload Value { get { return this; } }
                public Payload Read() { return Value; }
            }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Cache.Value = scoped.Read();
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughIndexPropertyGetterBodyIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Holder {
                public Payload Item { get(uint index) { return new Payload(); } }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    Cache.Value = holder.Item[0u];
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughPropertySetterBodyIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder {
                public Payload Value { set { Cache.Value = value; } }
            }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    new Holder().Value = new Payload();
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughIndexPropertySetterBodyIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Holder {
                public Payload Item { set(uint index) { Cache.Value = value; } }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Item[0u] = new Payload();
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughPropertyMutationSetterBodyIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Holder {
                public int Value {
                    get { return 0; }
                    set { Cache.Value = new Payload(); }
                }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Value++;
                    holder.Value += 2;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaScalarPropertyMutationRemainsAllowed()
    {
        var project = Project("""
            import System;
            public class Holder {
                private int _value;
                public int Value {
                    get { return _value; }
                    set { _value = value; }
                }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Value++;
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ArenaEscapeThroughCompoundPropertySetterBodyIsDiagnosed()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public class Holder {
                public int Value {
                    get { return 0; }
                    set { Cache.Value = new Payload(); }
                }
            }
            public void Run() {
                Holder holder = new Holder();
                Memory.WithArena(() => { holder.Value += 1; });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaEscapeThroughCompoundDelegateAssignmentIsDiagnosed()
    {
        var project = Project("""
            import System;
            public delegate void Callback();
            public static class Cache { public static Callback Value; }
            public void Run() {
                Callback callback = () => {};
                Memory.WithArena(() => {
                    callback += () => {};
                    Cache.Value = callback;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ConditionalExpressionMergesArenaProvenanceFromBothArms()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    bool chooseArena = true;
                    chooseArena ? (value = new Payload()) : (value = null);
                    Cache.Value = value;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void SwitchExpressionMergesArenaProvenanceFromAllArms()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    bool chooseArena = true;
                    chooseArena switch {
                        true => (value = new Payload()),
                        _ => (value = null),
                    };
                    Cache.Value = value;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ShortCircuitExpressionMergesSkippedAndEvaluatedProvenance()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    ((value = new Payload()) == null) && ((value = null) == null);
                    Cache.Value = value;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void NullCoalescingMergesSkippedAndEvaluatedProvenance()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    value = new Payload();
                    value ?? (value = null);
                    Cache.Value = value;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void CatchStateIncludesArenaAssignmentsBeforeThrow()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    Exception failure = null;
                    Memory.WithDurable(() => {
                        failure = new Exception("stop");
                    });
                    try {
                        value = new Payload();
                        throw failure;
                    } catch (Exception error) {
                        Cache.Value = value;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaScopedExceptionCanBeThrownBecauseRuntimeCopiesItDurably()
    {
        var project = Project("""
            import System;
            public void Run() {
                Memory.WithArena(() => {
                    Exception failure = new Exception("temporary");
                    throw failure;
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void SwitchFilterProvenanceFlowsIntoLaterSections()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    switch (1) {
                        case 1 when ((value = new Payload()) == null):
                            break;
                        default:
                            Cache.Value = value;
                            break;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void LoopIterationPropagatesArenaProvenanceToLaterIterations()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    int iteration = 0;
                    while (iteration < 2) {
                        if (iteration == 0) value = new Payload();
                        else Cache.Value = value;
                        iteration += 1;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void FailedCatchFilterProvenanceFlowsToLaterCatchClauses()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    Exception failure = null;
                    Memory.WithDurable(() => {
                        failure = new Exception("stop");
                    });
                    try {
                        throw failure;
                    } catch (Exception first) when ((value = new Payload()) == null) {
                    } catch (Exception second) {
                        Cache.Value = value;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ForLoopIterationPropagatesArenaProvenanceToLaterIterations()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = null;
                    for (int iteration = 0; iteration < 2; iteration += 1) {
                        if (iteration == 0) value = new Payload();
                        else Cache.Value = value;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ForeachElementProvenanceFlowsIntoLoopBody()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload[] values = new Payload[1];
                    values[0] = new Payload();
                    foreach (Payload value in values) {
                        Cache.Value = value;
                    }
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void OverloadedBinaryOperatorBodyIsAnalyzedForArenaEscapes()
    {
        var project = Project("""
            import System;
            public class Payload {
                public Payload operator +(Payload other) {
                    Cache.Value = this;
                    return new Payload();
                }
            }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload left = new Payload();
                    Payload result = left + new Payload();
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void OverloadedUnaryOperatorBodyIsAnalyzedForArenaEscapes()
    {
        var project = Project("""
            import System;
            public class Payload {
                public Payload operator ~() {
                    Cache.Value = this;
                    return new Payload();
                }
            }
            public static class Cache { public static Payload Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Payload value = new Payload();
                    Payload result = ~value;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void StringConcatenationCarriesArenaAllocationProvenance()
    {
        var project = Project("""
            import System;
            public static class Cache { public static string Value; }
            public void Run() {
                Memory.WithArena(() => {
                    string response = "prefix" + "suffix";
                    Cache.Value = response;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void DelegateCombinationCarriesArenaAllocationProvenance()
    {
        var project = Project("""
            import System;
            public delegate void Callback();
            public static class Cache { public static Callback Value; }
            public void Run() {
                Memory.WithArena(() => {
                    Callback first = () => {};
                    Callback second = () => {};
                    Cache.Value = first + second;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("arena-scoped", error.Message);
    }

    [Fact]
    public void ArenaIndexPropertySetterCanStoreInArenaOwner()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Holder {
                private Payload _value;
                public Payload Item { set(uint index) { _value = value; } }
            }
            public void Run() {
                Memory.WithArena(() => {
                    Holder holder = new Holder();
                    holder.Item[0u] = new Payload();
                });
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ScopedIndexCannotCrossOpaqueInterfaceIndexerGetter()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public interface ILookup { int Item { get(object key); } }
            public class Lookup : ILookup {
                public int Item { get(object key) { return 1; } }
            }
            public void Run() {
                ILookup lookup = new Lookup();
                Memory.WithArena(() => {
                    Payload key = new Payload();
                    int value = lookup.Item[key];
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque", error.Message);
    }

    [Fact]
    public void ScopedIndexCannotCrossOpaqueInterfaceIndexerSetter()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public interface ILookup { int Item { set(object key); } }
            public class Lookup : ILookup {
                public int Item { set(object key) {} }
            }
            public void Run() {
                ILookup lookup = new Lookup();
                Memory.WithArena(() => {
                    Payload key = new Payload();
                    lookup.Item[key] = 1;
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque", error.Message);
    }

    [Fact]
    public void ArenaArgumentCannotCrossOpaqueInterfacePropertySetter()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public interface IStore { Payload Value { set; } }
            public void Run(IStore sink) {
                Memory.WithArena(() => { sink.Value = new Payload(); });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque", error.Message);
    }

    [Fact]
    public void ArenaArgumentCannotCrossOpaqueConstructorIntoDurableObject()
    {
        var project = Project("""
            import System;
            public class Payload {}
            public class Box { public extern constructor(Payload value); }
            public void Run() {
                Memory.WithArena(() => {
                    Payload scoped = new Payload();
                    Memory.WithDurable(() => { Box durable = new Box(scoped); });
                });
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("opaque", error.Message);
    }

    private static CxProject Project(string source)
    {
        var project = NativeInteropTests.Project(source);
        AddMemoryContext(project);
        AddCollectionContext(project);
        return project;
    }

    private static void AddMemoryContext(CxProject project)
    {
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public static class Memory {
                public extern static ptr Alloc(uint size);
                public static unsafe extern ptr Realloc(ptr block, uint size);
                public static unsafe extern void Free(ptr block);
                public static unsafe extern void Copy(ptr dest, ptr src, uint size);
                public extern static void WithArena(delegate() => void action);
                public extern static void WithDurable(delegate() => void action);
                public extern static System.Threading.Tasks.TaskHandle WithArenaAsync(delegate() => System.Threading.Tasks.TaskHandle action);
                public extern static System.Threading.Tasks.TaskHandle WithArenaAsync(uint capacity, delegate() => System.Threading.Tasks.TaskHandle action);
                public extern static System.Threading.Tasks.Task<T> WithArenaAsync<T>(delegate() => object action);
                public extern static System.Threading.Tasks.Task<T> WithArenaAsync<T>(uint capacity, delegate() => object action);
            }
            """), "cxcore", syntheticCore: true);
    }

    private static void AddCollectionContext(CxProject project)
    {
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class List<T> {
                private T _value;
                public constructor() {}
                public ArrayEnumerator<T> GetEnumerator() { return new ArrayEnumerator<T>(); }
                public T Get(uint index) const { return _value; }
                public T GetOrDefault(uint index) const { return _value; }
                public void Set(uint index, T value) {}
                public void Add(T value) {}
                public void AddRange(List<T> values) {}
                public void Insert(uint index, T value) {}
                public bool TrySet(uint index, T value) { return true; }
                public bool TryInsert(uint index, T value) { return true; }
                public void CopyTo(Array<T> destination, uint destinationIndex) {}
                public bool TryCopyTo(Array<T> destination, uint destinationIndex) { return true; }
                public Array<T> ToArray() { return new Array<T>(0u); }
                public void Clear() {}
            }
            public class ArrayEnumerator<T> {
                private T _value;
                public constructor() {}
                public T Current { const get { return _value; } }
                public bool MoveNext() { return false; }
                public void Dispose() {}
            }
            public class Array<T> {
                private T _value;
                public constructor(uint length) {}
                public T Get(uint index) const { return _value; }
                public void Set(uint index, T value) {}
                public bool TrySet(uint index, T value) { return true; }
                public void Fill(T value) {}
                public bool TryCopyTo(Array<T> destination, uint destinationIndex) { return true; }
            }
            public interface ICollection<T> {
                public void Add(T value);
            }
            public class Dictionary<TKey, TValue> {
                private TValue _value;
                public TValue GetValueOrDefault(TKey key) const { return _value; }
                public void Set(TKey key, TValue value) {}
                public bool TryAdd(TKey key, TValue value) { return true; }
            }
            public class Queue<T> {
                private T _value;
                public void Enqueue(T value) {}
                public T Peek() const { return _value; }
                public T Dequeue() { return _value; }
            }
            public class Stack<T> {
                private T _value;
                public void Push(T value) {}
                public T Peek() const { return _value; }
                public T Pop() { return _value; }
            }
            public class LinkedList<T> {
                private T _value;
                public void AddFirst(T value) {}
                public void AddLast(T value) {}
                public T RemoveFirst() { return _value; }
                public T RemoveLast() { return _value; }
            }
            """), "cxcore", syntheticCore: true);
    }

}
