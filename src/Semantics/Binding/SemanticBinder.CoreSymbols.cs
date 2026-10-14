using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void AddCoreSymbols()
    {
        AddCoreFunction("Console", "Read", [BuiltInSystemTypes.UInt], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "ReadLine", [], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "Write", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreFunction("Console", "WriteLine", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreObjectMethods();
        AddCoreStringOperators();
        AddCoreStringTranscoding();
        AddCoreConstructor(BuiltInSystemTypes.Object, ClassType.Class, "Object", [], 1);
        AddCoreProperty(
            BuiltInSystemTypes.Object,
            ClassType.Class,
            "Object",
            "Type",
            new NamedType("TypeInfo", []),
            [new PropertyAccessorSymbol("get", true, [])]);
        if (FindCoreClass("System.Reflection.TypeInfo") is null)
        {
            AddCoreTypeInfoCreateInstance();
        }
        AddCoreFunctionInfoTypes();
        AddCoreThreadingTypes();
        AddCoreAnnotationTypes();
        AddCoreConstructor(BuiltInSystemTypes.Exception, ClassType.Class, "Exception", [], 3);
        AddCoreConstructor(BuiltInSystemTypes.Exception, ClassType.Class, "Exception",
            [BuiltInSystemTypes.String], 1);
        AddCoreConstructor(BuiltInSystemTypes.Exception, ClassType.Class, "Exception",
            [BuiltInSystemTypes.String, BuiltInSystemTypes.Exception], 2);
        AddCoreProperty(BuiltInSystemTypes.Exception, ClassType.Class, "Exception", "Message",
            BuiltInSystemTypes.String, [new PropertyAccessorSymbol("get", true, [])]);
        AddCoreConstructor(
            BuiltInSystemTypes.String,
            ClassType.Class,
            "String",
            [BuiltInSystemTypes.UInt, BuiltInSystemTypes.Char],
            3);
        AddCoreProperty(
            BuiltInSystemTypes.String,
            ClassType.Class,
            "String",
            "Length",
            BuiltInSystemTypes.UInt,
            [new PropertyAccessorSymbol("get", true, [])]);
        AddCoreProperty(
            BuiltInSystemTypes.String,
            ClassType.Class,
            "String",
            "Item",
            BuiltInSystemTypes.Char,
            [new PropertyAccessorSymbol("get", true, [BuiltInSystemTypes.UInt])]);
    }

    private void AddCoreTypeInfoCreateInstance()
    {
        var typeInfoScope = new DeclarationScope(
            @namespace: new QualifiedIdentifier("System", "Reflection"));
        var typeInfoDeclaration = new ClassDeclaration(
            [ClassModifier.Public],
            partial: false,
            ClassType.Struct,
            customClassTypeName: null,
            name: "TypeInfo",
            genericTypeNames: [],
            typeInfoScope);
        var typeInfo = new NamedType("TypeInfo", []);
        typeInfo.SetResolvedType(
            new QualifiedIdentifier("System", "Reflection", "TypeInfo"),
            "cxcore",
            ClassType.Struct);
        _types.Add(new TypeSymbol("cxcore", typeInfoDeclaration, typeInfo));

        var fieldInfo = new NamedType("FieldInfo", []);
        fieldInfo.SetResolvedType(
            new QualifiedIdentifier("System", "Reflection", "FieldInfo"),
            "cxcore",
            ClassType.Struct);
        var functionInfo = new NamedType("FunctionInfo", []);
        functionInfo.SetResolvedType(
            new QualifiedIdentifier("System", "Reflection", "FunctionInfo"),
            "cxcore",
            ClassType.Struct);

        foreach (var (name, fieldType) in new (string Name, TypeBase Type)[]
        {
            ("Hash", BuiltInSystemTypes.ULong),
            ("Flags", BuiltInSystemTypes.UInt),
            ("Size", BuiltInSystemTypes.UInt),
            ("Name", BuiltInSystemTypes.String),
            ("Namespace", BuiltInSystemTypes.String),
            ("GenericParams", new ArrayType(typeInfo)),
            ("BaseType", new NullableType(typeInfo)),
            ("Interfaces", new ArrayType(typeInfo)),
            ("GenericArity", BuiltInSystemTypes.UInt),
            ("Fields", new ArrayType(fieldInfo)),
            ("Functions", new ArrayType(functionInfo)),
        })
        {
            var field = new FieldDeclaration(
                name,
                fieldType,
                [MemberModifier.Public],
                typeInfoDeclaration);
            typeInfoDeclaration.MemberDeclarations.AddDeclaration(field);
            _fields.Add(new FieldSymbol("cxcore", typeInfo, ClassType.Struct, field));
        }

        var returnType = BuiltInSystemTypes.Object;
        var declaration = new FunctionDeclaration(
            "CreateInstance",
            typeInfoDeclaration.FullName,
            returnType,
            [MemberModifier.Public, MemberModifier.Extern],
            typeInfoDeclaration,
            @const: true);
        typeInfoDeclaration.MemberDeclarations.AddDeclaration(declaration);
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier(typeInfoDeclaration.FullName, declaration.Name),
            [],
            returnType,
            declaration: declaration));

        var createArgumentsParameter = new FunctionParameter(
            "arguments", new ArrayType(BuiltInSystemTypes.Object), null);
        var createWithArgumentsDeclaration = new FunctionDeclaration(
            "CreateInstance",
            typeInfoDeclaration.FullName,
            returnType,
            [MemberModifier.Public, MemberModifier.Extern],
            typeInfoDeclaration,
            @const: true);
        createWithArgumentsDeclaration.AddParameter(createArgumentsParameter);
        typeInfoDeclaration.MemberDeclarations.AddDeclaration(createWithArgumentsDeclaration);
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier(typeInfoDeclaration.FullName, createWithArgumentsDeclaration.Name),
            [createArgumentsParameter.ParameterType],
            returnType,
            overloadIndex: 2,
            declaration: createWithArgumentsDeclaration));

        var invokeParameters = new[]
        {
            new FunctionParameter("target", BuiltInSystemTypes.Object, null),
            new FunctionParameter("name", BuiltInSystemTypes.String, null),
            new FunctionParameter("arguments", new ArrayType(BuiltInSystemTypes.Object), null),
        };
        var invokeDeclaration = new FunctionDeclaration(
            "Invoke",
            typeInfoDeclaration.FullName,
            returnType,
            [MemberModifier.Public, MemberModifier.Extern],
            typeInfoDeclaration,
            @const: true);
        foreach (var parameter in invokeParameters)
        {
            invokeDeclaration.AddParameter(parameter);
        }
        typeInfoDeclaration.MemberDeclarations.AddDeclaration(invokeDeclaration);
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier(typeInfoDeclaration.FullName, invokeDeclaration.Name),
            invokeParameters.Select(parameter => parameter.ParameterType).ToArray(),
            returnType,
            declaration: invokeDeclaration));
    }

    private void AddCoreFunctionInfoTypes()
    {
        var typeInfo = new NamedType("TypeInfo", []);
        typeInfo.SetResolvedType(
            new QualifiedIdentifier("System", "Reflection", "TypeInfo"),
            "cxcore",
            ClassType.Struct);
        var functionParamInfo = new NamedType("FunctionParamInfo", []);
        functionParamInfo.SetResolvedType(
            new QualifiedIdentifier("System", "Reflection", "FunctionParamInfo"),
            "cxcore",
            ClassType.Struct);
        var fieldInfo = new NamedType("FieldInfo", []);
        fieldInfo.SetResolvedType(
            new QualifiedIdentifier("System", "Reflection", "FieldInfo"),
            "cxcore",
            ClassType.Struct);

        AddCoreReflectionStruct(
            "FieldInfo",
            [
                ("Flags", BuiltInSystemTypes.UInt),
                ("Offset", BuiltInSystemTypes.UInt),
                ("Type", typeInfo),
                ("Name", BuiltInSystemTypes.String),
            ]);
        AddCoreReflectionStruct(
            "FunctionParamInfo",
            [
                ("Flags", BuiltInSystemTypes.UInt),
                ("Type", typeInfo),
                ("Name", BuiltInSystemTypes.String),
                ("DefaultValue", BuiltInSystemTypes.Ptr),
            ]);
        AddCoreReflectionStruct(
            "FunctionInfo",
            [
                ("Flags", BuiltInSystemTypes.UInt),
                ("Offset", BuiltInSystemTypes.UInt),
                ("ReturnType", typeInfo),
                ("Name", BuiltInSystemTypes.String),
                ("Params", new ArrayType(functionParamInfo)),
            ]);
    }

    private void AddCoreReflectionStruct(
        string name,
        IReadOnlyList<(string Name, TypeBase Type)> fields)
    {
        var fullName = new QualifiedIdentifier("System", "Reflection", name);
        if (FindCoreClass(fullName.ToString()) is not null)
        {
            return;
        }

        var scope = new DeclarationScope(
            @namespace: new QualifiedIdentifier("System", "Reflection"));
        var declaration = new ClassDeclaration(
            [ClassModifier.Public],
            partial: false,
            ClassType.Struct,
            customClassTypeName: null,
            name,
            genericTypeNames: [],
            scope)
        {
            ProjectName = "cxcore",
        };
        var type = new NamedType(name, []);
        type.SetResolvedType(fullName, "cxcore", ClassType.Struct);
        _types.Add(new TypeSymbol("cxcore", declaration, type));

        foreach (var (fieldName, fieldType) in fields)
        {
            var field = new FieldDeclaration(
                fieldName,
                fieldType,
                [MemberModifier.Public],
                declaration);
            declaration.MemberDeclarations.AddDeclaration(field);
            _fields.Add(new FieldSymbol("cxcore", type, ClassType.Struct, field));
        }
    }

    private void AddCoreFunction(
        string typeName,
        string functionName,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType)
    {
        if (FindCoreClass($"System.{typeName}")?.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Any(function => function.Name == functionName &&
                function.Parameters.Count == parameterTypes.Count) == true)
        {
            return;
        }
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier("System", typeName, functionName),
            parameterTypes,
            returnType));
    }

    private void AddCoreObjectMethods()
    {
        var objectClass = FindCoreClass("System.Object");
        if (objectClass is null)
        {
            objectClass = new ClassDeclaration(
                [ClassModifier.Public],
                partial: false,
                ClassType.Class,
                customClassTypeName: null,
                name: "Object",
                genericTypeNames: [],
                new DeclarationScope(@namespace: new QualifiedIdentifier("System")));
            objectClass.ProjectName = "cxcore";
            _types.Add(new TypeSymbol("cxcore", objectClass, BuiltInSystemTypes.Object));
        }

        AddCoreObjectMethod(objectClass, "ToString", BuiltInSystemTypes.String, []);
        AddCoreObjectMethod(
            objectClass,
            "Equals",
            BuiltInSystemTypes.Bool,
            [BuiltInSystemTypes.Object]);
        AddCoreObjectMethod(objectClass, "GetHashCode", BuiltInSystemTypes.Int, []);
    }

    private void AddCoreObjectMethod(
        ClassDeclaration objectClass,
        string name,
        TypeBase returnType,
        IReadOnlyList<TypeBase> parameterTypes)
    {
        if (objectClass.MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Any(function => function.Name == name &&
                function.Parameters.Count == parameterTypes.Count))
        {
            return;
        }

        var declaration = new FunctionDeclaration(
            name,
            objectClass.FullName,
            returnType,
            [MemberModifier.Public, MemberModifier.Virtual, MemberModifier.Extern],
            objectClass,
            @const: true);
        foreach (var (parameterType, index) in parameterTypes.Select((type, index) => (type, index)))
        {
            declaration.AddParameter(new FunctionParameter($"arg{index}", parameterType, null));
        }
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier(objectClass.FullName, declaration.Name),
            parameterTypes,
            returnType,
            declaration: declaration));
        objectClass.MemberDeclarations.AddDeclaration(declaration);
    }

    private void AddCoreStringOperators()
    {
        var stringClass = FindCoreClass("System.String");
        if (stringClass is null)
        {
            stringClass = new ClassDeclaration(
                [ClassModifier.Public, ClassModifier.Final],
                partial: false,
                ClassType.Class,
                customClassTypeName: null,
                name: "String",
                genericTypeNames: [],
                new DeclarationScope(@namespace: new QualifiedIdentifier("System")));
            stringClass.ProjectName = "cxcore";
            var objectClass = _types.SingleOrDefault(candidate =>
                IsType(candidate.Type, BuiltInSystemTypes.Object))?.Declaration;
            stringClass.SetBaseClass(BuiltInSystemTypes.Object, objectClass);
            _types.Add(new TypeSymbol("cxcore", stringClass, BuiltInSystemTypes.String));
        }

        AddCoreStringOperator(stringClass, "+", "plus", BuiltInSystemTypes.String);
        AddCoreStringOperator(stringClass, "==", "equal", BuiltInSystemTypes.Bool);
        AddCoreStringOperator(stringClass, "!=", "not_equal", BuiltInSystemTypes.Bool);
        AddCoreStringOperator(stringClass, "<", "less", BuiltInSystemTypes.Bool);
        AddCoreStringOperator(stringClass, "<=", "less_equal", BuiltInSystemTypes.Bool);
        AddCoreStringOperator(stringClass, ">", "greater", BuiltInSystemTypes.Bool);
        AddCoreStringOperator(stringClass, ">=", "greater_equal", BuiltInSystemTypes.Bool);
    }

    private void AddCoreStringOperator(
        ClassDeclaration stringClass,
        string token,
        string operatorName,
        TypeBase returnType)
    {
        var name = $"__cx_operator_{operatorName}";
        if (stringClass.MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Any(function => function.Name == name && function.OperatorToken == token))
        {
            return;
        }

        var declaration = new FunctionDeclaration(
            name,
            stringClass.FullName,
            returnType,
            [MemberModifier.Public, MemberModifier.Extern],
            stringClass,
            @const: true,
            operatorToken: token);
        declaration.AddParameter(new FunctionParameter(
            "other", BuiltInSystemTypes.String, null));
        stringClass.MemberDeclarations.AddDeclaration(declaration);
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier(stringClass.FullName, declaration.Name),
            [BuiltInSystemTypes.String],
            returnType,
            declaration: declaration));
    }

    private void AddCoreConstructor(
        TypeBase constructedType,
        ClassType classType,
        string typeName,
        IReadOnlyList<TypeBase> parameterTypes,
        int overloadIndex)
    {
        if (FindCoreClass($"System.{typeName}")?.MemberDeclarations.Declarations
            .OfType<ConstructorDeclaration>()
            .Any(constructor => constructor.Parameters.Count == parameterTypes.Count &&
                constructor.Parameters.Zip(parameterTypes)
                    .All(pair => IsType(pair.First.ParameterType, pair.Second))) == true)
        {
            return;
        }

        _constructors.Add(new ConstructorSymbol(
            constructedType,
            classType,
            new FunctionSymbol(
                "cxcore",
                new QualifiedIdentifier("System", typeName, "__constructor"),
                parameterTypes,
                BuiltInSystemTypes.Void,
                overloadIndex)));
    }

    private void AddCoreProperty(
        TypeBase containingType,
        ClassType classType,
        string typeName,
        string propertyName,
        TypeBase propertyType,
        IReadOnlyList<PropertyAccessorSymbol> accessors)
    {
        if (FindCoreClass($"System.{typeName}")?.MemberDeclarations.Declarations
            .OfType<PropertyDeclaration>()
            .Any(property => property.Name == propertyName) == true)
        {
            return;
        }
        _properties.Add(new PropertySymbol(
            "cxcore",
            new QualifiedIdentifier("System", typeName, propertyName),
            containingType,
            classType,
            propertyType,
            false,
            accessors));
    }

    private ClassDeclaration? FindCoreClass(string fullName) =>
        _project?.CompilationContexts
            .Where(context => (context.ProjectName ?? _project.Name) == "cxcore")
            .SelectMany(context => EnumerateClasses(context.DeclarationScope.Declarations))
            .SingleOrDefault(declaration => declaration.FullName.ToString() == fullName);
}
