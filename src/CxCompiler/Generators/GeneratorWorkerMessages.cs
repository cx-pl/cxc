using System.Buffers.Binary;
using System.Text;

namespace CxCompiler.Generators;

public sealed record GeneratorSourceOutput(string HintName, string Source);
public sealed record GeneratorAdditionalFile(string Path, string ContentHash, string Content);
public sealed record GeneratorDiagnosticOutput(
    string Id,
    string Message,
    string Severity,
    GeneratorLocationSnapshot Location);

/// <summary>Payload encoders for messages emitted by generator workers.</summary>
public static class GeneratorWorkerMessages
{
    private const int MaximumFieldBytes = 1 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeSourceOutput(GeneratorSourceOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var stream = new BoundedPayloadStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        WriteString(writer, output.HintName);
        WriteString(writer, output.Source, allowLarge: true);
        writer.Flush();
        return stream.ToArray();
    }

    public static GeneratorSourceOutput DecodeSourceOutput(ReadOnlySpan<byte> payload)
    {
        var reader = new MessageReader(payload);
        var output = new GeneratorSourceOutput(reader.ReadString(), reader.ReadString(allowLarge: true));
        reader.EnsureEnd();
        return output;
    }

    public static byte[] EncodeAdditionalFile(GeneratorAdditionalFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var stream = new BoundedPayloadStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        WriteString(writer, file.Path);
        WriteString(writer, file.ContentHash);
        WriteString(writer, file.Content, allowLarge: true);
        writer.Flush();
        return stream.ToArray();
    }

    public static GeneratorAdditionalFile DecodeAdditionalFile(ReadOnlySpan<byte> payload)
    {
        var reader = new MessageReader(payload);
        var file = new GeneratorAdditionalFile(
            reader.ReadString(), reader.ReadString(), reader.ReadString(allowLarge: true));
        reader.EnsureEnd();
        return file;
    }

    public static byte[] EncodeDiagnostic(GeneratorDiagnosticOutput diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.Severity is not ("info" or "warning" or "error"))
            throw new InvalidDataException("Unknown generator diagnostic severity.");
        using var stream = new BoundedPayloadStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        WriteString(writer, diagnostic.Id);
        WriteString(writer, diagnostic.Message);
        WriteString(writer, diagnostic.Severity);
        WriteString(writer, diagnostic.Location.Path);
        writer.Write(diagnostic.Location.StartLine);
        writer.Write(diagnostic.Location.StartColumn);
        writer.Write(diagnostic.Location.EndLine);
        writer.Write(diagnostic.Location.EndColumn);
        writer.Flush();
        return stream.ToArray();
    }

    public static GeneratorDiagnosticOutput DecodeDiagnostic(ReadOnlySpan<byte> payload)
    {
        var reader = new MessageReader(payload);
        var id = reader.ReadString();
        var message = reader.ReadString();
        var severity = reader.ReadString();
        if (severity is not ("info" or "warning" or "error"))
            throw new InvalidDataException("Unknown generator diagnostic severity.");
        var location = new GeneratorLocationSnapshot(
            reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32());
        reader.EnsureEnd();
        return new GeneratorDiagnosticOutput(id, message, severity, location);
    }

    private static void WriteString(BinaryWriter writer, string value, bool allowLarge = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8.GetBytes(value);
        var maximum = allowLarge ? GeneratorProtocol.MaximumPayloadLength : MaximumFieldBytes;
        if (bytes.Length > maximum)
            throw new InvalidDataException("Generator worker message field exceeds the configured limit.");
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private sealed class MessageReader(ReadOnlySpan<byte> payload)
    {
        private readonly byte[] _bytes = payload.ToArray();
        private int _offset;

        public string ReadString(bool allowLarge = false)
        {
            var length = ReadUInt32();
            var maximum = allowLarge ? GeneratorProtocol.MaximumPayloadLength : MaximumFieldBytes;
            if (length > maximum) throw new InvalidDataException("Generator worker message field exceeds the configured limit.");
            var bytes = Take((int)length);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Generator worker message contains invalid UTF-8.", exception);
            }
        }

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
        private uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

        public void EnsureEnd()
        {
            if (_offset != _bytes.Length)
                throw new InvalidDataException("Generator worker message has trailing bytes.");
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _bytes.Length - _offset)
                throw new EndOfStreamException("Truncated generator worker message.");
            var value = _bytes.AsSpan(_offset, count);
            _offset += count;
            return value;
        }
    }

    private sealed class BoundedPayloadStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            base.WriteByte(value);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || count > GeneratorProtocol.MaximumPayloadLength - Length)
                throw new InvalidDataException("Generator worker message exceeds the protocol payload limit.");
        }
    }
}
