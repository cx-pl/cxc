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

    public QualifiedIdentifier(params string[] parts)
    {
        Parts = parts;
    }

    public QualifiedIdentifier(params QualifiedIdentifier?[] parts)
    {
        Parts = [.. parts.SelectMany(p => p?.Parts ?? [])];
    }

    public override string ToString()
    {
        return string.Join('.', Parts);
    }

    public static implicit operator QualifiedIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Empty;
        }

        return new QualifiedIdentifier([name]);
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

    public static QualifiedIdentifier operator +(QualifiedIdentifier? left, QualifiedIdentifier? right)
    {
        if ((left is null || left.IsEmpty) && (right is null || right.IsEmpty))
        {
            return Empty;
        }
        if (left is null || left.IsEmpty)
        {
            return right!;
        }
        if (right is null || right.IsEmpty)
        {
            return left;
        }
        return new QualifiedIdentifier(left, right);
    }

    public override bool Equals(object? obj)
    {
        return
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
