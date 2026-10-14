using CxCompiler.Model.Expressions;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericSpecializationTests
{
    [Fact]
    public void ClassifiesFunctionGenericListsDuringSemanticBinding()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_list_classification");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Adjust<T>(T value) { return value; }
            public int Adjust<int>(int value) { return value + 1; }
            """));
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();

        Assert.True(functions[0].HasUnclassifiedGenericTypeArguments);
        Assert.True(functions[1].HasUnclassifiedGenericTypeArguments);
        Assert.Empty(functions[0].GenericTypeNames);
        Assert.Empty(functions[0].SpecializationTypeArguments);
        Assert.NotNull(functions[1].SourceSpan);

        new SemanticBinder().Bind(project);

        Assert.Equal(["T"], functions[0].GenericTypeNames);
        Assert.Empty(functions[0].SpecializationTypeArguments);
        Assert.True(functions[1].IsGenericSpecialization);
        Assert.Empty(functions[1].GenericTypeNames);
        Assert.Single(functions[1].SpecializationTypeArguments);
    }

    [Fact]
    public void ClassifiesANameDeclaredLaterAsAClosedSpecializationType()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_type_declared_later_classification");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Keep<T>(T value) { return value; }
            public Model Keep<Model>(Model value) { return value; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Model { }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations
                .OfType<FunctionDeclaration>())
            .Where(function => function.Name == "Keep").ToArray();
        Assert.Equal(["T"], functions[0].GenericTypeNames);
        Assert.Empty(functions[1].GenericTypeNames);
        Assert.True(functions[1].IsGenericSpecialization);
        Assert.Equal("Model", Assert.IsType<NamedType>(
            functions[1].SpecializationTypeArguments.Single()).Name);
    }

    [Fact]
    public void RejectsAmbiguousVisibleTypeNamesDuringClassification()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_ambiguous_type_classification");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace First;
            public class Marker { }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Second;
            public class Marker { }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import First;
            import Second;
            namespace App;
            public T Echo<T>(T value) { return value; }
            public Marker Echo<Marker>(Marker value) { return value; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));
        Assert.Contains("Type name 'Marker' is ambiguous", exception.Message);
    }

    [Fact]
    public void ClassifiesAnImportedTypeNameAsASpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_imported_type_classification");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Models;
            public class Imported { }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Models;
            namespace App;
            public T Keep<T>(T value) { return value; }
            public Imported Keep<Imported>(Imported value) { return value; }
            """));

        new SemanticBinder().Bind(project);

        var specialization = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations
                .OfType<FunctionDeclaration>())
            .Single(function => function.Name == "Keep" && function.IsGenericSpecialization);
        var argument = Assert.IsType<NamedType>(
            specialization.SpecializationTypeArguments.Single());
        Assert.Equal(
            "generic_imported_type_classification.Models.Imported",
            argument.ResolvedTypeFullName.ToString());
    }

    [Fact]
    public void ClassifiesAndSelectsConstructedTypeSpecializationArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_constructed_type_specialization_argument");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> { }
            public U Echo<U>(U value) { return value; }
            public Box<int> Echo<Box<int>>(Box<int> value) { return value; }
            public int Run() {
                Box<int> value = new Box<int>();
                Echo<Box<int>>(value);
                return 42;
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var specialization = declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Echo" && function.IsGenericSpecialization);
        var argument = Assert.IsType<NamedType>(specialization.SpecializationTypeArguments.Single());
        Assert.Single(argument.TypeArguments);
        var run = declarations.OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(run.Body![1]).Expression);
        Assert.Same(specialization, call.TargetSymbol!.ImplementationDeclaration);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constructed-specialization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_constructed_type_specialization_argument.c"));
            Assert.Contains(call.TargetSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClassifiesQualifiedAndNullableTypeFormsAsSpecializations()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_qualified_nullable_specialization_arguments");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace Types;
            public class Marker { }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Keep<T>(T value) { return value; }
            public Types.Marker Keep<Types.Marker>(Types.Marker value) { return value; }
            public T Echo<T>(T value) { return value; }
            public int? Echo<int?>(int? value) { return value; }
            public Types.Marker RunQualified(Types.Marker value) { return Keep(value); }
            public int? RunNullable(int? value) { return Echo(value); }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.SelectMany(context =>
            context.DeclarationScope.Declarations.OfType<FunctionDeclaration>()).ToArray();
        var qualified = GetInvocation(functions.Single(function => function.Name == "RunQualified"));
        var nullable = GetInvocation(functions.Single(function => function.Name == "RunNullable"));
        Assert.True(qualified.TargetSymbol!.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.True(nullable.TargetSymbol!.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void RejectsUnboundSimpleIdentifierInPartiallySpecializedList()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_partial_specialization_identifier");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public int Convert<T, U>(T value, U other) { return 0; }
            public int Convert<int, U>(int value, U other) { return 1; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));
        Assert.Contains("Cannot resolve specialization type argument 'U'", exception.Message);
    }

    [Fact]
    public void RejectsNonConcreteTypeFormsAsSpecializationArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_non_concrete_specialization_argument");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public int Echo<T>(int value) { return value; }
            public int Echo<var>(int value) { return value + 1; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));
        Assert.Contains("must be closed types", exception.Message);
    }

    [Fact]
    public void AllowsFormerSpecializationKeywordAsAnIdentifier()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "specialize_identifier");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public int specialize(int value) { return value; }
            public int Run() { return specialize(3); }
            """));

        new SemanticBinder().Bind(project);

        var call = GetInvocation(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run"));
        Assert.Equal("specialize", call.TargetSymbol!.FullName.Parts[^1]);
    }

    [Fact]
    public void SelectsSpecializedBodyForInferredAndExplicitCallsAndFallsBackOtherwise()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Adjust<T>(T value) { return value; }
            public int Adjust <int>(int value) { return value + 1; }
            public int RunInferred() { return Adjust(4); }
            public int RunExplicit() { return Adjust<int>(5); }
            public long RunFallback() { return Adjust<long>(7L); }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var inferred = GetInvocation(functions.Single(function =>
            function.Name == "RunInferred"));
        var explicitCall = GetInvocation(functions.Single(function =>
            function.Name == "RunExplicit"));
        var fallback = GetInvocation(functions.Single(function =>
            function.Name == "RunFallback"));
        var specialization = functions.Single(function => function.IsGenericSpecialization);
        var inferredSymbol = inferred.TargetSymbol!;
        var explicitSymbol = explicitCall.TargetSymbol!;

        Assert.Same(specialization, inferredSymbol.ImplementationDeclaration);
        Assert.Same(specialization, explicitSymbol.ImplementationDeclaration);
        Assert.Null(fallback.TargetSymbol!.ImplementationDeclaration);
        Assert.Equal(inferredSymbol.SpecializationName, explicitSymbol.SpecializationName);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-specialization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_specialization.c"));
            Assert.Contains("return (value + 1);", source);
            Assert.Contains("return value;", source);
            Assert.Equal(1, source.Split("return (value + 1);", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsSpecializationWithMismatchedClosedSignature()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_signature");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Adjust<T>(T value) { return value; }
            public long Adjust <int>(int value) { return 1L; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("No generic function matches specialization 'Adjust'", exception.Message);
    }

    [Fact]
    public void RejectsSpecializationWithWrongTypeArgumentArity()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_wrong_arity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            public int Echo<int, string>(int value) { return value; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("No generic function matches specialization 'Echo'", exception.Message);
    }

    [Fact]
    public void RejectsMisspelledTypeInSpecializationList()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_misspelled_type");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public R Convert<T, R>(T value, R result) { return result; }
            public int Convert<int, Misspeled>(int value, Misspeled result) { return value; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot resolve specialization type argument 'Misspeled'",
            exception.Message);
    }

    [Fact]
    public void RejectsSpecializationWithOpenContainingTypeArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_open_type_argument");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> { }
            public class Utility<TState> {
                public U Echo<U>(U value) { return value; }
                public Box<TState> Echo<Box<TState> >(Box<TState> value) { return value; }
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must be closed types", exception.Message);
    }

    [Fact]
    public void EnforcesPrimaryConstraintsBeforeRegisteringSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) where T : class { return value; }
            public int Echo <int>(int value) { return value; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("reference-type constraint for parameter 'T'", exception.Message);
    }

    [Theory]
    [InlineData("""
        public class Base {
            public virtual T Echo<T>(T value) { return value; }
            public virtual int Echo<int>(int value) { return value + 1; }
        }
        """)]
    [InlineData("""
        public class Base {
            public virtual T Echo<T>(T value) { return value; }
        }
        public class Derived : Base {
            public override T Echo<T>(T value) { return value; }
            public override int Echo<int>(int value) { return value + 1; }
        }
        """)]
    public void BindsSpecializationsOfVirtualAndOverrideGenericMethods(
        string source)
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_method_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void SpecializationInDerivedTypeCannotTargetInheritedGenericMethod()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_inherited_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base {
                public int Echo<int>(int value) { return value + 1; }
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(
            "No generic function matches specialization 'Echo'",
            exception.Message);
    }

    [Fact]
    public void RejectsGenericAbstractMethodBeforeSpecializationRegistration()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_abstract_method_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public abstract class Base {
                public abstract T Echo<T>(T value);
                public int Echo<int>(int value) { return value + 1; }
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("cannot provide an implementation for an abstract generic method",
            exception.Message);
    }

    [Fact]
    public void BindsGenericVirtualAndOverrideMethodsToTheSameSlot()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_method_call");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public abstract class Base {
                public abstract T Echo<T>(T value);
            }
            public class Derived : Base {
                public override U Echo<U>(U value) { return value; }
            }
            """));

        new SemanticBinder().Bind(project);

        var types = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<ClassDeclaration>()
            .Where(type => type.Name is "Base" or "Derived")
            .ToDictionary(type => type.Name, StringComparer.Ordinal);
        var contract = Assert.Single(types["Base"].VirtualMethodSlots, slot =>
            slot.Contract.Name == "Echo");
        var implementation = Assert.Single(types["Derived"].VirtualMethodSlots, slot =>
            slot.Contract.Name == "Echo");
        Assert.Equal(contract.Index, implementation.Index);
        Assert.Same(contract.Contract, implementation.Contract);
        Assert.Same(types["Derived"].MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Echo"),
            implementation.Implementation);
    }

    [Fact]
    public void BindsGenericVirtualCallTargetsForKnownRuntimeClasses()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_method_call_pending_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base {
                public override U Echo<U>(U value) { return value; }
                public override int Echo<int>(int value) { return value + 1; }
            }
            public int Run() {
                Base value = new Derived();
                return value.Echo(42);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var run = declarations["Run"];
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        Assert.Contains(invocation.GenericVirtualDispatchTargets,
            target => target.Implementation.Declaration?.ParentClassDeclaration?.Name == "Derived" &&
                target.Implementation.ImplementationDeclaration?.IsGenericSpecialization == true);
        Assert.Contains(invocation.GenericVirtualDispatchTargets,
            target => target.Implementation.Declaration?.ParentClassDeclaration?.Name == "Base");
    }

    [Fact]
    public void BindsGenericVirtualMethodValueTargetsForKnownRuntimeClasses()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_method_value_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public delegate int Transform(int value);
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base {
                public override U Echo<U>(U value) { return value; }
                public override int Echo<int>(int value) { return value + 1; }
            }
            public int Run() {
                Base value = new Derived();
                Transform transform = value.Echo;
                return transform(41);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var run = declarations["Run"];
        var methodValue = Assert.IsType<MemberAccessExpression>(run.Body!
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(statement => statement.Declarators)
            .Single(declarator => declarator.Name == "transform").Initializer);
        var target = Assert.Single(methodValue.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.Equal("Derived", target.Implementation.Declaration?
            .ParentClassDeclaration?.Name);
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualTargetsForClosedGenericRuntimeClasses()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_closed_runtime_class");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived<TState> : Base {
                public TState State;
            }
            public int Run() {
                Base value = new Derived<string>();
                return value.Echo(41);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var derivedTarget = Assert.Single(invocation.GenericVirtualDispatchTargets,
            target => target.RuntimeType.ConstructedIdentity is not null &&
                target.RuntimeType.ResolvedTypeFullName.ToString().EndsWith(
                    ".Derived", StringComparison.Ordinal));
        Assert.NotNull(derivedTarget.RuntimeType.ConstructedIdentity);
        Assert.Equal("Base", derivedTarget.Implementation.Declaration?
            .ParentClassDeclaration?.Name);
        Assert.Null(derivedTarget.Implementation.ImplementationDeclaration);
    }

    [Fact]
    public void BindsGenericVirtualTargetsForClosedGenericCallerSpecializations()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_closed_generic_caller");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base { }
            public T Forward<T>(Base receiver, T value) {
                return receiver.Echo(value);
            }
            public int Run() { return Forward(new Derived(), 41); }
            """));

        new SemanticBinder().Bind(project);

        var forward = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                forward.Body!.OfType<ReturnStatement>())).Expression);
        var caller = Assert.Single(project.GenericFunctionInstances,
            instance => ReferenceEquals(instance.Declaration, forward));
        var closedContract = invocation.GetClosedGenericTarget(
            caller.SpecializationName!);
        Assert.NotNull(closedContract);
        Assert.IsType<IntType>(closedContract.GenericTypeArguments["T"]);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.IsType<IntType>(target.Implementation.GenericTypeArguments["T"]);
    }

    [Fact]
    public void BindsGenericInterfaceMethodToRuntimeImplementation()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_interface_virtual_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform {
                T Transform<T>(T value);
            }
            public class TransformImpl : ITransform {
                public T Transform<T>(T value) { return value; }
                public int Transform<int>(int value) { return value + 20; }
            }
            public int Run() {
                ITransform value = new TransformImpl();
                return value.Transform(2);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.Equal("Transform", target.Implementation.Declaration?.Name);
        Assert.IsType<IntType>(target.Implementation.GenericTypeArguments["T"]);
        Assert.NotNull(target.Implementation.SpecializationName);
    }

    [Fact]
    public void BindsGenericVirtualMethodThroughConstructedGenericInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_constructed_interface_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform<TState> {
                U Transform<U>(TState state, U value);
            }
            public class TransformImpl : ITransform<int> {
                public U Transform<U>(int state, U value) { return value; }
                public int Transform<int>(int state, int value) {
                    return state + value;
                }
            }
            public int Run() {
                ITransform<int> value = new TransformImpl();
                return value.Transform(1, 41);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.Equal("Transform", target.Implementation.Declaration?.Name);
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
    }

    [Fact]
    public void BindsGenericVirtualMethodOnClosedClassImplementingConstructedInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_closed_class_constructed_interface_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform<TState> {
                U Transform<U>(TState state, U value);
            }
            public class TransformImpl<TState> : ITransform<TState> {
                public U Transform<U>(TState state, U value) { return value; }
                public int Transform<int>(TState state, int value) {
                    return value + 1;
                }
            }
            public int Run() {
                ITransform<int> value = new TransformImpl<int>();
                return value.Transform(1, 41);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.ConstructedIdentity is not null &&
                item.RuntimeType.ResolvedTypeFullName.ToString().EndsWith(
                    ".TransformImpl", StringComparison.Ordinal));
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.All(target.Implementation.ParameterTypes, parameter =>
            Assert.IsType<IntType>(parameter));
    }

    [Fact]
    public void BindsGenericCallerForwardingThroughConstructedGenericInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_constructed_interface_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform<TState> {
                U Transform<U>(TState state, U value);
            }
            public class TransformImpl<TState> : ITransform<TState> {
                public U Transform<U>(TState state, U value) { return value; }
                public int Transform<int>(TState state, int value) {
                    return value + 1;
                }
            }
            public T Forward<T>(
                ITransform<int> receiver, int state, T value) {
                return receiver.Transform(state, value);
            }
            public int Run() {
                ITransform<int> receiver = new TransformImpl<int>();
                return Forward(receiver, 1, 41);
            }
            """));

        new SemanticBinder().Bind(project);

        var forward = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                forward.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.ConstructedIdentity is not null &&
                item.RuntimeType.ResolvedTypeFullName.ToString().EndsWith(
                    ".TransformImpl", StringComparison.Ordinal));
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.All(target.Implementation.ParameterTypes, parameter =>
            Assert.IsType<IntType>(parameter));
    }

    [Fact]
    public void BindsGenericVirtualCallerWithLocalResult()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_caller_local_result");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base {
                public override U Echo<U>(U value) { return value; }
                public override int Echo<int>(int value) { return value + 1; }
            }
            public T Forward<T>(Base receiver, T value) {
                T result = receiver.Echo(value);
                return result;
            }
            public int Run() { return Forward(new Derived(), 41); }
            """));

        new SemanticBinder().Bind(project);

        var forward = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var invocation = Assert.IsType<InvocationExpression>(Assert.Single(
            Assert.Single(forward.Body!.OfType<LocalVariableDeclarationStatement>())
                .Declarators).Initializer);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualCallerOnClosedGenericOwner()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_caller_closed_generic_owner");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform {
                T Transform<TState, T>(TState state, T value);
            }
            public class IntTransformImpl : ITransform {
                public T Transform<TState, T>(TState state, T value) { return value; }
                public int Transform<int, int>(int state, int value) {
                    return value + 55;
                }
            }
            public class LongTransformImpl : ITransform {
                public T Transform<TState, T>(TState state, T value) { return value; }
            }
            public class GenericRouter<TOwner> {
                public T Forward<T>(ITransform receiver,
                    TOwner state, T value) {
                    return receiver.Transform(state, value);
                }
            }
            public int RunExact() {
                GenericRouter<int> router = new GenericRouter<int>();
                return router.Forward(new IntTransformImpl(), 1, 2);
            }
            public long RunFallback() {
                GenericRouter<long> router = new GenericRouter<long>();
                return router.Forward(new LongTransformImpl(), 1l, 3l);
            }
            """));

        new SemanticBinder().Bind(project);

        var forward = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name == "GenericRouter")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                forward.Body!.OfType<ReturnStatement>())).Expression);
        var callerInstances = project.GenericFunctionInstances
            .Where(instance => ReferenceEquals(instance.Declaration, forward))
            .Where(instance => instance.SpecializationName is not null)
            .ToArray();
        Assert.Contains(callerInstances, instance =>
            instance.ClosedContainingType?.TypeArguments.Single() is IntType);
        Assert.Contains(callerInstances, instance =>
            instance.ClosedContainingType?.TypeArguments.Single() is LongType);

        Assert.Contains(invocation.GenericVirtualDispatchTargets,
            target => target.RuntimeType.Name == "IntTransformImpl" &&
                target.Implementation.ImplementationDeclaration?.IsGenericSpecialization == true &&
                target.Implementation.ParameterTypes.Last() is IntType);
        Assert.Contains(invocation.GenericVirtualDispatchTargets,
            target => target.RuntimeType.Name == "LongTransformImpl" &&
                target.Implementation.ImplementationDeclaration?.IsGenericSpecialization != true &&
                target.Implementation.ParameterTypes.Last() is LongType);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-virtual-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));
            var generatedC = File.ReadAllText(Path.Combine(
                outputDirectory, $"{project.Name}.c"));
            Assert.Contains("cx_require_generic_virtual_method", generatedC);
            Assert.Contains("ITransform.Transform`2(!TState,!T)->!T", generatedC);
            Assert.Contains("System.Int", generatedC);
            Assert.Contains("System.Long", generatedC);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void BindsGenericVirtualCallerWithConditionalDispatch()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_caller_conditional_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IApply { T Apply<T>(T value); }
            public class Handler : IApply {
                public T Apply<T>(T value) { return value; }
                public int Apply<int>(int value) { return value + 1; }
            }
            public T Forward<T>(bool dispatch, IApply target, T value) {
                return dispatch ? target.Apply(value) : value;
            }
            public T Reverse<T>(bool dispatch, IApply target, T value) {
                return dispatch ? value : target.Apply(value);
            }
            public T ForwardStatement<T>(bool dispatch, IApply target, T value) {
                if (dispatch) return target.Apply(value);
                return value;
            }
            public T ReverseStatement<T>(bool dispatch, IApply target, T value) {
                if (dispatch) return value;
                else return target.Apply(value);
            }
            public T ForwardBlockStatement<T>(bool dispatch, IApply target, T value) {
                if (dispatch) { return target.Apply(value); }
                else { return value; }
            }
            public T ReverseBlockStatement<T>(bool dispatch, IApply target, T value) {
                if (dispatch) { return value; }
                else { return target.Apply(value); }
            }
            public T InferredAliasStatement<T>(bool dispatch, IApply target, T value) {
                var forwarded = value;
                if (dispatch) { return target.Apply(forwarded); }
                else { return forwarded; }
            }
            public T LocalConditionStatement<T>(bool dispatch, IApply target, T value) {
                bool shouldDispatch = dispatch;
                if (shouldDispatch) { return target.Apply(value); }
                return value;
            }
            public T InferredLocalConditionStatement<T>(bool dispatch, IApply target, T value) {
                var shouldDispatch = dispatch;
                if (shouldDispatch) { return target.Apply(value); }
                return value;
            }
            public int Run() {
                return Forward(true, new Handler(), 41) +
                    Forward(false, new Handler(), 1) +
                    Reverse(false, new Handler(), 41) +
                    Reverse(true, new Handler(), 1) +
                    ForwardStatement(true, new Handler(), 41) +
                    ForwardStatement(false, new Handler(), 1) +
                    ReverseStatement(false, new Handler(), 41) +
                    ReverseStatement(true, new Handler(), 1) +
                    ForwardBlockStatement(true, new Handler(), 41) +
                    ForwardBlockStatement(false, new Handler(), 1) +
                    ReverseBlockStatement(false, new Handler(), 41) +
                    ReverseBlockStatement(true, new Handler(), 1) +
                    InferredAliasStatement(true, new Handler(), 41) +
                    InferredAliasStatement(false, new Handler(), 1) +
                    LocalConditionStatement(true, new Handler(), 41) +
                    LocalConditionStatement(false, new Handler(), 1) +
                    InferredLocalConditionStatement(true, new Handler(), 41) +
                    InferredLocalConditionStatement(false, new Handler(), 1);
            }
            """));

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-virtual-conditional-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));
            var generatedC = File.ReadAllText(Path.Combine(
                outputDirectory, $"{project.Name}.c"));
            Assert.Contains("int forwarded = value;", generatedC);
            Assert.DoesNotContain("cx_bool forwarded", generatedC);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        var forward = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var returned = Assert.IsType<ReturnStatement>(Assert.Single(forward.Body!));
        var conditional = Assert.IsType<ConditionalExpression>(returned.Expression);
        var invocation = Assert.IsType<InvocationExpression>(conditional.WhenTrue);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Handler");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));

        var reverse = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Reverse");
        var reverseReturned = Assert.IsType<ReturnStatement>(Assert.Single(reverse.Body!));
        var reverseConditional = Assert.IsType<ConditionalExpression>(reverseReturned.Expression);
        var reverseInvocation = Assert.IsType<InvocationExpression>(
            reverseConditional.WhenFalse);
        var reverseTarget = Assert.Single(reverseInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Handler");
        Assert.True(reverseTarget.Implementation.ImplementationDeclaration?
            .IsGenericSpecialization);

        static IEnumerable<ReturnStatement> GetBranchReturns(StatementBase? statement) =>
            statement switch
            {
                ReturnStatement returnStatement => [returnStatement],
                BlockStatement block => block.Statements.OfType<ReturnStatement>(),
                _ => [],
            };

        foreach (var name in new[]
            {
                "ForwardStatement", "ReverseStatement",
                "ForwardBlockStatement", "ReverseBlockStatement",
                "InferredAliasStatement",
                "LocalConditionStatement",
                "InferredLocalConditionStatement",
            })
        {
            var statementForwarder = project.CompilationContexts
                .SelectMany(context => context.DeclarationScope.Declarations)
                .OfType<FunctionDeclaration>()
                .Single(function => function.Name == name);
            var statementConditional = Assert.Single(
                statementForwarder.Body!.OfType<IfStatement>());
            var statementInvocation = new[]
                { statementConditional.ThenStatement, statementConditional.ElseStatement }
                .Where(statement => statement is not null)
                .SelectMany(GetBranchReturns)
                .Select(returnStatement => returnStatement.Expression)
                .OfType<InvocationExpression>()
                .Single();
            var statementTarget = Assert.Single(statementInvocation.GenericVirtualDispatchTargets,
                item => item.RuntimeType.Name == "Handler");
            Assert.True(statementTarget.Implementation.ImplementationDeclaration?
                .IsGenericSpecialization);
        }
    }

    [Fact]
    public void BindsInheritedGenericInterfaceMethodToRuntimeImplementation()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_inherited_interface_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IBaseTransform {
                T Transform<T>(T value);
            }
            public interface IDerivedTransform : IBaseTransform { }
            public class TransformImpl : IDerivedTransform {
                public T Transform<T>(T value) { return value; }
                public int Transform<int>(int value) { return value + 1; }
            }
            public int Run() {
                IDerivedTransform value = new TransformImpl();
                return value.Transform(41);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualDispatchThroughInterfaceConstrainedCaller()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_interface_constrained_caller");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform<TState> {
                U Transform<U>(TState state, U value);
            }
            public class TransformImpl<TState> : ITransform<TState> {
                public U Transform<U>(TState state, U value) { return value; }
                public int Transform<int>(TState state, int value) {
                    return value + 1;
                }
            }
            public T Forward<TReceiver, T>(TReceiver receiver, T value)
                where TReceiver : ITransform<int> {
                return receiver.Transform(1, value);
            }
            public int Run() {
                return Forward(new TransformImpl<int>(), 41);
            }
            """));

        new SemanticBinder().Bind(project);

        var forward = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                forward.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.ConstructedIdentity is not null &&
                item.RuntimeType.ResolvedTypeFullName.ToString().EndsWith(
                    ".TransformImpl", StringComparison.Ordinal));
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.All(target.Implementation.ParameterTypes, parameter =>
            Assert.IsType<IntType>(parameter));
    }

    [Fact]
    public void BindsConstrainedGenericVirtualMethodSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_method_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform {
                T Transform<T>(T value) where T : class;
            }
            public class TransformImpl : ITransform {
                public T Transform<T>(T value) where T : class { return value; }
                public string Transform<string>(string value) { return value; }
            }
            public string Run() {
                ITransform value = new TransformImpl();
                return value.Transform("seed");
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<StringType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualMethodWithInterfaceConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_interface_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public class Marker : IMarker { }
            public interface ITransformMarker {
                T Transform<T>(T value) where T : IMarker;
            }
            public class TransformMarkerImpl : ITransformMarker {
                public T Transform<T>(T value) where T : IMarker { return value; }
                public Marker Transform<Marker>(Marker value) { return value; }
            }
            public IMarker Run() {
                ITransformMarker value = new TransformMarkerImpl();
                return value.Transform(new Marker());
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformMarkerImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal("Marker", Assert.Single(target.Implementation.ParameterTypes).FullName.ToString());
    }

    [Fact]
    public void BindsStructConstrainedGenericVirtualMethodSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_struct_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IValueTransform {
                T TransformValue<T>(T value) where T : struct;
            }
            public class ValueTransformImpl : IValueTransform {
                public T TransformValue<T>(T value) where T : struct { return value; }
                public int TransformValue<int>(int value) { return value + 1; }
            }
            public int Run() {
                IValueTransform value = new ValueTransformImpl();
                return value.TransformValue(41);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "ValueTransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualMethodWithCombinedClassAndInterfaceConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_combined_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public class Marker : IMarker { }
            public interface ITransform {
                T Transform<T>(T value) where T : class, IMarker;
            }
            public class TransformImpl : ITransform {
                public T Transform<T>(T value) where T : class, IMarker { return value; }
                public Marker Transform<Marker>(Marker value) { return value; }
            }
            public IMarker Run() {
                ITransform value = new TransformImpl();
                return value.Transform(new Marker());
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal("Marker", Assert.Single(target.Implementation.ParameterTypes).FullName.ToString());
    }

    [Fact]
    public void BindsGenericVirtualMethodWithMultipleInterfaceConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_multiple_interface_constraints_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public interface ITraceable { }
            public class Marker : IMarker, ITraceable { }
            public interface ITransform {
                T Transform<T>(T value) where T : class, IMarker, ITraceable;
            }
            public class TransformImpl : ITransform {
                public T Transform<T>(T value)
                    where T : class, IMarker, ITraceable { return value; }
                public Marker Transform<Marker>(Marker value) { return value; }
            }
            public IMarker Run() {
                ITransform value = new TransformImpl();
                return value.Transform(new Marker());
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal("Marker", Assert.Single(target.Implementation.ParameterTypes).FullName.ToString());
    }

    [Fact]
    public void BindsGenericVirtualMethodWithStructAndInterfaceConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_struct_interface_constraints_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public interface IAuditable { }
            public struct Marker : IMarker, IAuditable { public int Value; }
            public struct Fallback : IMarker, IAuditable { public int Value; }
            public interface ITransform {
                T Transform<T>(T value) where T : struct, IMarker, IAuditable;
            }
            public class TransformImpl : ITransform {
                public T Transform<T>(T value)
                    where T : struct, IMarker, IAuditable { return value; }
                public Marker Transform<Marker>(Marker value) {
                    value.Value = 1;
                    return value;
                }
            }
            public int Run() {
                ITransform value = new TransformImpl();
                Marker marker = default;
                Fallback fallback = default;
                Marker exact = value.Transform(marker);
                Fallback primary = value.Transform(fallback);
                return exact.Value + primary.Value;
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocations = run.Body!
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(local => local.Declarators)
            .Select(declarator => declarator.Initializer)
            .OfType<InvocationExpression>()
            .ToArray();
        var exactInvocation = Assert.Single(invocations,
            invocation => invocation.Arguments.Single() is IdentifierExpression identifier &&
                identifier.Identifier.ToString() == "marker");
        var target = Assert.Single(exactInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal("Marker", Assert.Single(target.Implementation.ParameterTypes)
            .FullName.ToString());
        var fallbackInvocation = Assert.Single(invocations,
            invocation => invocation.Arguments.Single() is IdentifierExpression identifier &&
                identifier.Identifier.ToString() == "fallback");
        var fallbackTarget = Assert.Single(fallbackInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.False(fallbackTarget.Implementation.ImplementationDeclaration?
            .IsGenericSpecialization ?? false);
    }

    [Fact]
    public void BindsClassOverrideWithGenericMethodConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_override_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Transform<T>(T value) where T : class { return value; }
            }
            public class Derived : Base {
                public override U Transform<U>(U value) where U : class { return value; }
                public override string Transform<string>(string value) { return value; }
            }
            public string Run() {
                Base value = new Derived();
                return value.Transform("seed");
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.Equal("Derived", target.Implementation.Declaration?
            .ParentClassDeclaration?.Name);
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<StringType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualSpecializationOnClosedGenericOwner()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_specialization_closed_owner");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Marker { public int Value; }
            public class MarkerFallback { public int Value = 7; }
            public class Base<T> {
                public virtual U Transform<U>(U value) where U : class { return value; }
            }
            public class Derived<T> : Base<T> {
                public override U Transform<U>(U value) where U : class { return value; }
                public override Marker Transform<Marker>(Marker value) {
                    value.Value = 42;
                    return value;
                }
            }
            public Marker Run() {
                Base<int> value = new Derived<int>();
                return value.Transform(new Marker());
            }
            public MarkerFallback RunFallback() {
                Base<int> value = new Derived<int>();
                return value.Transform(new MarkerFallback());
            }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        foreach (var functionName in new[] { "Run", "RunFallback" })
        {
            var invocation = Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(
                    functions[functionName].Body!.OfType<ReturnStatement>())).Expression);
            var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
                item => item.RuntimeType.Name == "Derived");
            Assert.Equal(functionName == "Run",
                target.Implementation.ImplementationDeclaration?.IsGenericSpecialization ?? false);
            Assert.Equal("Derived", target.Implementation.Declaration?
                .ParentClassDeclaration?.Name);
        }
    }

    [Fact]
    public void BindsClassVirtualMethodWithCombinedClassAndInterfaceConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_class_combined_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            public interface ITraceable { }
            public class Marker : IMarker, ITraceable { }
            public class MarkerFallback : IMarker, ITraceable { }
            public class Base {
                public virtual T Transform<T>(T value)
                    where T : class, IMarker, ITraceable {
                    return value;
                }
            }
            public class Derived : Base {
                public override U Transform<U>(U value)
                    where U : class, IMarker, ITraceable {
                    return value;
                }
                public override Marker Transform<Marker>(Marker value) {
                    return value;
                }
            }
            public IMarker Run() {
                Base value = new Derived();
                return value.Transform(new Marker());
            }
            public IMarker RunFallback() {
                Base value = new Derived();
                return value.Transform(new MarkerFallback());
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var run = declarations["Run"];
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.Equal("Derived", target.Implementation.Declaration?
            .ParentClassDeclaration?.Name);
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal("Marker", Assert.Single(target.Implementation.ParameterTypes)
            .FullName.ToString());

        var fallbackInvocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                declarations["RunFallback"].Body!.OfType<ReturnStatement>())).Expression);
        var fallbackTarget = Assert.Single(fallbackInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.False(fallbackTarget.Implementation.ImplementationDeclaration?
            .IsGenericSpecialization ?? false);
    }

    [Fact]
    public void BindsClassVirtualMethodWithStructConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_class_struct_constraint_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Transform<T>(T value) where T : struct {
                    return value;
                }
            }
            public class Derived : Base {
                public override U Transform<U>(U value) where U : struct {
                    return value;
                }
                public override int Transform<int>(int value) {
                    return value + 30;
                }
            }
            public long RunFallback() {
                Base value = new Derived();
                return value.Transform(7L);
            }
            public int RunExact() {
                Base value = new Derived();
                return value.Transform(7);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var exactInvocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                declarations["RunExact"].Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(exactInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Derived");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualVoidForwardingCaller()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_void_forwarding_caller");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IApply { T Apply<T>(T value); }
            public class Handler : IApply {
                public T Apply<T>(T value) { return value; }
                public int Apply<int>(int value) { return value + 1; }
            }
            public void Forward<T>(IApply target, T value) {
                target.Apply(value);
            }
            public int Run() {
                Forward(new Handler(), 4);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var forwardInvocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(declarations["Forward"].Body!)).Expression);
        var target = Assert.Single(forwardInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "Handler");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsRecursiveGenericVirtualSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_recursive_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base {
                public virtual T Transform<T>(T value) { return value; }
            }
            public class Derived : Base {
                public override U Transform<U>(U value) { return value; }
                public override int Transform<int>(int value) {
                    if (value == 0) return 0;
                    return this.Transform(value - 1) - (-1);
                }
            }
            public int Run() {
                Base value = new Derived();
                return value.Transform(5);
            }
            """));

        new SemanticBinder().Bind(project);

        var derived = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name == "Derived");
        var recursiveSpecialization = derived.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization &&
                function.Name == "Transform");
        var recursiveReturn = Assert.IsType<ReturnStatement>(
            recursiveSpecialization.Body!.OfType<ReturnStatement>().Single());
        var recursiveExpression = Assert.IsType<BinaryExpression>(
            recursiveReturn.Expression);
        var recursiveInvocation = Assert.IsType<InvocationExpression>(
            recursiveExpression.Left);
        Assert.True(recursiveInvocation.TargetSymbol?.ImplementationDeclaration?
            .IsGenericSpecialization);
    }

    [Fact]
    public void BindsGenericVirtualMethodThroughDeepInterfaceInheritance()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_deep_interface_inheritance_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform { T Transform<T>(T value); }
            public interface IChildTransform : ITransform { }
            public interface IGrandchildTransform : IChildTransform { }
            public class TransformImpl : IGrandchildTransform {
                public T Transform<T>(T value) { return value; }
                public int Transform<int>(int value) { return value + 40; }
            }
            public int Run() {
                IGrandchildTransform value = new TransformImpl();
                return value.Transform(2);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));
    }

    [Fact]
    public void BindsGenericVirtualMethodThroughDiamondInterfaceInheritance()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_diamond_interface_inheritance_dispatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform { T Transform<T>(T value); }
            public interface ILeftTransform : ITransform {
                T Transform<T>(T value);
            }
            public interface IRightTransform : ITransform {
                T Transform<T>(T value);
            }
            public interface IDiamondTransform : ILeftTransform, IRightTransform { }
            public class TransformImpl : IDiamondTransform {
                public T Transform<T>(T value) { return value; }
                public int Transform<int>(int value) { return value + 50; }
            }
            public int Run() {
                IDiamondTransform value = new TransformImpl();
                return value.Transform(2);
            }
            public long RunFallback() {
                IDiamondTransform value = new TransformImpl();
                return value.Transform(11l);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                run.Body!.OfType<ReturnStatement>())).Expression);
        var target = Assert.Single(invocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(target.Implementation.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.IsType<IntType>(Assert.Single(target.Implementation.ParameterTypes));

        var fallback = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunFallback");
        var fallbackInvocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                fallback.Body!.OfType<ReturnStatement>())).Expression);
        var fallbackTarget = Assert.Single(fallbackInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.False(fallbackTarget.Implementation.ImplementationDeclaration?
            .IsGenericSpecialization ?? false);
    }

    [Fact]
    public void BindsGenericVirtualMethodRedeclaredByChildInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_redeclared_interface_contract");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform { T Transform<T>(T value); }
            public interface IChildTransform : ITransform {
                T Transform<T>(T value);
            }
            public class TransformImpl : IChildTransform {
                public T Transform<T>(T value) { return value; }
                public int Transform<int>(int value) { return value + 55; }
            }
            public int RunExact() {
                IChildTransform value = new TransformImpl();
                return value.Transform(2);
            }
            public long RunFallback() {
                IChildTransform value = new TransformImpl();
                return value.Transform(2l);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var exactInvocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                declarations["RunExact"].Body!.OfType<ReturnStatement>())).Expression);
        var exactTarget = Assert.Single(exactInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.True(exactTarget.Implementation.ImplementationDeclaration?.IsGenericSpecialization);

        var fallbackInvocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(
                declarations["RunFallback"].Body!.OfType<ReturnStatement>())).Expression);
        var fallbackTarget = Assert.Single(fallbackInvocation.GenericVirtualDispatchTargets,
            item => item.RuntimeType.Name == "TransformImpl");
        Assert.False(
            fallbackTarget.Implementation.ImplementationDeclaration?.IsGenericSpecialization ?? false);
    }

    [Fact]
    public void BindsDistinctGenericVirtualContractRedeclaredByChildInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_distinct_redeclared_interface_contract");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform { T Transform<T>(T value); }
            public interface IChildTransform : ITransform {
                T Transform<T>(T value, int offset);
            }
            public class TransformImpl : IChildTransform {
                public T Transform<T>(T value) { return value; }
                public int Transform<int>(int value) { return value + 1; }
                public T Transform<T>(T value, int offset) { return value; }
                public int Transform<int>(int value, int offset) {
                    return value + offset + 100;
                }
            }
            public int RunBaseContract() {
                IChildTransform value = new TransformImpl();
                return value.Transform(2);
            }
            public int RunChildExact() {
                IChildTransform value = new TransformImpl();
                return value.Transform(2, 3);
            }
            public long RunChildFallback() {
                IChildTransform value = new TransformImpl();
                return value.Transform(2l, 3);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var baseInvocation = GetInvocation("RunBaseContract");
        Assert.True(GetTarget(baseInvocation).Implementation
            .ImplementationDeclaration?.IsGenericSpecialization);

        var childExactInvocation = GetInvocation("RunChildExact");
        var childExactTarget = GetTarget(childExactInvocation);
        Assert.True(childExactTarget.Implementation
            .ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal(2, childExactTarget.Implementation.ParameterTypes.Count);

        var childFallbackInvocation = GetInvocation("RunChildFallback");
        var childFallbackTarget = GetTarget(childFallbackInvocation);
        Assert.False(childFallbackTarget.Implementation
            .ImplementationDeclaration?.IsGenericSpecialization ?? false);
        Assert.Equal(2, childFallbackTarget.Implementation.ParameterTypes.Count);

        InvocationExpression GetInvocation(string functionName) =>
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(
                    declarations[functionName].Body!.OfType<ReturnStatement>())).Expression);

        GenericVirtualDispatchTarget GetTarget(InvocationExpression invocation) =>
            Assert.Single(invocation.GenericVirtualDispatchTargets,
                item => item.RuntimeType.Name == "TransformImpl");
    }

    [Fact]
    public void RejectsGenericInterfaceRedeclarationWithIncompatibleReturnType()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_redeclared_interface_return_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform { T Transform<T>(T value); }
            public interface IChildTransform : ITransform {
                int Transform<T>(T value);
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must preserve the return type", error.Message);
    }

    [Fact]
    public void RejectsGenericInterfaceRedeclarationWithIncompatibleConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_virtual_redeclared_interface_constraint_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ITransform {
                T Transform<T>(T value) where T : class;
            }
            public interface IChildTransform : ITransform {
                T Transform<T>(T value);
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must preserve the generic constraints", error.Message);
    }

    [Fact]
    public void SpecializesGenericMethodsPerClosedContainingType()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_generic_container");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Router<TState> {
                public TState Last;
                public U Route<U>(TState state, U value) { return value; }
                public int Route<int>(TState state, int value) {
                    Last = state;
                    return value + 1;
                }
            }
            public struct StructRouter<TState> {
                public TState Last;
                public constructor() {}
                public U Route<U>(TState state, U value) { return value; }
                public int Route<int>(TState state, int value) {
                    Last = state;
                    return value + 1;
                }
            }
            public int RunText() {
                Router<string> router = new Router<string>();
                return router.Route<int>("state", 3);
            }
            public int RunArray() {
                Router<string[]> router = new Router<string[]>();
                return router.Route<int>(null, 6);
            }
            public long RunFallback() {
                Router<string> router = new Router<string>();
                return router.Route<long>("state", 6L);
            }
            public int RunStruct() {
                StructRouter<int> router = default;
                return router.Route<int>(5, 8);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single()
            .DeclarationScope.Declarations;
        var router = declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Router");
        var specialization = router.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);
        var calls = declarations.OfType<FunctionDeclaration>()
            .Where(function => function.Name is "RunText" or "RunArray")
            .Select(GetInvocation)
            .ToArray();
        var fallbackCall = GetInvocation(declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunFallback"));

        Assert.All(calls, call =>
            Assert.Same(specialization, call.TargetSymbol!.ImplementationDeclaration));
        var methodInstances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Route")
            .ToArray();
        var structCall = GetInvocation(declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunStruct"));
        var structSpecialization = declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "StructRouter")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);
        Assert.Same(structSpecialization, structCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Equal(4, methodInstances.Length);
        Assert.Null(fallbackCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Contains(methodInstances, instance =>
            Assert.IsType<NamedType>(instance.ClosedContainingType)
                .TypeArguments.Single() is ArrayType);
        Assert.Equal(3, methodInstances.Select(instance =>
            Assert.IsType<NamedType>(instance.ClosedContainingType)
                .ConstructedIdentity!.CanonicalName).Distinct().Count());

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-method-specialization-container-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_specialization_generic_container.c"));
            Assert.Equal(3,
                source.Split("return (value + 1);", StringSplitOptions.None).Length - 1);
            Assert.Contains("Last", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsDuplicateSpecializationsForTheSameClosedArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_duplicate");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            public int Echo <int>(int value) { return value + 1; }
            public int Echo <int>(int value) { return value + 2; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Duplicate generic function specialization", exception.Message);
    }

    [Fact]
    public void RejectsSpecializationThatMatchesMultipleConstnessVariants()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_ambiguous_primary");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Router {
                public T Echo<T>(T value) { return value; }
                public T Echo<T>(T value) const { return value; }
                public int Echo<int>(int value) const { return value + 1; }
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Generic function specialization 'Echo' is ambiguous",
            exception.Message);
    }

    [Fact]
    public void RejectsSpecializationThatChangesPrimaryVisibility()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_visibility_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            private int Echo<int>(int value) { return value + 1; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must preserve the primary declaration's modifiers and signature",
            exception.Message);
    }

    [Fact]
    public void RejectsSpecializationThatAddsDefaultParameterValues()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_default_parameter_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            public int Echo<int>(int value = 1) { return value + 1; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("inherits default arguments from its primary declaration",
            exception.Message);
    }

    [Fact]
    public void SelectsSpecializationIndependentlyOfDeclarationOrderAcrossFiles()
    {
        var specializationContext = CompilerTestHelper.Parse("""
            public int Echo <int>(int value) { return value + 1; }
            """);
        var callerContext = CompilerTestHelper.Parse("""
            public int Run() { return Echo(4); }
            """);
        var primaryContext = CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            """);
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_order");
        project.AddCompilationContext(specializationContext);
        project.AddCompilationContext(callerContext);
        project.AddCompilationContext(primaryContext);

        new SemanticBinder().Bind(project);

        var call = GetInvocation(callerContext.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single());
        Assert.Same(specializationContext.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(),
            call.TargetSymbol!.ImplementationDeclaration);
    }

    [Fact]
    public void OrdinaryOverloadWinsWithoutConsumingGenericSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_overload");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Choose<T>(T value) { return value; }
            public int Choose(int value) { return value + 10; }
            public int Choose <int>(int value) { return value + 1; }
            public int RunInferred() { return Choose(3); }
            public int RunExplicit() { return Choose<int>(3); }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>().ToArray();
        var inferred = GetInvocation(functions.Single(function =>
            function.Name == "RunInferred"));
        var explicitCall = GetInvocation(functions.Single(function =>
            function.Name == "RunExplicit"));
        var concreteOverload = functions.Single(function =>
            function.Name == "Choose" && !function.IsGenericSpecialization &&
            function.GenericTypeNames.Length == 0);

        Assert.Null(inferred.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(concreteOverload, inferred.TargetSymbol.Declaration);
        Assert.True(explicitCall.TargetSymbol!.ImplementationDeclaration!
            .IsGenericSpecialization);
    }

    [Fact]
    public void MatchesReferenceArrayConstructedAliasAndMultipleTypeArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_types");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            alias IntBox = Box<int>;
            alias IntAlias = IntBox;
            public T Keep<T>(T value) { return value; }
            public string Keep <string>(string value) { return "special"; }
            public int[] Keep <int[]>(int[] value) { return value; }
            public IntBox Keep <IntBox>(IntBox value) { return value; }
            public T First<T, U>(T value, U ignored) { return value; }
            public int First <int, string>(int value, string ignored) {
                return value + 2;
            }
            public T AliasKeep<T>(T value) { return value; }
            public IntBox AliasKeep<IntAlias>(IntBox value) { return value; }
            public string RunReference(string value) { return Keep(value); }
            public int[] RunArray(int[] value) { return Keep(value); }
            public Box<int> RunConstructed(Box<int> value) { return Keep(value); }
            public int RunMultiple(int value, string ignored) {
                return First(value, ignored);
            }
            public IntBox RunAlias(IntBox value) { return AliasKeep(value); }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>().ToArray();
        foreach (var name in new[]
        {
            "RunReference", "RunArray", "RunConstructed", "RunMultiple", "RunAlias",
        })
        {
            var call = GetInvocation(functions.Single(function => function.Name == name));
            Assert.True(call.TargetSymbol!.ImplementationDeclaration!.IsGenericSpecialization);
        }
        Assert.Equal(5, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void ForwardsFromOneSpecializationToAnotherGenericFunction()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            public int Echo <int>(int value) { return value + 1; }
            public T Forward<T>(T value) { return value; }
            public int Forward <int>(int value) { return Echo(value); }
            public int Run() { return Forward(3); }
            """));

        new SemanticBinder().Bind(project);
        var declarations = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>().ToArray();
        var runCall = GetInvocation(declarations.Single(function => function.Name == "Run"));

        Assert.True(runCall.TargetSymbol!.ImplementationDeclaration!.IsGenericSpecialization);
        var forwardBody = declarations.Single(function =>
            function.IsGenericSpecialization && function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardBody.Body!.Single()).Expression);
        Assert.True(forwardedCall.TargetSymbol!.ImplementationDeclaration!
            .IsGenericSpecialization);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void ResolvesForwardedCallsPerClosedGenericCallerInstance()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_open_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Echo<T>(T value) { return value; }
            public int Echo <int>(int value) { return value + 1; }
            public T Forward<T>(T value) { return Echo(value); }
            public int RunInt() { return Forward(4); }
            public string RunString() { return Forward("ok"); }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>().ToArray();
        var forwardCall = declarations.Single(function => function.Name == "Forward").Body!
            .OfType<ReturnStatement>().Single().Expression as InvocationExpression;
        Assert.NotNull(forwardCall);
        var callerInstances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Forward")
            .ToArray();
        Assert.Equal(2, callerInstances.Length);
        FunctionSymbol? intTarget = null;
        FunctionSymbol? stringTarget = null;
        foreach (var caller in callerInstances)
        {
            var closedTarget = forwardCall!.GetClosedGenericTarget(
                caller.SpecializationName!)!;
            Assert.NotNull(closedTarget.SpecializationName);
            if (caller.GenericTypeArguments["T"] is
                CxCompiler.Model.Types.BuiltInTypes.IntType)
            {
                Assert.True(closedTarget.ImplementationDeclaration?.IsGenericSpecialization);
                intTarget = closedTarget;
            }
            else
            {
                Assert.True(closedTarget.ImplementationDeclaration is null,
                    $"caller argument={caller.GenericTypeArguments["T"].FullName}; " +
                    $"callee argument={closedTarget.GenericTypeArguments["T"].FullName}");
                stringTarget = closedTarget;
            }
        }
        Assert.Equal(4, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-open-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_specialization_open_forwarding.c"));
            Assert.Contains($"return {intTarget!.SpecializationName}(value);", source);
            Assert.Contains($"return {stringTarget!.SpecializationName}(value);", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedGenericMethodCallsPerClosedCallerInstance()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_method_specialization_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Routes {
                public static T Echo<T>(T value) { return value; }
                public static int Echo <int>(int value) { return value + 1; }
                public static T Forward<T>(T value) { return Echo(value); }
            }
            public int RunInt() { return Routes.Forward(4); }
            public string RunString() { return Routes.Forward("ok"); }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single()
            .DeclarationScope.Declarations;
        var routeMethods = declarations.OfType<ClassDeclaration>().Single(type =>
                type.Name == "Routes").MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var forwardMethod = routeMethods.Single(function =>
            function.Name == "Forward" && !function.IsGenericSpecialization);
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        var callerInstances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Forward")
            .ToArray();
        Assert.Equal(2, callerInstances.Length);

        FunctionSymbol? intTarget = null;
        FunctionSymbol? stringTarget = null;
        foreach (var caller in callerInstances)
        {
            var target = forwardedCall.GetClosedGenericTarget(caller.SpecializationName!);
            Assert.NotNull(target);
            if (caller.GenericTypeArguments["T"] is
                CxCompiler.Model.Types.BuiltInTypes.IntType)
            {
                Assert.True(target.ImplementationDeclaration?.IsGenericSpecialization);
                intTarget = target;
            }
            else
            {
                Assert.Null(target.ImplementationDeclaration);
                stringTarget = target;
            }
        }
        Assert.NotNull(intTarget);
        Assert.NotNull(stringTarget);
        Assert.Equal(4, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-method-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_method_specialization_forwarding.c"));
            Assert.Contains($"return {intTarget!.SpecializationName}(value);", source);
            Assert.Contains($"return {stringTarget!.SpecializationName}(value);", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedInstanceGenericMethodCallsWithTheCurrentReceiver()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_instance_method_specialization_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Routes {
                public T Echo<T>(T value) { return value; }
                public int Echo<int>(int value) { return value + 1; }
                public T Forward<T>(T value) { return Echo(value); }
            }
            public int RunInt() {
                Routes routes = new Routes();
                return routes.Forward(4);
            }
            public string RunString() {
                Routes routes = new Routes();
                return routes.Forward("ok");
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var routes = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Routes");
        var forwardMethod = routes.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function =>
                function.Name == "Forward" && !function.IsGenericSpecialization);
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        var callers = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
        Assert.Equal(2, callers.Length);

        FunctionSymbol? intTarget = null;
        FunctionSymbol? stringTarget = null;
        foreach (var caller in callers)
        {
            var target = forwardedCall.GetClosedGenericTarget(caller.SpecializationName!);
            Assert.NotNull(target);
            Assert.False(target.Declaration!.IsStatic);
            if (caller.GenericTypeArguments["T"] is
                CxCompiler.Model.Types.BuiltInTypes.IntType)
            {
                Assert.True(target.ImplementationDeclaration?.IsGenericSpecialization);
                intTarget = target;
            }
            else
            {
                Assert.Null(target.ImplementationDeclaration);
                stringTarget = target;
            }
        }

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-instance-method-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_instance_method_specialization_forwarding.c"));
            Assert.Contains($"return {intTarget!.SpecializationName}(__this, value);", source);
            Assert.Contains($"return {stringTarget!.SpecializationName}(__this, value);", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedInstanceGenericMethodCallsOnAReceiverParameter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_instance_receiver_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class EchoRoutes {
                public T Echo<T>(T value) { return value; }
                public int Echo<int>(int value) { return value + 1; }
            }
            public class Forwarder {
                public T Forward<T>(EchoRoutes routes, T value) {
                    return routes.Echo(value);
                }
            }
            public int RunInt() {
                EchoRoutes routes = new EchoRoutes();
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, 4);
            }
            public string RunString() {
                EchoRoutes routes = new EchoRoutes();
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, "ok");
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var forwarder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Forwarder");
        var forwardMethod = forwarder.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        Assert.IsType<IdentifierExpression>(forwardedCall.Receiver);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-instance-receiver-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_instance_receiver_forwarding.c"));
            var callers = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
            Assert.Equal(2, callers.Length);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                Assert.Contains(
                    $"return {target.SpecializationName}(routes, value);", source);
            }
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration?.IsGenericSpecialization == true);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration is null);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedInstanceGenericMethodCallsOnAComputedReceiverOnce()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_instance_computed_receiver_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class EchoRoutes {
                public static int FactoryCalls = 0;
                public T Echo<T>(T value) { return value; }
                public int Echo<int>(int value) { return value + 1; }
                public static EchoRoutes Create() {
                    FactoryCalls = FactoryCalls + 1;
                    return new EchoRoutes();
                }
            }
            public class Forwarder {
                public T Forward<T>(T value) {
                    return EchoRoutes.Create().Echo(value);
                }
            }
            public int RunInt() {
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(4);
            }
            public string RunString() {
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward("ok");
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var forwarder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Forwarder");
        var forwardMethod = forwarder.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        Assert.IsType<InvocationExpression>(forwardedCall.Receiver);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-computed-receiver-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_instance_computed_receiver_forwarding.c"));
            var callers = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
            Assert.Equal(2, callers.Length);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                var emittedCall = Assert.Single(source.Split('\n'), line =>
                    line.Contains($"return {target.SpecializationName}(",
                        StringComparison.Ordinal));
                Assert.Contains("Create", emittedCall, StringComparison.Ordinal);
                Assert.Equal(1, emittedCall.Split("Create", StringSplitOptions.None).Length - 1);
            }
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration?.IsGenericSpecialization == true);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration is null);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedInstanceGenericMethodCallsOnAByValueStructParameter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_struct_parameter_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct EchoRoutes {
                public int Calls;
                public T Echo<T>(T value) { return value; }
                public int Echo<int>(int value) {
                    Calls += 1;
                    return Calls + value;
                }
            }
            public class Forwarder {
                public T Forward<T>(EchoRoutes routes, T value) {
                    return routes.Echo(value);
                }
            }
            public int RunInt() {
                EchoRoutes routes = default;
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, 4);
            }
            public string RunString() {
                EchoRoutes routes = default;
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, "ok");
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var forwarder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Forwarder");
        var forwardMethod = forwarder.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        Assert.IsType<IdentifierExpression>(forwardedCall.Receiver);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-struct-parameter-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_struct_parameter_forwarding.c"));
            var callers = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
            Assert.Equal(2, callers.Length);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                Assert.Contains(
                    $"return {target.SpecializationName}(&(routes), value);", source);
            }
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration?.IsGenericSpecialization == true);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration is null);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedInstanceGenericMethodCallsOnAComputedStructReceiverOnce()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_computed_struct_receiver_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct EchoRoutes {
                public int Calls;
                public static int FactoryCalls;
                public static EchoRoutes Create() {
                    FactoryCalls += 1;
                    return default;
                }
                public T Echo<T>(T value) { return value; }
                public int Echo<int>(int value) {
                    Calls += 1;
                    return Calls + value;
                }
            }
            public class Forwarder {
                public T Forward<T>(T value) {
                    return EchoRoutes.Create().Echo(value);
                }
            }
            public int RunInt() {
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(4);
            }
            public string RunString() {
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward("ok");
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var forwarder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Forwarder");
        var forwardMethod = forwarder.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        Assert.IsType<InvocationExpression>(forwardedCall.Receiver);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-computed-struct-receiver-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_computed_struct_receiver_forwarding.c"));
            var callers = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
            Assert.Equal(2, callers.Length);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                Assert.Contains($"return {target.SpecializationName}(&__cx_forward_receiver, value);",
                    source);
            }
            Assert.Equal(2, source.Split("__cx_forward_receiver =", StringSplitOptions.None).Length - 1);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration?.IsGenericSpecialization == true);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration is null);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedGenericCallsWithMultipleMatchingTypeParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_multi_argument_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class EchoRoutes {
                public T Echo<T, U>(T value, U other) { return value; }
                public int Echo<int, string>(int value, string other) {
                    return value + 1;
                }
            }
            public class Forwarder {
                public T Forward<T, U>(EchoRoutes routes, T value, U other) {
                    return routes.Echo(value, other);
                }
            }
            public int RunIntString() {
                EchoRoutes routes = new EchoRoutes();
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, 4, "ignored");
            }
            public string RunStringInt() {
                EchoRoutes routes = new EchoRoutes();
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, "ok", 4);
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var forwarder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Forwarder");
        var forwardMethod = forwarder.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        Assert.Equal(2, forwardedCall.Arguments.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-multi-argument-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_multi_argument_forwarding.c"));
            var callers = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
            Assert.Equal(2, callers.Length);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                var emittedCall = Assert.Single(source.Split('\n'), line =>
                    line.Contains($"return {target.SpecializationName}(",
                        StringComparison.Ordinal));
                Assert.Contains("routes, value, other", emittedCall, StringComparison.Ordinal);
            }
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration?.IsGenericSpecialization == true);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration is null);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesForwardedGenericCallsWithComputedConcreteArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_computed_argument_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class EchoRoutes {
                public T Echo<T, U>(T value, U other) { return value; }
                public int Echo<int, int>(int value, int other) {
                    return value + other;
                }
            }
            public class Forwarder {
                public T Forward<T>(EchoRoutes routes, T value, int offset) {
                    return routes.Echo(value, offset + 1);
                }
            }
            public int RunInt() {
                EchoRoutes routes = new EchoRoutes();
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, 4, 2);
            }
            public string RunString() {
                EchoRoutes routes = new EchoRoutes();
                Forwarder forwarder = new Forwarder();
                return forwarder.Forward(routes, "ok", 2);
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var forwarder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Forwarder");
        var forwardMethod = forwarder.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(forwardMethod.Body!.Single()).Expression);
        Assert.IsType<BinaryExpression>(forwardedCall.Arguments[1]);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-computed-argument-forwarding-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_computed_argument_forwarding.c"));
            var callers = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
            Assert.Equal(2, callers.Length);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                var emittedCall = Assert.Single(source.Split('\n'), line =>
                    line.Contains($"return {target.SpecializationName}(",
                        StringComparison.Ordinal));
                Assert.Contains("routes, value", emittedCall, StringComparison.Ordinal);
                Assert.Contains("offset", emittedCall, StringComparison.Ordinal);
                Assert.Contains("+", emittedCall, StringComparison.Ordinal);
                Assert.Equal(1, emittedCall.Split("offset", StringSplitOptions.None).Length - 1);
            }
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration?.IsGenericSpecialization == true);
            Assert.Contains(callers, caller =>
                forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)!
                    .ImplementationDeclaration is null);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SelectsGenericSpecializationWhenBindingDelegateFunctionValue()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_delegate_binding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public delegate int IntTransform(int value);
            public delegate long LongTransform(long value);
            public T Echo<T>(T value) { return value; }
            public int Echo <int>(int value) { return value + 1; }
            public int Run() {
                IntTransform transform = Echo;
                LongTransform longTransform = Echo;
                return transform(4) == 5 && longTransform(4L) == 4L ? 0 : 1;
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var local = Assert.IsType<LocalVariableDeclarationStatement>(run.Body![0]);
        var functionValue = Assert.IsType<IdentifierExpression>(
            local.Declarators.Single().Initializer);
        var symbol = functionValue.FunctionValueSymbol!;
        Assert.True(symbol.ImplementationDeclaration?.IsGenericSpecialization);
        var fallbackLocal = Assert.IsType<LocalVariableDeclarationStatement>(run.Body[1]);
        var fallbackValue = Assert.IsType<IdentifierExpression>(
            fallbackLocal.Declarators.Single().Initializer);
        var fallbackSymbol = fallbackValue.FunctionValueSymbol!;
        Assert.Null(fallbackSymbol.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-specialization-delegate-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_specialization_delegate_binding.c"));
            Assert.Contains($"{symbol.SpecializationName}(__cx_arg_0)", source);
            Assert.Contains($"{fallbackSymbol.SpecializationName}(__cx_arg_0)", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SelectsSpecializedGenericMethodsWhenBindingDelegateValues()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_method_specialization_delegate_binding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public delegate int IntTransform(int value);
            public class Counter {
                public int Value = 5;
                public T Adjust<T>(T value) { return value; }
                public int Adjust <int>(int value) { return Value + value; }
                public static T Offset<T>(T value) { return value; }
                public static int Offset <int>(int value) { return value + 2; }
            }
            public int Run() {
                Counter counter = new Counter();
                IntTransform instanceTransform = counter.Adjust;
                IntTransform staticTransform = Counter.Offset;
                return instanceTransform(3) + staticTransform(4);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var instanceLocal = Assert.IsType<LocalVariableDeclarationStatement>(run.Body![1]);
        var instanceValue = Assert.IsType<MemberAccessExpression>(
            instanceLocal.Declarators.Single().Initializer);
        var instanceSymbol = instanceValue.FunctionValueSymbol!;
        Assert.True(instanceSymbol.ImplementationDeclaration?.IsGenericSpecialization);

        var staticLocal = Assert.IsType<LocalVariableDeclarationStatement>(run.Body[2]);
        var staticValue = Assert.IsType<MemberAccessExpression>(
            staticLocal.Declarators.Single().Initializer);
        var staticSymbol = staticValue.FunctionValueSymbol!;
        Assert.True(staticSymbol.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-method-specialization-delegate-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_method_specialization_delegate_binding.c"));
            Assert.Contains($"{instanceSymbol.SpecializationName}(", source);
            Assert.Contains($"{staticSymbol.SpecializationName}(__cx_arg_0)", source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RecursiveCallTargetsTheSameClosedSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_specialization_recursion");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Step<T>(T value) { return value; }
            public int Step <int>(int value) {
                if (value == 0) return 42;
                return Step(value - 1);
            }
            public int Run() { return Step(3); }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>().ToArray();
        var specialization = functions.Single(function => function.IsGenericSpecialization);
        var recursiveCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(specialization.Body!.Last()).Expression);
        var runCall = GetInvocation(functions.Single(function => function.Name == "Run"));

        Assert.Same(specialization, recursiveCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(specialization, runCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Single(project.GenericFunctionInstances);
    }

    [Fact]
    public void SpecializesStaticAndInstanceGenericMethodsOnNonGenericClasses()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_method_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Counter {
                public int Value = 10;
                public T Adjust<T>(T value) { return value; }
                public int Adjust <int>(int value) { return Value + value; }
                public static T Offset<T>(T value) { return value; }
                public static int Offset <int>(int value) { return value + 3; }
            }
            public struct StructCounter {
                public int Value;
                public T Add<T>(T value) { return value; }
                public int Add <int>(int value) {
                    Value += value;
                    return Value;
                }
            }
            public int RunInstance() {
                Counter counter = new Counter();
                return counter.Adjust(2);
            }
            public int RunStatic() { return Counter.Offset<int>(4); }
            public long RunFallback() {
                Counter counter = new Counter();
                return counter.Adjust<long>(9L);
            }
            public int RunStruct() {
                StructCounter counter = default;
                counter.Value = 5;
                return counter.Add(2);
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single()
            .DeclarationScope.Declarations;
        var instanceCall = GetInvocation(declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunInstance"));
        var staticCall = GetInvocation(declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunStatic"));
        var fallbackCall = GetInvocation(declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunFallback"));
        var structCall = GetInvocation(declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "RunStruct"));
        var specializations = declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "Counter")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Where(function => function.IsGenericSpecialization).ToArray();
        var structSpecialization = declarations.OfType<ClassDeclaration>()
            .Single(type => type.Name == "StructCounter")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);

        Assert.Same(specializations.Single(function => function.Name == "Adjust"),
            instanceCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(specializations.Single(function => function.Name == "Offset"),
            staticCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Null(fallbackCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(structSpecialization, structCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Equal(4, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void SelectsStaticGenericMethodSpecializationThroughClosedGenericOwner()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_static_owner_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility<T> {
                public static U Select<U>(U value) { return value; }
                public static int Select <int>(int value) { return value + 20; }
            }
            public int Run() { return Utility<string>.Select<int>(3); }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = GetInvocation(run);
        var implementation = Assert.IsType<FunctionDeclaration>(
            call.TargetSymbol!.ImplementationDeclaration);
        Assert.True(implementation.IsGenericSpecialization);
        Assert.Equal("Utility", implementation.ParentClassDeclaration?.Name);
        Assert.NotNull(call.TargetSymbol.ClosedContainingType?.ConstructedIdentity);
        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Same(implementation, instance.ImplementationDeclaration);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-static-owner-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_static_owner_specialization.c"));
            Assert.Contains($"return {instance.SpecializationName}(3);", source);
            Assert.Contains("+ 20", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StaticGenericMethodSpecializationUsesClosedOwnerInSignatureAndIdentity()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_static_owner_identity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class StaticResult<T> {
                public static U Select<U>(T owner, U value) { return value; }
                public static int Select<int>(T owner, int value) { return value + 20; }
            }
            public int RunText() { return StaticResult<string>.Select<int>("text", 3); }
            public int RunInt() { return StaticResult<int>.Select<int>(17, 3); }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var calls = declarations.Select(GetInvocation).ToArray();
        Assert.All(calls, call => Assert.True(
            call.TargetSymbol!.ImplementationDeclaration?.IsGenericSpecialization));
        Assert.Same(BuiltInSystemTypes.String, calls[0].TargetSymbol!.ParameterTypes[0]);
        Assert.Same(BuiltInSystemTypes.Int, calls[1].TargetSymbol!.ParameterTypes[0]);
        Assert.Equal(2, calls.Select(call => call.TargetSymbol!.SpecializationName)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, calls.Select(call => call.TargetSymbol!.ClosedContainingType?
            .ConstructedIdentity?.CanonicalName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-static-owner-identity-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_static_owner_identity.c"));
            Assert.All(project.GenericFunctionInstances, instance =>
                Assert.Contains(instance.SpecializationName!, source));
            Assert.Contains("+ 20", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EnforcesContainingTypeConstraintsOnClosedStaticGenericOwner()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_static_owner_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class ReferenceUtility<T> where T : class {
                public static U Keep<U>(U value) { return value; }
            }
            public int Run() { return ReferenceUtility<int>.Keep<int>(1); }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("reference-type constraint for parameter 'T'", exception.Message);
    }

    [Fact]
    public void BindsGenericMethodSpecializationAcrossPartialGenericClassDeclarations()
    {
        var specializationContext = CompilerTestHelper.Parse("""
            public partial class PartialUtility<TState> {
                public int Select<int>(TState state, int value) { return value + 1; }
            }
            """);
        var callerContext = CompilerTestHelper.Parse("""
            public int Run() {
                PartialUtility<string> utility = new PartialUtility<string>();
                return utility.Select<int>("state", 4);
            }
            """);
        var primaryContext = CompilerTestHelper.Parse("""
            public partial class PartialUtility<TState> {
                public U Select<U>(TState state, U value) { return value; }
            }
            """);
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_specialization_partial_generic_class");
        project.AddCompilationContext(specializationContext);
        project.AddCompilationContext(callerContext);
        project.AddCompilationContext(primaryContext);

        new SemanticBinder().Bind(project);

        var declaration = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<ClassDeclaration>()
            .Single(type => type.Name == "PartialUtility");
        var specialization = declaration.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);
        var call = GetInvocation(callerContext.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single());
        Assert.Same(specialization, call.TargetSymbol!.ImplementationDeclaration);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-specialization-partial-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_specialization_partial_generic_class.c"));
            Assert.Contains("+ 1", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_method_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box { }
            public extension BoxExtensions : Box {
                public T Pick<T>(T value) const { return value; }
                public int Pick<int>(int value) const { return value + 5; }
            }
            public int Run() { return new Box().Pick<int>(3); }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = GetInvocation(run);
        Assert.True(call.TargetSymbol!.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Single(project.GenericFunctionInstances);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-specialization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_method_specialization.c"));
            Assert.Contains("+ 5", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecializationForClosedGenericTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_method_closed_target_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> { }
            public extension BoxExtensions<T> : Box<T> {
                public U Pick<U>(T state, U value) const { return value; }
                public int Pick<int>(T state, int value) const { return value + 6; }
            }
            public int Run() {
                Box<string> box = new Box<string>();
                return box.Pick<int>("state", 3);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = GetInvocation(run);
        Assert.True(call.TargetSymbol!.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.NotNull(call.TargetSymbol.ClosedContainingType?.ConstructedIdentity);
        Assert.Single(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "Pick");

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-closed-target-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_method_closed_target_specialization.c"));
            Assert.Contains("+ 6", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecializationForPrimitiveTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_primitive_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public extension IntExtensions : int {
                public U Echo<U>(U value) const { return value; }
                public int Echo<int>(int value) const { return value + 5; }
            }
            public int Run() {
                int receiver = 1;
                return receiver.Echo<int>(37);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = GetInvocation(run);
        var specialization = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>().Single()
            .Members.MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);

        Assert.Same(specialization, call.TargetSymbol!.ImplementationDeclaration);
        Assert.Single(project.GenericFunctionInstances);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-primitive-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_primitive_specialization.c"));
            Assert.Contains(call.TargetSymbol.SpecializationName!, source);
            Assert.Contains("value + 5", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecializationForStructTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_struct_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Counter { public int Value; }
            public extension CounterExtensions : Counter {
                public U Echo<U>(U value) const { return value; }
                public int Echo<int>(int value) const { return value + 7; }
            }
            public int Run() {
                Counter receiver = default;
                if (receiver.Echo<long>(11L) != 11L) return 1;
                return receiver.Echo<int>(35);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var fallbackCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<BinaryExpression>(run.Body!.OfType<IfStatement>().Single().Condition)
                .Left);
        var returnCall = GetInvocation(run);
        var extension = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>().Single();
        var specialization = extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.IsGenericSpecialization);

        Assert.Null(fallbackCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(specialization, returnCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-struct-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_struct_specialization.c"));
            Assert.Contains(returnCall.TargetSymbol.SpecializationName!, source);
            Assert.Contains("value + 7", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecializationForInterfaceTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_interface_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IValue { int Read() const; }
            public class Value : IValue { public int Read() const { return 1; } }
            public extension ValueExtensions : IValue {
                public U Echo<U>(U value) const { return value; }
                public int Echo<int>(int value) const { return value + 8; }
            }
            public int Run() {
                IValue receiver = new Value();
                if (receiver.Echo<long>(11L) != 11L) return 1;
                return receiver.Echo<int>(34);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var fallbackCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<BinaryExpression>(run.Body!.OfType<IfStatement>().Single().Condition)
                .Left);
        var returnCall = GetInvocation(run);
        var extension = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>().Single();
        var specialization = extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.IsGenericSpecialization);

        Assert.Null(fallbackCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(specialization, returnCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-interface-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_interface_specialization.c"));
            Assert.Contains(returnCall.TargetSymbol.SpecializationName!, source);
            Assert.Contains("value + 8", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecializationForEnumTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_enum_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public enum Shade { Dark, Light }
            public extension ShadeExtensions : Shade {
                public U Echo<U>(U value) const { return value; }
                public int Echo<int>(int value) const { return value + 9; }
            }
            public int Run() {
                Shade receiver = Shade.Light;
                if (receiver.Echo<long>(11L) != 11L) return 1;
                return receiver.Echo<int>(33);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var fallbackCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<BinaryExpression>(run.Body!.OfType<IfStatement>().Single().Condition)
                .Left);
        var returnCall = GetInvocation(run);
        var extension = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>().Single();
        var specialization = extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.IsGenericSpecialization);

        Assert.Null(fallbackCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(specialization, returnCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-enum-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_enum_specialization.c"));
            Assert.Contains(returnCall.TargetSymbol.SpecializationName!, source);
            Assert.Contains("value + 9", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsGenericExtensionMethodSpecializationForConstructedInterfaceTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_closed_interface_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IBox<T> { T Get() const; }
            public extension BoxExtensions<T> : IBox<T> {
                public U Echo<U>(T state, U value) const { return value; }
                public int Echo<int>(T state, int value) const { return value + 10; }
            }
            public int Run() {
                IBox<string> receiver = null;
                if (receiver.Echo<long>("state", 23L) != 23L) return 1;
                return receiver.Echo<int>("state", 32);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var fallbackCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<BinaryExpression>(run.Body!.OfType<IfStatement>().Single().Condition)
                .Left);
        var returnCall = GetInvocation(run);
        var extension = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>().Single();
        var specialization = extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.IsGenericSpecialization);

        Assert.Null(fallbackCall.TargetSymbol!.ImplementationDeclaration);
        Assert.Same(specialization, returnCall.TargetSymbol!.ImplementationDeclaration);
        Assert.NotNull(returnCall.TargetSymbol.ClosedContainingType?.ConstructedIdentity);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-closed-interface-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_closed_interface_specialization.c"));
            Assert.Contains(returnCall.TargetSymbol.SpecializationName!, source);
            Assert.Contains("value + 10", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SpecializesGenericMethodsOnConcreteArrayExtensionTargets()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_extension_array_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public extension IntArrayExtensions : int[] {
                public U Echo<U>(U value) const { return value; }
                public int Echo<int>(int value) const { return value + 6; }
            }
            public int Run() {
                int[] values = new int[1];
                return values.Echo(3);
            }
            """));

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var invocation = GetInvocation(run);
        var extension = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>().Single();
        var specialization = extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.IsGenericSpecialization);
        Assert.Same(specialization, invocation.TargetSymbol!.ImplementationDeclaration);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-extension-array-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_extension_array_specialization.c"));
            Assert.Contains(invocation.TargetSymbol.SpecializationName!, source);
            Assert.Contains("value + 6", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static InvocationExpression GetInvocation(FunctionDeclaration function) =>
        Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(function.Body!
                .OfType<ReturnStatement>().Single()).Expression);
}
