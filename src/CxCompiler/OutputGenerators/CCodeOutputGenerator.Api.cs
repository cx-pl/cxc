using CxCompiler.Model.Project;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static void WriteModuleApiMetadata(CxProject project, string outputPath)
    {
        var sources = project.ModuleApiSources
            .OrderBy(source => source.Name, StringComparer.Ordinal)
            .Select(source => new CxModuleApiSource(
                source.Name,
                CxModuleApiSourceSanitizer.CreateApiSource(source.Source, source.Name)))
            .ToArray();
        var document = new CxModuleApiMetadataDocument(
            CxModuleApiMetadataLoader.CurrentSchemaVersion,
            CurrentModuleMetadataFormatVersion,
            project.Name,
            project.Version,
            sources);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });
        File.WriteAllText(outputPath, json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
