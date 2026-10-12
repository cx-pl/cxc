using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public sealed record GenericConceptConstraint(
    string ParameterName,
    IReadOnlyList<QualifiedIdentifier> Concepts);
