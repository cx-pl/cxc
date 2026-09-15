using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class NamedType : TypeBase
{
    // TODO: Make null as default value and throw an exception if not set when reading
    public QualifiedIdentifier ResolvedTypeFullName { get; private set; } = new QualifiedIdentifier("void");
    public ClassType ClassType { get; set; } = ClassType.Class;
    public string[] GenericParams { get; }

    public NamedType(string name, string[] genericParams)
        : base(name, QualifiedIdentifier.Empty)
    {
        // TODO: Remove this after binding is implemented
        // Quick type resolution for primitive types
        switch (name)
        {
            case "Array":
                SetResolvedType(new QualifiedIdentifier("System", "Array"), "cxcore", ClassType.Class);
                break;

            case "DateTime":
                SetResolvedType(new QualifiedIdentifier("System", "DateTime"), "cxcore", ClassType.Struct);
                break;

            case "Nullable":
                SetResolvedType(new QualifiedIdentifier("System", "Nullable"), "cxcore", ClassType.Class);
                break;

            case "Exception":
                SetResolvedType(new QualifiedIdentifier("System", "Exception"), "cxcore", ClassType.Class);
                break;

            case "String":
                SetResolvedType(new QualifiedIdentifier("System", "String"), "cxcore", ClassType.Class);
                break;

            case "Random":
                SetResolvedType(new QualifiedIdentifier("System", "Random"), "cxcore", ClassType.Struct);
                break;

            case "TimeSpan":
                SetResolvedType(new QualifiedIdentifier("System", "TimeSpan"), "cxcore", ClassType.Struct);
                break;

            case "Uuid":
                SetResolvedType(new QualifiedIdentifier("System", "Uuid"), "cxcore", ClassType.Struct);
                break;

            case "Void":
                SetResolvedType(new QualifiedIdentifier("System", "Void"), "cxcore", ClassType.Struct);
                break;

            case "FieldInfo":
                SetResolvedType(new QualifiedIdentifier("System", "Reflection", "FieldInfo"), "cxcore", ClassType.Struct);
                break;

            case "FunctionInfo":
                SetResolvedType(new QualifiedIdentifier("System", "Reflection", "FunctionInfo"), "cxcore", ClassType.Struct);
                break;

            case "FunctionParamInfo":
                SetResolvedType(new QualifiedIdentifier("System", "Reflection", "FunctionParamInfo"), "cxcore", ClassType.Struct);
                break;

            case "ModuleInfo":
                SetResolvedType(new QualifiedIdentifier("System", "Reflection", "ModuleInfo"), "cxcore", ClassType.Struct);
                break;

            case "TypeInfo":
                SetResolvedType(new QualifiedIdentifier("System", "Reflection", "TypeInfo"), "cxcore", ClassType.Struct);
                break;

            case "VersionInfo":
                SetResolvedType(new QualifiedIdentifier("System", "Reflection", "VersionInfo"), "cxcore", ClassType.Struct);
                break;

            default:
                break;
        }

        GenericParams = genericParams;
    }

    public void SetResolvedType(QualifiedIdentifier typeFullName, string moduleName, ClassType classType)
    {
        ResolvedTypeFullName = new QualifiedIdentifier(moduleName, typeFullName);
        ClassType = classType;
    }
}
