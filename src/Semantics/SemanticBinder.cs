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
    private int _loopDepth;
    private int _breakableDepth;

    public void Bind(CxProject project)
    {
        _symbols.Clear();
        AddCoreSymbols();

        foreach (var context in project.CompilationContexts)
        {
            AddProjectSymbols(project.Name, context.DeclarationScope.Declarations);
        }

        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                BindFunction(function, context.Imports);
            }
        }
    }

    private void AddCoreSymbols()
    {
        AddCoreFunction("Console", "Read", [BuiltInSystemTypes.UInt], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "ReadLine", [], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "Write", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreFunction("Console", "WriteLine", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
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

            _symbols.Add(new FunctionSymbol(
                moduleName,
                function.FullName,
                function.Parameters.Select(parameter => parameter.ParameterType).ToArray(),
                function.ReturnType,
                overloadIndex,
                function));
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
                type = ResolveValue(identifier.Identifier, scope);
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

            default:
                throw new CompilationErrorException(
                    $"Cannot determine the type of expression '{expression.GetType().Name}'.");
        }

        expression.SetInferredType(type);
        return type;
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
            unary.Operand is not IdentifierExpression and not ArrayAccessExpression)
        {
            throw new CompilationErrorException(
                $"Operator '{unary.Operator}' requires an assignable value.");
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
        if (assignment.Target is not IdentifierExpression and not ArrayAccessExpression)
        {
            throw new CompilationErrorException(
                $"Expression '{assignment.Target.GetType().Name}' cannot be assigned to.");
        }

        var targetType = BindExpression(assignment.Target, function, imports, scope);
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

    private TypeBase BindArrayCreation(
        ArrayCreationExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
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
}
