using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Types;

public sealed class EnumDeclaration : DeclarationBase
{
    private readonly List<EnumMemberDeclaration> _members = [];

    public Visibility Visibility { get; }
    public IReadOnlyList<EnumMemberDeclaration> Members => _members;

    public EnumDeclaration(
        string name,
        QualifiedIdentifier @namespace,
        Visibility visibility)
        : base("enum", @namespace, name)
    {
        Visibility = visibility;
    }

    public void AddMember(EnumMemberDeclaration member)
    {
        if (_members.Any(candidate => candidate.Name == member.Name))
        {
            throw new CompilationErrorException(
                $"Enum '{FullName}' already contains a member named '{member.Name}'.");
        }
        _members.Add(member);
    }
}

public sealed class EnumMemberDeclaration : DeclarationBase
{
    public EnumDeclaration ParentEnum { get; }
    public LiteralExpression? Value { get; }

    public EnumMemberDeclaration(
        string name,
        EnumDeclaration parentEnum,
        LiteralExpression? value)
        : base("enumMember", parentEnum.FullName, name)
    {
        ParentEnum = parentEnum;
        Value = value;
    }
}
