using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericConstraintTests
{
    [Fact]
    public void GenericLocalFunctionChecksAndUsesItsInterfaceConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_local_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadable { int Read(int offset); }
            public class Value : IReadable {
                public constructor() {}
                public int Read(int offset) { return 42 + offset; }
            }
            public int Run() {
                int ReadValue<T>(T value, int offset) where T : IReadable {
                    return value.Read(offset);
                }
                return ReadValue(new Value(), 0);
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Equal("ReadValue", instance.Declaration!.Name);
        Assert.Equal("Value", instance.ParameterTypes[0].Name);
        Assert.Equal("System.Int", instance.ReturnType.FullName.ToString());
    }

    [Fact]
    public void GenericLocalFunctionRejectsAnArgumentThatViolatesItsConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_local_bad_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadable {}
            public class Value {}
            public int Run() {
                T Identity<T>(T value) where T : IReadable { return value; }
                return Identity(new Value());
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy generic constraint", error.Message);
    }

    [Fact]
    public void GenericLocalFunctionChecksTheSpecialStructConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_local_struct_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Value {}
            public Value Run() {
                T Identity<T>(T value) where T : struct { return value; }
                Value value;
                return Identity(value);
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Equal("Value", instance.ReturnType.Name);
    }

    [Fact]
    public void AllowsATypeArgumentThatImplementsItsInterfaceConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_interface_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker {}
            public interface IPrintable {}
            public class Marker : IMarker, IPrintable { public constructor() {} }
            public T Identity<T>(T value) where T : IMarker, IPrintable { return value; }
            public class Helper {
                public constructor() {}
                public T Keep<T>(T value) where T : IMarker { return value; }
            }
            public int Main() {
                Marker marker = new Marker();
                Identity(marker);
                Helper helper = new Helper();
                helper.Keep(marker);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void RejectsATypeArgumentThatDoesNotImplementItsInterfaceConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_unsatisfied_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker {}
            public interface IPrintable {}
            public class PartlyMarked : IMarker { public constructor() {} }
            public T Identity<T>(T value) where T : IMarker, IPrintable { return value; }
            public int Main() {
                PartlyMarked value = new PartlyMarked();
                Identity(value);
                return 0;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy generic constraint", error.Message);
    }

    [Fact]
    public void RequiresConstraintNamesToMatchDeclaredGenericParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_unknown_constraint_parameter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker {}
            public T Identity<T>(T value) where U : IMarker { return value; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Constraint refers to unknown generic parameter 'U'", error.Message);
    }

    [Fact]
    public void GenericClassConstraintAllowsImplementingType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_type_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public class Marker : IMarker { }
            public class Box<T> where T : IMarker { public T value; }
            public class Holder { public Box<Marker> box; }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericClassConstraintRejectsNonimplementingType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_type_unsatisfied_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public class Box<T> where T : IMarker { public T value; }
            public class Holder { public Box<int> box; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy generic type constraint", error.Message);
    }

    [Fact]
    public void GenericClassConstraintsMustNameClassesOrInterfaces()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_type_invalid_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> where T : int { public T value; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must be a class or interface", error.Message);
    }

    [Fact]
    public void GenericFunctionConstraintAcceptsDerivedClassArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_constraint_function");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base { }
            public class Derived : Base { }
            public T Identity<T>(T value) where T : Base { return value; }
            public int Main() {
                Derived value = new Derived();
                Identity(value);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericFunctionConstraintCombinesOneClassAndMultipleInterfaces()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_and_interfaces_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base { }
            public interface IReadable { }
            public interface IWritable { }
            public class Derived : Base, IReadable, IWritable { }
            public T Identity<T>(T value) where T : Base, IReadable, IWritable {
                return value;
            }
            public int Main() {
                Derived value = new Derived();
                Identity(value);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericFunctionConstraintRejectsUnrelatedClassArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_constraint_function_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base { }
            public class Unrelated { }
            public T Identity<T>(T value) where T : Base { return value; }
            public int Main() {
                Unrelated value = new Unrelated();
                Identity(value);
                return 0;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy generic constraint", error.Message);
        Assert.Contains("Base", error.Message);
    }

    [Fact]
    public void GenericClassConstraintAcceptsDerivedTypeAndForwardsConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_constraint_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base { }
            public class Derived : Base { }
            public class Box<T> where T : Base { public T value; }
            public class Holder<U> where U : Derived { public Box<U> box; }
            public class UsesBox { public Box<Derived> box; }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericConstraintAllowsAtMostOneClassType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_multiple_class_constraints");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base { }
            public class Other { }
            public class Box<T> where T : Base, Other { public T value; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("cannot name more than one class", error.Message);
    }

    [Fact]
    public void ReferenceTypeConstraintAcceptsClassesAndInterfaces()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_type_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadable { }
            public class Record : IReadable { }
            public T Identity<T>(T value) where T : class, IReadable { return value; }
            public class Box<T> where T : class { public T Value; }
            public class InterfaceBox { public Box<IReadable> Value; }
            public int Main() {
                Record value = new Record();
                Identity(value);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ReferenceTypeConstraintAllowsObjectUpcastsAndMembers()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_object_members");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class ReferenceBox<T> where T : class {
                public bool HasObjectIdentity(T value) {
                    object boxed = value;
                    return boxed.Equals(value);
                }
            }
            public int Main() {
                ReferenceBox<string> box = new ReferenceBox<string>();
                return box.HasObjectIdentity("key") ? 0 : 1;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ReferenceTypeConstraintRejectsValueTypeArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_type_constraint_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Identity<T>(T value) where T : class { return value; }
            public int Main() { Identity(42); return 0; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy the reference-type constraint", error.Message);
    }

    [Fact]
    public void ValueTypeConstraintAcceptsPrimitivesAndStructs()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_type_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Point { public int X; }
            public T Identity<T>(T value) where T : struct { return value; }
            public int Main() {
                Identity(42);
                Point point;
                Identity(point);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void ValueTypeConstraintRejectsClassArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_type_constraint_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Record { }
            public T Identity<T>(T value) where T : struct { return value; }
            public int Main() { Identity(new Record()); return 0; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy the value-type constraint", error.Message);
    }

    [Fact]
    public void SpecialConstraintCanBeForwardedThroughGenericClass()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_special_constraint_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> where T : class { public T Value; }
            public class Holder<U> where U : class { public Box<U> Value; }
            public struct ValueBox<T> where T : struct { public T Value; }
            public struct ValueHolder<V> where V : struct { public ValueBox<V> Value; }
            public class UsesSpecialConstraints {
                public Box<string> Reference;
                public ValueBox<int> Value;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void RejectsMultipleConstraintClausesForOneParameter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_duplicate_constraint_clauses");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base { }
            public T Identity<T>(T value) where T : class where T : Base { return value; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("cannot have multiple constraint clauses", error.Message);
    }

    [Fact]
    public void PartialGenericDeclarationsMustAgreeOnSpecialConstraintKind()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_partial_constraint_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public partial class Box<T> where T : class { }
            public partial class Box<T> where T : struct { }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Partial declarations of type", error.Message);
        Assert.Contains("conflicting", error.Message);
    }

    [Fact]
    public void OpenTypeArgumentMustDeclareTheRequiredInterfaceConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("open_generic_type_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public class Box<T> where T : IMarker { public T value; }
            public class Holder<U> { public Box<U> box; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy generic type constraint", error.Message);
        Assert.Contains("'U'", error.Message);
    }

    [Fact]
    public void OpenTypeArgumentAcceptsAnInheritedInterfaceConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("open_generic_type_constraint_satisfied");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IBaseMarker { }
            public interface IMarker : IBaseMarker { }
            public class Box<T> where T : IBaseMarker { public T value; }
            public class Holder<U> where U : IMarker { public Box<U> box; }
            """));

        new SemanticBinder().Bind(project);
    }

}
