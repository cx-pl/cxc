using System.Text;
using CxCompiler.Generators;

if (args.Length != 1)
    return 2;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var requestId = 0UL;
var sawRequest = false;
var requestFrames = new List<GeneratorProtocolFrame>();
while (await GeneratorProtocol.ReadAsync(input) is { } frame)
{
    requestId = frame.RequestId;
    sawRequest = true;
    requestFrames.Add(frame);
}

if (!sawRequest)
    return 3;

switch (args[0])
{
    case "crash":
        Console.Error.Write("fixture crashed");
        return 23;
    case "hang":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 4;
    case "invalid":
        await output.WriteAsync(new byte[] { 1, 2, 3 });
        await output.FlushAsync();
        return 0;
    case "failure":
        Console.Error.Write("fixture failure detail");
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Failure, requestId, Encoding.UTF8.GetBytes("fixture failed")));
        return 0;
    case "success":
        Console.Error.Write("fixture log");
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Handshake, requestId, []));
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.SourceOutput, requestId,
            GeneratorWorkerMessages.EncodeSourceOutput(new GeneratorSourceOutput(
                "Generated.g.cx", "public final class Generated { public static int GetValue() { return 42; } }"))));
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Diagnostic, requestId,
            GeneratorWorkerMessages.EncodeDiagnostic(new GeneratorDiagnosticOutput(
                "CXG001", "fixture warning", "warning",
                new GeneratorLocationSnapshot("input.cx", 1, 0, 1, 4)))));
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Complete, requestId, []));
        return 0;
    case "integration":
        var snapshotFrame = requestFrames.Single(item => item.Kind == GeneratorMessageKind.Snapshot);
        var snapshot = GeneratorSnapshotCodec.Decode(snapshotFrame.Payload);
        if (!snapshot.Types.Any(type => type.MetadataName == "Target"))
            return 6;
        var additional = GeneratorWorkerMessages.DecodeAdditionalFile(
            requestFrames.Single(item => item.Kind == GeneratorMessageKind.AdditionalFile).Payload);
        if (additional.Path != "value.txt" || additional.ContentHash.Length != 64)
            return 7;
        var expected = int.Parse(additional.Content.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.SourceOutput, requestId,
            GeneratorWorkerMessages.EncodeSourceOutput(new GeneratorSourceOutput(
                "Generated.g.cx", $"public final class Generated {{ public static int GetValue() {{ return {expected}; }} }}"))));
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Complete, requestId, []));
        return 0;
    case "consumer":
        var consumerSnapshot = GeneratorSnapshotCodec.Decode(
            requestFrames.Single(item => item.Kind == GeneratorMessageKind.Snapshot).Payload);
        if (!consumerSnapshot.Types.Any(type => type.MetadataName == "Generated" &&
            type.ModuleName == "GeneratedLibrary"))
            return 8;
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.SourceOutput, requestId,
            GeneratorWorkerMessages.EncodeSourceOutput(new GeneratorSourceOutput(
                "ConsumerWitness.g.cx", "public final class ConsumerWitness {}"))));
        await WriteAsync(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Complete, requestId, []));
        return 0;
    default:
        return 5;
}

async Task WriteAsync(GeneratorProtocolFrame frame)
{
    var bytes = GeneratorProtocol.Encode(frame);
    await output.WriteAsync(bytes);
    await output.FlushAsync();
}
