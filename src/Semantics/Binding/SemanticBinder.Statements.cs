using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void BindStatements(
        IEnumerable<StatementBase> statements,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var localFunctions = statements.OfType<LocalFunctionDeclarationStatement>().ToArray();
        var currentNamespace = GetCurrentNamespace(function);
        foreach (var localFunction in localFunctions)
        {
            ClassifyFunctionGenericTypeList(
                localFunction.Function, currentNamespace, imports);
        }
        foreach (var localFunction in localFunctions.Where(local =>
            !local.Function.IsGenericSpecialization))
        {
            DeclareLocalFunction(localFunction, function, imports, scope);
        }
        foreach (var localFunction in localFunctions.Where(local =>
            local.Function.IsGenericSpecialization))
        {
            DeclareLocalFunctionSpecialization(
                localFunction, function, imports, scope);
        }
        foreach (var statement in statements)
        {
            BindStatement(statement, function, imports, scope);
        }
    }

    private void BindStatement(
        StatementBase statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        try
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

                case LocalFunctionDeclarationStatement localFunction:
                    BindLocalFunction(localFunction, function, imports, scope);
                    break;

                case BlockStatement blockStatement:
                    if (blockStatement.IsUnsafe) ++_unsafeDepth;
                    try { BindStatements(blockStatement.Statements, function, imports, new LocalScope(scope)); }
                    finally { if (blockStatement.IsUnsafe) --_unsafeDepth; }
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

                case ThrowStatement throwStatement:
                    BindThrowStatement(throwStatement, function, imports, scope);
                    break;

                case TryStatement tryStatement:
                    BindTryStatement(tryStatement, function, imports, scope);
                    break;

                default:
                    throw new InternalCompilerException(
                        $"Binding statement '{statement.GetType().Name}' is not yet supported.");
            }
        }
        catch (CompilationErrorException exception) when (
            exception.SourceSpan is null && statement.SourceSpan is not null)
        {
            throw exception.WithSourceSpan(statement.SourceSpan);
        }
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

        if (IsStringSwitchType(selectorType) ||
            statement.Sections.SelectMany(section => section.Labels)
                .Any(label => label.Filter is not null))
        {
            var temporaryIndex = ++_switchTemporaryIndex;
            statement.BindTemporaries(
                $"__cx_switch_selector_{temporaryIndex}",
                $"__cx_switch_case_{temporaryIndex}");
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
            var labelType = BindExpression(label.Value!, function, imports, scope);
            var caseValue = label.Value switch
            {
                LiteralExpression literal => literal.SourceText,
                MemberAccessExpression { TargetEnumMember: not null } memberAccess =>
                    memberAccess.TargetEnumMember.Declaration.FullName.ToString(),
                _ => throw new CompilationErrorException(
                    "Switch case labels must be literals or enum members."),
            };
            var isNullStringLabel = label.Value is LiteralExpression { SourceText: "null" } &&
                IsStringSwitchType(selectorType);
            if (!isNullStringLabel && !IsType(selectorType, labelType))
            {
                throw new CompilationErrorException(
                    $"Switch case type '{GetTypeName(labelType)}' does not match " +
                    $"selector type '{GetTypeName(selectorType)}'.");
            }
            var identity = $"{GetTypeName(labelType)}:{caseValue}";
            if (label.Filter is null && !caseValues.Add(identity))
            {
                throw new CompilationErrorException(
                    $"Switch case label '{caseValue}' is duplicated.");
            }
            if (label.Filter is not null)
            {
                var filterType = BindExpression(label.Filter, function, imports, scope);
                if (!IsType(filterType, BuiltInSystemTypes.Bool))
                {
                    throw new CompilationErrorException(
                        $"Switch case filter must have type 'System.Bool', but found '{GetTypeName(filterType)}'.");
                }
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
        if (statement.DeclaredType is not AutoType)
        {
            ResolveTypeReference(
                statement.DeclaredType, GetCurrentNamespace(function), imports);
        }

        var collectionType = BindExpression(statement.Collection, function, imports, parentScope);
        if (collectionType is ArrayType arrayType)
        {
            var variableType = statement.DeclaredType is AutoType
                ? arrayType.ElementType
                : ArrayType.Canonicalize(statement.DeclaredType);
            if (!IsType(variableType, arrayType.ElementType))
            {
                throw new CompilationErrorException(
                    $"Cannot iterate array elements of type '{GetTypeName(arrayType.ElementType)}' " +
                    $"as '{GetTypeName(variableType)}'.");
            }

            var scope = new LocalScope(parentScope);
            if (!scope.TryDeclare(statement.VariableName, variableType, out var variableSymbol))
            {
                throw new CompilationErrorException(
                    $"Local '{statement.VariableName}' is already declared in this scope.");
            }

            statement.BindVariableType(variableType, variableSymbol);
            BindLoopBody(statement.Body, function, imports, scope);
            return;
        }

        if (UnwrapConst(collectionType) is StringType ||
            IsType(collectionType, BuiltInSystemTypes.String))
        {
            var variableType = statement.DeclaredType is AutoType
                ? BuiltInSystemTypes.Char
                : ArrayType.Canonicalize(statement.DeclaredType);
            if (!IsType(variableType, BuiltInSystemTypes.Char))
            {
                throw new CompilationErrorException(
                    $"Cannot iterate string characters of type 'System.Char' as " +
                    $"'{GetTypeName(variableType)}'.", statement.SourceSpan);
            }

            var scope = new LocalScope(parentScope);
            if (!scope.TryDeclare(statement.VariableName, variableType, out var variableSymbol))
            {
                throw new CompilationErrorException(
                    $"Local '{statement.VariableName}' is already declared in this scope.");
            }

            statement.BindString(variableType, variableSymbol);
            BindLoopBody(statement.Body, function, imports, scope);
            return;
        }

        var getEnumeratorInvocation = new InvocationExpression(
            new MemberAccessExpression(statement.Collection, "GetEnumerator"), []);
        TypeBase enumeratorType;
        try
        {
            enumeratorType = BindExpression(
                getEnumeratorInvocation, function, imports, parentScope);
        }
        catch (CompilationErrorException)
        {
            throw new CompilationErrorException(
                $"The foreach collection must be an array or expose a valid enumerator; " +
                $"'{GetTypeName(collectionType)}' does not provide a usable GetEnumerator().",
                statement.SourceSpan);
        }
        var enumeratorName = $"__cx_foreach_enumerator_{_foreachEnumeratorIndex++}";
        var scopeWithEnumerator = new LocalScope(parentScope);
        if (!scopeWithEnumerator.TryDeclare(
                enumeratorName, enumeratorType, out var enumeratorSymbol))
        {
            throw new CompilationErrorException(
                "Could not reserve a local for the foreach enumerator.", statement.SourceSpan);
        }

        var enumeratorIdentifier = new IdentifierExpression(
            new QualifiedIdentifier(enumeratorName));
        enumeratorIdentifier.BindLocal(enumeratorSymbol);
        enumeratorIdentifier.SetInferredType(enumeratorType);
        var moveNextInvocation = new InvocationExpression(
            new MemberAccessExpression(enumeratorIdentifier, "MoveNext"), []);
        var moveNextType = BindExpression(
            moveNextInvocation, function, imports, scopeWithEnumerator);
        if (!IsType(moveNextType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Enumerator method 'MoveNext' must return 'System.Bool', but found " +
                $"'{GetTypeName(moveNextType)}'.", statement.SourceSpan);
        }

        var currentProperty = new MemberAccessExpression(
            enumeratorIdentifier, "Current")
        {
            SourceSpan = statement.SourceSpan,
        };
        ExpressionBase currentExpression = currentProperty;
        TypeBase currentType;
        try
        {
            currentType = BindExpression(
                currentExpression, function, imports, scopeWithEnumerator);
            if (currentType is FunctionType)
            {
                currentType = BindCurrentMethod();
            }
        }
        catch (CompilationErrorException)
        {
            currentType = BindCurrentMethod();
        }

        TypeBase BindCurrentMethod()
        {
            var currentMethod = new MemberAccessExpression(
                enumeratorIdentifier, "Current")
            {
                SourceSpan = statement.SourceSpan,
            };
            var currentInvocation = new InvocationExpression(currentMethod, [])
            {
                SourceSpan = statement.SourceSpan,
            };
            var resultType = BindExpression(
                currentInvocation, function, imports, scopeWithEnumerator);
            currentExpression = currentInvocation;
            return resultType;
        }
        var foreachVariableType = statement.DeclaredType is AutoType
            ? currentType
            : statement.DeclaredType;
        if (!IsType(foreachVariableType, currentType))
        {
            throw new CompilationErrorException(
                $"Cannot iterate elements of type '{GetTypeDisplayName(currentType)}' " +
                $"as '{GetTypeDisplayName(foreachVariableType)}'.", statement.SourceSpan);
        }

        if (!scopeWithEnumerator.TryDeclare(
                statement.VariableName, foreachVariableType, out var foreachVariableSymbol))
        {
            throw new CompilationErrorException(
                $"Local '{statement.VariableName}' is already declared in this scope.");
        }

        statement.BindEnumerator(
            foreachVariableType,
            foreachVariableSymbol,
            enumeratorSymbol,
            getEnumeratorInvocation,
            moveNextInvocation,
            currentExpression);
        BindLoopBody(statement.Body, function, imports, scopeWithEnumerator);
    }

    private void BindThrowStatement(
        ThrowStatement throwStatement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope parentScope)
    {
        if (throwStatement.Expression is null)
        {
            if (_catchDepth == 0)
            {
                throw new CompilationErrorException(
                    "A rethrow statement can only be used inside a catch clause.");
            }
            return;
        }

        var exceptionType = BindExpression(
            throwStatement.Expression,
            function,
            imports,
            parentScope);
        if (!IsExceptionType(exceptionType))
        {
            throw new CompilationErrorException(
                $"Thrown expression must derive from 'System.Exception', but found " +
                $"'{GetTypeName(exceptionType)}'.");
        }
    }

    private void BindTryStatement(
        TryStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope parentScope)
    {
        BindStatement(statement.Body, function, imports, new LocalScope(parentScope));

        foreach (var clause in statement.CatchClauses)
        {
            ResolveTypeReference(
                clause.ExceptionType,
                GetCurrentNamespace(function),
                imports);
            if (!IsExceptionType(clause.ExceptionType))
            {
                throw new CompilationErrorException(
                    $"Catch type '{GetTypeName(clause.ExceptionType)}' must derive from " +
                    "'System.Exception'.");
            }

            var catchScope = new LocalScope(parentScope);
            if (clause.VariableName is not null)
            {
                if (!catchScope.TryDeclare(
                    clause.VariableName,
                    clause.ExceptionType,
                    out var catchVariableSymbol))
                {
                    throw new CompilationErrorException(
                        $"Local '{clause.VariableName}' is already declared in this scope.");
                }
                clause.BindVariableSymbol(catchVariableSymbol);
            }
            if (clause.Filter is not null)
            {
                BindCondition(clause.Filter, function, imports, catchScope);
            }

            _catchDepth++;
            try
            {
                BindStatement(clause.Body, function, imports, catchScope);
            }
            finally
            {
                _catchDepth--;
            }
        }

        if (statement.FinallyBody is not null)
        {
            BindStatement(
                statement.FinallyBody,
                function,
                imports,
                new LocalScope(parentScope));
        }
    }

    private bool IsExceptionType(TypeBase type)
    {
        type = UnwrapConst(type);
        if (GetTypeName(type).ToString() == "cxcore.System.Exception")
        {
            return true;
        }

        var declaration = GetClassDeclaration(type);
        while (declaration?.BaseClassType is { } baseType)
        {
            if (GetTypeName(baseType).ToString() == "cxcore.System.Exception")
            {
                return true;
            }
            declaration = declaration.BaseClassDeclaration;
        }

        return false;
    }

    private void BindLocalDeclaration(
        LocalVariableDeclarationStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        statement.DeclaredType = GenericTypeSubstitution.BindParameters(
            statement.DeclaredType,
            function.GenericTypeNames.Concat(
                function.ParentClassDeclaration?.GenericTypeNames ?? []).ToArray());
        ResolveTypeReference(
            statement.DeclaredType,
            GetCurrentNamespace(function),
            imports);
        statement.DeclaredType = ArrayType.Canonicalize(statement.DeclaredType);
        if (UnwrapConst(statement.DeclaredType) is FixedArrayType)
            throw new CompilationErrorException("Fixed inline arrays are supported as native record fields only.", statement.SourceSpan);
        if (UnwrapConst(statement.DeclaredType) is NamedType { NativeType.Opaque: true })
            throw new CompilationErrorException("Opaque native types are legal through pointers only.", statement.SourceSpan);
        foreach (var declarator in statement.Declarators)
        {
            var initializerType = declarator.Initializer is null
                ? null
                : BindExpression(
                    declarator.Initializer,
                    function,
                    imports,
                    scope,
                    statement.DeclaredType is AutoType ? null : statement.DeclaredType);
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
            if (!scope.TryDeclare(declarator.Name, variableType, out var localSymbol))
            {
                throw new CompilationErrorException(
                    $"Local '{declarator.Name}' is already declared in this scope.");
            }

            declarator.BindType(variableType, localSymbol);
            if (declarator.Initializer is not null) localSymbol.BorrowsStackStorage |= BorrowsStackStorage(declarator.Initializer);
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

        var expressionType = BindExpression(
            statement.Expression, function, imports, scope, function.ReturnType);
        if (BorrowsStackStorage(statement.Expression))
            throw new CompilationErrorException("Cannot return a pointer borrowing local stack storage.", statement.SourceSpan);
        if (!CanAssign(function.ReturnType, expressionType))
        {
            throw new CompilationErrorException(
                $"Function '{function.FullName}' returns '{GetTypeName(expressionType)}', " +
                $"but '{GetTypeName(function.ReturnType)}' is required.");
        }
        ApplyContextualType(statement.Expression, function.ReturnType);
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
            ThrowStatement => true,
            BlockStatement block => AlwaysReturns(block.Statements),
            IfStatement { ElseStatement: not null } conditional =>
                AlwaysReturns(conditional.ThenStatement) &&
                AlwaysReturns(conditional.ElseStatement),
            SwitchStatement switchStatement =>
                switchStatement.Sections.SelectMany(section => section.Labels).Any(label => label.IsDefault) &&
                switchStatement.Sections.Count > 0 &&
                switchStatement.Sections.All(section => AlwaysReturns(section.Statements)),
            TryStatement tryStatement =>
                (tryStatement.FinallyBody is not null && AlwaysReturns(tryStatement.FinallyBody)) ||
                (AlwaysReturns(tryStatement.Body) &&
                    tryStatement.CatchClauses.All(clause => AlwaysReturns(clause.Body))),
            _ => false,
        };
    }
}
