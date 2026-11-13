using CxCompiler.Model.Expressions;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class StandardLibraryApiTests
{
    [Fact]
    public void BuildsTypedProviderTreesFromTargetTypedQueryLambdas()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        AddQueryApi(project);
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections.Query;
            QueryPlan<int> FilterAbove(QueryCapture<int> minimum) {
                return new QueryPlan<int>("Source", "numbers", null, null, 0u, 0u)
                    .Filter(value => {
                        int increment = 1;
                        int threshold = minimum.Value + increment;
                        if (value < 0) { return false; }
                        return value > threshold;
                    });
            }
            QueryPlan<int> DoubleValues(QueryPlan<int> source) {
                return source.Select(value => value > 2 ? value * 2 : value);
            }
            QueryPlan<int> DoubleValuesWithBlock(QueryPlan<int> source) {
                return source.Select(value => {
                    if (value > 4) { return value * 2; }
                    else if (value > 2) { return value + 1; }
                    else { return value; }
                });
            }
            QueryScalarPlan<bool> HasAny(QueryPlan<int> source) {
                return source.Any();
            }
            QueryScalarPlan<uint> CountAbove(
                QueryPlan<int> source, QueryCapture<int> minimum) {
                return source.Count(value => value > minimum.Value);
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-query-lambda-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var output = File.ReadAllText(Path.Combine(directory, "unnamed.c"));
            Assert.Contains("GreaterThan", output);
            Assert.Contains("Let", output);
            Assert.Contains("Variable", output);
            Assert.Contains("Conditional", output);
            Assert.Contains("CapturedValue", output);
            Assert.Contains("minimum", output);
            Assert.Contains("Multiply", output);
            Assert.Contains("QueryLambda", output);
            Assert.Contains("QueryScalarPlan", output);
            Assert.Contains("CountAbove", output);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProviderQueryLambdaRejectsImplicitLocalCapture()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        AddQueryApi(project);
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections.Query;
            QueryPlan<int> FilterAbove() {
                int minimum = 2;
                return new QueryPlan<int>("Source", "numbers", null, null, 0u, 0u)
                    .Filter(value => value > minimum);
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("explicit QueryCapture<T> cell", exception.Message);
    }

    [Fact]
    public void ProviderQueryLambdaPreservesMethodCallsAndArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        AddQueryApi(project);
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections.Query;
            public final class QueryValue {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Add(int amount) { return Value + amount; }
            }
            QueryPlan<int> AddThree(QueryPlan<QueryValue> source) {
                return source.Select(value => value.Add(3));
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-query-call-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var output = File.ReadAllText(Path.Combine(directory, "unnamed.c"));
            Assert.Contains("Call", output);
            Assert.Contains("Argument", output);
            Assert.Contains("QueryValue.Add#1", output);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProviderQueryLambdaEncodesLocalAssignmentsAsNestedLets()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        AddQueryApi(project);
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections.Query;
            QueryPlan<int> FilterAbove() {
                return new QueryPlan<int>("Source", "numbers", null, null, 0u, 0u)
                    .Filter(value => {
                        int minimum = 2;
                        minimum = minimum + 1;
                        minimum += 1;
                        minimum -= 1;
                        minimum *= 1;
                        minimum /= 1;
                        minimum %= 100;
                        if (minimum > 3) { minimum += 1; minimum *= 2; }
                        else { minimum -= 1; minimum /= 1; }
                        return value > minimum;
                    });
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-query-local-assignment-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(directory, "input.cx"));
            var output = File.ReadAllText(
                Path.Combine(directory, "unnamed.c"));
            Assert.Contains("Let", output);
            Assert.Contains("Variable", output);
            Assert.Contains("Add", output);
            Assert.Contains("Conditional", output);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AddQueryApi(CxProject project) =>
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections.Query;
            public final class QueryExpression<T> {
                public string Kind;
                public string Name;
                public object ConstantValue;
                public object Left;
                public object Right;
                public constructor(string kind, string name, object constantValue, object left, object right) {
                    Kind = kind; Name = name; ConstantValue = constantValue; Left = left; Right = right;
                }
            }
            public final class QueryCapture<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public final class QueryLambda<TParameter, TResult> {
                public QueryExpression<TParameter> Parameter;
                public QueryExpression<TResult> Body;
                public constructor(QueryExpression<TParameter> parameter, QueryExpression<TResult> body) {
                    Parameter = parameter; Body = body;
                }
            }
            public final class QueryScalarPlan<TResult> {
                public string Kind;
                public object Source;
                public uint Index;
                public constructor(string kind, object source, uint index) {
                    Kind = kind; Source = source; Index = index;
                }
            }
            public final class QueryPlan<T> {
                public object Expression;
                public constructor(string kind, string sourceName, object source, object expression, uint count, uint secondCount) {
                    Expression = expression;
                }
                public QueryPlan<T> Filter(QueryLambda<T, bool> predicate) {
                    return new QueryPlan<T>("Filter", null, this, predicate, 0u, 0u);
                }
                public QueryPlan<TResult> Select<TResult>(QueryLambda<T, TResult> selector) {
                    return new QueryPlan<TResult>("Select", null, this, selector, 0u, 0u);
                }
                public QueryScalarPlan<bool> Any() {
                    return new QueryScalarPlan<bool>("Any", this, 0u);
                }
                public QueryScalarPlan<uint> Count() {
                    return new QueryScalarPlan<uint>("Count", this, 0u);
                }
                public QueryScalarPlan<bool> Any(QueryLambda<T, bool> predicate) {
                    return Filter(predicate).Any();
                }
                public QueryScalarPlan<uint> Count(QueryLambda<T, bool> predicate) {
                    return Filter(predicate).Count();
                }
            }
            """));

    [Fact]
    public void BindsMathOverloadsToTheRuntimeAbi()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System;
            int Integer() { return Math.Abs(Math.Clamp(Math.Floor(-4), Math.Min(-3, 1), Math.Max(0, 5))); }
            long LongInteger() { return Math.Abs(Math.Ceiling(-5000000000l)); }
            float Single() { return Math.Round(Math.Log(8.0f, 2.0f), 2); }
            double Double() { return Math.Round(Math.Log(8.0, 2.0), 2); }
            double Constants() { return Math.E + Math.Pi + Math.Tau + Math.Sqrt2 + Math.Sqrt3; }
            float Reciprocal() { return Math.ReciprocalEstimate(4.0f); }
            double ReciprocalSqrt() { return Math.ReciprocalSqrtEstimate(16.0); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-math-api-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var output = File.ReadAllText(Path.Combine(directory, "unnamed.c"));
            Assert.Contains("cxcore_System_Math_Abs", output);
            Assert.Contains("cxcore_System_Math_Abs__2", output);
            Assert.Contains("cxcore_System_Math_Round__2", output);
            Assert.Contains("cxcore_System_Math_Round__4", output);
            Assert.Contains("cxcore_System_Math_Log__3", output);
            Assert.Contains("cxcore_System_Math_Log__4", output);
            Assert.Contains("cxcore_System_Math_ReciprocalEstimate", output);
            Assert.Contains("cxcore_System_Math_ReciprocalSqrtEstimate__2", output);
            foreach (var constant in new[] { "E", "Pi", "Tau", "Sqrt2", "Sqrt3" })
                Assert.Contains($"cxcore_System_Math_{constant}", output);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void BindsRuntimeAdditionsWithoutSourceProjectReferences()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System;
            int? Parse(string text) { return Int.TryParse(text); }
            decimal? ParseDecimal(string text) { return Decimal.TryParse(text); }
            Uuid NewId() { return Uuid.NewUuidV4(); }
            Uuid NamedId() { return Uuid.NewUuidV5(Uuid.NamespaceDns, "example.com"); }
            char ReadWideCodeUnit(WideString text, uint index) {
                return (char)text.CodeUnitAt(index);
            }
            string Build() {
                StringBuilder builder = new StringBuilder();
                return builder.Append("value=").Append(42).Append(0.5m).Append((byte)1).ToString();
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-stdlib-api-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory,"input.cx"));
            var output = File.ReadAllText(Path.Combine(directory,"unnamed.c"));
            Assert.Contains("cxcore_System_Int_TryParse",output);
            Assert.Contains("cxcore_System_Decimal_TryParse",output);
            Assert.Contains("cxcore_System_Uuid_NewUuidV4",output);
            Assert.Contains("cxcore_System_WideString_CodeUnitAt", output);
            Assert.Contains("cxcore_System_StringBuilder_Append",output);
            Assert.Contains("cxcore_System_StringBuilder_Append__10",output);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData("0x123456789abcdeful")]
    [InlineData("0xDEADBEEFul")]
    public void HexadecimalDigitsAreNotTreatedAsFloatingExponents(string literal)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse($"ulong Value() {{ return {literal}; }}"));
        new SemanticBinder().Bind(project);
        var function = Assert.IsType<FunctionDeclaration>(Assert.Single(project.CompilationContexts.Single().DeclarationScope.Declarations));
        var value = Assert.IsType<LiteralExpression>(Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression);
        Assert.IsType<ULongType>(value.InferredType);
    }
}
