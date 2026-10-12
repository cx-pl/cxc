namespace CxCompiler.Model.Types;

public enum GenericConstraintKind
{
    Type,
    ReferenceType,
    ValueType,
}

public sealed record GenericTypeConstraint(
    string ParameterName,
    IReadOnlyList<TypeBase> Types,
    GenericConstraintKind Kind = GenericConstraintKind.Type);
