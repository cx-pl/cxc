using System.Buffers.Binary;
using System.Text;

namespace CxCompiler.Generators;

/// <summary>Deterministic, bounded binary encoding for generator input snapshots.</summary>
public static class GeneratorSnapshotCodec
{
    private const int MaximumStringLength = 1 * 1024 * 1024;
    private const int MaximumCollectionLength = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(GeneratorInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new BoundedPayloadStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        WriteString(writer, snapshot.ProjectName);
        WriteArray(writer, snapshot.SyntaxCandidates, WriteSyntax);
        WriteArray(writer, snapshot.Annotations, WriteAnnotation);
        WriteArray(writer, snapshot.Types, WriteType);
        WriteArray(writer, snapshot.ClosedGenericRequests, WriteClosedGeneric);
        writer.Flush();
        return stream.ToArray();
    }

    public static GeneratorInputSnapshot Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > GeneratorProtocol.MaximumPayloadLength)
            throw new InvalidDataException("Generator snapshot exceeds the protocol payload limit.");
        var reader = new PayloadReader(payload);
        var projectName = reader.ReadString();
        var syntax = reader.ReadArray(reader.ReadSyntax);
        var annotations = reader.ReadArray(reader.ReadAnnotation);
        var types = reader.ReadArray(reader.ReadType);
        var genericRequests = reader.ReadArray(reader.ReadClosedGeneric);
        reader.EnsureEnd();
        return new GeneratorInputSnapshot(
            projectName,
            Array.AsReadOnly(syntax),
            Array.AsReadOnly(annotations),
            Array.AsReadOnly(types),
            Array.AsReadOnly(genericRequests));
    }

    private static void WriteSyntax(BinaryWriter writer, GeneratorSyntaxSnapshot item)
    {
        WriteString(writer, item.Kind);
        WriteString(writer, item.Text);
        WriteString(writer, item.Identity);
        WriteLocation(writer, item.Location);
    }

    private static void WriteAnnotation(BinaryWriter writer, GeneratorAnnotationSnapshot item)
    {
        WriteString(writer, item.MetadataName);
        WriteString(writer, item.TargetIdentity);
        WriteArray(writer, item.ArgumentNames, WriteString);
        WriteArray(writer, item.ArgumentValues, WriteString);
        WriteLocation(writer, item.Location);
    }

    private static void WriteType(BinaryWriter writer, GeneratorTypeSnapshot item)
    {
        WriteString(writer, item.Identity);
        WriteString(writer, item.MetadataName);
        WriteString(writer, item.ModuleName);
        WriteString(writer, item.NamespaceName);
        WriteString(writer, item.Visibility);
        WriteString(writer, item.Kind);
        writer.Write(item.IsPartial);
        writer.Write(item.IsAbstract);
        writer.Write(item.IsFinal);
        writer.Write(item.IsGenericDefinition);
        WriteArray(writer, item.GenericParameters, WriteString);
        WriteArray(writer, item.BaseTypeIdentities, WriteString);
        WriteArray(writer, item.GenericConstraints, WriteGenericConstraint);
        WriteArray(writer, item.Members, WriteMember);
        WriteLocation(writer, item.Location);
    }

    private static void WriteGenericConstraint(BinaryWriter writer,
        GeneratorGenericConstraintSnapshot item)
    {
        WriteString(writer, item.ParameterName);
        WriteString(writer, item.Kind);
        WriteArray(writer, item.TypeIdentities, WriteString);
        WriteArray(writer, item.ConceptNames, WriteString);
    }

    private static void WriteMember(BinaryWriter writer, GeneratorMemberSnapshot item)
    {
        WriteString(writer, item.Identity);
        WriteString(writer, item.Name);
        WriteString(writer, item.TypeIdentity);
        WriteString(writer, item.Visibility);
        WriteString(writer, item.Kind);
        writer.Write(item.IsStatic);
        writer.Write(item.IsReadOnly);
        writer.Write(item.IsNullable);
        WriteArray(writer, item.Parameters, WriteParameter);
        WriteLocation(writer, item.Location);
        WriteOptionalString(writer, item.ConstantValue);
    }

    private static void WriteParameter(BinaryWriter writer, GeneratorParameterSnapshot item)
    {
        WriteString(writer, item.Name);
        WriteString(writer, item.TypeIdentity);
        writer.Write(item.IsNullable);
        writer.Write(item.DefaultValue is not null);
        if (item.DefaultValue is not null) WriteString(writer, item.DefaultValue);
        WriteLocation(writer, item.Location);
    }

    private static void WriteClosedGeneric(BinaryWriter writer, GeneratorClosedGenericSnapshot item)
    {
        WriteString(writer, item.DefinitionIdentity);
        WriteString(writer, item.ClosedIdentity);
        WriteString(writer, item.OwnerModule);
        WriteArray(writer, item.TypeArguments, WriteString);
        WriteLocation(writer, item.Location);
    }

    private static void WriteLocation(BinaryWriter writer, GeneratorLocationSnapshot item)
    {
        WriteString(writer, item.Path);
        writer.Write(item.StartLine);
        writer.Write(item.StartColumn);
        writer.Write(item.EndLine);
        writer.Write(item.EndColumn);
    }

    private static void WriteArray<T>(BinaryWriter writer, IReadOnlyList<T> values, Action<BinaryWriter, T> write)
    {
        if (values.Count > MaximumCollectionLength)
            throw new InvalidDataException("Generator snapshot collection exceeds the configured limit.");
        writer.Write((uint)values.Count);
        foreach (var value in values) write(writer, value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumStringLength)
            throw new InvalidDataException("Generator snapshot string exceeds the configured limit.");
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteOptionalString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) WriteString(writer, value);
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
                throw new InvalidDataException("Generator snapshot exceeds the protocol payload limit.");
        }
    }

    private sealed class PayloadReader(ReadOnlySpan<byte> payload)
    {
        private readonly ReadOnlyMemory<byte> _payload = payload.ToArray();
        private int _offset;

        public string ReadString()
        {
            var length = ReadCount(MaximumStringLength);
            var bytes = Take(length);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Generator snapshot contains invalid UTF-8.", exception);
            }
        }

        public T[] ReadArray<T>(Func<T> read)
        {
            var count = ReadCount(MaximumCollectionLength);
            var values = new T[count];
            for (var i = 0; i < count; i++) values[i] = read();
            return values;
        }

        public GeneratorSyntaxSnapshot ReadSyntax() => new(
            ReadString(), ReadString(), ReadString(), ReadLocation());

        public GeneratorAnnotationSnapshot ReadAnnotation()
        {
            var name = ReadString();
            var target = ReadString();
            var argumentNames = ReadArray(ReadString);
            var argumentValues = ReadArray(ReadString);
            if (argumentNames.Length != argumentValues.Length)
                throw new InvalidDataException("Generator annotation argument arrays have different lengths.");
            return new GeneratorAnnotationSnapshot(
                name, target, Array.AsReadOnly(argumentNames), Array.AsReadOnly(argumentValues), ReadLocation());
        }

        public GeneratorTypeSnapshot ReadType() => new(
            ReadString(), ReadString(), ReadString(), ReadString(), ReadString(), ReadString(),
            ReadBoolean(), ReadBoolean(), ReadBoolean(), ReadBoolean(),
            Array.AsReadOnly(ReadArray(ReadString)),
            Array.AsReadOnly(ReadArray(ReadString)),
            Array.AsReadOnly(ReadArray(ReadGenericConstraint)),
            Array.AsReadOnly(ReadArray(ReadMember)), ReadLocation());

        private GeneratorGenericConstraintSnapshot ReadGenericConstraint() => new(
            ReadString(), ReadString(), Array.AsReadOnly(ReadArray(ReadString)),
            Array.AsReadOnly(ReadArray(ReadString)));

        private GeneratorMemberSnapshot ReadMember()
        {
            var identity = ReadString();
            var name = ReadString();
            var typeIdentity = ReadString();
            var visibility = ReadString();
            var kind = ReadString();
            var isStatic = ReadBoolean();
            var isReadOnly = ReadBoolean();
            var isNullable = ReadBoolean();
            var parameters = Array.AsReadOnly(ReadArray(ReadParameter));
            var location = ReadLocation();
            var constantValue = ReadBoolean() ? ReadString() : null;
            return new GeneratorMemberSnapshot(identity, name, typeIdentity, visibility, kind,
                isStatic, isReadOnly, isNullable, parameters, location, constantValue);
        }

        private GeneratorParameterSnapshot ReadParameter()
        {
            var name = ReadString();
            var type = ReadString();
            var nullable = ReadBoolean();
            var hasDefault = ReadBoolean();
            var defaultValue = hasDefault ? ReadString() : null;
            return new GeneratorParameterSnapshot(name, type, nullable, defaultValue, ReadLocation());
        }

        public GeneratorClosedGenericSnapshot ReadClosedGeneric() => new(
            ReadString(), ReadString(), ReadString(), Array.AsReadOnly(ReadArray(ReadString)), ReadLocation());

        private GeneratorLocationSnapshot ReadLocation() => new(
            ReadString(), ReadInt32(), ReadInt32(), ReadInt32(), ReadInt32());

        public void EnsureEnd()
        {
            if (_offset != _payload.Length)
                throw new InvalidDataException("Generator snapshot payload has trailing bytes.");
        }

        private int ReadCount(int maximum)
        {
            var value = ReadUInt32();
            if (value > maximum)
                throw new InvalidDataException("Generator snapshot length exceeds the configured limit.");
            return (int)value;
        }

        private bool ReadBoolean()
        {
            var value = Take(1)[0];
            return value switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException("Generator snapshot contains an invalid boolean."),
            };
        }

        private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
        private uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _payload.Length - _offset)
                throw new EndOfStreamException("Truncated generator snapshot payload.");
            var result = _payload.Span.Slice(_offset, count);
            _offset += count;
            return result;
        }
    }
}
