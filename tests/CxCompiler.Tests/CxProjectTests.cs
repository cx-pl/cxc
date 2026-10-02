using CxCompiler.Model.Project;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CxCompiler.Tests;

public sealed class CxProjectTests
{
    [Fact]
    public void ProjectYamlExposesAllConfiguredTargetsToCompiler()
    {
        var project = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<CxProject>("""
                name: app
                targets:
                - win-x64
                - linux-arm64
                """);

        Assert.Equal(["win-x64", "linux-arm64"], project.Targets);
    }
}
