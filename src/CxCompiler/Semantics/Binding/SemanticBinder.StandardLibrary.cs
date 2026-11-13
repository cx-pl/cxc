namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    private static void AddCoreStandardLibraryDeclarations(Action<string> add)
    {
        add("""
            namespace System;

            public static class Math {
                public static extern const double E; // 2.7182818284590452354
                public static extern const double Pi; // 3.14159265358979323846
                public static extern const double Tau; // 6.283185307179586476925
                public static extern const double Sqrt2; // 1.41421356237
                public static extern const double Sqrt3; // 1.73205080757

                public extern static int Abs(int value);
                public extern static long Abs(long value);
                public extern static float Abs(float value);
                public extern static double Abs(double value);
                public extern static int Clamp(int value, int min, int max);
                public extern static long Clamp(long value, long min, long max);
                public extern static float Clamp(float value, float min, float max);
                public extern static double Clamp(double value, double min, double max);
                public extern static int Floor(int value);
                public extern static long Floor(long value);
                public extern static float Floor(float value);
                public extern static double Floor(double value);
                public extern static int Ceiling(int value);
                public extern static long Ceiling(long value);
                public extern static float Ceiling(float value);
                public extern static double Ceiling(double value);
                public extern static int Min(int value1, int value2);
                public extern static long Min(long value1, long value2);
                public extern static float Min(float value1, float value2);
                public extern static double Min(double value1, double value2);
                public extern static int Max(int value1, int value2);
                public extern static long Max(long value1, long value2);
                public extern static float Max(float value1, float value2);
                public extern static double Max(double value1, double value2);
                public extern static float Round(float value);
                public extern static float Round(float value, int decimals);
                public extern static double Round(double value);
                public extern static double Round(double value, int decimals);
                public extern static float Log(float value);
                public extern static double Log(double value);
                public extern static float Log(float value, float newBase);
                public extern static double Log(double value, double newBase);
                public extern static float ReciprocalEstimate(float value);
                public extern static double ReciprocalEstimate(double value);
                public extern static float ReciprocalSqrtEstimate(float value);
                public extern static double ReciprocalSqrtEstimate(double value);
            }
            """);

        add("""
            namespace System;

            public struct Byte {
                public static extern byte? TryParse(string text);

                private byte _value;

                public static extern const byte MinValue;
                public static extern const byte MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct SByte {
                public static extern sbyte? TryParse(string text);

                private sbyte _value;

                public static extern const sbyte MinValue;
                public static extern const sbyte MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct Short {
                public static extern short? TryParse(string text);

                private short _value;

                public static extern const short MinValue;
                public static extern const short MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct UShort {
                public static extern ushort? TryParse(string text);

                private ushort _value;

                public static extern ushort MinValue;
                public static extern ushort MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct Int {
                public static extern int? TryParse(string text);

                private int _value;

                public static extern const int MinValue;
                public static extern const int MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct UInt {
                public static extern uint? TryParse(string text);

                private uint _value;

                public static extern uint MinValue;
                public static extern uint MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct Long {
                public static extern long? TryParse(string text);

                private long _value;

                public static extern const long MinValue;
                public static extern const long MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct ULong {
                public static extern ulong? TryParse(string text);

                private ulong _value;

                public static extern ulong MinValue;
                public static extern ulong MaxValue;
            }

            """);

        add("""
            namespace System;

            public struct Float {
                public static extern float? TryParse(string text);

                private float _value;

                public static extern const float MinValue;
                public static extern const float MaxValue;
                public int Sign { extern const get; }
            }

            """);

        add("""
            namespace System;

            public struct Double {
                public static extern double? TryParse(string text);

                private double _value;

                public static extern const double MinValue;
                public static extern const double MaxValue;
                public int Sign { extern const get; }
            }

            """);

        add("""
            namespace System;

            public struct Decimal {
                private decimal _value;
                public static extern const decimal MinValue;
                public static extern const decimal MaxValue;
                public static extern decimal? TryParse(string text);
            }

            """);

        add("""
            namespace System;

            public struct Uuid {
                private uint _a;
                private uint _b;
                private uint _c;
                private uint _d;

                public static extern Uuid Zero;
                public static extern const Uuid NamespaceDns;
                public static extern const Uuid NamespaceUrl;
                public static extern const Uuid NamespaceOid;
                public static extern const Uuid NamespaceX500;

                public static extern Uuid NewUuidV1();
                public static extern Uuid NewUuidV2(byte localDomain, uint localIdentifier);
                public static extern Uuid NewUuidV3(Uuid namespaceId, string name);
                public static extern Uuid NewUuidV4();
                public static extern Uuid NewUuidV5(Uuid namespaceId, string name);
                public static extern Uuid NewUuidV6();
                public static extern Uuid NewUuidV7();
                public static extern Uuid NewUuidV8();
                public static extern Uuid NewUuidV8(ulong customA, ushort customB, ulong customC);
                public uint Version { extern const get; }
                public extern string ToString() const;

                public constructor(uint a, uint b, uint c, uint d) {
                    _a = a;
                    _b = b;
                    _c = c;
                    _d = d;
                }
            }

            """);

        add("""
            namespace System;

            public final class StringBuilder : Object {
                private uint _length;
                private uint _capacity;
                private uint _characters;
                private ptr _data;

                public extern constructor();
                public extern constructor(uint capacity);
                public uint Length { extern const get; }
                public uint Capacity { extern const get; }
                public extern uint EnsureCapacity(uint capacity);
                public extern StringBuilder Append(string value);
                public extern StringBuilder Append(char value);
                public extern StringBuilder Append(bool value);
                public extern StringBuilder Append(int value);
                public extern StringBuilder Append(uint value);
                public extern StringBuilder Append(long value);
                public extern StringBuilder Append(ulong value);
                public extern StringBuilder Append(float value);
                public extern StringBuilder Append(double value);
                public extern StringBuilder Append(decimal value);
                public extern StringBuilder Append(byte value);
                public extern StringBuilder Append(sbyte value);
                public extern StringBuilder Append(short value);
                public extern StringBuilder Append(ushort value);
                public extern StringBuilder AppendLine();
                public extern StringBuilder AppendLine(string value);
                public extern StringBuilder Clear();
                public override extern string ToString() const;
            }

            """);
    }
}
