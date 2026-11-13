using System.Buffers.Binary;

namespace CxCompiler.Generators;

public enum GeneratorMessageKind : ushort
{
    Handshake = 1,
    Snapshot = 2,
    AdditionalFile = 3,
    Generate = 4,
    SourceOutput = 5,
    Diagnostic = 6,
    Cancellation = 7,
    Complete = 8,
    Failure = 9,
}

public sealed record GeneratorProtocolFrame(
    ushort Version,
    GeneratorMessageKind Kind,
    ulong RequestId,
    byte[] Payload);

/// <summary>Checked length-prefixed framing for the out-of-process generator protocol.</summary>
public static class GeneratorProtocol
{
    public const ushort CurrentVersion = 3;
    public const int HeaderLength = 20;
    public const int MaximumPayloadLength = 64 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "CXGN"u8;

    public static byte[] Encode(GeneratorProtocolFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Payload);
        ValidateKind(frame.Kind);
        if (frame.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported generator protocol version {frame.Version}.");
        if (frame.Payload.Length > MaximumPayloadLength)
            throw new InvalidDataException("Generator protocol payload exceeds the configured limit.");

        var bytes = new byte[HeaderLength + frame.Payload.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), frame.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)frame.Kind);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), frame.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)frame.Payload.Length);
        frame.Payload.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    public static void Write(Stream stream, GeneratorProtocolFrame frame)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Encode(frame));
    }

    /// <summary>Reads exactly one frame. Returns null only for clean EOF before a header.</summary>
    public static GeneratorProtocolFrame? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderLength];
        var first = stream.Read(header[..1]);
        if (first == 0) return null;
        ReadExactly(stream, header[1..]);
        if (!header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid generator protocol magic.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported generator protocol version {version}.");
        var rawKind = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var kind = (GeneratorMessageKind)rawKind;
        ValidateKind(kind);
        var requestId = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (length > MaximumPayloadLength)
            throw new InvalidDataException("Generator protocol payload exceeds the configured limit.");

        var payload = new byte[(int)length];
        ReadExactly(stream, payload);
        return new GeneratorProtocolFrame(version, kind, requestId, payload);
    }

    /// <summary>Asynchronously reads one frame, returning null only for clean EOF before a header.</summary>
    public static async ValueTask<GeneratorProtocolFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken)
            .ConfigureAwait(false);
        if (first == 0) return null;
        await ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid generator protocol magic.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported generator protocol version {version}.");
        var rawKind = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var kind = (GeneratorMessageKind)rawKind;
        ValidateKind(kind);
        var requestId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        if (length > MaximumPayloadLength)
            throw new InvalidDataException("Generator protocol payload exceeds the configured limit.");

        var payload = new byte[(int)length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new GeneratorProtocolFrame(version, kind, requestId, payload);
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var count = stream.Read(destination);
            if (count == 0) throw new EndOfStreamException("Truncated generator protocol frame.");
            destination = destination[count..];
        }
    }

    private static void ReadExactly(Stream stream, byte[] destination) =>
        ReadExactly(stream, destination.AsSpan());

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            var count = await stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Truncated generator protocol frame.");
            destination = destination[count..];
        }
    }

    private static void ValidateKind(GeneratorMessageKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new InvalidDataException($"Unknown generator protocol message kind {(ushort)kind}.");
    }
}
