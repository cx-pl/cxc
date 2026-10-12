using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ConceptDeclarationTests
{
    [Fact]
    public void BindsAndEmitsConceptPropertyCompoundAssignments()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_property_compound_assignment");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CValue {
                int Value { const get; set; }
                int Adjustment { const get; }
            }
            public class ClassValue {
                public int Value { const get; set; }
                public int Adjustment { const get { return 2; } }
                public constructor(int value) { Value = value; }
            }
            public struct StructValue {
                public int Value { const get; set; }
                public int Adjustment { const get { return 2; } }
                public constructor(int value) { Value = value; }
            }
            public static class Utils {
                public static void Update<T>(T item, int amount) : T = CValue {
                    item.Value += amount;
                    item.Value -= amount;
                    item.Value *= amount;
                    item.Value /= amount;
                    item.Value %= amount;
                }
                public static void AddAdjustment<T>(T item, int amount) : T = CValue {
                    item.Value += item.Adjustment + amount;
                }
            }
            public int Run() {
                ClassValue classValue = new ClassValue(5);
                StructValue structValue = new StructValue(7);
                Utils.Update(classValue, 3);
                Utils.Update(structValue, 4);
                Utils.AddAdjustment(classValue, 1);
                Utils.AddAdjustment(structValue, 1);
                return classValue.Value;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-property-compound-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "concept_property_compound_assignment.c"));

            Assert.Contains(
                "__cx_concept_property_value_0 = __cx_concept_property_value_0 + amount;",
                source);
            Assert.Contains(
                "__cx_concept_property_value_1 = __cx_concept_property_value_1 - amount;",
                source);
            Assert.Contains(
                "__cx_concept_property_value_2 = __cx_concept_property_value_2 * amount;",
                source);
            Assert.Contains(
                "__cx_concept_property_value_3 = __cx_concept_property_value_3 / amount;",
                source);
            Assert.Contains(
                "__cx_concept_property_value_4 = __cx_concept_property_value_4 % amount;",
                source);
            Assert.Contains("__cx_concept_property_value_0 = __cx_concept_property_value_0 +", source);
            Assert.Contains("Adjustment", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsRequiredMethodCallsInConceptCompoundAssignments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_property_compound_method_call");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CAdjustable {
                int Value { const get; set; }
                int Adjustment(int amount) const;
            }
            public struct Adjuster {
                public int Value { const get; set; }
                public int Adjustment(int amount) const { return amount + 2; }
                public constructor(int value) { Value = value; }
            }
            public static class Utils {
                public static void Update<T>(T item, int amount)
                    : T = CAdjustable {
                    item.Value += item.Adjustment(amount) + 1;
                }
            }
            public int Run() {
                Adjuster item = new Adjuster(3);
                Utils.Update(item, 4);
                return item.Value;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-compound-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "concept_property_compound_method_call.c"));

            Assert.Contains("Adjustment", source);
            Assert.Contains("__cx_concept_property_value_0 +", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsNestedRequiredMethodCallsInConceptCompoundAssignments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_property_compound_nested_method_call");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CAdjustable {
                int Value { const get; set; }
                int Adjustment(int amount) const;
            }
            public struct Adjuster {
                public int Value { const get; set; }
                public int Adjustment(int amount) const { return amount; }
                public constructor(int value) { Value = value; }
            }
            public static class Utils {
                public static void Update<T>(T item, int amount)
                    : T = CAdjustable {
                    item.Value += item.Adjustment(item.Adjustment(amount));
                }
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-compound-nested-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "concept_property_compound_nested_method_call.c"));

            Assert.True(source.Split("Adjustment", StringSplitOptions.None).Length - 1 >= 2);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CombinesRequiredMethodResultsInAConceptGenericReturn()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_method_result_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable { int Read(int amount) const; }
            public struct Reader {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Read(int amount) const { return amount; }
            }
            public static class Utils {
                public static int Sum<T>(T value, int offset) : T = CReadable {
                    return value.Read(value.Read(offset)) + value.Read(offset);
                }
            }
            public int Run() {
                Reader value = new Reader(4);
                return Utils.Sum(value, 3);
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-method-expression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "concept_method_result_expression.c"));

            Assert.Contains("(&value, ", source);
            Assert.Contains(" + ", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsCompoundAssignmentToWriteOnlyConceptProperty()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_property_compound_requires_getter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CWriteOnly { int Value { set; } }
            public struct Value { public int Value { get; set; } }
            public static class Utils {
                public static void Add<T>(T item, int amount) : T = CWriteOnly {
                    item.Value += amount;
                }
            }
            public void Run() {
                Value item = default;
                Utils.Add(item, 1);
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("Property", error.Message);
    }

    [Fact]
    public void ParsesCompileTimeOperatorRequirements()
    {
        var context = CompilerTestHelper.Parse("""
            concept CEquatable {
                bool operator ==(const CEquatable other) const;
            }
            public concept CComparable {
                bool operator >(const CEquatable other) const;
            }
            """);

        var concepts = context.DeclarationScope.Declarations
            .OfType<ConceptDeclaration>().ToArray();
        Assert.Equal(["CEquatable", "CComparable"],
            concepts.Select(concept => concept.Name).ToArray());
        Assert.Equal(Visibility.Private, concepts[0].Visibility);
        Assert.Equal(Visibility.Public, concepts[1].Visibility);

        var equality = Assert.Single(concepts[0].OperatorRequirements);
        Assert.Equal("==", equality.OperatorToken);
        Assert.True(equality.Const);
        Assert.Null(equality.Body);
        var parameter = Assert.Single(equality.Parameters);
        var parameterType = Assert.IsType<ConstType>(parameter.ParameterType);
        Assert.Equal("CEquatable", Assert.IsType<NamedType>(parameterType.UnderlyingType).Name);
    }

    [Fact]
    public void ParsesConceptInheritance()
    {
        var context = CompilerTestHelper.Parse("""
            public concept CReadable {
                int Read(int scale) const;
                bool operator >(const CReadable other) const;
                void AddName(int amount);
            }
            public concept CHasName {
                int Name { set; }
            }
            public concept CMutableReadable : CReadable, CHasName {
                int Value { set; }
            }
            """);

        var concept = Assert.Single(context.DeclarationScope.Declarations
            .OfType<ConceptDeclaration>(), declaration => declaration.Name == "CMutableReadable");
        Assert.Equal(["CReadable", "CHasName"], concept.BaseConceptNames
            .Select(baseConcept => baseConcept.ToString()).ToArray());
        Assert.Equal("Value", Assert.Single(concept.PropertyRequirements).Name);
    }

    [Fact]
    public void InheritsConceptRequirementsWhenSpecializingGenericFunctions()
    {
        var project = CxProject.CreateDefaultApplicationProject("inherited_concept_requirements");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable {
                int Read(int scale) const;
                bool operator >(const CReadable other) const;
                void AddName(int amount);
            }
            public concept CHasName {
                int Name { set; }
            }
            public concept CMutableReadable : CReadable, CHasName {
                int Value { set; }
            }
            public class ClassValue {
                public int Value { const get; set; }
                public int Name { get; set; }
                public constructor(int value) { Value = value; }
                public int Read(int scale) const { return Value * scale; }
                public void AddName(int amount) { Name = Name + amount; }
                public bool operator >(const CReadable other) const {
                    return Value > other.Value;
                }
            }
            public struct StructValue {
                public int Value { const get; set; }
                public int Name { get; set; }
                public constructor(int value) { Value = value; }
                public int Read(int scale) const { return Value * scale; }
                public void AddName(int amount) { Name = Name + amount; }
                public bool operator >(const CReadable other) const {
                    return Value > other.Value;
                }
            }
            public static class Utils {
                public static void Set<T>(T value, int next, bool useMethod) : T = CMutableReadable {
                    bool shouldAdd = useMethod;
                    if (!shouldAdd) return;
                    if (shouldAdd) {
                        value.Value = next;
                        value.AddName(next);
                    } else {
                        value.Value = next + 1;
                        value.Name = next;
                    }
                    switch (next) {
                        case 0:
                            value.Value = next;
                            break;
                        default:
                            value.Value = next + 1;
                            break;
                    }
                }
                public static int Read<T>(T value, int scale) : T = CMutableReadable {
                    return value.Read(scale);
                }
                public static bool Greater<T>(T first, T second) : T = CMutableReadable {
                    return first > second;
                }
            }
            public int Main() {
                ClassValue classValue = new ClassValue(3);
                ClassValue otherClassValue = new ClassValue(2);
                StructValue structValue = new StructValue(4);
                StructValue otherStructValue = new StructValue(1);
                Utils.Set(classValue, 5, true);
                Utils.Set(structValue, 6, false);
                if (!Utils.Greater(classValue, otherClassValue) ||
                    !Utils.Greater(structValue, otherStructValue)) return -1;
                return Utils.Read(classValue, 2) + Utils.Read(structValue, 3);
            }
            """));

        new SemanticBinder().Bind(project);

        var setInstances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Set").ToArray();
        var readInstances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Read").ToArray();
        var greaterInstances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Greater").ToArray();
        Assert.Equal(2, setInstances.Length);
        Assert.Equal(2, readInstances.Length);
        Assert.Equal(2, greaterInstances.Length);
        Assert.All(setInstances, instance =>
        {
            Assert.Contains("property:Value:set", instance.ConceptOperatorBindings.Keys);
            Assert.Contains("property:Name:set", instance.ConceptOperatorBindings.Keys);
            Assert.Contains("method:AddName(Int)", instance.ConceptOperatorBindings.Keys);
        });
        Assert.All(readInstances, instance =>
            Assert.Contains("method:Read(Int)", instance.ConceptOperatorBindings.Keys));
        Assert.All(greaterInstances, instance =>
            Assert.Contains(">", instance.ConceptOperatorBindings.Keys));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-inheritance-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "inherited_concept_requirements.c"));
            Assert.DoesNotContain("_unknowntype_", source);
            Assert.Contains("Read", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsUnresolvedAndCyclicConceptInheritance()
    {
        var unresolved = CxProject.CreateDefaultApplicationProject("unknown_concept_base");
        unresolved.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CChild : CMissing { }
            """));
        var unresolvedError = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(unresolved));
        Assert.Contains("Cannot resolve base concept 'CMissing'", unresolvedError.Message);

        var cyclic = CxProject.CreateDefaultApplicationProject("cyclic_concept_bases");
        cyclic.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CFirst : CSecond { }
            concept CSecond : CFirst { }
            """));
        var cycleError = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(cyclic));
        Assert.Contains("Concept inheritance cycle", cycleError.Message);
    }

    [Fact]
    public void RejectsRedeclaredInheritedConceptRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "duplicate_inherited_concept_requirement");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable {
                int Read(int scale) const;
            }
            concept CFastReadable : CReadable {
                int Read(int scale) const;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("redeclares an inherited method requirement", error.Message);
    }

    [Fact]
    public void DeduplicatesDiamondConceptRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject("diamond_concept_requirements");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable { int Read(int scale) const; }
            concept CLeftReadable : CReadable { }
            concept CRightReadable : CReadable { }
            concept CDiamondReadable : CLeftReadable, CRightReadable { }
            public class Value {
                public int Number;
                public constructor(int number) { Number = number; }
                public int Read(int scale) const { return Number * scale; }
            }
            public static class Utils {
                public static int Read<T>(T value, int scale) : T = CDiamondReadable {
                    return value.Read(scale);
                }
            }
            public int Main() {
                Value value = new Value(21);
                return Utils.Read(value, 2);
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "Read");
        Assert.Contains("method:Read(Int)", instance.ConceptOperatorBindings.Keys);
    }

    [Fact]
    public void RejectsConflictingRequirementsFromMultipleConceptBases()
    {
        var project = CxProject.CreateDefaultApplicationProject("conflicting_concept_bases");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CLeft { int Read(int scale) const; }
            concept CRight { int Read(int scale) const; }
            concept CCombined : CLeft, CRight { }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("inherits conflicting method requirements", error.Message);

        var propertyProject = CxProject.CreateDefaultApplicationProject(
            "conflicting_concept_property_bases");
        propertyProject.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CLeft { int Value { set; } }
            concept CRight { int Value { set; } }
            concept CCombined : CLeft, CRight { }
            """));

        var propertyError = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(propertyProject));

        Assert.Contains("inherits conflicting property requirements", propertyError.Message);
    }

    [Fact]
    public void RejectsFunctionBodiesInConceptRequirements()
    {
        var error = Assert.Throws<CompilationErrorException>(() =>
            CompilerTestHelper.Parse("""
                concept CEquatable {
                    bool operator ==(const CEquatable other) const { return true; }
                }
                """));

        Assert.Contains("requirements cannot have bodies", error.Message);
    }

    [Fact]
    public void ParsesConceptConstraintsOnGenericFunctions()
    {
        var context = CompilerTestHelper.Parse("""
            public concept CEquatable {
                bool operator ==(const CEquatable other) const;
            }
            public concept CComparable {
                bool operator >(const CEquatable other) const;
            }
            public T Max<T>(T first, T second)
                : T = CEquatable, CComparable {
                return first;
            }
            """);

        var function = Assert.Single(context.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>());
        var constraint = Assert.Single(function.ConceptConstraints);
        Assert.Equal("T", constraint.ParameterName);
        Assert.Equal(["CEquatable", "CComparable"],
            constraint.Concepts.Select(concept => concept.ToString()).ToArray());
    }

    [Fact]
    public void ParsesConceptConstraintsForMultipleGenericParameters()
    {
        var context = CompilerTestHelper.Parse("""
            public concept CComparable {
                bool operator >(const CComparable other) const;
            }
            public T First<T, U>(T first, U second)
                : T = CComparable : U = CComparable {
                return first;
            }
            """);

        var function = Assert.Single(context.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>());
        Assert.Equal(["T", "U"], function.ConceptConstraints
            .Select(constraint => constraint.ParameterName).ToArray());
        Assert.All(function.ConceptConstraints, constraint =>
            Assert.Equal("CComparable", Assert.Single(constraint.Concepts).ToString()));
    }

    [Fact]
    public void SameConceptCanConstrainMultipleGenericArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("multi_concept_constraints");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CComparable {
                bool operator >(const CComparable other) const;
            }
            public class Address {
                public int Value;
                public bool operator >(const CComparable other) const {
                    return Value > other.Value;
                }
            }
            public struct Point {
                public int Value;
                public constructor() { }
                public bool operator >(const CComparable other) const {
                    return Value > other.Value;
                }
            }
            public static class Utils {
                public static T First<T, U>(T first, U second)
                    : T = CComparable : U = CComparable {
                    return first;
                }
            }
            public Address Main() {
                Address address = new Address();
                Point point = new Point();
                return Utils.First(address, point);
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "First");
        Assert.Equal("Address", Assert.IsType<NamedType>(instance.ParameterTypes[0])
            .ResolvedTypeFullName.Parts[^1]);
        Assert.Equal("Point", Assert.IsType<NamedType>(instance.ParameterTypes[1])
            .ResolvedTypeFullName.Parts[^1]);
    }

    [Fact]
    public void ConceptSuffixGrammarPreservesWhereConstraintsOnGenericOperators()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_generic_operator_constraint_parse");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct GenericOperator {
                public T operator +<T>(T other) where T : struct { return other; }
            }
            """));
        var context = project.CompilationContexts.Single();
        var type = Assert.Single(context.DeclarationScope.Declarations
            .OfType<ClassDeclaration>());
        var op = Assert.Single(type.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>());

        Assert.True(op.HasUnclassifiedGenericTypeArguments);
        new SemanticBinder().Bind(project);

        Assert.Equal(["T"], op.GenericTypeNames);
        Assert.Equal("T", Assert.IsType<GenericType>(op.ReturnType).Name);
        Assert.Equal("T", Assert.IsType<GenericType>(
            Assert.Single(op.Parameters).ParameterType).Name);
        Assert.Equal(GenericConstraintKind.ValueType, Assert.Single(op.GenericConstraints).Kind);
        Assert.Empty(op.ConceptConstraints);
    }

    [Fact]
    public void ParsesTheCheckedInConceptSyntaxShape()
    {
        var context = CompilerTestHelper.Parse("""
            concept CEquatable {
                bool operator ==(const CEquatable other) const;
            }
            concept CComparable {
                bool operator >(const CEquatable other) const;
            }
            public class Address {
                public int Value;
                public bool operator ==(const CEquatable other) const {
                    return Value == other.Value;
                }
                public bool operator >(const CEquatable other) const {
                    return Value > other.Value;
                }
            }
            public static class Utils {
                public static bool IsGreater<T>(T first, T second)
                    : T = CEquatable, CComparable {
                    return first > second;
                }
                public static T Max<T>(T first, T second)
                    : T = CEquatable, CComparable {
                    return (first > second) ? first : second;
                }
            }
            """);

        Assert.Equal(2, context.DeclarationScope.Declarations
            .OfType<ConceptDeclaration>().Count());
        var utilities = Assert.Single(context.DeclarationScope.Declarations
            .OfType<ClassDeclaration>(), declaration => declaration.Name == "Utils");
        var max = Assert.Single(utilities.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "Max");
        Assert.Equal(["CEquatable", "CComparable"], Assert.Single(max.ConceptConstraints)
            .Concepts.Select(concept => concept.ToString()).ToArray());
    }

    [Fact]
    public void BindsAndEmitsConceptConstrainedGenericOperatorCalls()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_semantics_pending");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CEquatable {
                bool operator ==(const CEquatable other) const;
            }
            concept CComparable {
                bool operator >(const CEquatable other) const;
            }
            public class Address {
                public int Value;
                public constructor(int value) { Value = value; }
                public bool operator ==(const CEquatable other) const {
                    return Value == other.Value;
                }
                public bool operator >(const CEquatable other) const {
                    return Value > other.Value;
                }
            }
            public static class Utils {
                public static bool IsGreater<T>(T first, T second)
                    : T = CEquatable, CComparable {
                    return first > second;
                }
                public static T Max<T>(T first, T second)
                    : T = CEquatable, CComparable {
                    return (first > second) ? first : second;
                }
            }
            public int Main() {
                Address first = new Address(4);
                Address second = new Address(9);
                if (!Utils.IsGreater(second, first)) return -1;
                return Utils.Max(first, second).Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var instance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "Max");
        Assert.Contains(">", instance.ConceptOperatorBindings.Keys);
        var comparisonInstance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "IsGreater");
        Assert.Contains(">", comparisonInstance.ConceptOperatorBindings.Keys);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-specialization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_semantics_pending.c"));
            Assert.Contains(
                instance.ConceptOperatorBindings[">"].FullName.Parts[^1], source);
            Assert.Contains(
                comparisonInstance.ConceptOperatorBindings[">"].FullName.Parts[^1], source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsArithmeticOperatorsRequiredByConcepts()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_arithmetic");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CAddable {
                CAddable operator +(const CAddable other) const;
            }
            public struct Number {
                public int Value;
                public constructor(int value) { Value = value; }
                public Number operator +(const CAddable other) const {
                    return new Number(Value + other.Value);
                }
            }
            public static class Utils {
                public static T Add<T>(T first, T second) : T = CAddable {
                    return first + second;
                }
            }
            public int Main() {
                Number first = new Number(3);
                Number second = new Number(4);
                return Utils.Add(first, second).Value;
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "Add");
        Assert.Contains("+", instance.ConceptOperatorBindings.Keys);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-arithmetic-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_arithmetic.c"));
            Assert.Contains(instance.ConceptOperatorBindings["+"].FullName.Parts[^1], source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsUnaryOperatorsRequiredByConcepts()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_unary");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CTruth {
                bool operator !() const;
            }
            concept CInvertible {
                CInvertible operator ~() const;
            }
            concept CNegatable {
                CNegatable operator -() const;
            }
            public struct Truth {
                public bool Value;
                public constructor(bool value) { Value = value; }
                public bool operator !() const { return !Value; }
            }
            public struct Number {
                public int Value;
                public constructor(int value) { Value = value; }
                public Number operator ~() const { return new Number(~Value); }
                public Number operator -() const { return new Number(-Value); }
            }
            public class ClassNumber {
                public int Value;
                public constructor(int value) { Value = value; }
                public ClassNumber operator -() const {
                    return new ClassNumber(-Value);
                }
            }
            public static class Utils {
                public static bool IsFalse<T>(T value) : T = CTruth {
                    return !value;
                }
                public static T Invert<T>(T value) : T = CInvertible {
                    return ~value;
                }
                public static T Negate<T>(T value) : T = CNegatable {
                    return -value;
                }
            }
            public int Main() {
                Truth value = new Truth(true);
                Number number = new Number(5);
                Number inverted = Utils.Invert(number);
                Number negated = Utils.Negate(number);
                ClassNumber classNumber = new ClassNumber(7);
                ClassNumber negatedClassNumber = Utils.Negate(classNumber);
                return Utils.IsFalse(value) || inverted.Value != ~5 ||
                    negated.Value != -5 || negatedClassNumber.Value != -7 ? 1 : 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "IsFalse");
        Assert.Contains("!", instance.ConceptOperatorBindings.Keys);
        var invertInstance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "Invert");
        Assert.Contains("~", invertInstance.ConceptOperatorBindings.Keys);
        var negateInstances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "Negate")
            .ToArray();
        Assert.Equal(2, negateInstances.Length);
        Assert.All(negateInstances, candidate =>
            Assert.Contains("-", candidate.ConceptOperatorBindings.Keys));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-unary-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_unary.c"));
            Assert.Contains(instance.ConceptOperatorBindings["!"].FullName.Parts[^1], source);
            Assert.Contains(invertInstance.ConceptOperatorBindings["~"].FullName.Parts[^1], source);
            foreach (var negateInstance in negateInstances)
            {
                Assert.Contains(
                    negateInstance.ConceptOperatorBindings["-"].FullName.Parts[^1], source);
            }
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsParameterizedMethodRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable {
                int GetValue(int multiplier) const;
            }
            public class ClassValue {
                public int Value;
                public constructor(int value) { Value = value; }
                public int GetValue(int multiplier) const { return Value * multiplier; }
            }
            public struct StructValue {
                public int Value;
                public constructor(int value) { Value = value; }
                public int GetValue(int multiplier) const { return Value * multiplier; }
            }
            public static class Utils {
                public static int Read<T>(T value, int multiplier) : T = CReadable {
                    return value.GetValue(multiplier);
                }
                public static int ReadWithLiteral<T>(T value) : T = CReadable {
                    return value.GetValue(2);
                }
            }
            public int Main() {
                ClassValue classValue = new ClassValue(3);
                StructValue structValue = new StructValue(4);
                return Utils.Read(classValue, 2) + Utils.Read(structValue, 3) +
                    Utils.ReadWithLiteral(classValue) + Utils.ReadWithLiteral(structValue);
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name is "Read" or "ReadWithLiteral")
            .ToArray();
        Assert.Equal(4, instances.Length);
        Assert.All(instances, instance =>
            Assert.Contains("method:GetValue(Int)",
                instance.ConceptOperatorBindings.Keys));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_method.c"));
            foreach (var instance in instances)
            {
                Assert.Contains(
                    instance.ConceptOperatorBindings["method:GetValue(Int)"].FullName.Parts[^1],
                    source);
            }
            Assert.DoesNotContain("CReadable, GetValue", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsGenericConceptMethodRequirementsWithExplicitAndInferredTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_generic_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CEcho {
                T Echo<T>(T value, int ignored = 1) const;
            }
            public class ClassEcho {
                public T Echo<T>(T value, int ignored) const { return value; }
            }
            public struct StructEcho {
                public T Echo<T>(T value, int ignored) const { return value; }
            }
            public static class Utils {
                public static U EchoExplicit<T, U>(T value, U next) : T = CEcho {
                    return value.Echo<U>(next);
                }
                public static U EchoInferred<T, U>(T value, U next) : T = CEcho {
                    return value.Echo(next);
                }
            }
            public int Main() {
                ClassEcho classEcho = new ClassEcho();
                StructEcho structEcho;
                Utils.EchoExplicit(classEcho, 41);
                Utils.EchoInferred(structEcho, 1L);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name is "EchoExplicit" or "EchoInferred")
            .ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance =>
        {
            var methodKey = Assert.Single(instance.ConceptOperatorBindings.Keys,
                key => key.StartsWith("method:Echo`1<", StringComparison.Ordinal));
            Assert.Contains(methodKey, instance.ConceptOperatorBindings.Keys);
        });
        Assert.Contains(project.GenericFunctionInstances, candidate =>
            candidate.Declaration?.Name == "Echo" && candidate.SpecializationName is not null);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-generic-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_generic_method.c"));
            Assert.All(instances, instance =>
            {
                var methodKey = instance.ConceptOperatorBindings.Keys.Single(key =>
                    key.StartsWith("method:Echo`1<", StringComparison.Ordinal));
                Assert.Contains(
                    instance.ConceptOperatorBindings[methodKey].SpecializationName!,
                    source);
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EnforcesGenericConceptMethodRequirementsConstraints()
    {
        var validProject = CxProject.CreateDefaultApplicationProject(
            "concept_generic_method_constraint");
        validProject.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            concept CStructEcho {
                T Echo<T>(T value) where T : struct, IMarker;
            }
            public class EchoProvider {
                public T Echo<T>(T value) where T : struct, IMarker { return value; }
            }
            public struct Marker : IMarker {
                public int Value;
            }
            public static class Utils {
                public static U Echo<T, U>(T receiver, U value) : T = CStructEcho {
                    return receiver.Echo(value);
                }
            }
            public int Main() {
                EchoProvider provider = new EchoProvider();
                Marker marker;
                Utils.Echo(provider, marker);
                return 0;
            }
            """));

        new SemanticBinder().Bind(validProject);
        var echoInstance = Assert.Single(validProject.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "Echo" &&
                instance.ConceptOperatorBindings.Keys.Any(key =>
                    key.StartsWith("method:Echo`1<", StringComparison.Ordinal)));
        Assert.Equal("Marker", echoInstance.ReturnType.FullName.ToString());

        var invalidProject = CxProject.CreateDefaultApplicationProject(
            "concept_generic_method_invalid_constraint");
        invalidProject.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker { }
            concept CStructEcho {
                T Echo<T>(T value) where T : struct, IMarker;
            }
            public class EchoProvider {
                public T Echo<T>(T value) where T : struct, IMarker { return value; }
            }
            public static class Utils {
                public static U Echo<T, U>(T receiver, U value) : T = CStructEcho {
                    return receiver.Echo(value);
                }
            }
            public int Main() {
                EchoProvider provider = new EchoProvider();
                Utils.Echo(provider, 42);
                return 0;
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(invalidProject));
        Assert.Contains("does not satisfy generic constraint", exception.Message);

        var mismatchedImplementationProject =
            CxProject.CreateDefaultApplicationProject(
                "concept_generic_method_mismatched_implementation");
        mismatchedImplementationProject.AddCompilationContext(
            CompilerTestHelper.Parse("""
                public interface IMarker { }
                concept CStructEcho {
                    T Echo<T>(T value) where T : struct, IMarker;
                }
                public class EchoProvider {
                    public T Echo<T>(T value) where T : struct { return value; }
                }
                public struct Marker : IMarker { }
                public static class Utils {
                    public static U Echo<T, U>(T receiver, U value) : T = CStructEcho {
                        return receiver.Echo(value);
                    }
                }
                public int Main() {
                    EchoProvider provider = new EchoProvider();
                    Marker marker;
                    Utils.Echo(provider, marker);
                    return 0;
                }
                """));

        var implementationException = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(mismatchedImplementationProject));
        Assert.Contains("does not satisfy concept", implementationException.Message);
    }

    [Fact]
    public void RejectsAmbiguousConceptMethodCallsWithDefaultArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_default_method_ambiguity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CDefaultReader {
                int Read(int value = 1) const;
                int Read(long value = 1L) const;
            }
            public class Reader {
                public int Read(int value) const { return value; }
                public int Read(long value) const { return 0; }
            }
            public static class Utils {
                public static int Read<T>(T reader) : T = CDefaultReader {
                    return reader.Read();
                }
            }
            public int Main() {
                Reader reader = new Reader();
                return Utils.Read(reader);
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));
        Assert.Contains("Concept method invocation 'Read' is ambiguous", exception.Message);
    }

    [Fact]
    public void EnforcesConceptConstraintsOnGenericConceptMethodParameters()
    {
        var validProject = CxProject.CreateDefaultApplicationProject(
            "concept_method_concept_constraint");
        validProject.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CHasMarker {
                int Marker { const get; }
            }
            concept CMarkedEcho {
                T Echo<T>(T value) : T = CHasMarker;
            }
            public class EchoProvider {
                public U Echo<U>(U value) : U = CHasMarker { return value; }
            }
            public struct MarkedValue {
                public int Marker { const get; set; }
            }
            public static class Utils {
                public static U Echo<T, U>(T receiver, U value) : T = CMarkedEcho {
                    return receiver.Echo(value);
                }
            }
            public int Main() {
                EchoProvider provider = new EchoProvider();
                MarkedValue value;
                Utils.Echo(provider, value);
                return 0;
            }
            """));

        new SemanticBinder().Bind(validProject);
        Assert.Contains(validProject.GenericFunctionInstances, instance =>
            instance.Declaration?.Name == "Echo" &&
            instance.ConceptOperatorBindings.Keys.Any(key =>
                key.StartsWith("method:Echo`1<", StringComparison.Ordinal)));

        var invalidProject = CxProject.CreateDefaultApplicationProject(
            "concept_method_concept_constraint_failure");
        invalidProject.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CHasMarker {
                int Marker { const get; }
            }
            concept CMarkedEcho {
                T Echo<T>(T value) : T = CHasMarker;
            }
            public class EchoProvider {
                public T Echo<T>(T value) : T = CHasMarker { return value; }
            }
            public struct UnmarkedValue { }
            public static class Utils {
                public static U Echo<T, U>(T receiver, U value) : T = CMarkedEcho {
                    return receiver.Echo(value);
                }
            }
            public int Main() {
                EchoProvider provider = new EchoProvider();
                UnmarkedValue value;
                Utils.Echo(provider, value);
                return 0;
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(invalidProject));
        Assert.Contains("does not satisfy concept", exception.Message);
    }

    [Fact]
    public void BindsNonConstConceptMethodRequirementsForClassAndStructArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_mutating_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CAdjustable {
                int Adjust(int amount);
            }
            public class ClassCounter {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Adjust(int amount) { Value += amount; return Value; }
            }
            public struct StructCounter {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Adjust(int amount) { Value += amount; return Value; }
            }
            public static class Utils {
                public static int Apply<T>(T value, int amount) : T = CAdjustable {
                    return value.Adjust(amount);
                }
            }
            public int Main() {
                ClassCounter classCounter = new ClassCounter(3);
                StructCounter structCounter = new StructCounter(4);
                return Utils.Apply(classCounter, 5) + Utils.Apply(structCounter, 6);
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "Apply").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance =>
            Assert.Contains("method:Adjust(Int)", instance.ConceptOperatorBindings.Keys));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-mutating-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_mutating_method.c"));
            foreach (var instance in instances)
            {
                Assert.Contains(
                    instance.ConceptOperatorBindings["method:Adjust(Int)"].FullName.Parts[^1],
                    source);
            }
            Assert.Contains("&value", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsReadOnlyConceptPropertyRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_property");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CHasValue {
                int Value { const get; }
            }
            public class ClassValue {
                public int Value { const get; set; }
                public constructor(int value) { Value = value; }
            }
            public struct StructValue {
                public int Value { const get; set; }
                public constructor(int value) { Value = value; }
            }
            public static class Utils {
                public static int Read<T>(T value) : T = CHasValue {
                    return value.Value;
                }
            }
            public int Main() {
                ClassValue classValue = new ClassValue(3);
                StructValue structValue = new StructValue(4);
                return Utils.Read(classValue) + Utils.Read(structValue);
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "Read").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance =>
            Assert.Contains("property:Value", instance.ConceptOperatorBindings.Keys));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_property.c"));
            foreach (var instance in instances)
            {
                Assert.Contains(
                    instance.ConceptOperatorBindings["property:Value"].FullName.Parts[^1],
                    source);
            }
            Assert.DoesNotContain("CHasValue, Value", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsMultipleConceptPropertyReadsInOneGenericExpression()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_multiple_properties");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CBounds {
                int Low { const get; }
                int High { const get; set; }
            }
            public struct Bounds {
                public int Low { const get; set; }
                public int High { const get; set; }
                public constructor(int low, int high) {
                    Low = low;
                    High = high;
                }
            }
            public static class Utils {
                public static int Width<T>(T value) : T = CBounds {
                    return value.High - value.Low;
                }
                public static void SetHigh<T>(T value, int delta) : T = CBounds {
                    value.High = value.Low < 10
                        ? value.Low + delta
                        : value.Low - delta;
                }
            }
            public int Main() {
                Bounds value = new Bounds(4, 11);
                Utils.SetHigh(value, 3);
                return Utils.Width(value);
            }
            """));

        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "Width");
        Assert.Contains("property:Low", instance.ConceptOperatorBindings.Keys);
        Assert.Contains("property:High", instance.ConceptOperatorBindings.Keys);
        var setterInstance = Assert.Single(project.GenericFunctionInstances,
            candidate => candidate.Declaration?.Name == "SetHigh");
        Assert.Contains("property:Low", setterInstance.ConceptOperatorBindings.Keys);
        Assert.Contains("property:High:set", setterInstance.ConceptOperatorBindings.Keys);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-multiple-properties-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_multiple_properties.c"));
            Assert.Contains(
                instance.ConceptOperatorBindings["property:High"].FullName.Parts[^1], source);
            Assert.Contains(
                instance.ConceptOperatorBindings["property:Low"].FullName.Parts[^1], source);
            Assert.Contains(
                setterInstance.ConceptOperatorBindings["property:Low"].FullName.Parts[^1], source);
            Assert.Contains(
                setterInstance.ConceptOperatorBindings["property:High:set"].FullName.Parts[^1], source);
            Assert.Contains(" - ", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsTypeWithMismatchedConceptPropertyType()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_property_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CHasValue {
                int Value { const get; }
            }
            public struct WrongValue {
                public long Value { const get; }
                public constructor() { }
            }
            public static class Utils {
                public static int Read<T>(T value) : T = CHasValue {
                    return value.Value;
                }
            }
            public int Main() { return Utils.Read(new WrongValue()); }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy concept", error.Message);
    }

    [Fact]
    public void BindsAndEmitsConceptPropertySetterRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_property_setter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CWritableValue {
                int Value { const get; set; }
                int Other { set; }
                void Add(int amount);
                void Merge(CWritableValue other);
                void Merge(CWritableValue[] others);
            }
            public class ClassValue {
                public int Value { const get; set; }
                public int Other { get; set; }
                public constructor(int value) { Value = value; Other = value + 1; }
                public void Add(int amount) { Value = Value + amount; }
                public void Merge(ClassValue other) { Value = Value + other.Value; }
                public void Merge(ClassValue[] others) {
                    foreach (var other in others) { Value = Value + other.Value; }
                }
            }
            public struct StructValue {
                public int Value { const get; set; }
                public int Other { get; set; }
                public constructor(int value) { Value = value; Other = value + 1; }
                public void Add(int amount) { Value = Value + amount; }
                public void Merge(StructValue other) { Value = Value + other.Value; }
                public void Merge(StructValue[] others) {
                    foreach (var other in others) { Value = Value + other.Value; }
                }
            }
            public static class Utils {
                public static void Set<T>(T value, int next, int other, int[] adjustments) : T = CWritableValue {
                    int amount = value.Value + next;
                    int iteration = 0;
                    while (iteration < 1) {
                        amount = amount + 1;
                        iteration = iteration + 1;
                    }
                    for (int index = 0; index < 3; index = index + 1) {
                        if (index == 0) continue;
                        if (index == 2) break;
                        amount = amount + 1;
                    }
                    int finalIteration = 0;
                    do {
                        amount = amount + 1;
                        finalIteration = finalIteration + 1;
                    } while (finalIteration < 1);
                    foreach (int adjustment in adjustments) {
                        amount = amount + adjustment;
                    }
                    value.Value = amount;
                    value.Add(other);
                    value.Other = amount + other;
                }
                public static void MergeAll<T>(T value, T[] values) : T = CWritableValue {
                    foreach (T other in values) {
                        value.Merge(other);
                    }
                }
                public static void MergeRows<T>(T value, T[][] rows) : T = CWritableValue {
                    foreach (var row in rows) {
                        value.Merge(row);
                    }
                }
            }
            public int Main() {
                ClassValue classValue = new ClassValue(3);
                StructValue structValue = new StructValue(4);
                int[] adjustments = new int[2];
                adjustments[0] = 1;
                adjustments[1] = 2;
                ClassValue[] classValues = new ClassValue[1];
                classValues[0] = new ClassValue(2);
                StructValue[] structValues = new StructValue[1];
                structValues[0] = new StructValue(1);
                ClassValue[][] classRows = new ClassValue[1][];
                classRows[0] = classValues;
                StructValue[][] structRows = new StructValue[1][];
                structRows[0] = structValues;
                Utils.Set(classValue, 8, 10, adjustments);
                Utils.Set(structValue, 9, 11, adjustments);
                Utils.MergeAll(classValue, classValues);
                Utils.MergeAll(structValue, structValues);
                Utils.MergeRows(classValue, classRows);
                Utils.MergeRows(structValue, structRows);
                return classValue.Value + classValue.Other;
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "Set").ToArray();
        var mergeInstances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "MergeAll").ToArray();
        var mergeRowsInstances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "MergeRows").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.Equal(2, mergeInstances.Length);
        Assert.Equal(2, mergeRowsInstances.Length);
        Assert.All(mergeInstances, instance =>
            Assert.Contains(instance.ConceptOperatorBindings.Keys,
                key => key.StartsWith("method:Merge(", StringComparison.Ordinal)));
        Assert.All(mergeRowsInstances, instance =>
            Assert.Contains(instance.ConceptOperatorBindings.Keys,
                key => key.StartsWith("method:Merge(", StringComparison.Ordinal)));
        Assert.All(instances, instance =>
        {
            Assert.Contains("property:Value", instance.ConceptOperatorBindings.Keys);
            Assert.Contains("property:Value:set", instance.ConceptOperatorBindings.Keys);
            Assert.Contains("property:Other:set", instance.ConceptOperatorBindings.Keys);
            Assert.Contains(instance.ConceptOperatorBindings.Keys,
                key => key.StartsWith("method:Add(", StringComparison.Ordinal));
        });

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-property-setter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_property_setter.c"));
            Assert.Contains("amount =", source);
            Assert.Contains("while (", source);
            Assert.Contains("for (", source);
            Assert.Contains("do", source);
            Assert.Contains("continue;", source);
            Assert.Contains("break;", source);
            Assert.Contains("__cx_concept_foreach_collection_adjustment", source);
            Assert.Contains("__cx_concept_foreach_collection_row", source);
            Assert.Contains("__cx_concept_foreach_collection_other", source);
            Assert.Contains("iteration =", source);
            foreach (var instance in instances)
            {
                Assert.Contains(
                    instance.ConceptOperatorBindings["property:Value"].FullName.Parts[^1],
                    source);
                Assert.Contains(
                    instance.ConceptOperatorBindings["property:Value:set"].FullName.Parts[^1],
                    source);
                Assert.Contains(
                    instance.ConceptOperatorBindings["property:Other:set"].FullName.Parts[^1],
                    source);
                Assert.Contains(
                    instance.ConceptOperatorBindings.First(pair =>
                        pair.Key.StartsWith("method:Add(", StringComparison.Ordinal))
                        .Value.FullName.Parts[^1],
                    source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsTypeMissingRequiredConceptPropertySetter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_property_setter_missing");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CWritableValue {
                int Value { set; }
            }
            public struct ReadOnlyValue {
                public int Value { const get; }
                public constructor() { }
            }
            public static class Utils {
                public static void Set<T>(T value, int next) : T = CWritableValue {
                    value.Value = next;
                }
            }
            public int Main() { Utils.Set(new ReadOnlyValue(), 5); return 0; }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy concept", error.Message);
    }

    [Fact]
    public void RejectsDuplicateConceptPropertyAccessorRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_property_duplicate_accessor");
        var error = Assert.Throws<CompilationErrorException>(() =>
        {
            project.AddCompilationContext(CompilerTestHelper.Parse("""
                concept CHasValue {
                    int Value { const get; const get; }
                }
                """));
            new SemanticBinder().Bind(project);
        });

        Assert.Contains("declares an accessor more than once", error.Message);
    }

    [Fact]
    public void RejectsTypeMissingConceptMethodRequirement()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_method_missing");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable {
                int GetValue(int multiplier) const;
            }
            public struct Empty { }
            public static class Utils {
                public static int Read<T>(T value, int multiplier) : T = CReadable {
                    return value.GetValue(multiplier);
                }
            }
            public int Main() { return Utils.Read(new Empty(), 2); }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("test.cx(10,39)", error.Message);
    }

    [Fact]
    public void RejectsConceptMethodImplementationWithDifferentConstness()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_method_constness_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CAdjustable {
                int Adjust(int amount);
            }
            public struct ReadOnlyCounter {
                public int Adjust(int amount) const { return amount; }
            }
            public static class Utils {
                public static int Apply<T>(T value, int amount) : T = CAdjustable {
                    return value.Adjust(amount);
                }
            }
            public int Main() { return Utils.Apply(new ReadOnlyCounter(), 1); }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("concept", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindsVoidConceptMethodRequirementsForClassAndStructArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_void_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CResettable {
                void Reset(int value);
                void Add(int amount);
            }
            public class ClassValue {
                public int Value;
                public void Reset(int value) { Value = value; }
                public void Add(int amount) { Value += amount; }
            }
            public struct StructValue {
                public int Value;
                public void Reset(int value) { Value = value; }
                public void Add(int amount) { Value += amount; }
            }
            public static class Utils {
                public static void Reset<T>(T value, int next) : T = CResettable {
                    value.Reset(next);
                    value.Add(2);
                }
            }
            public int Main() {
                ClassValue classValue = new ClassValue();
                StructValue structValue;
                Utils.Reset(classValue, 7);
                Utils.Reset(structValue, 9);
                return classValue.Value;
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name == "Reset").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance =>
        {
            Assert.Contains("method:Reset(Int)", instance.ConceptOperatorBindings.Keys);
            Assert.Contains("method:Add(Int)", instance.ConceptOperatorBindings.Keys);
        });

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-void-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_void_method.c"));
            foreach (var instance in instances)
            {
                Assert.Contains(
                    instance.ConceptOperatorBindings["method:Reset(Int)"].FullName.Parts[^1],
                    source);
                Assert.Contains(
                    instance.ConceptOperatorBindings["method:Add(Int)"].FullName.Parts[^1],
                    source);
            }
            Assert.Contains("&value", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsOverloadedConceptMethodRequirementsByExactSignature()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_method_overloads");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable {
                int GetValue(int multiplier) const;
                int GetValue(long multiplier) const;
            }
            public class ClassValue {
                public int Value;
                public constructor(int value) { Value = value; }
                public int GetValue(int multiplier) const { return Value; }
                public int GetValue(long multiplier) const { return Value + 1; }
            }
            public struct StructValue {
                public int Value;
                public constructor(int value) { Value = value; }
                public int GetValue(int multiplier) const { return Value; }
                public int GetValue(long multiplier) const { return Value + 1; }
            }
            public static class Utils {
                public static int ReadInt<T>(T value, int multiplier) : T = CReadable {
                    return value.GetValue(multiplier);
                }
                public static int ReadLong<T>(T value, long multiplier) : T = CReadable {
                    return value.GetValue(multiplier);
                }
            }
            public int Main() {
                ClassValue classValue = new ClassValue(3);
                StructValue structValue = new StructValue(4);
                return Utils.ReadInt(classValue, 2) + Utils.ReadLong(classValue, 2L) +
                    Utils.ReadInt(structValue, 2) + Utils.ReadLong(structValue, 2L);
            }
            """));

        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(candidate => candidate.Declaration?.Name is "ReadInt" or "ReadLong")
            .ToArray();
        Assert.Equal(4, instances.Length);
        Assert.All(instances, instance =>
        {
            Assert.Equal(2, instance.ConceptOperatorBindings.Count);
            Assert.Contains("method:GetValue(Int)",
                instance.ConceptOperatorBindings.Keys);
            Assert.Contains("method:GetValue(Long)",
                instance.ConceptOperatorBindings.Keys);
        });

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-concept-method-overloads-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "concept_method_overloads.c"));
            foreach (var instance in instances)
            {
                Assert.Contains(
                    instance.ConceptOperatorBindings["method:GetValue(Int)"].FullName.Parts[^1],
                    source);
                Assert.Contains(
                    instance.ConceptOperatorBindings["method:GetValue(Long)"].FullName.Parts[^1],
                    source);
            }
            Assert.DoesNotContain("CReadable, GetValue", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsDuplicateOverloadedConceptMethodRequirementSignatures()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "concept_method_duplicate_overload");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable {
                int GetValue(int multiplier) const;
                int GetValue(int multiplier) const;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("duplicate method requirement signature", error.Message);
    }

    [Fact]
    public void RejectsConceptMethodWithMismatchedParameterType()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_method_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CReadable {
                int GetValue(int multiplier) const;
            }
            public struct WrongValue {
                public int GetValue(long multiplier) const { return 1; }
            }
            public static class Utils {
                public static int Read<T>(T value) : T = CReadable {
                    return value.GetValue(2);
                }
            }
            public int Main() { return Utils.Read(new WrongValue()); }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("test.cx(12,39)", error.Message);
    }

    [Fact]
    public void UnmatchedConceptLookupLeavesUnrelatedGenericOperatorParametersIntact()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_lookup_regression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CComparable {
                bool operator >(const CComparable other) const;
            }
            public struct GenericOperatorMethodBox {
                public T operator +<T>(T other) where T : struct { return other; }
            }
            """));

        new SemanticBinder().Bind(project);

        var box = Assert.Single(project.CompilationContexts.Single().DeclarationScope
            .Declarations.OfType<ClassDeclaration>());
        var op = Assert.Single(box.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>());
        Assert.Equal("T", Assert.IsType<GenericType>(op.ReturnType).Name);
        Assert.Equal("T", Assert.IsType<GenericType>(
            Assert.Single(op.Parameters).ParameterType).Name);
    }

    [Fact]
    public void RejectsGenericArgumentThatDoesNotSatisfyConcept()
    {
        var project = CxProject.CreateDefaultApplicationProject("concept_missing_operator");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            concept CComparable {
                bool operator >(const CComparable other) const;
            }
            public class NoComparison { }
            public static class ConceptUtils {
                public static T Max<T>(T first, T second) : T = CComparable {
                    return (first > second) ? first : second;
                }
            }
            public int Main() {
                NoComparison first = new NoComparison();
                NoComparison second = new NoComparison();
                return ConceptUtils.Max(first, second) == null ? 0 : 1;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy concept 'CComparable'", error.Message);
    }

    [Fact]
    public void GenericClassConceptConstraintsValidateClosedTypeArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable { int Read() const; }
            public class Readable { public int Read() const { return 42; } }
            public class Box<T> : T = CReadable { }
            public interface IReadBox<T> : T = CReadable { T Value { get; } }
            public int Main() {
                Box<Readable> value = new Box<Readable>();
                IReadBox<Readable> interfaceValue;
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void GenericClassConceptConstraintsRejectMissingRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_missing");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable {
                int Read() const;
                void Add(int amount);
            }
            public class Missing { }
            public class Box<T> : T = CReadable { }
            public int Main() {
                Box<Missing> value = new Box<Missing>();
                return 0;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy concept 'CReadable'", error.Message);
    }

    [Fact]
    public void PartialGenericClassDeclarationsMustAgreeOnConceptConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject("partial_class_concept_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable { int Read() const; }
            public partial class Box<T> : T = CReadable { }
            public partial class Box<T> { }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("concept constraints", error.Message);
    }

    [Fact]
    public void GenericClassMemberBodiesCanUseConceptRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_member_body");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable {
                int Score { const get; }
                int Read() const;
            }
            public class Readable {
                public int Score { const get => 10; }
                public int Read() const { return 42; }
            }
            public class Wrapper<T> : T = CReadable {
                public int Read(T value) { return value.Score + value.Read(); }
            }
            public int Main() {
                Wrapper<Readable> wrapper = new Wrapper<Readable>();
                Readable value = new Readable();
                return wrapper.Read(value);
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-body-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_member_body.c"));
            Assert.DoesNotContain("CReadable", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericStructMemberBodiesCanUseConceptRequirements()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "struct_concept_member_body");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable {
                int Read() const;
                void Add(int amount);
            }
            public struct Reader {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Read() const { return Value; }
                public void Add(int amount) { Value += amount; }
            }
            public class MutableReader {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Read() const { return Value; }
                public void Add(int amount) { Value += amount; }
            }
            public struct Wrapper<T> : T = CReadable {
                public int ReadValue(T value) { return value.Read(); }
                public void AddTo(T value, int amount) { value.Add(amount); }
                public void Drain(T value) {
                    while (value.Read() > 0) { value.Add(-1); }
                }
            }
            public int Main() {
                Wrapper<Reader> valueWrapper = default;
                Reader value = new Reader(42);
                Wrapper<MutableReader> classWrapper = default;
                MutableReader reference = new MutableReader(10);
                classWrapper.AddTo(reference, 5);
                classWrapper.Drain(reference);
                return valueWrapper.ReadValue(value) + classWrapper.ReadValue(reference);
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-struct-concept-body-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "struct_concept_member_body.c"));

            Assert.Contains("ReadValue", source);
            Assert.Contains("AddTo", source);
            Assert.Contains("while (", source);
            Assert.DoesNotContain("CReadable", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericStructConceptConstraintsRejectUnsatisfiedArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "struct_concept_constraint_failure");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable { int Read() const; }
            public struct MissingRead { }
            public struct Wrapper<T> : T = CReadable { }
            public int Main() {
                Wrapper<MissingRead> value = default;
                return 0;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy concept 'CReadable'", error.Message);
    }

    [Fact]
    public void GenericClassVoidBodiesCanCallConceptMethodsInSourceOrder()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_void_body");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CResettable {
                int Value { const get; set; }
                int Adjustment { const get; }
                void Add(int amount);
            }
            public class Resettable {
                public int Value { const get; set; }
                public int Adjustment { const get => 2; }
                public void Add(int amount) { Value += amount; }
            }
            public class Resetter<T> : T = CResettable {
                public void SetAndAdd(T value, int amount) {
                    value.Value = amount + value.Adjustment;
                    value.Add(amount + value.Adjustment);
                }
                public void AddTwice(T value, int amount) {
                    value.Value += amount;
                    value.Add(amount);
                }
                public void ApplyBuiltInCompounds(T value) {
                    value.Value += 2;
                    value.Value -= 1;
                    value.Value *= 2;
                    value.Value /= 3;
                    value.Value %= 6;
                }
                public void Choose(T value, bool increase, int amount) {
                    if (increase) {
                        value.Value += amount;
                        value.Add(amount);
                    } else {
                        value.Value -= amount;
                        value.Add(amount);
                    }
                }
                public void Drain(T value) {
                    while (value.Value > 0) {
                        value.Add(-1);
                    }
                }
                public void DecrementOnce(T value) {
                    do { value.Add(-1); } while (value.Value > 0);
                }
            }
            public int Main() {
                Resettable value = new Resettable();
                Resetter<Resettable> resetter = new Resetter<Resettable>();
                resetter.SetAndAdd(value, 7);
                resetter.AddTwice(value, 2);
                resetter.Choose(value, true, 3);
                resetter.Choose(value, false, 2);
                resetter.Drain(value);
                resetter.DecrementOnce(value);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-void-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "class_concept_void_body.c"));
            Assert.DoesNotContain("CResettable", source);
            Assert.DoesNotContain("_unknowntype_", source);
            Assert.Contains("Reset", source);
            Assert.Contains("while (", source);
            Assert.Contains("do", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanSwitchOnConceptExpressions()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_switch_body");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CResettable {
                string ReadName() const;
                void Add(int amount);
            }
            public class Resettable {
                public int Value;
                public string Name;
                public string ReadName() const { return Name; }
                public void Add(int amount) { Value += amount; }
            }
            public class Resetter<T> : T = CResettable {
                public void Apply(T value, int amount, bool enabled) {
                    string name = value.ReadName();
                    switch (name) {
                        case "zero" when enabled:
                            value.Add(amount);
                            break;
                        case "zero" when !enabled:
                            value.Add(-amount);
                            break;
                        case null:
                            value.Add(7);
                            break;
                        default:
                            value.Add(-amount);
                            break;
                    }
                }
            }
            public int Main() {
                Resettable value = new Resettable();
                Resetter<Resettable> resetter = new Resetter<Resettable>();
                value.Name = "zero";
                resetter.Apply(value, 2, true);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-switch-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_switch_body.c"));

            Assert.Contains("switch (", source);
            Assert.Contains("memcmp", source);
            Assert.Contains("name", source);
            Assert.Contains("case 0:", source);
            Assert.Contains("case 1:", source);
            Assert.Contains("case 2:", source);
            Assert.Contains("case 3:", source);
            Assert.Contains("cx_int __cx_switch_case_", source);
            Assert.Contains("break;", source);
            Assert.DoesNotContain("CResettable", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanUseCountedLoops()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_counted_loop");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CAccumulator { void Add(int amount); }
            public class Accumulator {
                public int Value;
                public void Add(int amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CAccumulator {
                public void AddIndices(T value, int count) {
                    for (int index = 0; index < count; index++) {
                        value.Add(index);
                    }
                }
                public void AddDescending(T value, int count) {
                    for (int index = count; index > 0; index--) {
                        value.Add(index);
                    }
                }
                public void AddFromExistingCounter(T value, int count) {
                    int index = 0;
                    for (index = count; index > 0; index--) {
                        value.Add(index);
                    }
                }
            }
            public int Main() {
                Accumulator value = new Accumulator();
                AccumulatorRunner<Accumulator> runner = new AccumulatorRunner<Accumulator>();
                runner.AddIndices(value, 4);
                runner.AddDescending(value, 4);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-for-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_counted_loop.c"));

            Assert.Contains("for (", source);
            Assert.Contains("index++", source);
            Assert.Contains("index--", source);
            Assert.Contains("index = count;", source);
            Assert.Contains("Add", source);
            Assert.DoesNotContain("CAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanForeachConcreteArrayValues()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "class_concept_concrete_array_foreach");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CAccumulator { void Add(int amount); }
            public class Accumulator {
                public int Value;
                public void Add(int amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CAccumulator {
                public void AddEach(T value, int[] amounts) {
                    foreach (var amount in amounts) {
                        value.Add(amount);
                    }
                }
            }
            public int Main() {
                Accumulator value = new Accumulator();
                AccumulatorRunner<Accumulator> runner = new AccumulatorRunner<Accumulator>();
                int[] amounts = new int[3];
                amounts[0] = 2;
                amounts[1] = 3;
                amounts[2] = 5;
                runner.AddEach(value, amounts);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-concrete-foreach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_concrete_array_foreach.c"));

            Assert.Contains("__cx_class_concept_foreach_collection_amount", source);
            Assert.Contains("__cx_class_concept_foreach_index_amount", source);
            Assert.Contains("cx_int amount", source);
            Assert.Contains("Add", source);
            Assert.DoesNotContain("CAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanReturnEarly()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_early_return");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CAccumulator { void Add(int amount); }
            public class Accumulator {
                public int Value;
                public void Add(int amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CAccumulator {
                public void AddUnless(T value, bool stop) {
                    if (stop) return;
                    value.Add(3);
                }
            }
            public int Main() {
                Accumulator value = new Accumulator();
                AccumulatorRunner<Accumulator> runner = new AccumulatorRunner<Accumulator>();
                runner.AddUnless(value, true);
                runner.AddUnless(value, false);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-return-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_early_return.c"));

            Assert.Contains("return;", source);
            Assert.DoesNotContain("CAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanIterateConstrainedArrays()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_foreach");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CAccumulator { void Add(int amount); }
            public class Accumulator {
                public int Value;
                public void Add(int amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CAccumulator {
                public void AddAll(T[] values, int amount) {
                    foreach (var item in values) {
                        item.Add(amount);
                    }
                }
            }
            public int Main() {
                Accumulator[] values = new Accumulator[2];
                values[0] = new Accumulator();
                values[1] = new Accumulator();
                AccumulatorRunner<Accumulator> runner = new AccumulatorRunner<Accumulator>();
                runner.AddAll(values, 2);
                return values[0].Value + values[1].Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-foreach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_foreach.c"));

            Assert.Contains("__cx_class_concept_foreach_collection_item", source);
            Assert.Contains("__cx_class_concept_foreach_index_item", source);
            Assert.Contains("Add", source);
            Assert.DoesNotContain("CAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanReuseComputedLocals()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_locals");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CAccumulator {
                int Read() const;
                void Add(int amount);
            }
            public class Accumulator {
                public int Value;
                public int Read() const { return Value; }
                public void Add(int amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CAccumulator {
                public void AddThroughCopy(T value, int amount) {
                    T copy = value;
                    copy.Add(amount);
                }
                public void AddFromRead(T value, int amount) {
                    int delta = value.Read() + amount;
                    delta -= amount;
                    delta++;
                    delta += 2;
                    delta *= 2;
                    delta /= 2;
                    delta %= 7;
                    ++delta;
                    value.Add(delta);
                }
            }
            public int Main() {
                Accumulator value = new Accumulator();
                value.Value = 4;
                AccumulatorRunner<Accumulator> runner = new AccumulatorRunner<Accumulator>();
                runner.AddFromRead(value, 3);
                runner.AddThroughCopy(value, 2);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-locals-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_locals.c"));

            Assert.Contains("delta =", source);
            Assert.Contains("copy", source);
            Assert.Contains("Read", source);
            Assert.Contains("Add", source);
            Assert.DoesNotContain("CAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanBranchOnBooleanLocals()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_boolean_local");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CAccumulator {
                bool IsReady() const;
                void Add(int amount);
            }
            public class Accumulator {
                public int Value;
                public bool IsReady() const { return Value >= 0; }
                public void Add(int amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CAccumulator {
                public void AddWhenReady(T value, bool enabled) {
                    bool shouldAdd = enabled && value.IsReady();
                    if (shouldAdd) value.Add(2);
                }
            }
            public int Main() {
                Accumulator value = new Accumulator();
                AccumulatorRunner<Accumulator> runner = new AccumulatorRunner<Accumulator>();
                runner.AddWhenReady(value, false);
                runner.AddWhenReady(value, true);
                return value.Value;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-bool-local-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_boolean_local.c"));

            Assert.Contains("shouldAdd", source);
            Assert.Contains("if (", source);
            Assert.Contains("IsReady", source);
            Assert.DoesNotContain("CAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassConceptVoidBodiesCanUseUnsignedIntegerLocals()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_uint_local");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CUnsignedAccumulator {
                uint ReadCount() const;
                void Add(uint amount);
            }
            public class UnsignedAccumulator {
                public uint Value;
                public uint ReadCount() const { return Value; }
                public void Add(uint amount) { Value += amount; }
            }
            public class AccumulatorRunner<T> : T = CUnsignedAccumulator {
                public void AddNext(T value, uint amount) {
                    uint next = value.ReadCount();
                    next++;
                    value.Add(next + amount);
                }
            }
            public int Main() {
                UnsignedAccumulator value = new UnsignedAccumulator();
                AccumulatorRunner<UnsignedAccumulator> runner = new AccumulatorRunner<UnsignedAccumulator>();
                runner.AddNext(value, 2u);
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-class-concept-uint-local-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "class_concept_uint_local.c"));

            Assert.Contains("uint next", source);
            Assert.Contains("next++", source);
            Assert.Contains("ReadCount", source);
            Assert.Contains("Add", source);
            Assert.DoesNotContain("CUnsignedAccumulator", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OpenGenericTypeArgumentCanForwardAConceptConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable { int Read() const; }
            public class Box<T> : T = CReadable { }
            public extern Box<T> Forward<T>(Box<T> value) : T = CReadable;
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void OpenGenericTypeArgumentRequiresForwardedConceptConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_concept_unconstrained_forwarding");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public concept CReadable { int Read() const; }
            public class Box<T> : T = CReadable { }
            public extern Box<T> Forward<T>(Box<T> value);
            """));

        var error = Assert.Throws<CompilationErrorException>(() =>
            new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy concept 'CReadable'", error.Message);
    }
}
