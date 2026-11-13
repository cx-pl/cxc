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

    [Fact]
    public void ProjectYamlReadsEffectMetadataReferences()
    {
        var project = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<CxProject>("""
                name: app
                effect_metadata_references:
                - ../binary/binary.cxeffects.json
                """);

        Assert.Equal(["../binary/binary.cxeffects.json"], project.EffectMetadataReferences);
    }

    [Fact]
    public void ProjectYamlReadsModuleApiReferences()
    {
        var project = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<CxProject>("""
                name: app
                module_api_references:
                - module_name: binary_module
                  source: ../binary/api.cxapi
                  version: 1.0
                - module_name: cxcore
                  source: ../cxcore/core.cxapi
                  synthetic_core: true
                """);

        Assert.Equal(2, project.ModuleApiReferences.Count);
        Assert.Equal("binary_module", project.ModuleApiReferences[0].ModuleName);
        Assert.Equal("../binary/api.cxapi", project.ModuleApiReferences[0].Source);
        Assert.Equal("1.0", project.ModuleApiReferences[0].Version);
        Assert.True(project.ModuleApiReferences[1].SyntheticCore);
    }

    [Fact]
    public void ProjectYamlReadsModuleApiSearchPaths()
    {
        var project = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<CxProject>("""
                name: app
                module_api_search_paths:
                - ../binary/bin
                - vendor/cx
                """);

        Assert.Equal(["../binary/bin", "vendor/cx"], project.ModuleApiSearchPaths);
    }

    [Fact]
    public void ProjectYamlReadsExplicitGeneratorBuildToolsAndInputs()
    {
        var project = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<CxProject>("""
                name: app
                generators:
                - id: dto-json
                  executable: tools/dto-generator.exe
                  arguments: [--profile, strict]
                  additional_files: [schema.json]
                  timeout_seconds: 45
                """);

        var generator = Assert.Single(project.Generators);
        Assert.Equal("dto-json", generator.Id);
        Assert.Equal("tools/dto-generator.exe", generator.Executable);
        Assert.Equal(["--profile", "strict"], generator.Arguments);
        Assert.Equal(["schema.json"], generator.AdditionalFiles);
        Assert.Equal(45, generator.TimeoutSeconds);
    }

    [Fact]
    public void ProjectJsonReadsExplicitGeneratorBuildToolsAndInputs()
    {
        var project = System.Text.Json.JsonSerializer.Deserialize<CxProject>("""
            {"name":"app","generators":[{"id":"dto-json","executable":"tools/dto-generator",
             "arguments":["--strict"],"additionalFiles":["schema.json"],"timeoutSeconds":30}]}
            """, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

        var generator = Assert.Single(Assert.IsType<CxProject>(project).Generators);
        Assert.Equal("dto-json", generator.Id);
        Assert.Equal(["--strict"], generator.Arguments);
        Assert.Equal(["schema.json"], generator.AdditionalFiles);
        Assert.Equal(30, generator.TimeoutSeconds);
    }
}
