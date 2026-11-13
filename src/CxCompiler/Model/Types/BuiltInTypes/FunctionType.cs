using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class FunctionType : TypeBase
{
    public TypeBase ReturnType { get; }
    public IReadOnlyList<TypeBase> ParameterTypes { get; }

    public FunctionType(
        QualifiedIdentifier @namespace,
        TypeBase returnType,
        IReadOnlyList<TypeBase> parameterTypes)
        : base("Function", @namespace)
    {
        ReturnType = returnType;
        ParameterTypes = parameterTypes;
    }
}
