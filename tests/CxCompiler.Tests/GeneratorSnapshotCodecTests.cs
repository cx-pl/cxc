using CxCompiler.Generators;

namespace CxCompiler.Tests;

public sealed class GeneratorSnapshotCodecTests
{
    [Fact]
    public void RoundTripsCompleteSnapshotDeterministically()
    {
        var snapshot = new GeneratorInputSnapshot(
            "Mödel",
            [new GeneratorSyntaxSnapshot("class", "class C { }", "M.C", Location("src/c.cx"))],
            [new GeneratorAnnotationSnapshot("M.Mark", "M.C", ["label"], ["δοκιμή"], Location("src/c.cx"))],
            [new GeneratorTypeSnapshot(
                "M.C`1", "M.C`1", "M", "M", "Public", "Class", true, false, true, true, ["T"],
                ["System.Object"],
                [new GeneratorGenericConstraintSnapshot("T", "ReferenceType", [], [])],
                [new GeneratorMemberSnapshot(
                    "M.C`1.Create(System.String)", "Create", "M.C<T>", "Public", "Constructor",
                    false, false, false,
                    [new GeneratorParameterSnapshot("label", "System.String", false, null, Location("src/c.cx"))],
                    Location("src/c.cx"))],
                Location("src/c.cx")),
                new GeneratorTypeSnapshot("M.Mode", "M.Mode", "M", "M", "Public", "Enum",
                    false, false, false, false, [], [], [],
                    [new GeneratorMemberSnapshot("M.Mode.Ready", "Ready", "System.Int", "Public",
                        "EnumValue", false, true, false, [], Location("src/c.cx"), "7")],
                    Location("src/c.cx"))],
            [new GeneratorClosedGenericSnapshot("M.C`1", "M.C<System.Int>", "M", ["System.Int"],
                Location("src/use.cx"))]);

        var encoded = GeneratorSnapshotCodec.Encode(snapshot);
        var decoded = GeneratorSnapshotCodec.Decode(encoded);

        Assert.Equal(snapshot.ProjectName, decoded.ProjectName);
        Assert.Equal(snapshot.SyntaxCandidates.Single(), decoded.SyntaxCandidates.Single());
        Assert.Equal(snapshot.Annotations.Single().ArgumentValues, decoded.Annotations.Single().ArgumentValues);
        Assert.Equal(snapshot.Types.Single(type => type.Kind == "Class").Members.Single()
                .Parameters.Single(),
            decoded.Types.Single(type => type.Kind == "Class").Members.Single().Parameters.Single());
        Assert.Equal(snapshot.Types.Single(type => type.Kind == "Class").BaseTypeIdentities,
            decoded.Types.Single(type => type.Kind == "Class").BaseTypeIdentities);
        Assert.False(decoded.Types.Single(type => type.Kind == "Class").IsAbstract);
        Assert.True(decoded.Types.Single(type => type.Kind == "Class").IsFinal);
        var expectedConstraint = snapshot.Types.Single(type => type.Kind == "Class").GenericConstraints.Single();
        var actualConstraint = decoded.Types.Single(type => type.Kind == "Class").GenericConstraints.Single();
        Assert.Equal(expectedConstraint.ParameterName, actualConstraint.ParameterName);
        Assert.Equal(expectedConstraint.Kind, actualConstraint.Kind);
        Assert.Equal(expectedConstraint.TypeIdentities, actualConstraint.TypeIdentities);
        Assert.Equal(expectedConstraint.ConceptNames, actualConstraint.ConceptNames);
        Assert.Equal("7", decoded.Types.Single(type => type.Kind == "Enum")
            .Members.Single().ConstantValue);
        Assert.Equal(snapshot.ClosedGenericRequests.Single().DefinitionIdentity,
            decoded.ClosedGenericRequests.Single().DefinitionIdentity);
        Assert.Equal(snapshot.ClosedGenericRequests.Single().TypeArguments,
            decoded.ClosedGenericRequests.Single().TypeArguments);
        Assert.Equal(encoded, GeneratorSnapshotCodec.Encode(decoded));
    }

    [Fact]
    public void RejectsInvalidUtf8TruncationTrailingBytesAndOversizedStrings()
    {
        Assert.Throws<InvalidDataException>(() => GeneratorSnapshotCodec.Decode([1, 0, 0, 0, 0xff]));
        Assert.Throws<EndOfStreamException>(() => GeneratorSnapshotCodec.Decode([0]));
        var validEmpty = GeneratorSnapshotCodec.Encode(new GeneratorInputSnapshot(
            string.Empty, [], [], [], []));
        Assert.Throws<InvalidDataException>(() => GeneratorSnapshotCodec.Decode([.. validEmpty, 0]));
        Assert.Throws<InvalidDataException>(() => GeneratorSnapshotCodec.Decode([1, 0, 16, 0]));
    }

    private static GeneratorLocationSnapshot Location(string path) => new(path, 1, 2, 3, 4);
}
