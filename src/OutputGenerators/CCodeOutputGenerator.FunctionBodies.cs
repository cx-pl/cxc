using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static void WriteFunctionDefinitions(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is ClassDeclaration classDeclaration)
            {
                WriteFunctionDefinitions(
                    writer,
                    classDeclaration.MemberDeclarations.Declarations,
                    moduleName);
                continue;
            }

            if (declaration is PropertyDeclaration propertyDeclaration)
            {
                WritePropertyAccessorDefinitions(writer, propertyDeclaration, moduleName);
                continue;
            }

            if (declaration is not FunctionDeclaration { Body: not null } functionDeclaration)
            {
                continue;
            }

            var nameOverrideIndex = GetNameOverrideIndex(functionDeclaration, declarations);
            writer.Write($"{functionDeclaration.ToCIdentifier(moduleName, nameOverrideIndex)}(");

            var parameters = new List<string>();
            if (!functionDeclaration.IsStatic)
            {
                var receiverConst = functionDeclaration.Const ? "const " : string.Empty;
                parameters.Add(
                    $"{receiverConst}{functionDeclaration.ParentClassDeclaration!.ToCIdentifier(moduleName)}* __this");
            }
            parameters.AddRange(functionDeclaration.Parameters.Select(
                parameter => $"{parameter.ParameterType.ToCIdentifier(false)} {parameter.Name}"));

            writer.Write(string.Join(", ", parameters));
            writer.WriteLine(") {");
            writer.IncreaseIndent();

            if (functionDeclaration is ConstructorDeclaration &&
                functionDeclaration.ParentClassDeclaration!.ClassType == ClassType.Class)
            {
                writer.WriteLine(
                    $"CX_INIT_VTABLE(__this, {functionDeclaration.ParentClassDeclaration.ToCIdentifier(moduleName, false)});");
            }
            if (functionDeclaration is ConstructorDeclaration)
            {
                foreach (var field in functionDeclaration.ParentClassDeclaration!.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>()
                    .Where(field => !field.IsStatic && field.Initializer is not null))
                {
                    writer.WriteLine(
                        $"__this->{field.Name} = {ToCFieldInitializer(field.Initializer!, moduleName)};");
                }
            }

            foreach (var statement in functionDeclaration.Body)
            {
                WriteStatement(writer, statement, functionDeclaration, moduleName);
            }

            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    private static void WritePropertyAccessorDefinitions(
        IndentingWriter writer,
        PropertyDeclaration property,
        string moduleName)
    {
        foreach (var accessor in property.PropertyAccessorDeclarations
            .Where(accessor => accessor.BodyFunction is not null))
        {
            var function = accessor.BodyFunction!;
            writer.Write($"{accessor.ToCIdentifier(moduleName)}(");

            var parameters = new List<string>();
            if (!property.IsStatic)
            {
                var receiverConst = accessor.Const ? "const " : string.Empty;
                parameters.Add(
                    $"{receiverConst}{property.ParentClassDeclaration.ToCIdentifier(moduleName)}* __this");
            }
            parameters.AddRange(function.Parameters.Select(
                parameter => $"{parameter.ParameterType.ToCIdentifier(false)} {parameter.Name}"));

            writer.Write(string.Join(", ", parameters));
            writer.WriteLine(") {");
            writer.IncreaseIndent();

            foreach (var statement in function.Body!)
            {
                WriteStatement(writer, statement, function, moduleName);
            }

            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    private static void WriteStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        foreach (var creation in EnumerateDirectObjectCreations(statement))
        {
            var temporaryName = creation.TemporaryName ?? throw new InternalCompilerException(
                "Object creation expression is not bound.");
            writer.WriteLine($"{creation.RequestedType.ToCIdentifier(false)} {temporaryName};");
        }
        foreach (var assignment in EnumerateDirectPropertyAssignments(statement))
        {
            var temporaryName = assignment.TemporaryName ?? throw new InternalCompilerException(
                "Property assignment expression is not bound.");
            writer.WriteLine(
                $"{assignment.TargetProperty!.Type.ToCIdentifier(false)} {temporaryName};");
        }

        switch (statement)
        {
            case ExpressionStatement expressionStatement:
                writer.WriteLine(
                    $"{ToCExpression(expressionStatement.Expression, functionDeclaration, moduleName)};");
                break;

            case ReturnStatement returnStatement:
                var expression = returnStatement.Expression is null
                    ? string.Empty
                    : $" {ToCExpression(returnStatement.Expression, functionDeclaration, moduleName)}";
                writer.WriteLine($"return{expression};");
                break;

            case LocalVariableDeclarationStatement localDeclaration:
                foreach (var declarator in localDeclaration.Declarators)
                {
                    var type = declarator.Type ?? throw new InternalCompilerException(
                        $"Local '{declarator.Name}' is not bound.");
                    var initializer = declarator.Initializer is null
                        ? string.Empty
                        : $" = {ToCExpression(declarator.Initializer, functionDeclaration, moduleName)}";
                    writer.WriteLine($"{type.ToCIdentifier(false)} {declarator.Name}{initializer};");
                }
                break;

            case BlockStatement block:
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var nestedStatement in block.Statements)
                {
                    WriteStatement(writer, nestedStatement, functionDeclaration, moduleName);
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                break;

            case IfStatement ifStatement:
                writer.WriteLine(
                    $"if ({ToCExpression(ifStatement.Condition, functionDeclaration, moduleName)})");
                WriteControlledStatement(
                    writer,
                    ifStatement.ThenStatement,
                    functionDeclaration,
                    moduleName);
                if (ifStatement.ElseStatement is not null)
                {
                    writer.WriteLine("else");
                    WriteControlledStatement(
                        writer,
                        ifStatement.ElseStatement,
                        functionDeclaration,
                        moduleName);
                }
                break;

            case SwitchStatement switchStatement:
                WriteSwitchStatement(
                    writer,
                    switchStatement,
                    functionDeclaration,
                    moduleName);
                break;

            case WhileStatement whileStatement:
                writer.WriteLine(
                    $"while ({ToCExpression(whileStatement.Condition, functionDeclaration, moduleName)})");
                WriteControlledStatement(
                    writer,
                    whileStatement.Body,
                    functionDeclaration,
                    moduleName);
                break;

            case DoWhileStatement doWhileStatement:
                writer.WriteLine("do");
                WriteControlledStatement(
                    writer,
                    doWhileStatement.Body,
                    functionDeclaration,
                    moduleName);
                writer.WriteLine(
                    $"while ({ToCExpression(doWhileStatement.Condition, functionDeclaration, moduleName)});");
                break;

            case ForStatement forStatement:
                var forInitializer = forStatement.DeclarationInitializer is not null
                    ? ToCForDeclaration(
                        forStatement.DeclarationInitializer,
                        functionDeclaration,
                        moduleName)
                    : string.Join(", ", forStatement.InitializerExpressions.Select(
                        expression => ToCExpression(expression, functionDeclaration, moduleName)));
                var condition = forStatement.Condition is null
                    ? string.Empty
                    : ToCExpression(forStatement.Condition, functionDeclaration, moduleName);
                var iterators = string.Join(", ", forStatement.Iterators.Select(
                    expression => ToCExpression(expression, functionDeclaration, moduleName)));
                writer.WriteLine($"for ({forInitializer}; {condition}; {iterators})");
                WriteControlledStatement(
                    writer,
                    forStatement.Body,
                    functionDeclaration,
                    moduleName);
                break;

            case ForeachStatement foreachStatement:
                WriteForeachStatement(
                    writer,
                    foreachStatement,
                    functionDeclaration,
                    moduleName);
                break;

            case EmptyStatement:
                writer.WriteLine(";");
                break;

            case BreakStatement:
                writer.WriteLine("break;");
                break;

            case ContinueStatement:
                writer.WriteLine("continue;");
                break;

            default:
                throw new InternalCompilerException(
                    $"Statement '{statement.GetType().Name}' is not yet supported by the C generator.");
        }
    }

    private static void WriteControlledStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (statement is BlockStatement)
        {
            WriteStatement(writer, statement, functionDeclaration, moduleName);
            return;
        }

        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteStatement(writer, statement, functionDeclaration, moduleName);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteForeachStatement(
        IndentingWriter writer,
        ForeachStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var variableType = statement.VariableType ?? throw new InternalCompilerException(
            $"Foreach variable '{statement.VariableName}' is not bound.");
        var collectionName = $"__cx_foreach_collection_{statement.VariableName}";
        var indexName = $"__cx_foreach_index_{statement.VariableName}";

        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine(
            $"struct CX_ID_3(cxcore, System, Array)* {collectionName} = " +
            $"{ToCExpression(statement.Collection, functionDeclaration, moduleName)};");
        writer.WriteLine(
            $"for (cx_uint {indexName} = 0; " +
            $"{indexName} < {collectionName}->_length; {indexName}++)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine(
            $"{variableType.ToCIdentifier(false)} {statement.VariableName} = " +
            $"(({variableType.ToCIdentifier(false)}*){collectionName}->_data)[{indexName}];");
        if (statement.Body is BlockStatement block)
        {
            foreach (var nestedStatement in block.Statements)
            {
                WriteStatement(writer, nestedStatement, functionDeclaration, moduleName);
            }
        }
        else
        {
            WriteStatement(writer, statement.Body, functionDeclaration, moduleName);
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteSwitchStatement(
        IndentingWriter writer,
        SwitchStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        writer.WriteLine(
            $"switch ({ToCExpression(statement.Expression, functionDeclaration, moduleName)})");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        foreach (var section in statement.Sections)
        {
            foreach (var label in section.Labels)
            {
                if (label.Filter is not null)
                {
                    throw new InternalCompilerException("A filtered switch label reached C generation.");
                }

                writer.WriteLine(label.IsDefault
                    ? "default:"
                    : $"case {ToCExpression(label.Value!, functionDeclaration, moduleName)}:");
            }

            writer.WriteLine("{");
            writer.IncreaseIndent();
            foreach (var nestedStatement in section.Statements)
            {
                WriteStatement(writer, nestedStatement, functionDeclaration, moduleName);
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static string ToCForDeclaration(
        LocalVariableDeclarationStatement declaration,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var first = declaration.Declarators.First();
        var type = first.Type ?? throw new InternalCompilerException(
            $"Local '{first.Name}' is not bound.");
        var declarators = declaration.Declarators.Select(declarator =>
        {
            var declaratorType = declarator.Type ?? throw new InternalCompilerException(
                $"Local '{declarator.Name}' is not bound.");
            if (declaratorType.ToCIdentifier(false) != type.ToCIdentifier(false))
            {
                throw new InternalCompilerException(
                    "A for-loop declaration cannot contain differently typed variables.");
            }

            var initializer = declarator.Initializer is null
                ? string.Empty
                : $" = {ToCExpression(declarator.Initializer, functionDeclaration, moduleName)}";
            return $"{declarator.Name}{initializer}";
        });
        return $"{type.ToCIdentifier(false)} {string.Join(", ", declarators)}";
    }

    private static string ToCExpression(
        ExpressionBase expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        return expression switch
        {
            LiteralExpression { IsString: true } literal =>
                $"&{GetStringIdentifier(literal, moduleName).ToCIdentifier()}",
            LiteralExpression literal => literal.SourceText switch
            {
                "true" => "CX_TRUE",
                "false" => "CX_FALSE",
                "null" => ToCNullLiteral(literal),
                _ => literal.SourceText,
            },
            InvocationExpression invocation =>
                $"{ToCIdentifier(invocation.TargetSymbol ?? throw new InternalCompilerException("Invocation target is not bound."))}" +
                $"({string.Join(", ", invocation.Arguments.Select(argument => ToCExpression(argument, functionDeclaration, moduleName)))})",
            IdentifierExpression { PropertyGetter: not null } identifier =>
                ToCPropertyCall(
                    identifier.TargetProperty!,
                    identifier.PropertyGetter,
                    null,
                    [],
                    null,
                    functionDeclaration,
                    moduleName),
            IdentifierExpression identifier => identifier.TargetField is null
                ? identifier.Identifier.ToCIdentifier()
                : ToCFieldAccess(identifier.TargetField, null, functionDeclaration, moduleName),
            ThisExpression => "__this",
            BinaryExpression binary =>
                ToCBinaryExpression(binary, functionDeclaration, moduleName),
            NullCoalescingExpression coalescing =>
                ToCNullCoalescingExpression(coalescing, functionDeclaration, moduleName),
            ConditionalExpression conditional =>
                $"({ToCExpression(conditional.Condition, functionDeclaration, moduleName)} ? " +
                $"{ToCExpression(conditional.WhenTrue, functionDeclaration, moduleName)} : " +
                $"{ToCExpression(conditional.WhenFalse, functionDeclaration, moduleName)})",
            UnaryExpression unary when unary.Postfix =>
                $"({ToCExpression(unary.Operand, functionDeclaration, moduleName)}{unary.Operator})",
            UnaryExpression unary =>
                $"({unary.Operator}{ToCExpression(unary.Operand, functionDeclaration, moduleName)})",
            AssignmentExpression { PropertySetter: not null } assignment =>
                ToCPropertyAssignment(assignment, functionDeclaration, moduleName),
            AssignmentExpression assignment =>
                $"{ToCExpression(assignment.Target, functionDeclaration, moduleName)} {assignment.Operator} " +
                ToCExpression(assignment.Value, functionDeclaration, moduleName),
            ArrayCreationExpression arrayCreation =>
                $"cx_array_new((cx_uint)({ToCExpression(arrayCreation.Length, functionDeclaration, moduleName)}), " +
                $"(cx_uint)sizeof({arrayCreation.ElementType.ToCIdentifier(false)}))",
            ObjectCreationExpression objectCreation =>
                ToCObjectCreationExpression(objectCreation, functionDeclaration, moduleName),
            ArrayAccessExpression { PropertyGetter: not null } arrayAccess =>
                ToCIndexedPropertyGetter(arrayAccess, functionDeclaration, moduleName),
            ArrayAccessExpression arrayAccess =>
                ToCArrayAccess(arrayAccess, functionDeclaration, moduleName),
            MemberAccessExpression { PropertyGetter: not null } memberAccess =>
                ToCPropertyCall(
                    memberAccess.TargetProperty!,
                    memberAccess.PropertyGetter,
                    memberAccess.TargetProperty!.IsStatic ? null : memberAccess.Target,
                    [],
                    null,
                    functionDeclaration,
                    moduleName),
            MemberAccessExpression { TargetField: not null } memberAccess =>
                ToCFieldAccess(
                    memberAccess.TargetField,
                    memberAccess.Target,
                    functionDeclaration,
                    moduleName),
            MemberAccessExpression memberAccess =>
                FlattenIdentifier(memberAccess).ToCIdentifier(),
            _ => throw new InternalCompilerException(
                $"Expression '{expression.GetType().Name}' is not yet supported by the C generator."),
        };
    }

    private static string ToCIndexedPropertyGetter(
        ArrayAccessExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var receiver = expression.Target switch
        {
            MemberAccessExpression memberAccess when !expression.TargetProperty!.IsStatic =>
                memberAccess.Target,
            _ => null,
        };
        return ToCPropertyCall(
            expression.TargetProperty!,
            expression.PropertyGetter!,
            receiver,
            expression.Indices,
            null,
            functionDeclaration,
            moduleName);
    }

    private static string ToCPropertyAssignment(
        AssignmentExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var temporaryName = expression.TemporaryName ?? throw new InternalCompilerException(
            "Property assignment temporary is not bound.");
        var indexExpressions = expression.Target is ArrayAccessExpression indexed
            ? indexed.Indices
            : [];
        var propertyExpression = expression.Target is ArrayAccessExpression arrayAccess
            ? arrayAccess.Target
            : expression.Target;
        var receiver = propertyExpression switch
        {
            MemberAccessExpression memberAccess when !expression.TargetProperty!.IsStatic =>
                memberAccess.Target,
            _ => null,
        };
        var value = $"{temporaryName} = " +
            ToCExpression(expression.Value, functionDeclaration, moduleName);
        var setterCall = ToCPropertyCall(
            expression.TargetProperty!,
            expression.PropertySetter!,
            receiver,
            indexExpressions,
            value,
            functionDeclaration,
            moduleName);
        return $"({setterCall}, {temporaryName})";
    }

    private static string ToCPropertyCall(
        PropertySymbol property,
        PropertyAccessorSymbol accessor,
        ExpressionBase? receiver,
        IReadOnlyList<ExpressionBase> indexArguments,
        string? value,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var arguments = new List<string>();
        if (!property.IsStatic)
        {
            if (receiver is null)
            {
                arguments.Add("__this");
            }
            else
            {
                var receiverExpression = ToCExpression(receiver, functionDeclaration, moduleName);
                arguments.Add(property.ContainingClassType == ClassType.Class
                    ? receiverExpression
                    : $"&({receiverExpression})");
            }
        }
        arguments.AddRange(indexArguments.Select(argument =>
            ToCExpression(argument, functionDeclaration, moduleName)));
        if (value is not null)
        {
            arguments.Add(value);
        }

        var accessorName = $"__{(accessor.Const ? "const_" : string.Empty)}{accessor.Name}";
        var fullName = new QualifiedIdentifier(
            property.ModuleName,
            property.FullName,
            accessorName);
        return $"{fullName.ToCIdentifier()}({string.Join(", ", arguments)})";
    }

    private static string ToCFieldAccess(
        FieldSymbol field,
        ExpressionBase? receiver,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (field.Declaration.IsStatic)
        {
            return GetStaticFieldIdentifier(field.Declaration, field.ModuleName);
        }
        if (receiver is null)
        {
            return $"__this->{field.Declaration.Name}";
        }

        var receiverExpression = ToCExpression(receiver, functionDeclaration, moduleName);
        var accessOperator = field.ContainingClassType == ClassType.Class ? "->" : ".";
        return $"({receiverExpression}){accessOperator}{field.Declaration.Name}";
    }

    private static string ToCObjectCreationExpression(
        ObjectCreationExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var constructor = expression.Constructor ?? throw new InternalCompilerException(
            "Object creation constructor is not bound.");
        var temporaryName = expression.TemporaryName ?? throw new InternalCompilerException(
            "Object creation temporary is not bound.");
        var arguments = expression.Arguments
            .Select(argument => ToCExpression(argument, functionDeclaration, moduleName))
            .ToArray();

        string receiver;
        if (expression.ClassType == ClassType.Class)
        {
            var cType = expression.RequestedType.ToCIdentifier(false);
            var storageType = cType.TrimEnd().TrimEnd('*').TrimEnd();
            receiver = $"{temporaryName} = ({cType})cx_object_new(" +
                $"(cx_uint)sizeof({storageType}))";
        }
        else
        {
            receiver = $"&{temporaryName}";
        }

        var constructorArguments = new[] { receiver }.Concat(arguments);
        return $"({ToCIdentifier(constructor)}({string.Join(", ", constructorArguments)}), " +
            $"{temporaryName})";
    }

    private static string ToCNullLiteral(LiteralExpression literal)
    {
        var inferredType = literal.InferredType is ConstType constType
            ? constType.UnderlyingType
            : literal.InferredType;
        return inferredType is NullableType nullableType
            ? $"({nullableType.ToCIdentifier(false)}){{ CX_NULL }}"
            : "CX_NULL";
    }

    private static string ToCBinaryExpression(
        BinaryExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (expression.Operator is "==" or "!=")
        {
            if (expression.Left is LiteralExpression { SourceText: "null" } &&
                IsNullableExpression(expression.Right))
            {
                return $"(({ToCExpression(expression.Right, functionDeclaration, moduleName)})._obj " +
                    $"{expression.Operator} CX_NULL)";
            }
            if (expression.Right is LiteralExpression { SourceText: "null" } &&
                IsNullableExpression(expression.Left))
            {
                return $"(({ToCExpression(expression.Left, functionDeclaration, moduleName)})._obj " +
                    $"{expression.Operator} CX_NULL)";
            }
        }

        return $"({ToCExpression(expression.Left, functionDeclaration, moduleName)} {expression.Operator} " +
            $"{ToCExpression(expression.Right, functionDeclaration, moduleName)})";
    }

    private static string ToCNullCoalescingExpression(
        NullCoalescingExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (expression.Left is LiteralExpression { SourceText: "null" })
        {
            return ToCExpression(expression.Right, functionDeclaration, moduleName);
        }

        var left = ToCExpression(expression.Left, functionDeclaration, moduleName);
        var right = ToCExpression(expression.Right, functionDeclaration, moduleName);
        var leftType = expression.Left.InferredType is ConstType constType
            ? constType.UnderlyingType
            : expression.Left.InferredType;
        if (leftType is NullableType nullableType)
        {
            var valueType = nullableType.UnderlyingType.ToCIdentifier(false);
            return $"(({left})._obj != CX_NULL ? *({valueType}*)({left})._obj : {right})";
        }

        return $"(({left}) != CX_NULL ? ({left}) : {right})";
    }

    private static bool IsNullableExpression(ExpressionBase expression)
    {
        var type = expression.InferredType is ConstType constType
            ? constType.UnderlyingType
            : expression.InferredType;
        return type is NullableType;
    }

    private static string ToCArrayAccess(
        ArrayAccessExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (expression.Indices.Count != 1)
        {
            throw new InternalCompilerException("Only one-dimensional array indexing can be emitted.");
        }

        var elementType = expression.InferredType ?? throw new InternalCompilerException(
            "Array access expression is not bound.");
        return $"(*({elementType.ToCIdentifier(false)}*)cx_array_at(" +
            $"{ToCExpression(expression.Target, functionDeclaration, moduleName)}, " +
            $"(cx_uint)({ToCExpression(expression.Indices[0], functionDeclaration, moduleName)}), " +
            $"(cx_uint)sizeof({elementType.ToCIdentifier(false)})))";
    }

    private static QualifiedIdentifier FlattenIdentifier(ExpressionBase expression)
    {
        return expression switch
        {
            IdentifierExpression identifier => identifier.Identifier,
            MemberAccessExpression memberAccess =>
                new QualifiedIdentifier(FlattenIdentifier(memberAccess.Target), memberAccess.MemberName),
            _ => throw new InternalCompilerException(
                $"Expression '{expression.GetType().Name}' cannot be used as a function name."),
        };
    }

    private static string ToCIdentifier(FunctionSymbol symbol)
    {
        var name = symbol.OverloadIndex > 1
            ? new QualifiedIdentifier(symbol.FullName, $"_{symbol.OverloadIndex}")
            : symbol.FullName;
        return new QualifiedIdentifier(symbol.ModuleName, name).ToCIdentifier();
    }

    private static IEnumerable<FunctionDeclaration> EnumerateFunctions(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is FunctionDeclaration functionDeclaration)
            {
                yield return functionDeclaration;
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

    private static IEnumerable<LiteralExpression> EnumerateStringLiterals(ExpressionBase expression)
    {
        switch (expression)
        {
            case LiteralExpression { IsString: true } literal:
                yield return literal;
                break;

            case MemberAccessExpression memberAccess:
                foreach (var literal in EnumerateStringLiterals(memberAccess.Target))
                {
                    yield return literal;
                }
                break;

            case InvocationExpression invocation:
                foreach (var literal in EnumerateStringLiterals(invocation.Target))
                {
                    yield return literal;
                }
                foreach (var argument in invocation.Arguments)
                {
                    foreach (var literal in EnumerateStringLiterals(argument))
                    {
                        yield return literal;
                    }
                }
                break;

            case ObjectCreationExpression creation:
                foreach (var argument in creation.Arguments)
                {
                    foreach (var literal in EnumerateStringLiterals(argument))
                    {
                        yield return literal;
                    }
                }
                break;

            case BinaryExpression binary:
                foreach (var literal in EnumerateStringLiterals(binary.Left))
                {
                    yield return literal;
                }
                foreach (var literal in EnumerateStringLiterals(binary.Right))
                {
                    yield return literal;
                }
                break;

            case ConditionalExpression conditional:
                foreach (var literal in EnumerateStringLiterals(conditional.Condition))
                {
                    yield return literal;
                }
                foreach (var literal in EnumerateStringLiterals(conditional.WhenTrue))
                {
                    yield return literal;
                }
                foreach (var literal in EnumerateStringLiterals(conditional.WhenFalse))
                {
                    yield return literal;
                }
                break;

            case NullCoalescingExpression coalescing:
                foreach (var literal in EnumerateStringLiterals(coalescing.Left))
                {
                    yield return literal;
                }
                foreach (var literal in EnumerateStringLiterals(coalescing.Right))
                {
                    yield return literal;
                }
                break;

            case UnaryExpression unary:
                foreach (var literal in EnumerateStringLiterals(unary.Operand))
                {
                    yield return literal;
                }
                break;

            case AssignmentExpression assignment:
                foreach (var literal in EnumerateStringLiterals(assignment.Target))
                {
                    yield return literal;
                }
                foreach (var literal in EnumerateStringLiterals(assignment.Value))
                {
                    yield return literal;
                }
                break;

            case ArrayCreationExpression arrayCreation:
                foreach (var literal in EnumerateStringLiterals(arrayCreation.Length))
                {
                    yield return literal;
                }
                break;

            case ArrayAccessExpression arrayAccess:
                foreach (var literal in EnumerateStringLiterals(arrayAccess.Target))
                {
                    yield return literal;
                }
                foreach (var index in arrayAccess.Indices)
                {
                    foreach (var literal in EnumerateStringLiterals(index))
                    {
                        yield return literal;
                    }
                }
                break;
        }
    }

    private static IEnumerable<ObjectCreationExpression> EnumerateObjectCreations(
        ExpressionBase expression)
    {
        if (expression is ObjectCreationExpression creation)
        {
            yield return creation;
            foreach (var argument in creation.Arguments)
            {
                foreach (var nested in EnumerateObjectCreations(argument))
                {
                    yield return nested;
                }
            }
            yield break;
        }

        foreach (var child in GetExpressionChildren(expression))
        {
            foreach (var nested in EnumerateObjectCreations(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<ObjectCreationExpression> EnumerateDirectObjectCreations(
        StatementBase statement)
    {
        return GetDirectExpressions(statement).SelectMany(EnumerateObjectCreations);
    }

    private static IEnumerable<AssignmentExpression> EnumeratePropertyAssignments(
        ExpressionBase expression)
    {
        if (expression is AssignmentExpression { PropertySetter: not null } assignment)
        {
            yield return assignment;
        }
        foreach (var child in GetExpressionChildren(expression))
        {
            foreach (var nested in EnumeratePropertyAssignments(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<AssignmentExpression> EnumerateDirectPropertyAssignments(
        StatementBase statement)
    {
        return GetDirectExpressions(statement).SelectMany(EnumeratePropertyAssignments);
    }

    private static IEnumerable<ExpressionBase> GetExpressionChildren(ExpressionBase expression)
    {
        return expression switch
        {
            MemberAccessExpression memberAccess => [memberAccess.Target],
            InvocationExpression invocation => [invocation.Target, .. invocation.Arguments],
            BinaryExpression binary => [binary.Left, binary.Right],
            ConditionalExpression conditional =>
                [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            NullCoalescingExpression coalescing => [coalescing.Left, coalescing.Right],
            UnaryExpression unary => [unary.Operand],
            AssignmentExpression assignment => [assignment.Target, assignment.Value],
            ArrayCreationExpression arrayCreation => [arrayCreation.Length],
            ArrayAccessExpression arrayAccess => [arrayAccess.Target, .. arrayAccess.Indices],
            ObjectCreationExpression creation => creation.Arguments,
            _ => [],
        };
    }

    private static IEnumerable<ExpressionBase> GetDirectExpressions(StatementBase statement)
    {
        return statement switch
        {
            ExpressionStatement expressionStatement => [expressionStatement.Expression],
            ReturnStatement { Expression: not null } returnStatement => [returnStatement.Expression],
            LocalVariableDeclarationStatement declaration => declaration.Declarators
                .Where(declarator => declarator.Initializer is not null)
                .Select(declarator => declarator.Initializer!),
            IfStatement conditional => [conditional.Condition],
            SwitchStatement switchStatement => [switchStatement.Expression, .. switchStatement.Sections
                .SelectMany(section => section.Labels)
                .SelectMany(label => new[] { label.Value, label.Filter })
                .Where(expression => expression is not null)
                .Select(expression => expression!)],
            WhileStatement whileStatement => [whileStatement.Condition],
            DoWhileStatement doWhileStatement => [doWhileStatement.Condition],
            ForStatement forStatement => [.. forStatement.InitializerExpressions,
                .. forStatement.Condition is null ? [] : new[] { forStatement.Condition },
                .. forStatement.Iterators,
                .. forStatement.DeclarationInitializer?.Declarators
                    .Where(declarator => declarator.Initializer is not null)
                    .Select(declarator => declarator.Initializer!) ?? []],
            ForeachStatement foreachStatement => [foreachStatement.Collection],
            _ => [],
        };
    }

    private static IEnumerable<LiteralExpression> EnumerateStringLiterals(StatementBase statement)
    {
        var expression = statement switch
        {
            ExpressionStatement expressionStatement => expressionStatement.Expression,
            ReturnStatement returnStatement => returnStatement.Expression,
            _ => null,
        };

        if (expression is not null)
        {
            return EnumerateStringLiterals(expression);
        }
        if (statement is LocalVariableDeclarationStatement declaration)
        {
            return declaration.Declarators
                .Where(declarator => declarator.Initializer is not null)
                .SelectMany(declarator => EnumerateStringLiterals(declarator.Initializer!));
        }
        if (statement is BlockStatement block)
        {
            return block.Statements.SelectMany(EnumerateStringLiterals);
        }
        if (statement is IfStatement conditional)
        {
            return EnumerateStringLiterals(conditional.Condition)
                .Concat(EnumerateStringLiterals(conditional.ThenStatement))
                .Concat(conditional.ElseStatement is null
                    ? []
                    : EnumerateStringLiterals(conditional.ElseStatement));
        }
        if (statement is SwitchStatement switchStatement)
        {
            return EnumerateStringLiterals(switchStatement.Expression)
                .Concat(switchStatement.Sections.SelectMany(section =>
                    section.Labels.SelectMany(label =>
                        (label.Value is null
                            ? Enumerable.Empty<LiteralExpression>()
                            : EnumerateStringLiterals(label.Value))
                        .Concat(label.Filter is null
                            ? Enumerable.Empty<LiteralExpression>()
                            : EnumerateStringLiterals(label.Filter)))))
                .Concat(switchStatement.Sections.SelectMany(section =>
                    section.Statements.SelectMany(EnumerateStringLiterals)));
        }
        if (statement is WhileStatement whileStatement)
        {
            return EnumerateStringLiterals(whileStatement.Condition)
                .Concat(EnumerateStringLiterals(whileStatement.Body));
        }
        if (statement is DoWhileStatement doWhileStatement)
        {
            return EnumerateStringLiterals(doWhileStatement.Body)
                .Concat(EnumerateStringLiterals(doWhileStatement.Condition));
        }
        if (statement is ForStatement forStatement)
        {
            var literals = forStatement.DeclarationInitializer is null
                ? Enumerable.Empty<LiteralExpression>()
                : EnumerateStringLiterals(forStatement.DeclarationInitializer);
            literals = literals.Concat(
                forStatement.InitializerExpressions.SelectMany(EnumerateStringLiterals));
            if (forStatement.Condition is not null)
            {
                literals = literals.Concat(EnumerateStringLiterals(forStatement.Condition));
            }
            return literals
                .Concat(forStatement.Iterators.SelectMany(EnumerateStringLiterals))
                .Concat(EnumerateStringLiterals(forStatement.Body));
        }
        if (statement is ForeachStatement foreachStatement)
        {
            return EnumerateStringLiterals(foreachStatement.Collection)
                .Concat(EnumerateStringLiterals(foreachStatement.Body));
        }

        return [];
    }
}
