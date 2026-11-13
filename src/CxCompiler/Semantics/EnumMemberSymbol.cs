using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed record EnumMemberSymbol(
    string ModuleName,
    EnumMemberDeclaration Declaration,
    TypeBase EnumType);
