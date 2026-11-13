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

    private static bool IsLambdaTargetType(TypeBase type) =>
        GetFunctionSignature(type) is not null ||
        TryGetQueryLambdaTypes(type, out _, out _);

    private static bool TryGetQueryLambdaTypes(
        TypeBase? type,
        out TypeBase parameterType,
        out TypeBase resultType)
    {
        if (type is not null)
        {
            type = UnwrapConst(type);
        }
        if (type is NamedType named && named.TypeArguments.Count == 2 &&
            IsQueryLambdaNamedType(named))
        {
            parameterType = named.TypeArguments[0];
            resultType = named.TypeArguments[1];
            return true;
        }
        parameterType = null!;
        resultType = null!;
        return false;
    }

    private ObjectCreationExpression BindQueryLambda(
        LambdaExpression lambda,
        TypeBase parameterType,
        TypeBase targetResultType,
        FunctionDeclaration enclosingFunction,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope enclosingScope)
    {
        var queryBody = lambda.ExpressionBody;
        var queryLocalStatements = new List<LocalVariableDeclarationStatement>();
        var queryAssignments = new List<AssignmentExpression>();
        var queryConditionalAssignments = new List<(
            IfStatement Conditional,
            IReadOnlyList<AssignmentExpression> ThenAssignments,
            IReadOnlyList<AssignmentExpression> ElseAssignments)>();
        var queryGuards = new List<(ExpressionBase Condition, ExpressionBase Body)>();
        if (queryBody is null && lambda.StatementBody is { } blockStatements &&
            blockStatements.Count > 0)
        {
            var statementIndex = 0;
            while (statementIndex < blockStatements.Count &&
                blockStatements[statementIndex] is LocalVariableDeclarationStatement localStatement)
            {
                if (localStatement.Declarators.Count == 0 ||
                    localStatement.Declarators.Any(declarator => declarator.Initializer is null))
                {
                    throw new CompilationErrorException(
                        "Provider query lambda blocks support initialized local declarations " +
                        "before return expressions.",
                        localStatement.SourceSpan ?? lambda.SourceSpan);
                }
                queryLocalStatements.Add(localStatement);
                statementIndex++;
            }
            while (statementIndex < blockStatements.Count &&
                blockStatements[statementIndex] is ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: IdentifierExpression,
                        Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                    } assignment,
                })
            {
                queryAssignments.Add(assignment);
                statementIndex++;
            }
            while (statementIndex < blockStatements.Count &&
                blockStatements[statementIndex] is IfStatement conditional &&
                conditional.ElseStatement is { } elseStatement &&
                GetQueryLambdaAssignments(conditional.ThenStatement) is
                    { } thenAssignments &&
                GetQueryLambdaAssignments(elseStatement) is
                    { } elseAssignments)
            {
                queryConditionalAssignments.Add((
                    conditional, thenAssignments, elseAssignments));
                statementIndex++;
            }
            while (statementIndex < blockStatements.Count)
            {
                if (blockStatements[statementIndex] is not IfStatement guard ||
                    guard.ElseStatement is not null ||
                    GetQueryLambdaDirectReturn(guard.ThenStatement) is not
                        { Expression: { } guardBody })
                {
                    break;
                }
                queryGuards.Add((guard.Condition, guardBody));
                statementIndex++;
            }
            if (statementIndex == blockStatements.Count - 1 &&
                blockStatements[statementIndex] is IfStatement terminalConditional &&
                TryGetQueryLambdaConditionalReturns(
                    terminalConditional,
                    out var terminalGuards,
                    out var falseBody))
            {
                queryGuards.AddRange(terminalGuards);
                queryBody = falseBody;
                statementIndex++;
            }
            else if (statementIndex == blockStatements.Count - 1 &&
                blockStatements[statementIndex] is ReturnStatement
                    { Expression: { } returnedExpression })
            {
                queryBody = returnedExpression;
                statementIndex++;
            }
            if (queryBody is null || statementIndex != blockStatements.Count)
            {
                throw new CompilationErrorException(
                    "Provider query lambda blocks support initialized local declarations " +
                    "and local assignments, including straight-line if/else branches, " +
                    "followed by direct-return guards or a direct return.",
                    lambda.SourceSpan);
            }
        }
        if (queryBody is null)
        {
            throw new CompilationErrorException(
                "Provider query lambdas require an expression body or a block containing " +
                "initialized local declarations, supported local assignments, and " +
                "single-return guard clauses followed by one direct return expression.",
                lambda.SourceSpan);
        }
        if (lambda.Parameters.Count != 1)
        {
            throw new CompilationErrorException(
                "Provider query lambdas require exactly one parameter.",
                lambda.SourceSpan);
        }

        var parameter = lambda.Parameters[0];
        var currentNamespace = GetCurrentNamespace(enclosingFunction);
        if (parameter.Type is { } declaredParameterType)
        {
            ResolveTypeReference(declaredParameterType, currentNamespace, imports);
            if (!IsType(declaredParameterType, parameterType))
            {
                throw new CompilationErrorException(
                    $"Query lambda parameter '{parameter.Name}' has type " +
                    $"'{GetTypeName(declaredParameterType)}', but " +
                    $"'{GetTypeName(parameterType)}' is required.",
                    lambda.SourceSpan);
            }
        }
        parameter.Type = parameterType;

        var enclosingClass = enclosingFunction.ParentClassDeclaration ??
            enclosingFunction.LambdaOwner?.EnclosingClassDeclaration;
        var thisSymbol = enclosingFunction.LambdaOwner?.ThisSymbol;
        if (thisSymbol is null && enclosingFunction.ParentClassDeclaration is { } thisClass &&
            !enclosingFunction.IsStatic)
        {
            var thisType = _types.Single(type => ReferenceEquals(type.Declaration, thisClass)).Type;
            if (enclosingFunction.Const)
            {
                thisType = new ConstType(thisType);
            }
            thisSymbol = new LocalVariableSymbol("this", thisType, isThis: true);
        }
        lambda.SetEnclosingContext(enclosingClass, thisSymbol, enclosingFunction.LambdaOwner);

        var lambdaScope = new LocalScope(enclosingScope, lambda);
        if (!lambdaScope.TryDeclare(parameter.Name, parameterType, out var parameterSymbol))
        {
            throw new CompilationErrorException(
                $"Duplicate query lambda parameter '{parameter.Name}'.",
                lambda.SourceSpan);
        }
        parameter.LocalSymbol = parameterSymbol;
        foreach (var localStatement in queryLocalStatements)
        {
            BindLocalDeclaration(
                localStatement, enclosingFunction, imports, lambdaScope);
        }
        var queryLocalSymbols = queryLocalStatements
            .SelectMany(statement => statement.Declarators)
            .Select(declarator => declarator.Symbol)
            .OfType<LocalVariableSymbol>()
            .ToHashSet();
        foreach (var assignment in queryAssignments)
        {
            BindExpression(
                assignment, enclosingFunction, imports, lambdaScope);
            ValidateQueryLambdaLocalAssignment(
                assignment, queryLocalSymbols, lambda.SourceSpan);
        }
        foreach (var branch in queryConditionalAssignments)
        {
            var conditionType = BindExpression(
                branch.Conditional.Condition,
                enclosingFunction,
                imports,
                lambdaScope);
            if (!IsType(conditionType, BuiltInSystemTypes.Bool))
            {
                throw new CompilationErrorException(
                    "Provider query lambda assignment branch requires a bool condition.",
                    branch.Conditional.Condition.SourceSpan ?? lambda.SourceSpan);
            }
            LocalVariableSymbol? branchTarget = null;
            foreach (var assignment in branch.ThenAssignments.Concat(
                branch.ElseAssignments))
            {
                BindExpression(
                    assignment, enclosingFunction, imports, lambdaScope);
                ValidateQueryLambdaLocalAssignment(
                    assignment, queryLocalSymbols, lambda.SourceSpan);
                var target = (IdentifierExpression)assignment.Target;
                if (branchTarget is null)
                {
                    branchTarget = target.TargetLocal;
                }
                else if (!ReferenceEquals(branchTarget, target.TargetLocal))
                {
                    throw new CompilationErrorException(
                        "Both provider query lambda assignment branches must update the same local.",
                        branch.Conditional.SourceSpan ?? lambda.SourceSpan);
                }
            }
        }
        var bodyType = BindExpression(
            queryBody,
            enclosingFunction,
            imports,
            lambdaScope,
            ContainsOpenTypeParameter(targetResultType) ? null : targetResultType);
        if (!ContainsOpenTypeParameter(targetResultType) &&
            !CanAssign(targetResultType, bodyType))
        {
            throw new CompilationErrorException(
                $"Query lambda returns '{GetTypeName(bodyType)}', but its target requires " +
                $"'{GetTypeName(targetResultType)}'.",
                lambda.SourceSpan);
        }
        foreach (var guard in queryGuards)
        {
            var conditionType = BindExpression(
                guard.Condition, enclosingFunction, imports, lambdaScope);
            if (!IsType(conditionType, BuiltInSystemTypes.Bool))
            {
                throw new CompilationErrorException(
                    "Provider query lambda guard requires a bool condition.",
                    guard.Condition.SourceSpan ?? lambda.SourceSpan);
            }
            var guardType = BindExpression(
                guard.Body,
                enclosingFunction,
                imports,
                lambdaScope,
                ContainsOpenTypeParameter(targetResultType)
                    ? bodyType : targetResultType);
            if (!CanAssign(bodyType, guardType))
            {
                throw new CompilationErrorException(
                    $"Provider query lambda guard returns '{GetTypeName(guardType)}', " +
                    $"but the final return has type '{GetTypeName(bodyType)}'.",
                    guard.Body.SourceSpan ?? lambda.SourceSpan);
            }
        }

        var parameterExpression = CreateQueryExpressionNode(
            parameterType,
            "Parameter",
            parameter.Name,
            new LiteralExpression("null"),
            new LiteralExpression("null"),
            new LiteralExpression("null"));
        var localNames = new Dictionary<LocalVariableSymbol, string>();
        var translatedInitializers = new List<(string Name, ExpressionBase Value)>();
        foreach (var localStatement in queryLocalStatements)
        {
            foreach (var declarator in localStatement.Declarators)
            {
                var localSymbol = declarator.Symbol ?? throw new InternalCompilerException(
                    "Provider query local was translated before it was bound.");
                var initializer = declarator.Initializer ?? throw new InternalCompilerException(
                    "Provider query local was translated without an initializer.");
                translatedInitializers.Add((
                    declarator.Name,
                    TranslateQueryExpression(
                        initializer,
                        parameter.Name,
                        lambda.SourceSpan,
                        parameterSymbol,
                        localNames)));
                localNames.Add(localSymbol, declarator.Name);
            }
        }
        foreach (var assignment in queryAssignments)
        {
            var target = (IdentifierExpression)assignment.Target;
            var localSymbol = target.TargetLocal!;
            translatedInitializers.Add((
                localNames[localSymbol],
                TranslateQueryLambdaAssignmentValue(
                    assignment,
                    parameter.Name,
                    lambda.SourceSpan,
                    parameterSymbol,
                    localNames)));
        }
        foreach (var branch in queryConditionalAssignments)
        {
            var target = (IdentifierExpression)branch.ThenAssignments[0].Target;
            var localSymbol = target.TargetLocal!;
            var conditionalValue = CreateQueryExpressionNode(
                target.InferredType ?? throw new InternalCompilerException(
                    "Query local assignment branch was translated before binding."),
                "Conditional",
                null,
                TranslateQueryExpression(
                    branch.Conditional.Condition,
                    parameter.Name,
                    lambda.SourceSpan,
                    parameterSymbol,
                    localNames),
                TranslateQueryLambdaAssignmentBranch(
                    branch.ThenAssignments,
                    localSymbol,
                    parameter.Name,
                    lambda.SourceSpan,
                    parameterSymbol,
                    localNames),
                TranslateQueryLambdaAssignmentBranch(
                    branch.ElseAssignments,
                    localSymbol,
                    parameter.Name,
                    lambda.SourceSpan,
                    parameterSymbol,
                    localNames));
            translatedInitializers.Add((
                localNames[localSymbol], conditionalValue));
        }
        var bodyExpression = TranslateQueryExpression(
            queryBody,
            parameter.Name,
            lambda.SourceSpan,
            parameterSymbol,
            localNames);
        for (var index = queryGuards.Count - 1; index >= 0; index--)
        {
            var guard = queryGuards[index];
            bodyExpression = CreateQueryExpressionNode(
                bodyType,
                "Conditional",
                null,
                TranslateQueryExpression(
                    guard.Condition,
                    parameter.Name,
                    lambda.SourceSpan,
                    parameterSymbol,
                    localNames),
                TranslateQueryExpression(
                    guard.Body,
                    parameter.Name,
                    lambda.SourceSpan,
                    parameterSymbol,
                    localNames),
                bodyExpression);
        }
        for (var index = translatedInitializers.Count - 1; index >= 0; index--)
        {
            var (localName, initializer) = translatedInitializers[index];
            bodyExpression = CreateQueryExpressionNode(
                bodyType,
                "Let",
                localName,
                new LiteralExpression("null"),
                initializer,
                bodyExpression);
        }
        var queryLambdaType = NamedType.CreateWithTypeArguments(
            "System.Collections.Query.QueryLambda",
            [parameterType, bodyType]);
        var queryLambda = new ObjectCreationExpression(
            queryLambdaType,
            [parameterExpression, bodyExpression]);
        BindExpression(queryLambda, enclosingFunction, imports, enclosingScope);
        return queryLambda;
    }

    private static ReturnStatement? GetQueryLambdaDirectReturn(
        StatementBase statement) => statement switch
    {
        ReturnStatement direct => direct,
        BlockStatement { Statements: [ReturnStatement returned] } => returned,
        _ => null,
    };

    private static IReadOnlyList<AssignmentExpression>? GetQueryLambdaAssignments(
        StatementBase statement)
    {
        IReadOnlyList<StatementBase> statements = statement is BlockStatement block
            ? block.Statements
            : [statement];
        if (statements.Count == 0)
        {
            return null;
        }
        var assignments = new List<AssignmentExpression>(statements.Count);
        foreach (var branchStatement in statements)
        {
            if (branchStatement is not ExpressionStatement
                { Expression: AssignmentExpression assignment })
            {
                return null;
            }
            assignments.Add(assignment);
        }
        return assignments;
    }

    private void ValidateQueryLambdaLocalAssignment(
        AssignmentExpression assignment,
        HashSet<LocalVariableSymbol> localSymbols,
        SourceSpan? lambdaSourceSpan)
    {
        if (assignment.OperatorSymbol is not null ||
            assignment.Operator is not ("=" or "+=" or "-=" or "*=" or "/=" or "%=") ||
            assignment.Target is not IdentifierExpression
            {
                TargetLocal: { } targetLocal,
            } || !localSymbols.Contains(targetLocal))
        {
            throw new CompilationErrorException(
                "Provider query lambda assignments must target a declared local and use a " +
                "simple assignment or built-in arithmetic compound assignment.",
                assignment.SourceSpan ?? lambdaSourceSpan);
        }
    }

    private ExpressionBase TranslateQueryLambdaAssignmentValue(
        AssignmentExpression assignment,
        string parameterName,
        SourceSpan? sourceSpan,
        LocalVariableSymbol parameterSymbol,
        IReadOnlyDictionary<LocalVariableSymbol, string> localNames)
    {
        if (assignment.Operator == "=")
        {
            return TranslateQueryExpression(
                assignment.Value,
                parameterName,
                sourceSpan,
                parameterSymbol,
                localNames);
        }

        var kind = assignment.Operator switch
        {
            "+=" => "Add",
            "-=" => "Subtract",
            "*=" => "Multiply",
            "/=" => "Divide",
            "%=" => "Modulo",
            _ => throw new InternalCompilerException(
                "Unsupported query-lambda local compound assignment."),
        };
        return CreateQueryExpressionNode(
            assignment.Target.InferredType ?? throw new InternalCompilerException(
                "Query local compound assignment was translated before binding."),
            kind,
            null,
            new LiteralExpression("null"),
            TranslateQueryExpression(
                assignment.Target,
                parameterName,
                sourceSpan,
                parameterSymbol,
                localNames),
            TranslateQueryExpression(
                assignment.Value,
                parameterName,
                sourceSpan,
                parameterSymbol,
                localNames));
    }

    private ExpressionBase TranslateQueryLambdaAssignmentBranch(
        IReadOnlyList<AssignmentExpression> assignments,
        LocalVariableSymbol localSymbol,
        string parameterName,
        SourceSpan? sourceSpan,
        LocalVariableSymbol parameterSymbol,
        IReadOnlyDictionary<LocalVariableSymbol, string> localNames)
    {
        var localName = localNames[localSymbol];
        var localType = assignments[0].Target.InferredType ??
            throw new InternalCompilerException(
                "Query local assignment branch was translated before binding.");
        ExpressionBase branchValue = CreateQueryExpressionNode(
            localType,
            "Variable",
            localName,
            new LiteralExpression("null"),
            new LiteralExpression("null"),
            new LiteralExpression("null"));
        for (var index = assignments.Count - 1; index >= 0; index--)
        {
            branchValue = CreateQueryExpressionNode(
                localType,
                "Let",
                localName,
                new LiteralExpression("null"),
                TranslateQueryLambdaAssignmentValue(
                    assignments[index],
                    parameterName,
                    sourceSpan,
                    parameterSymbol,
                    localNames),
                branchValue);
        }
        return branchValue;
    }

    private static bool TryGetQueryLambdaConditionalReturns(
        IfStatement conditional,
        out List<(ExpressionBase Condition, ExpressionBase Body)> branches,
        out ExpressionBase finalBody)
    {
        branches = [];
        finalBody = null!;
        var current = conditional;
        while (true)
        {
            var returned = GetQueryLambdaDirectReturn(current.ThenStatement);
            if (returned?.Expression is not { } trueBody)
            {
                return false;
            }
            branches.Add((current.Condition, trueBody));
            if (current.ElseStatement is IfStatement nextConditional)
            {
                current = nextConditional;
                continue;
            }
            if (current.ElseStatement is { } elseStatement &&
                GetQueryLambdaDirectReturn(elseStatement)?.Expression is { } elseBody)
            {
                finalBody = elseBody;
                return true;
            }
            return false;
        }
    }

    private ExpressionBase TranslateQueryExpression(
        ExpressionBase expression,
        string parameterName,
        CxCompiler.Model.Common.SourceSpan? sourceSpan,
        LocalVariableSymbol parameterSymbol,
        IReadOnlyDictionary<LocalVariableSymbol, string> localNames)
    {
        var resultType = expression.InferredType ?? throw new InternalCompilerException(
            "Provider query expression was translated before type binding.");
        switch (expression)
        {
            case IdentifierExpression identifier when
                ReferenceEquals(identifier.TargetLocal, parameterSymbol):
                return CreateQueryExpressionNode(
                    resultType, "Parameter", parameterName,
                    new LiteralExpression("null"),
                    new LiteralExpression("null"),
                    new LiteralExpression("null"));

            case IdentifierExpression identifier when identifier.TargetLocal is { } localSymbol &&
                localNames.TryGetValue(localSymbol, out var localName):
                return CreateQueryExpressionNode(
                    resultType,
                    "Variable",
                    localName,
                    new LiteralExpression("null"),
                    new LiteralExpression("null"),
                    new LiteralExpression("null"));

            case IdentifierExpression:
                throw new CompilationErrorException(
                    "Provider query lambda captures must use an explicit QueryCapture<T> cell.",
                    sourceSpan);

            case LiteralExpression literal when literal.SourceText != "null":
                return CreateQueryExpressionNode(
                    resultType, "Constant", null,
                    new LiteralExpression(literal.SourceText) { SourceSpan = literal.SourceSpan },
                    new LiteralExpression("null"),
                    new LiteralExpression("null"));

            case InvocationExpression invocation when invocation.TargetSymbol is { } target:
            {
                ExpressionBase arguments = new LiteralExpression("null");
                for (var index = invocation.Arguments.Count - 1; index >= 0; index--)
                {
                    var argument = invocation.Arguments[index];
                    arguments = CreateQueryExpressionNode(
                        argument.InferredType ?? throw new InternalCompilerException(
                            "Provider query invocation argument was translated before type binding."),
                        "Argument",
                        null,
                        new LiteralExpression("null"),
                        TranslateQueryExpression(
                            argument, parameterName, sourceSpan,
                            parameterSymbol, localNames),
                        arguments);
                }

                return CreateQueryExpressionNode(
                    resultType,
                    "Call",
                    $"{target.FullName}#{target.OverloadIndex}",
                    new LiteralExpression("null"),
                    invocation.Receiver is null
                        ? new LiteralExpression("null")
                        : TranslateQueryExpression(
                            invocation.Receiver, parameterName, sourceSpan,
                            parameterSymbol, localNames),
                    arguments);
            }

            case MemberAccessExpression memberAccess:
                if (memberAccess.Target is IdentifierExpression captureIdentifier &&
                    IsQueryCaptureType(captureIdentifier.InferredType))
                {
                    return CreateQueryExpressionNode(
                        resultType,
                        "CapturedValue",
                        captureIdentifier.Identifier.Parts[^1],
                        new IdentifierExpression(captureIdentifier.Identifier)
                        {
                            SourceSpan = captureIdentifier.SourceSpan,
                        },
                        new LiteralExpression("null"),
                        new LiteralExpression("null"));
                }
                return CreateQueryExpressionNode(
                    resultType,
                    "MemberAccess",
                    memberAccess.MemberName,
                    new LiteralExpression("null"),
                    TranslateQueryExpression(
                        memberAccess.Target, parameterName, sourceSpan,
                        parameterSymbol, localNames),
                    new LiteralExpression("null"));

            case ConditionalExpression conditional:
                return CreateQueryExpressionNode(
                    resultType,
                    "Conditional",
                    null,
                    TranslateQueryExpression(
                        conditional.Condition, parameterName, sourceSpan,
                        parameterSymbol, localNames),
                    TranslateQueryExpression(
                        conditional.WhenTrue, parameterName, sourceSpan,
                        parameterSymbol, localNames),
                    TranslateQueryExpression(
                        conditional.WhenFalse, parameterName, sourceSpan,
                        parameterSymbol, localNames));

            case BinaryExpression binary:
                if (!TryGetQueryBinaryKind(binary.Operator, out var kind))
                {
                    break;
                }
                return CreateQueryExpressionNode(
                    resultType,
                    kind,
                    null,
                    new LiteralExpression("null"),
                    TranslateQueryExpression(
                        binary.Left, parameterName, sourceSpan,
                        parameterSymbol, localNames),
                    TranslateQueryExpression(
                        binary.Right, parameterName, sourceSpan,
                        parameterSymbol, localNames));

            case UnaryExpression unary when unary.Operator is "!" or "-":
                return CreateQueryExpressionNode(
                    resultType,
                    unary.Operator == "!" ? "Not" : "Negate",
                    null,
                    new LiteralExpression("null"),
                    TranslateQueryExpression(
                        unary.Operand, parameterName, sourceSpan,
                        parameterSymbol, localNames),
                    new LiteralExpression("null"));
        }

        throw new CompilationErrorException(
            $"Provider query lambda expression '{expression.GetType().Name}' is not supported.",
            sourceSpan);
    }

    private static bool TryGetQueryBinaryKind(string @operator, out string kind)
    {
        kind = @operator switch
        {
            "==" => "Equal",
            "!=" => "NotEqual",
            ">" => "GreaterThan",
            ">=" => "GreaterThanOrEqual",
            "<" => "LessThan",
            "<=" => "LessThanOrEqual",
            "+" => "Add",
            "-" => "Subtract",
            "*" => "Multiply",
            "/" => "Divide",
            "%" => "Modulo",
            "&&" => "AndAlso",
            "||" => "OrElse",
            _ => "",
        };
        return kind.Length > 0;
    }

    private static bool IsQueryCaptureType(TypeBase? type)
    {
        if (type is not null)
        {
            type = UnwrapConst(type);
        }
        return type is NamedType named && named.TypeArguments.Count == 1 &&
            IsQueryTypeNamedType(named, "QueryCapture");
    }

    private static bool IsQueryLambdaNamedType(NamedType type) =>
        IsQueryTypeNamedType(type, "QueryLambda");

    private static bool IsQueryTypeNamedType(NamedType type, string name)
    {
        var suffix = new[] { "System", "Collections", "Query", name };
        var parts = type.ResolvedTypeFullName.Parts;
        return parts.Length >= suffix.Length &&
            parts.Skip(parts.Length - suffix.Length).SequenceEqual(suffix) ||
            type.Name == $"System.Collections.Query.{name}";
    }

    private static ObjectCreationExpression CreateQueryExpressionNode(
        TypeBase valueType,
        string kind,
        string? name,
        ExpressionBase constantValue,
        ExpressionBase left,
        ExpressionBase right)
    {
        var requestedType = NamedType.CreateWithTypeArguments(
            "System.Collections.Query.QueryExpression", [valueType]);
        return new ObjectCreationExpression(
            requestedType,
            [new LiteralExpression($"\"{kind}\""),
             name is null ? new LiteralExpression("null") : new LiteralExpression($"\"{name}\""),
             constantValue,
             left,
             right]);
    }

    private TypeBase BindLambda(
        LambdaExpression lambda,
        FunctionDeclaration enclosingFunction,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope enclosingScope,
        TypeBase? expectedType)
    {
        var targetType = expectedType is null
            ? null
            : GetFunctionSignature(expectedType)
                ?? throw new CompilationErrorException(
                    $"A lambda requires a function type, but '{GetTypeName(expectedType)}' was expected.");
        if (targetType is not null && targetType.ParameterTypes.Count != lambda.Parameters.Count)
        {
            throw new CompilationErrorException(
                $"Lambda has {lambda.Parameters.Count} parameter(s), but the target function type " +
                $"requires {targetType.ParameterTypes.Count}.");
        }

        var currentNamespace = GetCurrentNamespace(enclosingFunction);
        var enclosingClass = enclosingFunction.ParentClassDeclaration ??
            enclosingFunction.LambdaOwner?.EnclosingClassDeclaration;
        var thisSymbol = enclosingFunction.LambdaOwner?.ThisSymbol;
        if (thisSymbol is null && enclosingFunction.ParentClassDeclaration is { } thisClass &&
            !enclosingFunction.IsStatic)
        {
            var thisType = _types.Single(type => ReferenceEquals(type.Declaration, thisClass)).Type;
            if (enclosingFunction.Const)
            {
                thisType = new ConstType(thisType);
            }
            thisSymbol = new LocalVariableSymbol("this", thisType, isThis: true);
        }
        lambda.SetEnclosingContext(
            enclosingClass,
            thisSymbol,
            enclosingFunction.LambdaOwner);
        var parameterTypes = new TypeBase[lambda.Parameters.Count];
        for (var index = 0; index < lambda.Parameters.Count; index++)
        {
            var parameter = lambda.Parameters[index];
            if (parameter.Type is { } explicitType)
            {
                ResolveTypeReference(explicitType, currentNamespace, imports);
                if (targetType is not null &&
                    !IsType(explicitType, targetType.ParameterTypes[index]))
                {
                    throw new CompilationErrorException(
                        $"Lambda parameter '{parameter.Name}' has type '{GetTypeName(explicitType)}', " +
                        $"but '{GetTypeName(targetType.ParameterTypes[index])}' is required.");
                }
                parameterTypes[index] = explicitType;
            }
            else
            {
                parameterTypes[index] = targetType?.ParameterTypes[index]
                    ?? throw new CompilationErrorException(
                        $"Lambda parameter '{parameter.Name}' needs a type annotation or a target " +
                        "function type.");
                parameter.Type = parameterTypes[index];
            }
        }

        if (lambda.StatementBody is not null && targetType is null)
        {
            throw new CompilationErrorException(
                "A block-bodied lambda requires a target function type.",
                lambda.SourceSpan);
        }

        var placeholderReturnType = targetType?.ReturnType ?? BuiltInSystemTypes.Void;
        var inferOpenLambdaReturnType = targetType is not null &&
            ContainsOpenTypeParameter(targetType.ReturnType);
        var generatedName = lambda.GeneratedCName ?? GetLambdaCName(lambda);
        var helper = new FunctionDeclaration(
            generatedName,
            currentNamespace,
            placeholderReturnType,
            enclosingFunction.MemberModifiers.Contains(MemberModifier.Unsafe) || _unsafeDepth > 0 ? [MemberModifier.Unsafe] : [],
            null,
            genericTypeNames: enclosingFunction.GenericTypeNames);
        helper.SourceSpan = lambda.SourceSpan;
        helper.SetLocalCName(generatedName);
        helper.SetLambdaOwner(lambda);
        foreach (var parameter in lambda.Parameters)
        {
            helper.AddParameter(new FunctionParameter(parameter.Name, parameter.Type!, null));
        }

        IReadOnlyList<StatementBase> body;
        TypeBase returnType;
        if (lambda.ExpressionBody is { } expressionBody)
        {
            var inferenceScope = new LocalScope(enclosingScope, lambda);
            foreach (var parameter in lambda.Parameters)
            {
                inferenceScope.TryDeclare(parameter.Name, parameter.Type!, out var localSymbol);
                parameter.LocalSymbol = localSymbol;
            }
            returnType = BindExpression(
                expressionBody,
                helper,
                imports,
                inferenceScope,
                inferOpenLambdaReturnType ? null : targetType?.ReturnType);
            if (targetType is not null && !inferOpenLambdaReturnType &&
                !CanAssign(targetType.ReturnType, returnType))
            {
                throw new CompilationErrorException(
                    $"Lambda returns '{GetTypeName(returnType)}', but its target requires " +
                    $"'{GetTypeName(targetType.ReturnType)}'.",
                    lambda.SourceSpan);
            }
            returnType = targetType is null || inferOpenLambdaReturnType
                ? returnType
                : targetType.ReturnType;
            helper = new FunctionDeclaration(
                generatedName,
                currentNamespace,
                targetType is not null && !inferOpenLambdaReturnType
                    ? targetType.ReturnType
                    : returnType,
                enclosingFunction.MemberModifiers.Contains(MemberModifier.Unsafe) || _unsafeDepth > 0 ? [MemberModifier.Unsafe] : [],
                null,
                genericTypeNames: enclosingFunction.GenericTypeNames);
            helper.SourceSpan = lambda.SourceSpan;
            helper.SetLocalCName(generatedName);
            helper.SetLambdaOwner(lambda);
            foreach (var parameter in lambda.Parameters)
            {
                var helperParameter = new FunctionParameter(parameter.Name, parameter.Type!, null);
                if (parameter.LocalSymbol is { } localSymbol)
                {
                    helperParameter.BindLocalSymbol(localSymbol);
                }
                helper.AddParameter(helperParameter);
            }
            body = IsType(helper.ReturnType, BuiltInSystemTypes.Void)
                ? [new ExpressionStatement(expressionBody)]
                : [new ReturnStatement(expressionBody)];
            helper.SetBody(body);
        }
        else
        {
            returnType = targetType!.ReturnType;
            helper.SetBody(lambda.StatementBody!);
            body = lambda.StatementBody!;
        }

        BindLambdaGenericTypeReferences(helper, body);

        BindFunction(
            helper,
            imports,
            captureScope: enclosingScope,
            lambdaOwner: lambda);
        var functionType = new FunctionType(
            QualifiedIdentifier.Empty,
            returnType,
            parameterTypes);
        var targetSignatureMatches = targetType is null ||
            (!inferOpenLambdaReturnType
                ? IsType(functionType, targetType)
                : !targetType.ParameterTypes.Any(ContainsOpenTypeParameter) &&
                    functionType.ParameterTypes.Count == targetType.ParameterTypes.Count &&
                    functionType.ParameterTypes.Zip(targetType.ParameterTypes)
                        .All(pair => IsType(pair.First, pair.Second)));
        if (!targetSignatureMatches)
        {
            throw new CompilationErrorException(
                "Lambda signature does not match its target function type.",
                lambda.SourceSpan);
        }
        lambda.Bind(
            helper,
            functionType,
            $"__cx_lambda_environment_{++_operatorTemporaryIndex}");
        return functionType;
    }

    private static void BindLambdaGenericTypeReferences(
        FunctionDeclaration helper,
        IReadOnlyList<StatementBase> body)
    {
        if (helper.GenericTypeNames.Length == 0)
        {
            return;
        }

        foreach (var statement in CoroutineSyntaxAnalysis.EnumerateBodyStatements(body))
        {
            if (statement is LocalVariableDeclarationStatement local)
            {
                local.DeclaredType = GenericTypeSubstitution.BindParameters(
                    local.DeclaredType, helper.GenericTypeNames);
            }
        }
        foreach (var expression in CoroutineSyntaxAnalysis.EnumerateBodyExpressions(body))
        {
            switch (expression)
            {
                case CastExpression cast:
                    cast.SetClosedTargetType(GenericTypeSubstitution.BindParameters(
                        cast.TargetType, helper.GenericTypeNames));
                    break;
                case ObjectCreationExpression creation:
                    creation.BindGenericTypeParameters(helper.GenericTypeNames);
                    break;
                case TypeOfExpression typeOf:
                    typeOf.SetClosedType(GenericTypeSubstitution.BindParameters(
                        typeOf.Type, helper.GenericTypeNames));
                    break;
                case TypeTestExpression typeTest:
                    typeTest.BindGenericTypeParameters(helper.GenericTypeNames);
                    break;
            }
        }
    }

    private static string GetLambdaCName(LambdaExpression lambda)
    {
        var location = lambda.SourceSpan
            ?? throw new InternalCompilerException("Lambda expression has no source location.");
        var pathHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(location.FilePath)))[..10];
        return $"__cx_lambda_{pathHash}_{location.StartLine}_{location.StartColumn}";
    }

    private static string GetMethodValueName(MemberAccessExpression memberAccess)
    {
        var location = memberAccess.SourceSpan
            ?? throw new InternalCompilerException("Method value has no source location.");
        var pathHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(location.FilePath)))[..10];
        return $"__cx_method_value_{pathHash}_{location.StartLine}_{location.StartColumn}";
    }

    private static LambdaCapture CaptureThroughEnclosingLambdas(
        LambdaExpression currentLambda,
        LocalVariableSymbol local)
    {
        var capture = currentLambda.Capture(local);
        for (var lambda = currentLambda;
            lambda is not null && !ReferenceEquals(local.OwnerLambda, lambda);
            lambda = lambda.EnclosingLambda)
        {
            lambda.Capture(local);
        }
        return capture;
    }
}
