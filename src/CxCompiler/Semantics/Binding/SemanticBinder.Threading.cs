using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    // As with Object/Console/reflection, standalone programs can use the core
    // threading ABI without reparsing the entire cxcore source project.
    private void AddCoreThreadingTypes()
    {
        foreach (var name in new[] { "Thread", "Mutex", "AtomicInt" })
        {
            if (FindCoreClass($"System.Threading.{name}") is not null) continue;
            var owner = new ClassDeclaration([ClassModifier.Public], false,
                ClassType.Struct, null, name, [],
                new DeclarationScope(@namespace: new QualifiedIdentifier("System", "Threading")))
            { ProjectName = "cxcore" };
            var type = new NamedType(name, []);
            type.SetResolvedType(owner.FullName, "cxcore", ClassType.Struct);
            _types.Add(new TypeSymbol("cxcore", owner, type));
            var field = new FieldDeclaration("_state", BuiltInSystemTypes.Ptr,
                [MemberModifier.Private], owner);
            owner.MemberDeclarations.AddDeclaration(field);
            _fields.Add(new FieldSymbol("cxcore", type, ClassType.Struct, field));

            void Method(string methodName, TypeBase result, bool isStatic, bool isConst,
                params (string Name, TypeBase Type)[] parameters)
            {
                var modifiers = isStatic
                    ? new[] { MemberModifier.Public, MemberModifier.Static, MemberModifier.Extern }
                    : new[] { MemberModifier.Public, MemberModifier.Extern };
                var declaration = new FunctionDeclaration(methodName, owner.FullName,
                    result, modifiers, owner, @const: isConst);
                foreach (var parameter in parameters)
                    declaration.AddParameter(new FunctionParameter(parameter.Name, parameter.Type, null));
                owner.MemberDeclarations.AddDeclaration(declaration);
                _symbols.Add(new FunctionSymbol("cxcore", declaration.FullName,
                    parameters.Select(parameter => parameter.Type).ToArray(), result,
                    declaration: declaration));
            }
            void Constructor(int overload, params (string Name, TypeBase Type)[] parameters)
            {
                var declaration = new ConstructorDeclaration(owner.FullName,
                    [MemberModifier.Public, MemberModifier.Extern], owner);
                foreach (var parameter in parameters)
                    declaration.AddParameter(new FunctionParameter(parameter.Name, parameter.Type, null));
                owner.MemberDeclarations.AddDeclaration(declaration);
                _constructors.Add(new ConstructorSymbol(type, ClassType.Struct,
                    new FunctionSymbol("cxcore", declaration.FullName,
                        parameters.Select(parameter => parameter.Type).ToArray(), BuiltInSystemTypes.Void,
                        overload, declaration)));
            }
            void Property(string propertyName, TypeBase result, bool isStatic = false, bool setter = false)
            {
                var accessors = new List<PropertyAccessorSymbol> { new("get", true, []) };
                if (setter) accessors.Add(new PropertyAccessorSymbol("set", false, []));
                _properties.Add(new PropertySymbol("cxcore", new QualifiedIdentifier(owner.FullName, propertyName),
                    type, ClassType.Struct, result, isStatic, accessors));
            }

            switch (name)
            {
                case "Thread":
                    Constructor(1);
                    Method("Start", type, true, false, ("action",
                        new FunctionType(new QualifiedIdentifier("System"), BuiltInSystemTypes.Void, [])));
                    Method("Join", BuiltInSystemTypes.Void, false, true);
                    Method("Sleep", BuiltInSystemTypes.Void, true, false, ("milliseconds", BuiltInSystemTypes.Int));
                    Method("Yield", BuiltInSystemTypes.Void, true, false);
                    Property("IsAlive", BuiltInSystemTypes.Bool);
                    Property("Id", BuiltInSystemTypes.ULong);
                    Property("CurrentId", BuiltInSystemTypes.ULong, true);
                    break;
                case "Mutex":
                    Constructor(1);
                    Method("Lock", BuiltInSystemTypes.Void, false, true);
                    Method("Unlock", BuiltInSystemTypes.Void, false, true);
                    break;
                case "AtomicInt":
                    Constructor(1, ("value", BuiltInSystemTypes.Int));
                    Constructor(2);
                    Property("Value", BuiltInSystemTypes.Int, setter: true);
                    Method("Increment", BuiltInSystemTypes.Int, false, true);
                    Method("Decrement", BuiltInSystemTypes.Int, false, true);
                    Method("Exchange", BuiltInSystemTypes.Int, false, true, ("value", BuiltInSystemTypes.Int));
                    Method("CompareExchange", BuiltInSystemTypes.Int, false, true,
                        ("value", BuiltInSystemTypes.Int), ("comparand", BuiltInSystemTypes.Int));
                    break;
            }
        }
    }
}
