using CxCompiler.Generators;

namespace CxCompiler.Tests;

public sealed class GeneratorProtocolTests
{
    [Fact]
    public void EncodesAndReadsVersionedFramesAcrossShortReads()
    {
        var expected = new GeneratorProtocolFrame(
            GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Snapshot,
            0x0102030405060708,
            [0, 1, 2, 255]);
        using var stream = new ShortReadStream(GeneratorProtocol.Encode(expected), 3);

        var actual = GeneratorProtocol.Read(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Null(GeneratorProtocol.Read(stream));
    }

    [Fact]
    public async Task AsynchronouslyReadsVersionedFramesAcrossShortReads()
    {
        var expected = new GeneratorProtocolFrame(
            GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.AdditionalFile,
            42,
            [7, 8, 9, 10]);
        await using var stream = new AsyncShortReadStream(GeneratorProtocol.Encode(expected), 2);

        var actual = await GeneratorProtocol.ReadAsync(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Null(await GeneratorProtocol.ReadAsync(stream));
    }

    [Fact]
    public void RejectsTruncatedHeadersAndPayloads()
    {
        var bytes = GeneratorProtocol.Encode(new GeneratorProtocolFrame(
            GeneratorProtocol.CurrentVersion, GeneratorMessageKind.Generate, 1, [1, 2, 3]));

        using var truncatedHeader = new MemoryStream(bytes[..8]);
        Assert.Throws<EndOfStreamException>(() => GeneratorProtocol.Read(truncatedHeader));

        using var truncatedPayload = new MemoryStream(bytes[..^1]);
        Assert.Throws<EndOfStreamException>(() => GeneratorProtocol.Read(truncatedPayload));
    }

    [Fact]
    public void RejectsUnknownKindsAndVersions()
    {
        var bytes = GeneratorProtocol.Encode(new GeneratorProtocolFrame(
            GeneratorProtocol.CurrentVersion, GeneratorMessageKind.Handshake, 1, []));

        var unknownKind = bytes.ToArray();
        unknownKind[6] = 0xff;
        unknownKind[7] = 0x7f;
        using var badKind = new MemoryStream(unknownKind);
        Assert.Throws<InvalidDataException>(() => GeneratorProtocol.Read(badKind));

        var unsupportedVersion = bytes.ToArray();
        unsupportedVersion[4] = 1;
        using var badVersion = new MemoryStream(unsupportedVersion);
        Assert.Throws<InvalidDataException>(() => GeneratorProtocol.Read(badVersion));

        var legacyVersion = bytes.ToArray();
        legacyVersion[4] = 2;
        using var legacyFrame = new MemoryStream(legacyVersion);
        Assert.Throws<InvalidDataException>(() => GeneratorProtocol.Read(legacyFrame));
    }

    [Fact]
    public void RoundTripsOutputAndDiagnosticPayloads()
    {
        var source = new GeneratorSourceOutput("Models/Json.g.cx", "public class Generated { }");
        Assert.Equal(source, GeneratorWorkerMessages.DecodeSourceOutput(
            GeneratorWorkerMessages.EncodeSourceOutput(source)));

        var diagnostic = new GeneratorDiagnosticOutput("CXG001", "Unsupported type Ω", "warning",
            new GeneratorLocationSnapshot("src/models.cx", 4, 2, 4, 17));
        Assert.Equal(diagnostic, GeneratorWorkerMessages.DecodeDiagnostic(
            GeneratorWorkerMessages.EncodeDiagnostic(diagnostic)));
        Assert.Throws<InvalidDataException>(() => GeneratorWorkerMessages.EncodeDiagnostic(
            diagnostic with { Severity = "fatal" }));
    }

    [Fact]
    public void RoundTripsAdditionalFilePayloadsWithUnicodeAndHash()
    {
        var file = new GeneratorAdditionalFile("schemas/user.json", "abc123", "{\"name\":\"Ω\"}");

        Assert.Equal(file, GeneratorWorkerMessages.DecodeAdditionalFile(
            GeneratorWorkerMessages.EncodeAdditionalFile(file)));
    }

    private sealed class ShortReadStream(byte[] bytes, int maximumRead) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, maximumRead)]);
    }

    private sealed class AsyncShortReadStream(byte[] bytes, int maximumRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
    }
}
