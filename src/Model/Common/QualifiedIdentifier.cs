namespace CxCompiler.Model.Common;

public class QualifiedIdentifier
{
    public static readonly QualifiedIdentifier Empty = new();

    public string[] Parts { get; }
    public bool IsEmpty => Parts.Length == 0;

    private QualifiedIdentifier()
    {
        Parts = [];
    }

    public QualifiedIdentifier(string name)
    {
        Parts = [name];
    }

    public QualifiedIdentifier(QualifiedIdentifier @base, string name)
    {
        Parts = [.. @base.Parts, name];
    }

    public QualifiedIdentifier(string @base, QualifiedIdentifier name)
    {
        Parts = [@base, .. name.Parts];
    }

    public QualifiedIdentifier(ReadOnlySpan<string> parts)
    {
        Parts = parts.ToArray();
    }

    public QualifiedIdentifier(params string[] parts)
    {
        Parts = [.. parts];
    }

    public override string ToString()
    {
        return string.Join('.', Parts);
    }

    public static bool operator ==(QualifiedIdentifier left, QualifiedIdentifier right)
    {
        return
            left is not null &&
            right is not null &&
            left.Parts.Length == right.Parts.Length &&
            left.Parts.SequenceEqual(right.Parts);
    }

    public static bool operator !=(QualifiedIdentifier left, QualifiedIdentifier right)
    {
        return !(left == right);
    }

    public override bool Equals(object? obj)
    {
        return
            obj is not null &&
            obj is QualifiedIdentifier &&
            this == (obj as QualifiedIdentifier)!;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            if (Parts == null)
            {
                return 0;
            }

            int hash = 17;
            foreach (var part in Parts)
            {
                hash = hash * 31 + part.GetHashCode();
            }
            return hash;
        }
    }

    public static int Compare(QualifiedIdentifier? left, QualifiedIdentifier? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }
        return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }
}
