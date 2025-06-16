using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public static class BuiltInSystemTypes
{
    // TODO: Read these from cxcore module

    private static readonly QualifiedIdentifier SystemNamespace = new QualifiedIdentifier("System");

    public static readonly BoolType Bool = new BoolType(SystemNamespace);
    public static readonly CharType Char = new CharType(SystemNamespace);

    public static readonly SByteType SByte = new SByteType(SystemNamespace);
    public static readonly ShortType Short = new ShortType(SystemNamespace);
    public static readonly IntType Int = new IntType(SystemNamespace);
    public static readonly LongType Long = new LongType(SystemNamespace);

    public static readonly ByteType Byte = new ByteType(SystemNamespace);
    public static readonly UShortType UShort = new UShortType(SystemNamespace);
    public static readonly UIntType UInt = new UIntType(SystemNamespace);
    public static readonly ULongType ULong = new ULongType(SystemNamespace);

    public static readonly FloatType Float = new FloatType(SystemNamespace);
    public static readonly DoubleType Double = new DoubleType(SystemNamespace);

    public static readonly ObjectType Object = new ObjectType(SystemNamespace);
    public static readonly StringType String = new StringType(SystemNamespace);
    public static readonly PtrType Ptr = new PtrType(SystemNamespace);

    public static readonly FunctionType Function = new FunctionType(SystemNamespace);
    public static readonly VoidType Void = new VoidType(SystemNamespace);
}