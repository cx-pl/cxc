using CxCompiler.Model.Common;

namespace CxCompiler.Tests;

public sealed class QualifiedIdentifierTests
{
    [Fact]
    public void ComposesAndComparesByValue()
    {
        var identifier = new QualifiedIdentifier("Acme", "Tools") + "Widget";

        Assert.Equal("Acme.Tools.Widget", identifier.ToString());
        Assert.Equal(new QualifiedIdentifier("Acme", "Tools", "Widget"), identifier);
    }

    [Fact]
    public void DeclarationScopesComposeNestedNamespaces()
    {
        var root = new DeclarationScope(
            @namespace: new QualifiedIdentifier("Acme", "Tools"));
        var nested = new DeclarationScope(root, "Widget");

        Assert.Equal("Acme.Tools.Widget", nested.FullNamespace.ToString());
    }
}
