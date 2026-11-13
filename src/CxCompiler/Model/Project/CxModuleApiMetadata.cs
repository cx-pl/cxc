using CxCompiler.Model.Errors;
using CxCompiler.OutputGenerators;
using System.Text.Json;

namespace CxCompiler.Model.Project;

public sealed record CxModuleApiMetadataDocument(
    int SchemaVersion,
    int ModuleMetadataFormatVersion,
    string ModuleName,
    string ModuleVersion,
    IReadOnlyList<CxModuleApiSource> Sources);

public static class CxModuleApiMetadataLoader
{
    public const int CurrentSchemaVersion = 1;

    public static CxModuleApiMetadataDocument Read(string path)
    {
        CxModuleApiMetadataDocument document;
        try
        {
            var json = File.ReadAllText(path);
            document = JsonSerializer.Deserialize<CxModuleApiMetadataDocument>(json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = 128,
                }) ?? throw new JsonException("The module API document is empty.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CompilationErrorException(
                $"Cannot read CX module API metadata '{path}': {error.Message}");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new CompilationErrorException(
                $"CX module API metadata '{path}' uses schema version " +
                $"{document.SchemaVersion}; version {CurrentSchemaVersion} is required.");
        }
        if (document.ModuleMetadataFormatVersion !=
            CCodeOutputGenerator.CurrentModuleMetadataFormatVersion)
        {
            throw new CompilationErrorException(
                $"CX module API metadata '{path}' requires module metadata format " +
                $"{document.ModuleMetadataFormatVersion}; format " +
                $"{CCodeOutputGenerator.CurrentModuleMetadataFormatVersion} is required.");
        }
        if (string.IsNullOrWhiteSpace(document.ModuleName) ||
            string.IsNullOrWhiteSpace(document.ModuleVersion) ||
            document.Sources is null || document.Sources.Any(source =>
                source is null || string.IsNullOrWhiteSpace(source.Name) ||
                source.Source is null))
        {
            throw new CompilationErrorException(
                $"CX module API metadata '{path}' is missing its module identity or source units.");
        }

        return document;
    }

    public static string? FindSiblingApiMetadata(string effectsPath)
    {
        const string effectsSuffix = ".cxeffects.json";
        if (!effectsPath.EndsWith(effectsSuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var apiPath = effectsPath[..^effectsSuffix.Length] + ".cxapi.json";
        return File.Exists(apiPath) ? apiPath : null;
    }

    public static string? FindSiblingEffectsMetadata(string apiPath)
    {
        const string apiSuffix = ".cxapi.json";
        if (!apiPath.EndsWith(apiSuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var effectsPath = apiPath[..^apiSuffix.Length] + ".cxeffects.json";
        return File.Exists(effectsPath) ? effectsPath : null;
    }
}
