using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
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
            if (functionDeclaration.GenericTypeNames.Length > 0)
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
                parameter => $"{ToCParameterType(parameter.ParameterType)} {parameter.Name}"));

            writer.Write(string.Join(", ", parameters));
            writer.WriteLine(") {");
            writer.IncreaseIndent();
            writer.WriteLine("#if !defined(CX_STATIC_LINK)");
            writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#endif");
            var context = new StatementWriteContext(functionDeclaration);
            context.WriteDeclarations(writer);

            if (functionDeclaration is ConstructorDeclaration constructor)
            {
                if (constructor.Initializer is { } initializer)
                {
                    foreach (var creation in initializer.Arguments.SelectMany(
                        EnumerateObjectCreations))
                    {
                        writer.WriteLine(
                            $"{creation.RequestedType.ToCIdentifier(false)} {creation.TemporaryName};");
                    }
                    var target = initializer.Target ?? throw new InternalCompilerException(
                        "Constructor initializer is not bound.");
                    var receiver = initializer.Kind == ConstructorInitializerKind.Base
                        ? "&__this->__base"
                        : "__this";
                    var initializerArguments = new[] { receiver }.Concat(
                        initializer.Arguments.Zip(target.ParameterTypes).Select(pair =>
                            ToCExpressionAsType(
                                pair.First,
                                pair.Second,
                                functionDeclaration,
                                moduleName)));
                    writer.WriteLine(
                        $"{ToCIdentifier(target)}({string.Join(", ", initializerArguments)});");
                }

                if (constructor.Initializer?.Kind != ConstructorInitializerKind.This)
                {
                    if (constructor.ParentClassDeclaration!.ClassType == ClassType.Class)
                    {
                        writer.WriteLine(
                            $"CX_INIT_VTABLE(__this, {constructor.ParentClassDeclaration.ToCIdentifier(moduleName, false)});");
                    }
                    foreach (var field in constructor.ParentClassDeclaration!.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>()
                        .Where(field => !field.IsStatic && field.Initializer is not null))
                    {
                        writer.WriteLine(
                            $"__this->{field.Name} = {ToCFieldInitializer(field.Initializer!, moduleName)};");
                    }
                }
            }

            foreach (var statement in functionDeclaration.Body)
            {
                WriteStatement(writer, statement, functionDeclaration, moduleName, context);
            }
            context.WriteEpilogue(writer);

            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    private static void WriteGenericFunctionDefinitions(
        IndentingWriter writer,
        IEnumerable<FunctionSymbol> instances,
        string moduleName)
    {
        foreach (var instance in instances
            .Where(item => item.Declaration?.Body is not null)
            .OrderBy(item => item.SpecializationName, StringComparer.Ordinal))
        {
            var declaration = instance.Declaration!;
            var parameters = GetGenericFunctionParameters(instance);
            writer.WriteLine($"{instance.ReturnType.ToCIdentifier(false)} " +
                $"{instance.SpecializationName}({string.Join(", ", parameters)})");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine("#if !defined(CX_STATIC_LINK)");
            writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#endif");
            if (!declaration.IsStatic && declaration is not ConstructorDeclaration)
            {
                writer.WriteLine("(void)__this;");
            }
            if (declaration is ConstructorDeclaration closedConstructor)
            {
                foreach (var statement in closedConstructor.Body ?? [])
                {
                    if (statement is ExpressionStatement
                        {
                            Expression: AssignmentExpression
                            {
                                Target: IdentifierExpression field,
                                Value: IdentifierExpression parameter,
                            },
                        })
                    {
                        writer.WriteLine(
                            $"__this->{field.Identifier.Parts[^1]} = " +
                            $"{parameter.Identifier.Parts[^1]};");
                    }
                }
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.Body is
                [ReturnStatement { Expression: IdentifierExpression { TargetField: { } field } }])
            {
                writer.WriteLine($"return __this->{field.Declaration.Name};");
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.Body is
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: IdentifierExpression parameter,
                    },
                }])
            {
                writer.WriteLine(
                    $"__this->{targetField.Declaration.Name} = {parameter.Identifier.Parts[^1]};");
            }
            else if (declaration.Body is
                [LocalVariableDeclarationStatement local,
                    ExpressionStatement
                    {
                        Expression: AssignmentExpression
                        {
                            Target: IdentifierExpression target,
                            Value: IdentifierExpression replacement,
                        },
                    },
                    ReturnStatement])
            {
                var declarator = local.Declarators.Single();
                var initial = (IdentifierExpression)declarator.Initializer!;
                writer.WriteLine($"{instance.ReturnType.ToCIdentifier(false)} " +
                    $"{declarator.Name} = {initial.Identifier.Parts[0]};");
                writer.WriteLine(
                    $"{target.Identifier.Parts[0]} = {replacement.Identifier.Parts[0]};");
                writer.WriteLine($"return {declarator.Name};");
            }
            else if (declaration.Body is
                [LocalVariableDeclarationStatement localCopy, ReturnStatement])
            {
                var declarator = localCopy.Declarators.Single();
                var source = (IdentifierExpression)declarator.Initializer!;
                writer.WriteLine($"{instance.ReturnType.ToCIdentifier(false)} " +
                    $"{declarator.Name} = {source.Identifier.Parts[0]};");
                writer.WriteLine($"return {declarator.Name};");
            }
            else if (declaration.Body is
                [IfStatement
                {
                    Condition: { } condition,
                    ThenStatement: ReturnStatement { Expression: IdentifierExpression whenTrue },
                    ElseStatement: ReturnStatement { Expression: IdentifierExpression whenFalse },
                }])
            {
                writer.WriteLine($"if ({ToCExpression(condition, declaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.WriteLine($"return {whenTrue.Identifier.Parts[0]};");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.WriteLine("else");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.WriteLine($"return {whenFalse.Identifier.Parts[0]};");
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            else
            {
                var returnedParameter = (IdentifierExpression)
                    ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine($"return {returnedParameter.Identifier.Parts[0]};");
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
                parameter => $"{ToCParameterType(parameter.ParameterType)} {parameter.Name}"));

            writer.Write(string.Join(", ", parameters));
            writer.WriteLine(") {");
            writer.IncreaseIndent();
            writer.WriteLine("#if !defined(CX_STATIC_LINK)");
            writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#endif");
            var context = new StatementWriteContext(function);
            context.WriteDeclarations(writer);

            foreach (var statement in function.Body!)
            {
                WriteStatement(writer, statement, function, moduleName, context);
            }
            context.WriteEpilogue(writer);

            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    private static void WriteStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        WriteExpressionTemporaries(writer, GetDirectExpressions(statement));

        switch (statement)
        {
            case ExpressionStatement expressionStatement:
                writer.WriteLine(
                    $"{ToCExpression(expressionStatement.Expression, functionDeclaration, moduleName)};");
                break;

            case ReturnStatement returnStatement:
                WriteReturnStatement(writer, returnStatement, functionDeclaration, moduleName, context);
                break;

            case LocalVariableDeclarationStatement localDeclaration:
                foreach (var declarator in localDeclaration.Declarators)
                {
                    var type = declarator.Type ?? throw new InternalCompilerException(
                        $"Local '{declarator.Name}' is not bound.");
                    var initializer = declarator.Initializer is null
                        ? string.Empty
                        : $" = {ToCExpressionAsType(declarator.Initializer, type, functionDeclaration, moduleName)}";
                    writer.WriteLine($"{type.ToCIdentifier(false)} {declarator.Name}{initializer};");
                }
                break;

            case BlockStatement block:
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var nestedStatement in block.Statements)
                {
                    WriteStatement(writer, nestedStatement, functionDeclaration, moduleName, context);
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
                    moduleName,
                    context);
                if (ifStatement.ElseStatement is not null)
                {
                    writer.WriteLine("else");
                    WriteControlledStatement(
                        writer,
                        ifStatement.ElseStatement,
                        functionDeclaration,
                        moduleName,
                        context);
                }
                break;

            case SwitchStatement switchStatement:
                WriteSwitchStatement(
                    writer,
                    switchStatement,
                    functionDeclaration,
                    moduleName,
                    context);
                break;

            case WhileStatement whileStatement:
                var whileTarget = context.PushLoop();
                writer.WriteLine(
                    $"while ({ToCExpression(whileStatement.Condition, functionDeclaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                WriteStatement(writer, whileStatement.Body, functionDeclaration, moduleName, context);
                if (whileTarget.ContinueLabelUsed)
                {
                    writer.WriteLine($"{whileTarget.ContinueLabel}:;");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                if (whileTarget.BreakLabelUsed)
                {
                    writer.WriteLine($"{whileTarget.BreakLabel}:;");
                }
                context.PopLoop(whileTarget);
                break;

            case DoWhileStatement doWhileStatement:
                var doTarget = context.PushLoop();
                writer.WriteLine("do");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                WriteStatement(writer, doWhileStatement.Body, functionDeclaration, moduleName, context);
                if (doTarget.ContinueLabelUsed)
                {
                    writer.WriteLine($"{doTarget.ContinueLabel}:;");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.WriteLine(
                    $"while ({ToCExpression(doWhileStatement.Condition, functionDeclaration, moduleName)});");
                if (doTarget.BreakLabelUsed)
                {
                    writer.WriteLine($"{doTarget.BreakLabel}:;");
                }
                context.PopLoop(doTarget);
                break;

            case ForStatement forStatement:
                var forTarget = context.PushLoop();
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
                writer.WriteLine("{");
                writer.IncreaseIndent();
                WriteStatement(writer, forStatement.Body, functionDeclaration, moduleName, context);
                if (forTarget.ContinueLabelUsed)
                {
                    writer.WriteLine($"{forTarget.ContinueLabel}:;");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                if (forTarget.BreakLabelUsed)
                {
                    writer.WriteLine($"{forTarget.BreakLabel}:;");
                }
                context.PopLoop(forTarget);
                break;

            case ForeachStatement foreachStatement:
                WriteForeachStatement(
                    writer,
                    foreachStatement,
                    functionDeclaration,
                    moduleName,
                    context);
                break;

            case ThrowStatement throwStatement:
                writer.WriteLine(throwStatement.Expression is null
                    ? "CX_RETHROW();"
                    : $"CX_THROW({ToCExpression(throwStatement.Expression, functionDeclaration, moduleName)});");
                break;

            case TryStatement tryStatement:
                WriteTryStatement(writer, tryStatement, functionDeclaration, moduleName, context);
                break;

            case EmptyStatement:
                writer.WriteLine(";");
                break;

            case BreakStatement:
                WriteLoopTransfer(writer, context, context.BreakTarget, isContinue: false);
                break;

            case ContinueStatement:
                WriteLoopTransfer(writer, context, context.ContinueTarget, isContinue: true);
                break;

            default:
                throw new InternalCompilerException(
                    $"Statement '{statement.GetType().Name}' is not yet supported by the C generator.");
        }
    }

    private static void WriteExpressionTemporaries(
        IndentingWriter writer,
        IEnumerable<ExpressionBase> expressions)
    {
        var expressionList = expressions.ToArray();
        foreach (var creation in expressionList.SelectMany(EnumerateObjectCreations))
        {
            var temporaryName = creation.TemporaryName ?? throw new InternalCompilerException(
                "Object creation expression is not bound.");
            writer.WriteLine($"{creation.RequestedType.ToCIdentifier(false)} {temporaryName};");
        }
        foreach (var assignment in expressionList.SelectMany(EnumeratePropertyAssignments))
        {
            var temporaryName = assignment.TemporaryName ?? throw new InternalCompilerException(
                "Property assignment expression is not bound.");
            writer.WriteLine(
                $"{assignment.TargetProperty!.Type.ToCIdentifier(false)} {temporaryName};");
        }
        foreach (var invocation in expressionList.SelectMany(EnumerateInterfaceInvocations))
        {
            var temporaryName = invocation.ReceiverTemporaryName!;
            var receiverType = invocation.Receiver?.InferredType ??
                throw new InternalCompilerException("Interface invocation receiver is not bound.");
            var temporaryType = receiverType is ConstType constType
                ? constType.UnderlyingType
                : receiverType;
            writer.WriteLine($"{temporaryType.ToCIdentifier(false)} {temporaryName};");
        }
        foreach (var propertyExpression in expressionList.SelectMany(EnumerateInterfacePropertyReceivers))
        {
            var receiver = GetPropertyReceiver(propertyExpression) ??
                throw new InternalCompilerException("Interface property receiver is not bound.");
            var receiverType = receiver.InferredType ??
                throw new InternalCompilerException("Interface property receiver type is not bound.");
            var temporaryType = receiverType is ConstType constType
                ? constType.UnderlyingType
                : receiverType;
            writer.WriteLine(
                $"{temporaryType.ToCIdentifier(false)} {GetInterfacePropertyTemporaryName(propertyExpression)};");
        }

    }

    private static void WriteReturnStatement(
        IndentingWriter writer,
        ReturnStatement statement,
        FunctionDeclaration function,
        string moduleName,
        StatementWriteContext context)
    {
        var expression = statement.Expression is null
            ? null
            : ToCExpressionAsType(statement.Expression, function.ReturnType, function, moduleName);
        var target = FlowTransfer.Return(context.NextTransferId(), context.FrameCount);
        var crossesFinally = context.CrossesFinally(target);
        var leavesExceptionRegion = context.FrameCount > target.FrameDepth ||
            context.InCatch || context.InFinallyBody;
        if (!crossesFinally && !leavesExceptionRegion)
        {
            writer.WriteLine(expression is null ? "return;" : $"return {expression};");
            return;
        }

        if (expression is not null)
        {
            writer.WriteLine($"__cx_return_value = {expression};");
        }
        context.HasStructuredReturn = true;
        if (crossesFinally)
        {
            WriteStructuredTransfer(writer, context, target);
        }
        else
        {
            context.WriteCatchClear(writer);
            writer.WriteLine($"__cx_transfer = {target.Id};");
            context.WriteFramePops(writer, target.FrameDepth);
            writer.WriteLine($"if (__cx_transfer == {target.Id}) " +
                (function.ReturnType is VoidType ? "return;" : "return __cx_return_value;"));
        }
    }

    private static void WriteLoopTransfer(
        IndentingWriter writer,
        StatementWriteContext context,
        FlowTarget target,
        bool isContinue)
    {
        if (isContinue)
        {
            target.ContinueLabelUsed = context.FrameCount > target.FrameDepth ||
                context.FinallyDepth > target.FinallyDepth || context.InCatch ||
                context.InFinallyBody;
        }
        else
        {
            target.BreakLabelUsed = context.FrameCount > target.FrameDepth ||
                context.FinallyDepth > target.FinallyDepth || context.InCatch ||
                context.InFinallyBody;
        }
        var transfer = new FlowTransfer(
            context.NextTransferId(),
            isContinue ? target.ContinueLabel! : target.BreakLabel,
            target.FinallyDepth,
            target.FrameDepth,
            IsReturn: false);
        if (!context.CrossesFinally(transfer))
        {
            context.WriteCatchClear(writer);
            if (context.FrameCount > transfer.FrameDepth || context.InCatch || context.InFinallyBody)
            {
                writer.WriteLine($"__cx_transfer = {transfer.Id};");
            }
            context.WriteFramePops(writer, transfer.FrameDepth);
            writer.WriteLine(context.FrameCount > transfer.FrameDepth || context.InCatch || context.InFinallyBody
                ? $"if (__cx_transfer == {transfer.Id}) goto {transfer.DestinationLabel};"
                : isContinue ? "continue;" : "break;");
            return;
        }
        WriteStructuredTransfer(writer, context, transfer);
    }

    private static void WriteStructuredTransfer(
        IndentingWriter writer,
        StatementWriteContext context,
        FlowTransfer transfer)
    {
        var finallyScope = context.RegisterWithInnermostFinally(transfer);
        context.WriteCatchClear(writer);
        writer.WriteLine($"__cx_transfer = {transfer.Id};");
        context.WriteFramePops(writer, finallyScope.EntryFrameDepth);
        writer.WriteLine($"if (__cx_transfer == {transfer.Id}) goto {finallyScope.CleanupLabel};");
    }

    private static void WriteTransferDispatch(
        IndentingWriter writer,
        StatementWriteContext context,
        FinallyScope scope)
    {
        foreach (var transfer in scope.Transfers)
        {
            writer.WriteLine($"if (__cx_transfer == {transfer.Id})");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            if (context.CrossesFinally(transfer))
            {
                var outer = context.RegisterWithInnermostFinally(transfer);
                context.WriteFramePops(writer, outer.EntryFrameDepth);
                writer.WriteLine($"goto {outer.CleanupLabel};");
            }
            else
            {
                context.WriteFramePops(writer, transfer.FrameDepth);
                writer.WriteLine(transfer.IsReturn
                    ? context.Function.ReturnType is VoidType
                        ? "return;"
                        : "return __cx_return_value;"
                    : $"goto {transfer.DestinationLabel};");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
    }

    private sealed class StatementWriteContext(FunctionDeclaration function)
    {
        private int _nextId;
        private int _catchDepth;
        private int _finallyBodyDepth;
        private readonly List<string> _frames = [];
        private readonly List<FinallyScope> _finallyScopes = [];
        private readonly Stack<FlowTarget> _breakTargets = [];
        private readonly Stack<FlowTarget> _continueTargets = [];

        public FunctionDeclaration Function { get; } = function;
        public int FrameCount => _frames.Count;
        public int FinallyDepth => _finallyScopes.Count;
        public bool InCatch => _catchDepth > 0;
        public bool InFinallyBody => _finallyBodyDepth > 0;
        public bool HasStructuredReturn { get; set; }
        public FlowTarget BreakTarget => _breakTargets.Peek();
        public FlowTarget ContinueTarget => _continueTargets.Peek();

        public int NextTransferId() => ++_nextId;

        public void WriteDeclarations(IndentingWriter writer)
        {
            writer.WriteLine("cx_int __cx_transfer = 0;");
            writer.WriteLine("(void)&__cx_transfer;");
            if (Function.ReturnType is not VoidType)
            {
                var returnType = ReturnsGenericClassReference(Function)
                    ? "void*"
                    : Function.ReturnType.ToCIdentifier(false);
                writer.WriteLine($"{returnType} __cx_return_value;");
                writer.WriteLine("(void)&__cx_return_value;");
            }
        }

        public void WriteEpilogue(IndentingWriter writer)
        {
            if (HasStructuredReturn && Function.ReturnType is not VoidType)
            {
                writer.WriteLine("return __cx_return_value;");
            }
        }

        public string PushExceptionFrame()
        {
            var name = $"__cx_exception_frame_{++_nextId}";
            _frames.Add(name);
            return name;
        }

        public void PopExceptionFrame(string name)
        {
            if (_frames.Count == 0 || _frames[^1] != name)
            {
                throw new InternalCompilerException("Exception frame generation stack is unbalanced.");
            }
            _frames.RemoveAt(_frames.Count - 1);
        }

        public FinallyScope PushFinally()
        {
            var id = ++_nextId;
            var scope = new FinallyScope(
                $"__cx_finally_frame_{id}",
                $"__cx_finally_{id}",
                _frames.Count);
            _finallyScopes.Add(scope);
            _frames.Add(scope.FrameName);
            return scope;
        }

        public void LeaveFinallyProtectedRegion(FinallyScope scope)
        {
            PopExceptionFrame(scope.FrameName);
            if (_finallyScopes.Count == 0 || _finallyScopes[^1] != scope)
            {
                throw new InternalCompilerException("Finally generation stack is unbalanced.");
            }
            _finallyScopes.RemoveAt(_finallyScopes.Count - 1);
        }

        public bool CrossesFinally(FlowTransfer transfer) =>
            _finallyScopes.Count > transfer.FinallyDepth;

        public FinallyScope RegisterWithInnermostFinally(FlowTransfer transfer)
        {
            var scope = _finallyScopes[^1];
            scope.Transfers.Add(transfer);
            return scope;
        }

        public FlowTarget PushLoop()
        {
            var id = ++_nextId;
            var target = new FlowTarget(
                $"__cx_break_{id}",
                $"__cx_continue_{id}",
                _finallyScopes.Count,
                _frames.Count);
            _breakTargets.Push(target);
            _continueTargets.Push(target);
            return target;
        }

        public void PopLoop(FlowTarget target)
        {
            if (_breakTargets.Pop() != target || _continueTargets.Pop() != target)
            {
                throw new InternalCompilerException("Loop generation stack is unbalanced.");
            }
        }

        public FlowTarget PushSwitch()
        {
            var id = ++_nextId;
            var target = new FlowTarget(
                $"__cx_break_{id}",
                null,
                _finallyScopes.Count,
                _frames.Count);
            _breakTargets.Push(target);
            return target;
        }

        public void PopSwitch(FlowTarget target)
        {
            if (_breakTargets.Pop() != target)
            {
                throw new InternalCompilerException("Switch generation stack is unbalanced.");
            }
        }

        public void EnterCatch() => _catchDepth++;
        public void LeaveCatch() => _catchDepth--;
        public void EnterFinallyBody() => _finallyBodyDepth++;
        public void LeaveFinallyBody() => _finallyBodyDepth--;

        public void WriteCatchClear(IndentingWriter writer)
        {
            if (_catchDepth > 0)
            {
                writer.WriteLine("cx_exception_clear();");
            }
            else if (_frames.Count > 0 || _finallyBodyDepth > 0)
            {
                writer.WriteLine("if (cx_exception_pending()) cx_exception_clear();");
            }
        }

        public void WriteFramePops(IndentingWriter writer, int targetDepth)
        {
            for (var index = _frames.Count - 1; index >= targetDepth; index--)
            {
                writer.WriteLine($"cx_exception_pop(&{_frames[index]});");
            }
        }
    }

    private sealed class FlowTarget(
        string breakLabel,
        string? continueLabel,
        int finallyDepth,
        int frameDepth)
    {
        public string BreakLabel { get; } = breakLabel;
        public string? ContinueLabel { get; } = continueLabel;
        public int FinallyDepth { get; } = finallyDepth;
        public int FrameDepth { get; } = frameDepth;
        public bool BreakLabelUsed { get; set; }
        public bool ContinueLabelUsed { get; set; }
    }

    private sealed record FlowTransfer(
        int Id,
        string? DestinationLabel,
        int FinallyDepth,
        int FrameDepth,
        bool IsReturn)
    {
        public static FlowTransfer Return(int id, int frameDepth) =>
            new(id, null, 0, 0, IsReturn: true);
    }

    private sealed record FinallyScope(
        string FrameName,
        string CleanupLabel,
        int EntryFrameDepth)
    {
        public List<FlowTransfer> Transfers { get; } = [];
    }

    private static void WriteTryStatement(
        IndentingWriter writer,
        TryStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        writer.WriteLine("{");
        writer.IncreaseIndent();

        if (statement.FinallyBody is not null)
        {
            var finallyScope = context.PushFinally();
            writer.WriteLine($"struct cx_exception_frame {finallyScope.FrameName};");
            writer.WriteLine($"cx_exception_push(&{finallyScope.FrameName});");
            writer.WriteLine($"if (setjmp({finallyScope.FrameName}.environment) == 0)");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            WriteTryAndCatches(writer, statement, functionDeclaration, moduleName, context);
            writer.WriteLine($"cx_exception_pop(&{finallyScope.FrameName});");
            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine("else");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine($"cx_exception_pop(&{finallyScope.FrameName});");
            writer.DecreaseIndent();
            writer.WriteLine("}");
            context.LeaveFinallyProtectedRegion(finallyScope);
            writer.WriteLine($"{finallyScope.CleanupLabel}:;");
            context.EnterFinallyBody();
            WriteControlledStatement(
                writer,
                statement.FinallyBody,
                functionDeclaration,
                moduleName,
                context);
            context.LeaveFinallyBody();
            writer.WriteLine("if (cx_exception_pending())");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine("CX_RETHROW();");
            writer.DecreaseIndent();
            writer.WriteLine("}");
            WriteTransferDispatch(writer, context, finallyScope);
        }
        else
        {
            WriteTryAndCatches(writer, statement, functionDeclaration, moduleName, context);
        }

        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteTryAndCatches(
        IndentingWriter writer,
        TryStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        var frameName = context.PushExceptionFrame();
        writer.WriteLine($"struct cx_exception_frame {frameName};");
        writer.WriteLine($"cx_exception_push(&{frameName});");
        writer.WriteLine($"if (setjmp({frameName}.environment) == 0)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteStatement(writer, statement.Body, functionDeclaration, moduleName, context);
        writer.WriteLine($"cx_exception_pop(&{frameName});");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("else");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        context.PopExceptionFrame(frameName);
        writer.WriteLine($"cx_exception_pop(&{frameName});");

        foreach (var clause in statement.CatchClauses)
        {
            var typeInfo = clause.ExceptionType is NamedType namedType
                ? new QualifiedIdentifier(namedType.ResolvedTypeFullName, "__typeinfo").ToCIdentifier()
                : throw new InternalCompilerException("A catch clause has a non-class type.");
            writer.WriteLine(
                $"if (cx_exception_pending() && CX_ID_4(cxcore, System, Exception, Matches)(" +
                $"cx_exception_current(), {typeInfo}))");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            if (clause.VariableName is not null)
            {
                writer.WriteLine(
                    $"{clause.ExceptionType.ToCIdentifier(false)} {clause.VariableName} = " +
                    $"({clause.ExceptionType.ToCIdentifier(false)})cx_exception_current();");
            }

            if (clause.Filter is not null)
            {
                WriteExpressionTemporaries(writer, [clause.Filter]);
            }

            if (clause.Filter is not null)
            {
                writer.WriteLine(
                    $"if ({ToCExpression(clause.Filter, functionDeclaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
            }

            context.EnterCatch();
            WriteStatement(writer, clause.Body, functionDeclaration, moduleName, context);
            context.LeaveCatch();
            writer.WriteLine("cx_exception_clear();");

            if (clause.Filter is not null)
            {
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }

        writer.WriteLine("if (cx_exception_pending())");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("CX_RETHROW();");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteControlledStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        if (statement is BlockStatement)
        {
            WriteStatement(writer, statement, functionDeclaration, moduleName, context);
            return;
        }

        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteStatement(writer, statement, functionDeclaration, moduleName, context);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteForeachStatement(
        IndentingWriter writer,
        ForeachStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        var variableType = statement.VariableType ?? throw new InternalCompilerException(
            $"Foreach variable '{statement.VariableName}' is not bound.");
        var collectionName = $"__cx_foreach_collection_{statement.VariableName}";
        var indexName = $"__cx_foreach_index_{statement.VariableName}";

        writer.WriteLine("{");
        writer.IncreaseIndent();
        var loopTarget = context.PushLoop();
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
                WriteStatement(writer, nestedStatement, functionDeclaration, moduleName, context);
            }
        }
        else
        {
            WriteStatement(writer, statement.Body, functionDeclaration, moduleName, context);
        }
        if (loopTarget.ContinueLabelUsed)
        {
            writer.WriteLine($"{loopTarget.ContinueLabel}:;");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
        if (loopTarget.BreakLabelUsed)
        {
            writer.WriteLine($"{loopTarget.BreakLabel}:;");
        }
        context.PopLoop(loopTarget);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteSwitchStatement(
        IndentingWriter writer,
        SwitchStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        var switchTarget = context.PushSwitch();
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
                WriteStatement(writer, nestedStatement, functionDeclaration, moduleName, context);
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
        if (switchTarget.BreakLabelUsed)
        {
            writer.WriteLine($"{switchTarget.BreakLabel}:;");
        }
        context.PopSwitch(switchTarget);
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
                : $" = {ToCExpressionAsType(declarator.Initializer, declaratorType, functionDeclaration, moduleName)}";
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
                ToCInvocation(invocation, functionDeclaration, moduleName),
            IdentifierExpression { PropertyGetter: not null } identifier =>
                ToCPropertyCall(
                    identifier.TargetProperty!,
                    identifier.PropertyGetter,
                    null,
                    [],
                    null,
                    identifier.ReceiverBaseDepth,
                    null,
                    null,
                    functionDeclaration,
                    moduleName),
            IdentifierExpression identifier => identifier.TargetField is null
                ? identifier.Identifier.ToCIdentifier()
                : ToCFieldAccess(
                    identifier.TargetField,
                    null,
                    identifier.ReceiverBaseDepth,
                    functionDeclaration,
                    moduleName),
            ThisExpression => "__this",
            BinaryExpression binary =>
                ToCBinaryExpression(binary, functionDeclaration, moduleName),
            CastExpression cast =>
                ToCCastExpression(cast, functionDeclaration, moduleName),
            TypeTestExpression typeTest =>
                ToCTypeTestExpression(typeTest, functionDeclaration, moduleName),
            NullCoalescingExpression coalescing =>
                ToCNullCoalescingExpression(coalescing, functionDeclaration, moduleName),
            ConditionalExpression conditional =>
                $"({ToCExpression(conditional.Condition, functionDeclaration, moduleName)} ? " +
                $"{ToCExpressionAsType(conditional.WhenTrue, conditional.InferredType!, functionDeclaration, moduleName)} : " +
                $"{ToCExpressionAsType(conditional.WhenFalse, conditional.InferredType!, functionDeclaration, moduleName)})",
            UnaryExpression unary when unary.Postfix =>
                $"({ToCExpression(unary.Operand, functionDeclaration, moduleName)}{unary.Operator})",
            UnaryExpression unary =>
                $"({unary.Operator}{ToCExpression(unary.Operand, functionDeclaration, moduleName)})",
            AssignmentExpression { PropertySetter: not null } assignment =>
                ToCPropertyAssignment(assignment, functionDeclaration, moduleName),
            AssignmentExpression assignment =>
                $"{ToCExpression(assignment.Target, functionDeclaration, moduleName)} {assignment.Operator} " +
                (assignment.Operator == "=" && assignment.Target.InferredType is { } targetType
                    ? ToCExpressionAsType(assignment.Value, targetType, functionDeclaration, moduleName)
                    : ToCExpression(assignment.Value, functionDeclaration, moduleName)),
            ArrayCreationExpression arrayCreation =>
                $"cx_array_new((cx_uint)({ToCExpression(arrayCreation.Length, functionDeclaration, moduleName)}), " +
                $"(cx_uint)sizeof({arrayCreation.ElementType.ToCIdentifier(false)}))",
            ObjectCreationExpression objectCreation =>
                ToCObjectCreationExpression(objectCreation, functionDeclaration, moduleName),
            ArrayAccessExpression { PropertyGetter: not null } arrayAccess =>
                ToCIndexedPropertyGetter(arrayAccess, functionDeclaration, moduleName),
            ArrayAccessExpression arrayAccess =>
                ToCArrayAccess(arrayAccess, functionDeclaration, moduleName),
            MemberAccessExpression { TargetEnumMember: not null } memberAccess =>
                new QualifiedIdentifier(
                    memberAccess.TargetEnumMember.ModuleName,
                    memberAccess.TargetEnumMember.Declaration.FullName).ToCIdentifier(),
            MemberAccessExpression { PropertyGetter: not null } memberAccess =>
                ToCPropertyCall(
                    memberAccess.TargetProperty!,
                    memberAccess.PropertyGetter,
                    memberAccess.TargetProperty!.IsStatic ? null : memberAccess.Target,
                    [],
                    null,
                    memberAccess.ReceiverBaseDepth,
                    memberAccess.InterfaceDispatchSlotIndex,
                    memberAccess.InterfaceReceiverTemporaryName,
                    functionDeclaration,
                    moduleName),
            MemberAccessExpression { TargetField: not null } memberAccess =>
                ToCFieldAccess(
                    memberAccess.TargetField,
                    memberAccess.Target,
                    memberAccess.ReceiverBaseDepth,
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
            expression.ReceiverBaseDepth,
            expression.InterfaceDispatchSlotIndex,
            expression.InterfaceReceiverTemporaryName,
            functionDeclaration,
            moduleName);
    }

    private static string ToCInvocation(
        InvocationExpression invocation,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var target = invocation.TargetSymbol ?? throw new InternalCompilerException(
            "Invocation target is not bound.");
        if (invocation.DispatchSlotIndex is { } interfaceSlotIndex)
        {
            var interfaceReceiver = invocation.Receiver ?? throw new InternalCompilerException(
                "Interface invocation has no receiver.");
            var receiver = ToCExpression(
                interfaceReceiver,
                functionDeclaration,
                moduleName);
            var receiverTemporary = invocation.ReceiverTemporaryName ??
                throw new InternalCompilerException(
                    "Interface invocation receiver temporary is not bound.");
            var convertedArguments = invocation.Arguments.Zip(target.ParameterTypes).Select(pair =>
                ToCExpressionAsType(pair.First, pair.Second, functionDeclaration, moduleName));
            var interfaceParameterTypes = new[] { "cx_ptr" }.Concat(
                target.ParameterTypes.Select(type => type.ToCIdentifier(false)));
            var interfaceFunctionPointer =
                $"({target.ReturnType.ToCReturnType(false)} (*)({string.Join(", ", interfaceParameterTypes)}))";
            var interfaceArguments = new[] { $"({receiverTemporary}).instance" }
                .Concat(convertedArguments);
            var call = $"({interfaceFunctionPointer}((union cx_vtable_entry*)" +
                $"({receiverTemporary}).vtable)[{interfaceSlotIndex}].function)" +
                $"({string.Join(", ", interfaceArguments)})";
            return $"({receiverTemporary} = {receiver}, {call})";
        }

        var arguments = new List<string>();
        if (target.Declaration is { IsStatic: false })
        {
            if (invocation.Receiver is null)
            {
                arguments.Add(ToCBaseReceiver(
                    "__this",
                    true,
                    invocation.ReceiverBaseDepth));
            }
            else
            {
                var receiver = ToCExpression(
                    invocation.Receiver,
                    functionDeclaration,
                    moduleName);
                arguments.Add(ToCBaseReceiver(
                    receiver,
                    IsCPointerReceiver(invocation.Receiver),
                    invocation.ReceiverBaseDepth));
            }
        }
        arguments.AddRange(invocation.Arguments.Zip(target.ParameterTypes).Select(pair =>
            ToCExpressionAsType(pair.First, pair.Second, functionDeclaration, moduleName)));
        if (target.Declaration?.VirtualSlotIndex is not { } slotIndex)
        {
            return $"{ToCIdentifier(target)}({string.Join(", ", arguments)})";
        }

        var dispatchReceiver = arguments[0];
        var contract = target.Declaration.VirtualContract ?? target.Declaration;
        var receiverConst = contract.Const ? "const " : string.Empty;
        var parameterTypes = new List<string>
        {
            $"{receiverConst}{contract.ParentClassDeclaration!.ToCIdentifier(moduleName)}*",
        };
        parameterTypes.AddRange(contract.Parameters.Select(parameter =>
            parameter.ParameterType.ToCIdentifier(false)));
        var returnType = contract.ReturnType.ToCReturnType(false);
        var functionPointer =
            $"({returnType} (*)({string.Join(", ", parameterTypes)}))";
        return $"({functionPointer}((union cx_vtable_entry*)CX_GET_VTABLE({dispatchReceiver}))[{slotIndex}].function)" +
            $"({string.Join(", ", arguments)})";
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
            ToCExpressionAsType(
                expression.Value,
                expression.TargetProperty!.Type,
                functionDeclaration,
                moduleName);
        var setterCall = ToCPropertyCall(
            expression.TargetProperty!,
            expression.PropertySetter!,
            receiver,
            indexExpressions,
            value,
            expression.ReceiverBaseDepth,
            expression.InterfaceDispatchSlotIndex,
            expression.InterfaceReceiverTemporaryName,
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
        int receiverBaseDepth,
        int? interfaceDispatchSlotIndex,
        string? interfaceReceiverTemporaryName,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (interfaceDispatchSlotIndex is { } slotIndex)
        {
            if (receiver is null || interfaceReceiverTemporaryName is null)
            {
                throw new InternalCompilerException("Interface property dispatch is not bound.");
            }
            var receiverExpression = ToCExpression(receiver, functionDeclaration, moduleName);
            var convertedIndices = indexArguments.Zip(accessor.ParameterTypes).Select(pair =>
                ToCExpressionAsType(pair.First, pair.Second, functionDeclaration, moduleName));
            var parameterTypes = new[] { "cx_ptr" }
                .Concat(accessor.ParameterTypes.Select(type => type.ToCIdentifier(false)))
                .Concat(value is null ? [] : [property.Type.ToCIdentifier(false)]);
            var returnType = value is null
                ? property.Type.ToCReturnType(false)
                : BuiltInSystemTypes.Void.ToCReturnType(false);
            var functionPointer =
                $"({returnType} (*)({string.Join(", ", parameterTypes)}))";
            var interfaceArguments = new[] { $"({interfaceReceiverTemporaryName}).instance" }
                .Concat(convertedIndices)
                .Concat(value is null ? [] : [value]);
            var call = $"({functionPointer}((union cx_vtable_entry*)" +
                $"({interfaceReceiverTemporaryName}).vtable)[{slotIndex}].function)" +
                $"({string.Join(", ", interfaceArguments)})";
            return $"({interfaceReceiverTemporaryName} = {receiverExpression}, {call})";
        }

        var arguments = new List<string>();
        if (!property.IsStatic)
        {
            if (receiver is null)
            {
                arguments.Add(ToCBaseReceiver("__this", true, receiverBaseDepth));
            }
            else
            {
                var receiverExpression = ToCExpression(receiver, functionDeclaration, moduleName);
                var receiverIsPointer = IsCPointerReceiver(receiver);
                arguments.Add(ToCBaseReceiver(
                    receiverExpression,
                    receiverIsPointer,
                    receiverBaseDepth));
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
        int receiverBaseDepth,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (field.Declaration.IsStatic)
        {
            return GetStaticFieldIdentifier(field.Declaration, field.ModuleName);
        }
        if (receiver is null)
        {
            if (receiverBaseDepth == 0)
            {
                return $"__this->{field.Declaration.Name}";
            }
            return $"{ToCBaseValue("__this", true, receiverBaseDepth)}.{field.Declaration.Name}";
        }

        var receiverExpression = ToCExpression(receiver, functionDeclaration, moduleName);
        if (receiverBaseDepth > 0)
        {
            return $"{ToCBaseValue(receiverExpression, true, receiverBaseDepth)}.{field.Declaration.Name}";
        }
        if (receiver.InferredType is NamedType { } closedType &&
            RequiresClosedValueLayout(closedType))
        {
            return $"((struct {closedType.ConstructedIdentity!.CIdentifier}*)" +
                $"({receiverExpression})){(field.ContainingClassType == ClassType.Class ? "->" : ".")}" +
                field.Declaration.Name;
        }
        var accessOperator = field.ContainingClassType == ClassType.Class ? "->" : ".";
        return $"({receiverExpression}){accessOperator}{field.Declaration.Name}";
    }

    private static string ToCBaseReceiver(
        string receiver,
        bool receiverIsPointer,
        int baseDepth)
    {
        return baseDepth == 0
            ? receiverIsPointer ? receiver : $"&({receiver})"
            : $"&({ToCBaseValue(receiver, receiverIsPointer, baseDepth)})";
    }

    private static string ToCBaseValue(
        string receiver,
        bool receiverIsPointer,
        int baseDepth)
    {
        var value = receiverIsPointer ? $"({receiver})->__base" : $"({receiver}).__base";
        for (var depth = 1; depth < baseDepth; depth++)
        {
            value += ".__base";
        }
        return value;
    }

    private static bool IsCReferenceType(TypeBase type)
    {
        type = type is ConstType constType ? constType.UnderlyingType : type;
        return type is ReferenceTypeBase or ArrayType ||
            type is NamedType { ClassType: ClassType.Class };
    }

    private static bool IsCPointerReceiver(ExpressionBase receiver)
    {
        return receiver is ThisExpression ||
            receiver.InferredType is not null && IsCReferenceType(receiver.InferredType);
    }

    private static string ToCExpressionAsType(
        ExpressionBase expression,
        TypeBase targetType,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var value = ToCExpression(expression, functionDeclaration, moduleName);
        if (expression.InferredType is not { } sourceType)
        {
            return value;
        }

        var unwrappedTarget = targetType is ConstType targetConst
            ? targetConst.UnderlyingType
            : targetType;
        var unwrappedSource = sourceType is ConstType sourceConst
            ? sourceConst.UnderlyingType
            : sourceType;
        if (unwrappedTarget is NullableType nullableTarget &&
            unwrappedSource is not NullableType &&
            unwrappedSource is not NullType)
        {
            var nullableType = nullableTarget.ToCIdentifier(false);
            if (IsCReferenceType(nullableTarget.UnderlyingType))
            {
                return $"({nullableType}){{ (cx_ptr)({value}) }}";
            }

            var valueType = nullableTarget.UnderlyingType.ToCIdentifier(false);
            return $"({nullableType}){{ CX_ID_4(cxcore, System, Nullable, CreateValueStorage)(" +
                $"(cx_ptr)&(({valueType}[]){{ {value} }})[0], " +
                $"(cx_uint)sizeof({valueType})) }}";
        }
        if (unwrappedTarget is NamedType { ClassType: ClassType.Interface } targetInterface &&
            unwrappedSource is NamedType { ClassType: ClassType.Class } sourceClass)
        {
            var vtable = GetInterfaceVTableIdentifier(
                sourceClass.ResolvedTypeFullName,
                targetInterface.ResolvedTypeFullName).ToCIdentifier();
            return $"({targetType.ToCIdentifier(false)}){{ {vtable}, (cx_ptr)({value}) }}";
        }
        if (unwrappedTarget is NamedType { ClassType: ClassType.Interface } &&
            unwrappedSource is NamedType { ClassType: ClassType.Interface } &&
            expression.InterfaceUpcastSlotIndex is { } upcastSlotIndex)
        {
            return $"cx_iface_upcast({value}, {upcastSlotIndex})";
        }

        if (!IsCReferenceType(targetType) ||
            !IsCReferenceType(sourceType) ||
            targetType.ToCIdentifier(false) == sourceType.ToCIdentifier(false))
        {
            return value;
        }

        return $"({targetType.ToCIdentifier(false)})({value})";
    }

    private static string ToCCastExpression(
        CastExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var targetType = expression.TargetType;
        var targetClassType = GetReferenceClassType(targetType);
        var targetCType = targetType.ToCIdentifier(false);
        if (expression.Operand.InferredType is NullType)
        {
            return targetClassType == ClassType.Interface
                ? $"({targetCType}){{ CX_NULL, CX_NULL }}"
                : $"({targetCType})CX_NULL";
        }

        var sourceClassType = GetReferenceClassType(expression.Operand.InferredType!);
        var value = ToCExpression(expression.Operand, functionDeclaration, moduleName);
        var typeInfo = $"&{GetTypeInfoIdentifier(targetType).ToCIdentifier()}";
        return (sourceClassType, targetClassType) switch
        {
            (ClassType.Class, ClassType.Class) =>
                $"({targetCType})cx_checked_cast_object((cx_ptr)({value}), {typeInfo})",
            (ClassType.Interface, ClassType.Class) =>
                $"({targetCType})cx_checked_cast_interface({value}, {typeInfo})",
            (ClassType.Class, ClassType.Interface) =>
                $"cx_checked_cast_object_to_interface((cx_ptr)({value}), {typeInfo})",
            (ClassType.Interface, ClassType.Interface) =>
                $"cx_checked_cast_interface_to_interface({value}, {typeInfo})",
            _ => throw new InternalCompilerException("Checked cast requires reference types."),
        };
    }

    private static string ToCTypeTestExpression(
        TypeTestExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (expression.Operand.InferredType is NullType)
        {
            return "CX_FALSE";
        }

        var value = ToCExpression(expression.Operand, functionDeclaration, moduleName);
        var typeInfo = $"&{GetTypeInfoIdentifier(expression.TargetType).ToCIdentifier()}";
        return GetReferenceClassType(expression.Operand.InferredType!) == ClassType.Interface
            ? $"cx_is_interface({value}, {typeInfo})"
            : $"cx_is_object((cx_ptr)({value}), {typeInfo})";
    }

    private static ClassType GetReferenceClassType(TypeBase type)
    {
        type = type is ConstType constType ? constType.UnderlyingType : type;
        return type switch
        {
            ObjectType or StringType => ClassType.Class,
            NamedType namedType when namedType.ClassType is ClassType.Class or ClassType.Interface =>
                namedType.ClassType,
            _ => throw new InternalCompilerException($"Type '{type.FullName}' is not a reference type."),
        };
    }

    private static QualifiedIdentifier GetTypeInfoIdentifier(TypeBase type)
    {
        type = type is ConstType constType ? constType.UnderlyingType : type;
        var fullName = type switch
        {
            NamedType { ConstructedIdentity: { } identity } =>
                new QualifiedIdentifier(identity.CIdentifier),
            NamedType namedType => namedType.ResolvedTypeFullName,
            ArrayType => new QualifiedIdentifier("cxcore", "System", "Array"),
            NullableType => new QualifiedIdentifier("cxcore", "System", "Nullable"),
            BoolType or CharType or SByteType or ShortType or IntType or LongType or
            ByteType or UShortType or UIntType or ULongType or FloatType or DoubleType or
            ObjectType or StringType or VoidType =>
                new QualifiedIdentifier("cxcore", type.FullName),
            _ => throw new InternalCompilerException(
                $"Type '{type.FullName}' has no runtime type information."),
        };
        return new QualifiedIdentifier(fullName, "__typeinfo");
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
            .Zip(constructor.ParameterTypes)
            .Select(pair => ToCExpressionAsType(
                pair.First,
                pair.Second,
                functionDeclaration,
                moduleName))
            .ToArray();

        string receiver;
        if (expression.ClassType == ClassType.Class)
        {
            var cType = expression.RequestedType.ToCIdentifier(false);
            var storageType = cType.TrimEnd().TrimEnd('*').TrimEnd();
            receiver = $"{temporaryName} = ({cType})CX_ID_4(cxcore, System, Memory, Alloc)(" +
                $"(cx_uint)sizeof({storageType}))";
        }
        else
        {
            receiver = $"&{temporaryName}";
        }

        var constructorReceiver = expression.RequestedType is NamedType named &&
            RequiresClosedValueLayout(named) && constructor.ClosedContainingType is null
            ? $"({constructor.Declaration!.ParentClassDeclaration!.ToCIdentifier(
                constructor.ModuleName)}*)({receiver})"
            : receiver;
        var constructorArguments = new[] { constructorReceiver }.Concat(arguments);
        var constructorCall = $"{ToCIdentifier(constructor)}(" +
            $"{string.Join(", ", constructorArguments)})";
        if (expression.RequestedType is NamedType { ConstructedIdentity: { } identity } &&
            expression.ClassType == ClassType.Class)
        {
            return $"({constructorCall}, CX_INIT_VTABLE({temporaryName}, " +
                $"{identity.CIdentifier}), {temporaryName})";
        }
        return $"({constructorCall}, {temporaryName})";
    }

    private static string ToCNullLiteral(LiteralExpression literal)
    {
        var inferredType = literal.InferredType is ConstType constType
            ? constType.UnderlyingType
            : literal.InferredType;
        if (inferredType is NamedType { ClassType: ClassType.Interface } interfaceType)
        {
            return $"({interfaceType.ToCIdentifier(false)}){{ CX_NULL, CX_NULL }}";
        }
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
                IsInterfaceExpression(expression.Right))
            {
                return $"(({ToCExpression(expression.Right, functionDeclaration, moduleName)}).instance " +
                    $"{expression.Operator} CX_NULL)";
            }
            if (expression.Right is LiteralExpression { SourceText: "null" } &&
                IsInterfaceExpression(expression.Left))
            {
                return $"(({ToCExpression(expression.Left, functionDeclaration, moduleName)}).instance " +
                    $"{expression.Operator} CX_NULL)";
            }
            if (IsInterfaceExpression(expression.Left) &&
                IsInterfaceExpression(expression.Right))
            {
                return $"(({ToCExpression(expression.Left, functionDeclaration, moduleName)}).instance " +
                    $"{expression.Operator} " +
                    $"({ToCExpression(expression.Right, functionDeclaration, moduleName)}).instance)";
            }
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

        var resultType = expression.InferredType ?? throw new InternalCompilerException(
            "Null-coalescing expression is not bound.");
        var left = ToCExpressionAsType(
            expression.Left,
            resultType,
            functionDeclaration,
            moduleName);
        var right = ToCExpressionAsType(
            expression.Right,
            resultType,
            functionDeclaration,
            moduleName);
        var leftType = expression.Left.InferredType is ConstType constType
            ? constType.UnderlyingType
            : expression.Left.InferredType;
        if (leftType is NullableType nullableType)
        {
            var valueType = nullableType.UnderlyingType.ToCIdentifier(false);
            if (IsCReferenceType(nullableType.UnderlyingType))
            {
                return $"(({left})._obj != CX_NULL ? ({valueType})({left})._obj : {right})";
            }
            return $"(({left})._obj != CX_NULL ? *({valueType}*)({left})._obj : {right})";
        }
        if (leftType is NamedType { ClassType: ClassType.Interface })
        {
            return $"(({left}).instance != CX_NULL ? ({left}) : {right})";
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

    private static bool IsInterfaceExpression(ExpressionBase expression)
    {
        var type = expression.InferredType is ConstType constType
            ? constType.UnderlyingType
            : expression.InferredType;
        return type is NamedType { ClassType: ClassType.Interface };
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
        if (symbol.SpecializationName is { } specializedName)
        {
            return specializedName;
        }
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

    private static IEnumerable<InvocationExpression> EnumerateInterfaceInvocations(
        ExpressionBase expression)
    {
        if (expression is InvocationExpression { ReceiverTemporaryName: not null } invocation)
        {
            yield return invocation;
        }
        foreach (var child in GetExpressionChildren(expression))
        {
            foreach (var nested in EnumerateInterfaceInvocations(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<InvocationExpression> EnumerateDirectInterfaceInvocations(
        StatementBase statement)
    {
        return GetDirectExpressions(statement).SelectMany(EnumerateInterfaceInvocations);
    }

    private static IEnumerable<ExpressionBase> EnumerateInterfacePropertyReceivers(
        ExpressionBase expression)
    {
        if (GetInterfacePropertyTemporaryName(expression) is not null)
        {
            yield return expression;
        }
        foreach (var child in GetExpressionChildren(expression))
        {
            foreach (var nested in EnumerateInterfacePropertyReceivers(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<ExpressionBase> EnumerateDirectInterfacePropertyReceivers(
        StatementBase statement)
    {
        return GetDirectExpressions(statement).SelectMany(EnumerateInterfacePropertyReceivers);
    }

    private static string? GetInterfacePropertyTemporaryName(ExpressionBase expression)
    {
        return expression switch
        {
            MemberAccessExpression memberAccess => memberAccess.InterfaceReceiverTemporaryName,
            ArrayAccessExpression arrayAccess => arrayAccess.InterfaceReceiverTemporaryName,
            AssignmentExpression assignment => assignment.InterfaceReceiverTemporaryName,
            _ => null,
        };
    }

    private static ExpressionBase? GetPropertyReceiver(ExpressionBase expression)
    {
        ExpressionBase propertyExpression = expression switch
        {
            AssignmentExpression { Target: ArrayAccessExpression indexed } => indexed.Target,
            AssignmentExpression assignment => assignment.Target,
            ArrayAccessExpression indexed => indexed.Target,
            _ => expression,
        };
        return propertyExpression is MemberAccessExpression memberAccess
            ? memberAccess.Target
            : null;
    }

    private static IEnumerable<ExpressionBase> GetExpressionChildren(ExpressionBase expression)
    {
        return expression switch
        {
            MemberAccessExpression memberAccess => [memberAccess.Target],
            InvocationExpression invocation => [invocation.Target, .. invocation.Arguments],
            BinaryExpression binary => [binary.Left, binary.Right],
            CastExpression cast => [cast.Operand],
            TypeTestExpression typeTest => [typeTest.Operand],
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
            ThrowStatement { Expression: not null } throwStatement => [throwStatement.Expression],
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
        if (statement is ThrowStatement { Expression: not null } throwStatement)
        {
            return EnumerateStringLiterals(throwStatement.Expression);
        }
        if (statement is TryStatement tryStatement)
        {
            return EnumerateStringLiterals(tryStatement.Body)
                .Concat(tryStatement.CatchClauses.SelectMany(clause =>
                    (clause.Filter is null ? [] : EnumerateStringLiterals(clause.Filter))
                    .Concat(EnumerateStringLiterals(clause.Body))))
                .Concat(tryStatement.FinallyBody is null
                    ? []
                    : EnumerateStringLiterals(tryStatement.FinallyBody));
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
