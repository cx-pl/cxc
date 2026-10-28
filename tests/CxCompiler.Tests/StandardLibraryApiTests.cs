using CxCompiler.Model.Expressions;
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
