namespace CxCompiler.Generators;

public sealed record GeneratorInputSnapshot(
    string ProjectName,
    IReadOnlyList<GeneratorSyntaxSnapshot> SyntaxCandidates,
    IReadOnlyList<GeneratorAnnotationSnapshot> Annotations,
    IReadOnlyList<GeneratorTypeSnapshot> Types,
    IReadOnlyList<GeneratorClosedGenericSnapshot> ClosedGenericRequests);

public sealed record GeneratorLocationSnapshot(
    string Path,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record GeneratorSyntaxSnapshot(
    string Kind,
    string Text,
    string Identity,
    GeneratorLocationSnapshot Location);

public sealed record GeneratorAnnotationSnapshot(
    string MetadataName,
    string TargetIdentity,
    IReadOnlyList<string> ArgumentNames,
    IReadOnlyList<string> ArgumentValues,
    GeneratorLocationSnapshot Location);

public sealed record GeneratorTypeSnapshot(
    string Identity,
    string MetadataName,
    string ModuleName,
    string NamespaceName,
    string Visibility,
    string Kind,
    bool IsPartial,
    bool IsAbstract,
    bool IsFinal,
    bool IsGenericDefinition,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> BaseTypeIdentities,
    IReadOnlyList<GeneratorGenericConstraintSnapshot> GenericConstraints,
    IReadOnlyList<GeneratorMemberSnapshot> Members,
    GeneratorLocationSnapshot Location);

public sealed record GeneratorGenericConstraintSnapshot(
    string ParameterName,
    string Kind,
    IReadOnlyList<string> TypeIdentities,
    IReadOnlyList<string> ConceptNames);

public sealed record GeneratorMemberSnapshot(
    string Identity,
    string Name,
    string TypeIdentity,
    string Visibility,
    string Kind,
    bool IsStatic,
    bool IsReadOnly,
    bool IsNullable,
    IReadOnlyList<GeneratorParameterSnapshot> Parameters,
    GeneratorLocationSnapshot Location,
    string? ConstantValue = null);

public sealed record GeneratorParameterSnapshot(
    string Name,
    string TypeIdentity,
    bool IsNullable,
    string? DefaultValue,
    GeneratorLocationSnapshot Location);

public sealed record GeneratorClosedGenericSnapshot(
    string DefinitionIdentity,
    string ClosedIdentity,
    string OwnerModule,
    IReadOnlyList<string> TypeArguments,
    GeneratorLocationSnapshot Location);
