using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed class SemanticBinder
{
    private readonly List<FunctionSymbol> _symbols = [];
    private readonly List<TypeSymbol> _types = [];
    private readonly List<ConstructorSymbol> _constructors = [];
    private readonly List<FieldSymbol> _fields = [];
    private readonly List<PropertySymbol> _properties = [];
    private int _loopDepth;
    private int _breakableDepth;
    private int _objectCreationIndex;
    private int _propertyAssignmentIndex;

    public void Bind(CxProject project)
    {
        _symbols.Clear();
        _types.Clear();
        _constructors.Clear();
        _fields.Clear();
        _properties.Clear();
        AddCoreSymbols();

        foreach (var context in project.CompilationContexts)
        {
            AddProjectTypes(project.Name, context.DeclarationScope.Declarations);
        }

        foreach (var context in project.CompilationContexts)
        {
            ResolveDeclarationTypes(
                context.DeclarationScope.Declarations,
                context.Namespace,
                context.Imports);
            AddProjectSymbols(project.Name, context.DeclarationScope.Declarations);
        }

        BindFieldInitializers();

        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                BindFunction(function, context.Imports);
            }
            foreach (var accessor in EnumeratePropertyAccessors(
                context.DeclarationScope.Declarations))
            {
                BindPropertyAccessor(accessor, context.Imports);
            }
        }
    }

    private void AddCoreSymbols()
    {
        AddCoreFunction("Console", "Read", [BuiltInSystemTypes.UInt], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "ReadLine", [], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "Write", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreFunction("Console", "WriteLine", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreConstructor(BuiltInSystemTypes.Object, ClassType.Class, "Object", [], 1);
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

    private void AddCoreFunction(
        string typeName,
        string functionName,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType)
    {
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier("System", typeName, functionName),
            parameterTypes,
            returnType));
    }

    private void AddCoreConstructor(
        TypeBase constructedType,
        ClassType classType,
        string typeName,
        IReadOnlyList<TypeBase> parameterTypes,
        int overloadIndex)
    {
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
        _properties.Add(new PropertySymbol(
            "cxcore",
            new QualifiedIdentifier("System", typeName, propertyName),
            containingType,
            classType,
            propertyType,
            false,
            accessors));
    }

    private void AddProjectTypes(
        string moduleName,
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>())
        {
            var type = new NamedType(classDeclaration.FullName.ToString(), []);
            type.SetResolvedType(
                classDeclaration.FullName,
                moduleName,
                classDeclaration.ClassType);
            _types.Add(new TypeSymbol(moduleName, classDeclaration, type));
            foreach (var field in classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
            {
                _fields.Add(new FieldSymbol(
                    moduleName,
                    type,
                    classDeclaration.ClassType,
                    field));
            }
            foreach (var property in classDeclaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>())
            {
                _properties.Add(new PropertySymbol(
                    moduleName,
                    property.FullName,
                    type,
                    classDeclaration.ClassType,
                    property.Type,
                    property.IsStatic,
                    property.PropertyAccessorDeclarations
                        .Select(accessor => new PropertyAccessorSymbol(
                            accessor.Name,
                            accessor.Const,
                            accessor.Parameters
                                .Select(parameter => parameter.ParameterType)
                                .ToArray()))
                        .ToArray()));
            }
            AddProjectTypes(
                moduleName,
                classDeclaration.MemberDeclarations.Declarations);
        }
    }

    private void ResolveDeclarationTypes(
        IEnumerable<DeclarationBase> declarations,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case ClassDeclaration classDeclaration:
                    foreach (var field in classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
                    {
                        ResolveTypeReference(field.Type, currentNamespace, imports);
                    }
                    foreach (var property in classDeclaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>())
                    {
                        ResolveTypeReference(property.Type, currentNamespace, imports);
                        foreach (var parameter in property.PropertyAccessorDeclarations
                            .SelectMany(accessor => accessor.Parameters))
                        {
                            ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
                        }
                    }
                    ResolveDeclarationTypes(
                        classDeclaration.MemberDeclarations.Declarations,
                        classDeclaration.Namespace,
                        imports);
                    break;

                case FunctionDeclaration function:
                    ResolveTypeReference(function.ReturnType, currentNamespace, imports);
                    foreach (var parameter in function.Parameters)
                    {
                        ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
                    }
                    break;
            }
        }
    }

    private void AddProjectSymbols(
        string moduleName,
        IEnumerable<DeclarationBase> declarations)
    {
        var overloadIndexes = new Dictionary<QualifiedIdentifier, int>();
        foreach (var function in EnumerateFunctions(declarations))
        {
            overloadIndexes.TryGetValue(function.FullName, out var overloadIndex);
            overloadIndex++;
            overloadIndexes[function.FullName] = overloadIndex;

            var symbol = new FunctionSymbol(
                moduleName,
                function.FullName,
                function.Parameters.Select(parameter => parameter.ParameterType).ToArray(),
                function.ReturnType,
                overloadIndex,
                function);
            _symbols.Add(symbol);

            if (function is ConstructorDeclaration constructor)
            {
                var type = _types.Single(typeSymbol =>
                    ReferenceEquals(typeSymbol.Declaration, constructor.ParentClassDeclaration));
                _constructors.Add(new ConstructorSymbol(
                    type.Type,
                    constructor.ParentClassDeclaration!.ClassType,
                    symbol));
            }
        }
    }

    private void BindFieldInitializers()
    {
        foreach (var field in _fields.Where(field => field.Declaration.Initializer is not null))
        {
            var initializer = (LiteralExpression)field.Declaration.Initializer!;
            var initializerType = BindLiteral(initializer);
            initializer.SetInferredType(initializerType);
            if (!CanAssign(field.Declaration.Type, initializerType))
            {
                throw new CompilationErrorException(
                    $"Cannot initialize field '{field.Declaration.FullName}' of type " +
                    $"'{GetTypeName(field.Declaration.Type)}' with '{GetTypeName(initializerType)}'.");
            }
            ApplyContextualType(initializer, field.Declaration.Type);
        }
    }

    private void BindFunction(
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (function.Body is null)
        {
            return;
        }

        _loopDepth = 0;
        _breakableDepth = 0;
        _objectCreationIndex = 0;
        _propertyAssignmentIndex = 0;

        var scope = new LocalScope();
        foreach (var parameter in function.Parameters)
        {
            if (!scope.TryDeclare(parameter.Name, parameter.ParameterType))
            {
                throw new CompilationErrorException(
                    $"Parameter '{parameter.Name}' is already declared in function '{function.FullName}'.");
            }
        }

        BindStatements(function.Body, function, imports, scope);

        if (!IsType(function.ReturnType, BuiltInSystemTypes.Void) &&
            !AlwaysReturns(function.Body))
        {
            throw new CompilationErrorException(
                $"Function '{function.FullName}' must return a value of type '{GetTypeName(function.ReturnType)}'.");
        }
    }

    private void BindPropertyAccessor(
        PropertyAccessorDeclaration accessor,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (accessor.BodyFunction is null)
        {
            return;
        }
        if (accessor.Extern)
        {
            throw new CompilationErrorException(
                $"Extern property accessor '{accessor.FullName}' cannot have a body.");
        }

        var propertyType = UnwrapConst(accessor.ParentPropertyDeclaration.Type);
        if (propertyType is GenericType ||
            propertyType is NamedType namedType && namedType.GenericParams.Length > 0)
        {
            throw new CompilationErrorException(
                $"Generic property accessor bodies are not supported yet: '{accessor.FullName}'.");
        }

        BindFunction(accessor.BodyFunction, imports);
    }

    private void BindStatements(
        IEnumerable<StatementBase> statements,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case ExpressionStatement expressionStatement:
                    BindExpression(expressionStatement.Expression, function, imports, scope);
                    break;

                case ReturnStatement returnStatement:
                    BindReturnStatement(returnStatement, function, imports, scope);
                    break;

                case LocalVariableDeclarationStatement declarationStatement:
                    BindLocalDeclaration(declarationStatement, function, imports, scope);
                    break;

                case BlockStatement blockStatement:
                    BindStatements(
                        blockStatement.Statements,
                        function,
                        imports,
                        new LocalScope(scope));
                    break;

                case IfStatement ifStatement:
                    BindCondition(ifStatement.Condition, function, imports, scope);
                    BindStatement(
                        ifStatement.ThenStatement,
                        function,
                        imports,
                        new LocalScope(scope));
                    if (ifStatement.ElseStatement is not null)
                    {
                        BindStatement(
                            ifStatement.ElseStatement,
                            function,
                            imports,
                            new LocalScope(scope));
                    }
                    break;

                case SwitchStatement switchStatement:
                    BindSwitchStatement(switchStatement, function, imports, scope);
                    break;

                case WhileStatement whileStatement:
                    BindCondition(whileStatement.Condition, function, imports, scope);
                    BindLoopBody(
                        whileStatement.Body,
                        function,
                        imports,
                        new LocalScope(scope));
                    break;

                case DoWhileStatement doWhileStatement:
                    BindLoopBody(
                        doWhileStatement.Body,
                        function,
                        imports,
                        new LocalScope(scope));
                    BindCondition(doWhileStatement.Condition, function, imports, scope);
                    break;

                case ForStatement forStatement:
                    BindForStatement(forStatement, function, imports, scope);
                    break;

                case ForeachStatement foreachStatement:
                    BindForeachStatement(foreachStatement, function, imports, scope);
                    break;

                case EmptyStatement:
                    break;

                case BreakStatement:
                    ValidateBreak();
                    break;

                case ContinueStatement:
                    ValidateContinue();
                    break;

                default:
                    throw new InternalCompilerException(
                        $"Binding statement '{statement.GetType().Name}' is not yet supported.");
            }
        }
    }

    private void BindStatement(
        StatementBase statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        BindStatements([statement], function, imports, scope);
    }

    private void BindLoopBody(
        StatementBase statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        _loopDepth++;
        _breakableDepth++;
        try
        {
            BindStatement(statement, function, imports, scope);
        }
        finally
        {
            _loopDepth--;
            _breakableDepth--;
        }
    }

    private void ValidateBreak()
    {
        if (_breakableDepth == 0)
        {
            throw new CompilationErrorException(
                "The 'break' statement can only be used inside a loop or switch.");
        }
    }

    private void ValidateContinue()
    {
        if (_loopDepth == 0)
        {
            throw new CompilationErrorException(
                "The 'continue' statement can only be used inside a loop.");
        }
    }

    private void BindSwitchStatement(
        SwitchStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var selectorType = BindExpression(statement.Expression, function, imports, scope);
        if (!IsSwitchType(selectorType))
        {
            throw new CompilationErrorException(
                $"Switch expression type '{GetTypeName(selectorType)}' is not supported.");
        }

        var hasDefault = false;
        var caseValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in statement.Sections.SelectMany(section => section.Labels))
        {
            if (label.IsDefault)
            {
                if (hasDefault)
                {
                    throw new CompilationErrorException("A switch statement can contain only one default label.");
                }
                hasDefault = true;
                continue;
            }
            if (label.Filter is not null)
            {
                throw new CompilationErrorException("Switch case filters are not supported yet.");
            }
            if (label.Value is not LiteralExpression literal)
            {
                throw new CompilationErrorException("Switch case labels must be constant literals.");
            }

            var labelType = BindExpression(label.Value, function, imports, scope);
            if (!IsType(selectorType, labelType))
            {
                throw new CompilationErrorException(
                    $"Switch case type '{GetTypeName(labelType)}' does not match " +
                    $"selector type '{GetTypeName(selectorType)}'.");
            }
            if (!caseValues.Add(literal.SourceText))
            {
                throw new CompilationErrorException(
                    $"Switch case label '{literal.SourceText}' is duplicated.");
            }
        }

        _breakableDepth++;
        try
        {
            foreach (var section in statement.Sections)
            {
                BindStatements(section.Statements, function, imports, new LocalScope(scope));
            }
        }
        finally
        {
            _breakableDepth--;
        }
    }

    private void BindCondition(
        ExpressionBase condition,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var conditionType = BindExpression(condition, function, imports, scope);
        if (!IsType(conditionType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Condition must have type 'System.Bool', but found '{GetTypeName(conditionType)}'.");
        }
    }

    private void BindForStatement(
        ForStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope parentScope)
    {
        var scope = new LocalScope(parentScope);
        if (statement.DeclarationInitializer is not null)
        {
            BindLocalDeclaration(statement.DeclarationInitializer, function, imports, scope);
        }
        foreach (var initializer in statement.InitializerExpressions)
        {
            BindExpression(initializer, function, imports, scope);
        }
        if (statement.Condition is not null)
        {
            BindCondition(statement.Condition, function, imports, scope);
        }
        foreach (var iterator in statement.Iterators)
        {
            BindExpression(iterator, function, imports, scope);
        }

        BindLoopBody(statement.Body, function, imports, new LocalScope(scope));
    }

    private void BindForeachStatement(
        ForeachStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope parentScope)
    {
        var collectionType = BindExpression(statement.Collection, function, imports, parentScope);
        if (collectionType is not ArrayType arrayType)
        {
            throw new CompilationErrorException(
                $"The foreach collection must be an array, but found '{GetTypeName(collectionType)}'.");
        }

        var variableType = statement.DeclaredType is AutoType
            ? arrayType.ElementType
            : statement.DeclaredType;
        if (!IsType(variableType, arrayType.ElementType))
        {
            throw new CompilationErrorException(
                $"Cannot iterate array elements of type '{GetTypeName(arrayType.ElementType)}' " +
                $"as '{GetTypeName(variableType)}'.");
        }

        var scope = new LocalScope(parentScope);
        if (!scope.TryDeclare(statement.VariableName, variableType))
        {
            throw new CompilationErrorException(
                $"Local '{statement.VariableName}' is already declared in this scope.");
        }

        statement.BindVariableType(variableType);
        BindLoopBody(statement.Body, function, imports, scope);
    }

    private void BindLocalDeclaration(
        LocalVariableDeclarationStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        ResolveTypeReference(
            statement.DeclaredType,
            function.ParentClassDeclaration?.Namespace ?? function.Namespace,
            imports);
        foreach (var declarator in statement.Declarators)
        {
            var initializerType = declarator.Initializer is null
                ? null
                : BindExpression(declarator.Initializer, function, imports, scope);
            var variableType = statement.DeclaredType is AutoType
                ? initializerType ?? throw new CompilationErrorException(
                    $"Implicitly typed local '{declarator.Name}' requires an initializer.")
                : statement.DeclaredType;

            if (statement.DeclaredType is AutoType && initializerType is NullType)
            {
                throw new CompilationErrorException(
                    $"Cannot infer the type of local '{declarator.Name}' from null.");
            }

            if (initializerType is not null && !CanAssign(variableType, initializerType))
            {
                throw new CompilationErrorException(
                    $"Cannot initialize local '{declarator.Name}' of type '{GetTypeName(variableType)}' " +
                    $"with '{GetTypeName(initializerType)}'.");
            }
            if (declarator.Initializer is not null)
            {
                ApplyContextualType(declarator.Initializer, variableType);
            }
            if (!scope.TryDeclare(declarator.Name, variableType))
            {
                throw new CompilationErrorException(
                    $"Local '{declarator.Name}' is already declared in this scope.");
            }

            declarator.BindType(variableType);
        }
    }

    private void BindReturnStatement(
        ReturnStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (statement.Expression is null)
        {
            if (!IsType(function.ReturnType, BuiltInSystemTypes.Void))
            {
                throw new CompilationErrorException(
                    $"Function '{function.FullName}' must return a value of type '{GetTypeName(function.ReturnType)}'.");
            }
            return;
        }

        if (IsType(function.ReturnType, BuiltInSystemTypes.Void))
        {
            throw new CompilationErrorException(
                $"Void function '{function.FullName}' cannot return a value.");
        }

        var expressionType = BindExpression(statement.Expression, function, imports, scope);
        if (!CanAssign(function.ReturnType, expressionType))
        {
            throw new CompilationErrorException(
                $"Function '{function.FullName}' returns '{GetTypeName(expressionType)}', " +
                $"but '{GetTypeName(function.ReturnType)}' is required.");
        }
        ApplyContextualType(statement.Expression, function.ReturnType);
    }

    private TypeBase BindExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        TypeBase type;
        switch (expression)
        {
            case LiteralExpression literal:
                type = BindLiteral(literal);
                break;

            case IdentifierExpression identifier:
                type = BindIdentifier(identifier, function, scope);
                break;

            case ThisExpression:
                type = BindThis(function);
                break;

            case MemberAccessExpression memberAccess:
                type = BindMemberAccess(memberAccess, function, imports, scope);
                break;

            case InvocationExpression invocation:
                type = BindInvocation(invocation, function, imports, scope);
                break;

            case BinaryExpression binary:
                type = BindBinary(binary, function, imports, scope);
                break;

            case ConditionalExpression conditional:
                type = BindConditional(conditional, function, imports, scope);
                break;

            case NullCoalescingExpression coalescing:
                type = BindNullCoalescing(coalescing, function, imports, scope);
                break;

            case UnaryExpression unary:
                type = BindUnary(unary, function, imports, scope);
                break;

            case AssignmentExpression assignment:
                type = BindAssignment(assignment, function, imports, scope);
                break;

            case ArrayCreationExpression arrayCreation:
                type = BindArrayCreation(arrayCreation, function, imports, scope);
                break;

            case ArrayAccessExpression arrayAccess:
                type = BindArrayAccess(arrayAccess, function, imports, scope);
                break;

            case ObjectCreationExpression objectCreation:
                type = BindObjectCreation(objectCreation, function, imports, scope);
                break;

            default:
                throw new CompilationErrorException(
                    $"Cannot determine the type of expression '{expression.GetType().Name}'.");
        }

        expression.SetInferredType(type);
        return type;
    }

    private TypeBase BindIdentifier(
        IdentifierExpression identifier,
        FunctionDeclaration function,
        LocalScope scope)
    {
        if (identifier.Identifier.Parts.Length == 1 &&
            scope.TryLookup(identifier.Identifier.Parts[0], out var localType))
        {
            return localType;
        }

        if (identifier.Identifier.Parts.Length == 1 &&
            function.ParentClassDeclaration is { } parentClass)
        {
            var field = _fields.SingleOrDefault(candidate =>
                ReferenceEquals(candidate.Declaration.ParentClassDeclaration, parentClass) &&
                candidate.Declaration.Name == identifier.Identifier.Parts[0]);
            if (field is not null)
            {
                if (!field.Declaration.IsStatic && function.IsStatic)
                {
                    throw new CompilationErrorException(
                        $"Instance field '{field.Declaration.Name}' cannot be used from a static function.");
                }
                identifier.BindField(field);
                return field.Declaration.Type;
            }


            var property = _properties.SingleOrDefault(candidate =>
                candidate.FullName == new QualifiedIdentifier(parentClass.FullName, identifier.Identifier.Parts[0]));
            if (property is not null)
            {
                if (!property.IsStatic && function.IsStatic)
                {
                    throw new CompilationErrorException(
                        $"Instance property '{identifier.Identifier.Parts[0]}' cannot be used from a static function.");
                }
                EnsurePropertyValueIsSupported(property);
                var getter = SelectPropertyAccessor(
                    property,
                    "get",
                    [],
                    !property.IsStatic && function.Const);
                identifier.BindProperty(property, getter);
                return property.Type;
            }
        }

        throw new CompilationErrorException($"Cannot resolve value '{identifier.Identifier}'.");
    }

    private TypeBase BindThis(FunctionDeclaration function)
    {
        if (function.IsStatic || function.ParentClassDeclaration is null)
        {
            throw new CompilationErrorException(
                "The 'this' expression is only available in instance functions and constructors.");
        }

        var type = _types.Single(type =>
            ReferenceEquals(type.Declaration, function.ParentClassDeclaration)).Type;
        return function.Const ? new ConstType(type) : type;
    }

    private TypeBase BindMemberAccess(
        MemberAccessExpression memberAccess,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        var staticTarget = TryResolveTypeExpression(
            memberAccess.Target,
            currentNamespace,
            imports,
            scope);
        if (staticTarget is not null)
        {
            var staticField = _fields.SingleOrDefault(candidate =>
                IsType(candidate.ContainingType, staticTarget.Type) &&
                candidate.Declaration.Name == memberAccess.MemberName &&
                candidate.Declaration.IsStatic);
            if (staticField is null)
            {
                var staticProperty = _properties.SingleOrDefault(candidate =>
                    IsType(candidate.ContainingType, staticTarget.Type) &&
                    candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                    candidate.IsStatic);
                if (staticProperty is null)
                {
                    throw new CompilationErrorException(
                        $"Static member '{staticTarget.Declaration.FullName}.{memberAccess.MemberName}' does not exist.");
                }
                EnsurePropertyValueIsSupported(staticProperty);
                var getter = SelectPropertyAccessor(staticProperty, "get", [], false);
                memberAccess.BindProperty(staticProperty, getter);
                return staticProperty.Type;
            }
            memberAccess.BindField(staticField);
            return staticField.Declaration.Type;
        }

        var targetType = BindExpression(memberAccess.Target, function, imports, scope);
        var field = _fields.SingleOrDefault(candidate =>
            IsType(candidate.ContainingType, targetType) &&
            candidate.Declaration.Name == memberAccess.MemberName &&
            !candidate.Declaration.IsStatic);
        if (field is null)
        {
            var property = _properties.SingleOrDefault(candidate =>
                IsType(candidate.ContainingType, targetType) &&
                candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                !candidate.IsStatic);
            if (property is null)
            {
                throw new CompilationErrorException(
                    $"Instance member '{GetTypeName(targetType)}.{memberAccess.MemberName}' does not exist.");
            }
            EnsurePropertyValueIsSupported(property);
            var getter = SelectPropertyAccessor(
                property,
                "get",
                [],
                UnwrapConst(targetType) != targetType);
            memberAccess.BindProperty(property, getter);
            return property.Type;
        }

        memberAccess.BindField(field);
        return field.Declaration.Type;
    }

    private TypeSymbol? TryResolveTypeExpression(
        ExpressionBase expression,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (!TryFlattenIdentifier(expression, out var sourceName) ||
            sourceName.IsEmpty ||
            scope.TryLookup(sourceName.Parts[0], out _))
        {
            return null;
        }

        var candidateNames = GetCandidateNames(sourceName, currentNamespace, imports);
        var candidates = _types
            .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new CompilationErrorException($"Type name '{sourceName}' is ambiguous.");
        }
        return candidates.SingleOrDefault();
    }

    private PropertyReference? ResolvePropertyReference(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (expression is IdentifierExpression identifier &&
            identifier.Identifier.Parts.Length == 1 &&
            function.ParentClassDeclaration is { } parentClass)
        {
            if (scope.TryLookup(identifier.Identifier.Parts[0], out _))
            {
                return null;
            }
            var property = _properties.SingleOrDefault(candidate =>
                candidate.FullName == new QualifiedIdentifier(
                    parentClass.FullName,
                    identifier.Identifier.Parts[0]));
            if (property is null)
            {
                return null;
            }
            if (!property.IsStatic && function.IsStatic)
            {
                throw new CompilationErrorException(
                    $"Instance property '{identifier.Identifier}' cannot be used from a static function.");
            }
            return new PropertyReference(
                property,
                null,
                !property.IsStatic && function.Const);
        }

        if (expression is not MemberAccessExpression memberAccess)
        {
            return null;
        }

        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        var staticTarget = TryResolveTypeExpression(
            memberAccess.Target,
            currentNamespace,
            imports,
            scope);
        if (staticTarget is not null)
        {
            var property = _properties.SingleOrDefault(candidate =>
                IsType(candidate.ContainingType, staticTarget.Type) &&
                candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                candidate.IsStatic);
            return property is null
                ? null
                : new PropertyReference(property, null, false);
        }

        var receiverType = BindExpression(memberAccess.Target, function, imports, scope);
        var instanceProperty = _properties.SingleOrDefault(candidate =>
            IsType(candidate.ContainingType, receiverType) &&
            candidate.FullName.Parts[^1] == memberAccess.MemberName &&
            !candidate.IsStatic);
        return instanceProperty is null
            ? null
            : new PropertyReference(
                instanceProperty,
                memberAccess.Target,
                receiverType is ConstType);
    }

    private static PropertyAccessorSymbol SelectPropertyAccessor(
        PropertySymbol property,
        string accessorName,
        IReadOnlyList<TypeBase> argumentTypes,
        bool receiverIsConst)
    {
        if (receiverIsConst && accessorName == "set")
        {
            throw new CompilationErrorException(
                $"Property '{property.FullName}' cannot be assigned through a const receiver.");
        }

        var candidates = property.Accessors
            .Where(accessor => accessor.Name == accessorName &&
                ParametersMatch(accessor.ParameterTypes, argumentTypes))
            .ToArray();
        if (accessorName == "get")
        {
            candidates = receiverIsConst
                ? candidates.Where(accessor => accessor.Const).ToArray()
                : candidates.OrderBy(accessor => accessor.Const).ToArray();
        }
        if (candidates.Length == 0)
        {
            var operation = accessorName == "get" ? "readable" : "writable";
            throw new CompilationErrorException(
                $"Property '{property.FullName}' is not {operation} with " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }

        return candidates[0];
    }

    private static void EnsurePropertyValueIsSupported(PropertySymbol property)
    {
        var type = UnwrapConst(property.Type);
        if (type is GenericType ||
            type is NamedType namedType && namedType.GenericParams.Length > 0)
        {
            throw new CompilationErrorException(
                $"Generic property '{property.FullName}' cannot be used in expressions yet.");
        }
    }

    private static bool TryFlattenIdentifier(
        ExpressionBase expression,
        out QualifiedIdentifier identifier)
    {
        switch (expression)
        {
            case IdentifierExpression source:
                identifier = source.Identifier;
                return true;
            case MemberAccessExpression memberAccess when
                TryFlattenIdentifier(memberAccess.Target, out var target):
                identifier = new QualifiedIdentifier(target, memberAccess.MemberName);
                return true;
            default:
                identifier = QualifiedIdentifier.Empty;
                return false;
        }
    }

    private TypeBase BindInvocation(
        InvocationExpression invocation,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var argumentTypes = new List<TypeBase>();
        foreach (var argument in invocation.Arguments)
        {
            argumentTypes.Add(BindExpression(argument, function, imports, scope));
        }

        var sourceName = FlattenIdentifier(invocation.Target);
        var candidateNames = GetCandidateNames(sourceName, function.Namespace, imports);
        var namedCandidates = _symbols
            .Where(symbol => candidateNames.Contains(symbol.FullName))
            .ToArray();

        if (namedCandidates.Length == 0)
        {
            throw new CompilationErrorException(
                $"Cannot resolve function '{sourceName}' with {invocation.Arguments.Count} argument(s).");
        }

        var candidates = namedCandidates
            .Where(symbol => ParametersMatch(symbol.ParameterTypes, argumentTypes))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new CompilationErrorException(
                $"No overload of '{sourceName}' accepts ({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }

        if (candidates.Length > 1)
        {
            throw new CompilationErrorException(
                $"Function call '{sourceName}' with {invocation.Arguments.Count} argument(s) is ambiguous.");
        }

        var target = candidates[0];
        invocation.BindTarget(target);
        foreach (var pair in invocation.Arguments.Zip(target.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        return target.ReturnType;
    }

    private TypeBase BindObjectCreation(
        ObjectCreationExpression creation,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        ResolveTypeReference(creation.RequestedType, currentNamespace, imports);

        var argumentTypes = creation.Arguments
            .Select(argument => BindExpression(argument, function, imports, scope))
            .ToArray();
        var declaredType = _types.SingleOrDefault(candidate =>
            IsType(candidate.Type, creation.RequestedType));
        if (declaredType?.Declaration is { ClassType: ClassType.Interface })
        {
            throw new CompilationErrorException(
                $"Interface '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaredType?.Declaration is { IsStatic: true })
        {
            throw new CompilationErrorException(
                $"Static type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaredType?.Declaration is { IsAbstract: true })
        {
            throw new CompilationErrorException(
                $"Abstract type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }

        var typeCandidates = _constructors
            .Where(candidate => IsType(candidate.ConstructedType, creation.RequestedType))
            .ToArray();
        if (typeCandidates.Length == 0)
        {
            throw new CompilationErrorException(
                $"Type '{GetTypeName(creation.RequestedType)}' has no available constructor.");
        }

        var classType = typeCandidates[0].ClassType;
        var declaration = typeCandidates
            .Select(candidate => candidate.Constructor.Declaration?.ParentClassDeclaration)
            .FirstOrDefault(candidate => candidate is not null);
        if (classType == ClassType.Interface)
        {
            throw new CompilationErrorException(
                $"Interface '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaration is { IsStatic: true })
        {
            throw new CompilationErrorException(
                $"Static type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaration is { IsAbstract: true })
        {
            throw new CompilationErrorException(
                $"Abstract type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }

        var matchingConstructors = typeCandidates
            .Where(candidate => ParametersMatch(
                candidate.Constructor.ParameterTypes,
                argumentTypes))
            .ToArray();
        if (matchingConstructors.Length == 0)
        {
            throw new CompilationErrorException(
                $"No constructor for '{GetTypeName(creation.RequestedType)}' accepts " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }
        if (matchingConstructors.Length > 1)
        {
            throw new CompilationErrorException(
                $"Constructor call for '{GetTypeName(creation.RequestedType)}' is ambiguous.");
        }

        var constructor = matchingConstructors[0];
        foreach (var pair in creation.Arguments.Zip(constructor.Constructor.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        creation.BindConstructor(
            constructor.Constructor,
            constructor.ClassType,
            $"__cx_new_{_objectCreationIndex++}");
        return constructor.ConstructedType;
    }

    private void ResolveTypeReference(
        TypeBase type,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        switch (type)
        {
            case ConstType constType:
                ResolveTypeReference(constType.UnderlyingType, currentNamespace, imports);
                return;
            case ArrayType arrayType:
                ResolveTypeReference(arrayType.ElementType, currentNamespace, imports);
                return;
            case NullableType nullableType:
                ResolveTypeReference(nullableType.UnderlyingType, currentNamespace, imports);
                return;
            case NamedType namedType when namedType.ResolvedTypeFullName.ToString() == "void":
                var sourceName = new QualifiedIdentifier(
                    namedType.Name.Split('.', StringSplitOptions.RemoveEmptyEntries));
                var candidateNames = GetCandidateNames(sourceName, currentNamespace, imports);
                var candidates = _types
                    .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
                    .ToArray();
                if (candidates.Length > 1)
                {
                    throw new CompilationErrorException(
                        $"Type name '{namedType.Name}' is ambiguous.");
                }
                if (candidates.Length == 1)
                {
                    var candidate = candidates[0];
                    namedType.SetResolvedType(
                        candidate.Declaration.FullName,
                        candidate.ModuleName,
                        candidate.Declaration.ClassType);
                }
                return;
        }
    }

    private TypeBase BindBinary(
        BinaryExpression binary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var leftType = BindExpression(binary.Left, function, imports, scope);
        var rightType = BindExpression(binary.Right, function, imports, scope);
        return GetBinaryResultType(binary.Operator, leftType, rightType);
    }

    private TypeBase BindConditional(
        ConditionalExpression conditional,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var conditionType = BindExpression(conditional.Condition, function, imports, scope);
        if (!IsType(conditionType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Conditional expression requires a bool condition, but found '{GetTypeName(conditionType)}'.");
        }

        var whenTrueType = BindExpression(conditional.WhenTrue, function, imports, scope);
        var whenFalseType = BindExpression(conditional.WhenFalse, function, imports, scope);
        if (whenTrueType is NullType && IsNullAssignable(whenFalseType))
        {
            ApplyContextualType(conditional.WhenTrue, whenFalseType);
            return whenFalseType;
        }
        if (whenFalseType is NullType && IsNullAssignable(whenTrueType))
        {
            ApplyContextualType(conditional.WhenFalse, whenTrueType);
            return whenTrueType;
        }
        if (!IsType(whenTrueType, whenFalseType))
        {
            throw new CompilationErrorException(
                $"Conditional expression branches must have the same type, but found " +
                $"'{GetTypeName(whenTrueType)}' and '{GetTypeName(whenFalseType)}'.");
        }

        return whenTrueType;
    }

    private TypeBase BindNullCoalescing(
        NullCoalescingExpression coalescing,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var leftType = BindExpression(coalescing.Left, function, imports, scope);
        var rightType = BindExpression(coalescing.Right, function, imports, scope);

        if (coalescing.Left is not IdentifierExpression and
            not LiteralExpression { SourceText: "null" })
        {
            throw new CompilationErrorException(
                "The left operand of '??' must currently be a local or parameter so it is evaluated only once.");
        }

        if (leftType is NullType)
        {
            if (!IsNullAssignable(rightType))
            {
                throw new CompilationErrorException(
                    $"Operator '??' cannot use '{GetTypeName(rightType)}' as a null fallback.");
            }
            ApplyContextualType(coalescing.Left, rightType);
            return rightType;
        }

        var effectiveLeftType = UnwrapConst(leftType);
        var resultType = effectiveLeftType is NullableType nullableType
            ? nullableType.UnderlyingType
            : leftType;
        if (!IsNullAssignable(leftType))
        {
            throw new CompilationErrorException(
                $"Operator '??' requires a nullable or reference left operand, but found '{GetTypeName(leftType)}'.");
        }
        if (!CanAssign(resultType, rightType))
        {
            throw new CompilationErrorException(
                $"Operator '??' fallback has type '{GetTypeName(rightType)}', " +
                $"but '{GetTypeName(resultType)}' is required.");
        }

        ApplyContextualType(coalescing.Right, resultType);
        return resultType;
    }

    private TypeBase BindUnary(
        UnaryExpression unary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var operandType = BindExpression(unary.Operand, function, imports, scope);
        if (unary.Operator is "++" or "--" &&
            unary.Operand is not IdentifierExpression and
            not ArrayAccessExpression and
            not MemberAccessExpression)
        {
            throw new CompilationErrorException(
                $"Operator '{unary.Operator}' requires an assignable value.");
        }
        if (unary.Operator is "++" or "--")
        {
            ValidateWritableField(unary.Operand);
        }

        return unary.Operator switch
        {
            "+" or "-" when IsNumeric(operandType) => operandType,
            "~" when IsInteger(operandType) => operandType,
            "!" when IsType(operandType, BuiltInSystemTypes.Bool) => BuiltInSystemTypes.Bool,
            "++" or "--" when IsNumeric(operandType) => operandType,
            _ => throw new CompilationErrorException(
                $"Operator '{unary.Operator}' cannot be applied to '{GetTypeName(operandType)}'."),
        };
    }

    private TypeBase BindAssignment(
        AssignmentExpression assignment,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyAssignmentType = TryBindPropertyAssignment(
            assignment,
            function,
            imports,
            scope);
        if (propertyAssignmentType is not null)
        {
            return propertyAssignmentType;
        }

        if (assignment.Target is not IdentifierExpression and
            not ArrayAccessExpression and
            not MemberAccessExpression)
        {
            throw new CompilationErrorException(
                $"Expression '{assignment.Target.GetType().Name}' cannot be assigned to.");
        }

        var targetType = BindExpression(assignment.Target, function, imports, scope);
        ValidateWritableField(assignment.Target);
        var valueType = BindExpression(assignment.Value, function, imports, scope);

        if (assignment.Operator == "=")
        {
            if (!CanAssign(targetType, valueType))
            {
                throw new CompilationErrorException(
                    $"Cannot assign '{GetTypeName(valueType)}' to '{GetTypeName(targetType)}'.");
            }
            ApplyContextualType(assignment.Value, targetType);
        }
        else
        {
            var resultType = GetBinaryResultType(
                assignment.Operator[..^1],
                targetType,
                valueType);
            if (!IsType(resultType, targetType))
            {
                throw new CompilationErrorException(
                    $"Compound assignment '{assignment.Operator}' changes the target type.");
            }
        }

        return targetType;
    }

    private TypeBase? TryBindPropertyAssignment(
        AssignmentExpression assignment,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyExpression = assignment.Target is ArrayAccessExpression arrayAccess
            ? arrayAccess.Target
            : assignment.Target;
        var propertyReference = ResolvePropertyReference(
            propertyExpression,
            function,
            imports,
            scope);
        if (propertyReference is null)
        {
            return null;
        }

        var indexExpressions = assignment.Target is ArrayAccessExpression indexed
            ? indexed.Indices
            : [];
        if (assignment.Target is ArrayAccessExpression &&
            !propertyReference.Property.Accessors.Any(accessor =>
                accessor.ParameterTypes.Count > 0))
        {
            return null;
        }
        if (assignment.Operator != "=")
        {
            throw new CompilationErrorException(
                "Compound assignment to properties is not supported yet.");
        }

        EnsurePropertyValueIsSupported(propertyReference.Property);
        var argumentTypes = indexExpressions
            .Select(index => BindExpression(index, function, imports, scope))
            .ToArray();
        var setter = SelectPropertyAccessor(
            propertyReference.Property,
            "set",
            argumentTypes,
            propertyReference.ReceiverIsConst);
        var valueType = BindExpression(assignment.Value, function, imports, scope);
        if (!CanAssign(propertyReference.Property.Type, valueType))
        {
            throw new CompilationErrorException(
                $"Cannot assign '{GetTypeName(valueType)}' to property " +
                $"'{propertyReference.Property.FullName}' of type " +
                $"'{GetTypeName(propertyReference.Property.Type)}'.");
        }

        ApplyContextualType(assignment.Value, propertyReference.Property.Type);
        assignment.BindPropertySetter(
            propertyReference.Property,
            setter,
            $"__cx_property_value_{_propertyAssignmentIndex++}");
        return propertyReference.Property.Type;
    }

    private static void ValidateWritableField(ExpressionBase expression)
    {
        var field = expression switch
        {
            IdentifierExpression identifier => identifier.TargetField,
            MemberAccessExpression memberAccess => memberAccess.TargetField,
            _ => null,
        };
        if (field?.Declaration.Type is ConstType)
        {
            throw new CompilationErrorException(
                $"Const field '{field.Declaration.FullName}' cannot be assigned to.");
        }
    }

    private TypeBase BindArrayCreation(
        ArrayCreationExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        ResolveTypeReference(
            expression.ElementType,
            function.ParentClassDeclaration?.Namespace ?? function.Namespace,
            imports);
        if (expression.ElementType is VoidType or AutoType or ArrayType)
        {
            throw new CompilationErrorException(
                $"Arrays of type '{GetTypeName(expression.ElementType)}' cannot be created.");
        }

        var lengthType = BindExpression(expression.Length, function, imports, scope);
        if (!IsInteger(lengthType))
        {
            throw new CompilationErrorException(
                $"Array length must be an integer, but found '{GetTypeName(lengthType)}'.");
        }
        if (expression.Length is UnaryExpression
            {
                Operator: "-",
                Operand: LiteralExpression
            })
        {
            throw new CompilationErrorException("Array length cannot be negative.");
        }

        return new ArrayType(expression.ElementType);
    }

    private TypeBase BindArrayAccess(
        ArrayAccessExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyReference = ResolvePropertyReference(
            expression.Target,
            function,
            imports,
            scope);
        if (propertyReference is not null &&
            propertyReference.Property.Accessors.Any(accessor =>
                accessor.Name == "get" && accessor.ParameterTypes.Count > 0))
        {
            EnsurePropertyValueIsSupported(propertyReference.Property);
            var argumentTypes = expression.Indices
                .Select(index => BindExpression(index, function, imports, scope))
                .ToArray();
            var getter = SelectPropertyAccessor(
                propertyReference.Property,
                "get",
                argumentTypes,
                propertyReference.ReceiverIsConst);
            expression.BindProperty(propertyReference.Property, getter);
            return propertyReference.Property.Type;
        }

        var targetType = BindExpression(expression.Target, function, imports, scope);
        if (targetType is not ArrayType arrayType)
        {
            throw new CompilationErrorException(
                $"Cannot index expression of type '{GetTypeName(targetType)}'.");
        }
        if (expression.Indices.Count != 1)
        {
            throw new CompilationErrorException("Only one-dimensional array indexing is supported.");
        }

        var indexType = BindExpression(expression.Indices[0], function, imports, scope);
        if (!IsInteger(indexType))
        {
            throw new CompilationErrorException(
                $"Array index must be an integer, but found '{GetTypeName(indexType)}'.");
        }

        return arrayType.ElementType;
    }

    private static TypeBase BindLiteral(LiteralExpression literal)
    {
        if (literal.IsString)
        {
            return BuiltInSystemTypes.String;
        }
        if (literal.SourceText.StartsWith('\''))
        {
            return BuiltInSystemTypes.Char;
        }
        if (literal.SourceText is "true" or "false")
        {
            return BuiltInSystemTypes.Bool;
        }
        if (literal.SourceText.Contains('.') ||
            literal.SourceText.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return literal.SourceText.EndsWith("f", StringComparison.OrdinalIgnoreCase)
                ? BuiltInSystemTypes.Float
                : BuiltInSystemTypes.Double;
        }
        if (literal.SourceText == "null")
        {
            return BuiltInSystemTypes.Null;
        }

        var suffix = literal.SourceText.TrimEnd('l', 'L', 'u', 'U');
        var isUnsigned = literal.SourceText.EndsWith("u", StringComparison.OrdinalIgnoreCase) ||
            literal.SourceText.EndsWith("ul", StringComparison.OrdinalIgnoreCase) ||
            literal.SourceText.EndsWith("lu", StringComparison.OrdinalIgnoreCase);
        var isLong = suffix.Length != literal.SourceText.Length &&
            literal.SourceText.Contains('l', StringComparison.OrdinalIgnoreCase);
        return (isUnsigned, isLong) switch
        {
            (true, true) => BuiltInSystemTypes.ULong,
            (true, false) => BuiltInSystemTypes.UInt,
            (false, true) => BuiltInSystemTypes.Long,
            _ => BuiltInSystemTypes.Int,
        };
    }

    private static TypeBase GetBinaryResultType(
        string @operator,
        TypeBase leftType,
        TypeBase rightType)
    {
        if (@operator is "==" or "!=" &&
            (leftType is NullType && IsNullAssignable(rightType) ||
             rightType is NullType && IsNullAssignable(leftType)))
        {
            return BuiltInSystemTypes.Bool;
        }

        if (!IsType(leftType, rightType))
        {
            throw new CompilationErrorException(
                $"Operator '{@operator}' cannot combine '{GetTypeName(leftType)}' and '{GetTypeName(rightType)}'.");
        }

        return @operator switch
        {
            "+" or "-" or "*" or "/" or "%" when IsNumeric(leftType) => leftType,
            "<<" or ">>" when IsInteger(leftType) => leftType,
            "&" or "|" or "^" when IsInteger(leftType) || IsType(leftType, BuiltInSystemTypes.Bool) => leftType,
            "&&" or "||" when IsType(leftType, BuiltInSystemTypes.Bool) => BuiltInSystemTypes.Bool,
            "==" or "!=" => BuiltInSystemTypes.Bool,
            "<" or "<=" or ">" or ">=" when IsNumeric(leftType) => BuiltInSystemTypes.Bool,
            _ => throw new CompilationErrorException(
                $"Operator '{@operator}' cannot be applied to '{GetTypeName(leftType)}'."),
        };
    }

    private static TypeBase ResolveValue(
        QualifiedIdentifier identifier,
        LocalScope scope)
    {
        if (identifier.Parts.Length != 1 ||
            !scope.TryLookup(identifier.Parts[0], out var type))
        {
            throw new CompilationErrorException($"Cannot resolve value '{identifier}'.");
        }

        return type;
    }

    private static bool IsNumeric(TypeBase type)
    {
        return IsInteger(type) ||
            IsType(type, BuiltInSystemTypes.Float) ||
            IsType(type, BuiltInSystemTypes.Double);
    }

    private static bool IsInteger(TypeBase type)
    {
        return type is IntTypeBase;
    }

    private static bool IsSwitchType(TypeBase type)
    {
        return IsInteger(type) ||
            IsType(type, BuiltInSystemTypes.Bool) ||
            IsType(type, BuiltInSystemTypes.Char);
    }

    private static bool AlwaysReturns(IEnumerable<StatementBase> statements)
    {
        return statements.Any(AlwaysReturns);
    }

    private static bool AlwaysReturns(StatementBase statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            BlockStatement block => AlwaysReturns(block.Statements),
            IfStatement { ElseStatement: not null } conditional =>
                AlwaysReturns(conditional.ThenStatement) &&
                AlwaysReturns(conditional.ElseStatement),
            SwitchStatement switchStatement =>
                switchStatement.Sections.SelectMany(section => section.Labels).Any(label => label.IsDefault) &&
                switchStatement.Sections.Count > 0 &&
                switchStatement.Sections.All(section => AlwaysReturns(section.Statements)),
            _ => false,
        };
    }

    private static bool ParametersMatch(
        IReadOnlyList<TypeBase> parameterTypes,
        IReadOnlyList<TypeBase> argumentTypes)
    {
        return parameterTypes.Count == argumentTypes.Count &&
            parameterTypes.Zip(argumentTypes).All(pair => CanAssign(pair.First, pair.Second));
    }

    private static bool CanAssign(TypeBase targetType, TypeBase valueType)
    {
        return IsType(targetType, valueType) ||
            valueType is NullType && IsNullAssignable(targetType);
    }

    private static bool IsNullAssignable(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is NullType or ReferenceTypeBase or ArrayType or NullableType or PtrType ||
            type is NamedType { ClassType: ClassType.Class or ClassType.Interface };
    }

    private static void ApplyContextualType(ExpressionBase expression, TypeBase type)
    {
        switch (expression)
        {
            case LiteralExpression { SourceText: "null" }:
                expression.SetInferredType(type);
                break;
            case ConditionalExpression conditional:
                ApplyContextualType(conditional.WhenTrue, type);
                ApplyContextualType(conditional.WhenFalse, type);
                break;
        }
    }

    private static bool IsType(TypeBase left, TypeBase right)
    {
        left = UnwrapConst(left);
        right = UnwrapConst(right);

        if (left is ArrayType leftArray && right is ArrayType rightArray)
        {
            return IsType(leftArray.ElementType, rightArray.ElementType);
        }

        return GetTypeName(left) == GetTypeName(right);
    }

    private static QualifiedIdentifier GetTypeName(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is NamedType namedType && namedType.ResolvedTypeFullName.ToString() != "void"
            ? namedType.ResolvedTypeFullName
            : type.FullName;
    }

    private static TypeBase UnwrapConst(TypeBase type)
    {
        return type is ConstType constType ? constType.UnderlyingType : type;
    }

    private static HashSet<QualifiedIdentifier> GetCandidateNames(
        QualifiedIdentifier sourceName,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        var candidates = new HashSet<QualifiedIdentifier>
        {
            sourceName,
            new(currentNamespace, sourceName),
        };

        foreach (var import in imports)
        {
            candidates.Add(new QualifiedIdentifier(import, sourceName));
        }

        return candidates;
    }

    private static QualifiedIdentifier FlattenIdentifier(ExpressionBase expression)
    {
        return expression switch
        {
            IdentifierExpression identifier => identifier.Identifier,
            MemberAccessExpression memberAccess =>
                new QualifiedIdentifier(FlattenIdentifier(memberAccess.Target), memberAccess.MemberName),
            _ => throw new CompilationErrorException(
                $"Expression '{expression.GetType().Name}' cannot be invoked."),
        };
    }

    private static IEnumerable<FunctionDeclaration> EnumerateFunctions(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is FunctionDeclaration function)
            {
                yield return function;
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                foreach (var member in EnumerateFunctions(classDeclaration.MemberDeclarations.Declarations))
                {
                    yield return member;
                }
            }
        }
    }

    private static IEnumerable<PropertyAccessorDeclaration> EnumeratePropertyAccessors(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is PropertyDeclaration property)
            {
                foreach (var accessor in property.PropertyAccessorDeclarations)
                {
                    yield return accessor;
                }
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                foreach (var accessor in EnumeratePropertyAccessors(
                    classDeclaration.MemberDeclarations.Declarations))
                {
                    yield return accessor;
                }
            }
        }
    }

    private sealed class LocalScope
    {
        private readonly LocalScope? _parent;
        private readonly Dictionary<string, TypeBase> _locals = new(StringComparer.Ordinal);

        public LocalScope(LocalScope? parent = null)
        {
            _parent = parent;
        }

        public bool TryDeclare(string name, TypeBase type)
        {
            return _locals.TryAdd(name, type);
        }

        public bool TryLookup(string name, out TypeBase type)
        {
            if (_locals.TryGetValue(name, out type!))
            {
                return true;
            }

            if (_parent is not null)
            {
                return _parent.TryLookup(name, out type);
            }

            type = null!;
            return false;
        }
    }

    private sealed record TypeSymbol(
        string ModuleName,
        ClassDeclaration Declaration,
        TypeBase Type);

    private sealed record ConstructorSymbol(
        TypeBase ConstructedType,
        ClassType ClassType,
        FunctionSymbol Constructor);

    private sealed record PropertyReference(
        PropertySymbol Property,
        ExpressionBase? Receiver,
        bool ReceiverIsConst);
}
