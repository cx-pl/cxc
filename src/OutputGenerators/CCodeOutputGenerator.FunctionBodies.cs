using System.Security.Cryptography;
using System.Text;
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
    private static void WriteFunctionTypeDeclarations(
        IndentingWriter writer,
        IEnumerable<DeclarationBase> declarations,
        string moduleName)
    {
        var functionTypes = EnumerateFunctionTypesFromDeclarations(declarations)
            .DistinctBy(GetFunctionTypeCIdentifier)
            .ToArray();
        foreach (var functionType in functionTypes)
        {
            var returnType = functionType.ReturnType.ToCReturnType(false);
            var cType = GetFunctionTypeCIdentifier(functionType);
            var nodeType = GetFunctionTypeNodeCIdentifier(functionType);
            var parameters = "void *__environment" +
                (functionType.ParameterTypes.Count == 0
                    ? string.Empty
                    : $", {string.Join(", ", functionType.ParameterTypes.Select(ToCParameterType))}");
            writer.WriteLine($"struct {nodeType};");
            writer.WriteLine($"typedef struct {cType} {{");
            writer.IncreaseIndent();
            writer.WriteLine($"{returnType} (*__function)({parameters});");
            writer.WriteLine("void *__environment;");
            writer.WriteLine($"struct {nodeType} *__invocation_list;");
            writer.DecreaseIndent();
            writer.WriteLine($"}} {cType};");
        }
        foreach (var delegateDeclaration in declarations.OfType<DelegateDeclaration>())
        {
            var delegateName = new QualifiedIdentifier(
                moduleName,
                delegateDeclaration.FullName).ToCIdentifier();
            writer.WriteLine(
                $"typedef {GetFunctionTypeCIdentifier(delegateDeclaration.Signature)} " +
                $"{delegateName};");
        }
        if (functionTypes.Length > 0)
        {
            writer.WriteLine();
        }
    }

    private static IEnumerable<FunctionType> EnumerateFunctionTypesFromDeclarations(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case ClassDeclaration classDeclaration:
                    foreach (var nested in EnumerateFunctionTypesFromDeclarations(
                        classDeclaration.MemberDeclarations.Declarations))
                    {
                        yield return nested;
                    }
                    break;
                case DelegateDeclaration delegateDeclaration:
                    foreach (var functionType in EnumerateFunctionTypes(
                        delegateDeclaration.Signature))
                    {
                        yield return functionType;
                    }
                    break;
                case FieldDeclaration field:
                    foreach (var functionType in EnumerateFunctionTypes(field.Type))
                        yield return functionType;
                    break;
                case PropertyDeclaration property:
                    foreach (var functionType in EnumerateFunctionTypes(property.Type))
                        yield return functionType;
                    break;
                case FunctionDeclaration function:
                    foreach (var functionType in EnumerateFunctionTypes(function.ReturnType))
                        yield return functionType;
                    foreach (var parameter in function.Parameters)
                    {
                        foreach (var functionType in EnumerateFunctionTypes(parameter.ParameterType))
                            yield return functionType;
                    }
                    if (function.Body is { } body)
                    {
                        foreach (var statement in EnumerateStatements(body))
                        {
                            if (statement is LocalVariableDeclarationStatement local)
                            {
                                foreach (var functionType in EnumerateFunctionTypes(local.DeclaredType))
                                    yield return functionType;
                            }
                            foreach (var expression in GetDirectExpressions(statement)
                                .SelectMany(EnumerateAllExpressions))
                            {
                                if (expression.InferredType is { } inferred)
                                {
                                    foreach (var functionType in EnumerateFunctionTypes(inferred))
                                        yield return functionType;
                                }
                            }
                        }
                        foreach (var localFunction in EnumerateLocalFunctions(body))
                        {
                            foreach (var functionType in EnumerateFunctionTypes(localFunction.ReturnType))
                                yield return functionType;
                            foreach (var parameter in localFunction.Parameters)
                            {
                                foreach (var functionType in EnumerateFunctionTypes(parameter.ParameterType))
                                    yield return functionType;
                            }
                            foreach (var localStatement in EnumerateStatements(localFunction.Body ?? []))
                            {
                                if (localStatement is LocalVariableDeclarationStatement local)
                                {
                                    foreach (var functionType in EnumerateFunctionTypes(local.DeclaredType))
                                        yield return functionType;
                                }
                                foreach (var expression in GetDirectExpressions(localStatement)
                                    .SelectMany(EnumerateAllExpressions))
                                {
                                    if (expression.InferredType is { } inferred)
                                    {
                                        foreach (var functionType in EnumerateFunctionTypes(inferred))
                                            yield return functionType;
                                    }
                                }
                            }
                        }
                    }
                    break;
            }
        }
    }

    private static IEnumerable<FunctionType> EnumerateFunctionTypes(TypeBase type)
    {
        switch (type)
        {
            case ConstType constant:
                return EnumerateFunctionTypes(constant.UnderlyingType);
            case ArrayType array:
                return EnumerateFunctionTypes(array.ElementType);
            case NullableType nullable:
                return EnumerateFunctionTypes(nullable.UnderlyingType);
            case FunctionType function:
                return function.ParameterTypes.SelectMany(EnumerateFunctionTypes)
                    .Concat(EnumerateFunctionTypes(function.ReturnType))
                    .Append(function);
            case NamedType { IsDelegate: true, DelegateSignature: { } signature }:
                return EnumerateFunctionTypes(signature);
            case NamedType named:
                return named.TypeArguments.SelectMany(EnumerateFunctionTypes);
            default:
                return [];
        }
    }

    private static IEnumerable<StatementBase> EnumerateStatements(
        IEnumerable<StatementBase> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            IEnumerable<StatementBase> children = statement switch
            {
                BlockStatement block => block.Statements,
                IfStatement conditional => new[] { conditional.ThenStatement }
                    .Concat(conditional.ElseStatement is null
                        ? []
                        : [conditional.ElseStatement]),
                WhileStatement loop => [loop.Body],
                DoWhileStatement loop => [loop.Body],
                ForStatement loop => [loop.Body],
                ForeachStatement loop => [loop.Body],
                SwitchStatement switchStatement => switchStatement.Sections
                    .SelectMany(section => section.Statements),
                TryStatement tryStatement => new[] { tryStatement.Body }
                    .Concat(tryStatement.CatchClauses.Select(clause => clause.Body))
                    .Concat(tryStatement.FinallyBody is null
                        ? []
                        : [tryStatement.FinallyBody]),
                LocalFunctionDeclarationStatement local when local.Function.Body is { } body =>
                    body,
                _ => [],
            };
            foreach (var nested in EnumerateStatements(children))
                yield return nested;
        }
    }

    internal static IEnumerable<ExpressionBase> EnumerateAllExpressions(ExpressionBase expression)
    {
        yield return expression;
        foreach (var child in GetExpressionChildren(expression))
        {
            foreach (var nested in EnumerateAllExpressions(child))
                yield return nested;
        }
    }

    internal static IEnumerable<ExpressionBase> EnumerateBodyExpressions(
        IEnumerable<StatementBase> body) => EnumerateStatements(body)
            .SelectMany(GetDirectExpressions)
            .SelectMany(EnumerateAllExpressions);

    internal static IEnumerable<StatementBase> EnumerateBodyStatements(
        IEnumerable<StatementBase> body) => EnumerateStatements(body);

    private static void WriteFunctionDefinitions(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        bool includeLocalFunctions = true)
    {
        if (includeLocalFunctions)
        {
            var localFunctions = EnumerateLocalFunctions(declarations).ToArray();
            var concreteLocalFunctions = localFunctions.Where(function =>
                function.GenericTypeNames.Length == 0 &&
                !function.IsGenericSpecialization).ToArray();
            WriteClosureEnvironmentDeclarations(writer, declarations, localFunctions);
            foreach (var localFunction in concreteLocalFunctions)
            {
                var parameters = localFunction.Parameters.Count == 0
                    ? "void"
                    : string.Join(", ", localFunction.Parameters.Select(parameter =>
                        $"{ToCParameterType(parameter.ParameterType)} {parameter.Name}"));
                if (localFunction.LambdaOwner is not null)
                {
                    parameters = "void *__cx_environment" +
                        (parameters == "void" ? string.Empty : $", {parameters}");
                }
                writer.WriteLine(
                    $"static {localFunction.ReturnType.ToCReturnType(false)} " +
                    $"{new QualifiedIdentifier(moduleName, localFunction.LocalCName!).ToCIdentifier()}({parameters});");
            }
            WriteFunctionValueAdapters(writer, declarations, moduleName, concreteLocalFunctions);
            if (concreteLocalFunctions.Length > 0)
            {
                writer.WriteLine();
                WriteFunctionDefinitions(
                    writer,
                    concreteLocalFunctions.Cast<DeclarationBase>().ToArray(),
                    moduleName,
                    includeLocalFunctions: false);
            }
        }
        foreach (var declaration in declarations)
        {
            if (declaration is ClassDeclaration classDeclaration)
            {
                WriteFunctionDefinitions(
                    writer,
                    classDeclaration.MemberDeclarations.Declarations,
                    moduleName,
                    includeLocalFunctions: false);
                continue;
            }

            if (declaration is ExtensionDeclaration extensionDeclaration)
            {
                WriteFunctionDefinitions(
                    writer,
                    extensionDeclaration.Members.MemberDeclarations.Declarations,
                    moduleName,
                    includeLocalFunctions: false);
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
            if (functionDeclaration.IsGenericSpecialization)
            {
                continue;
            }
            if (functionDeclaration is not ConstructorDeclaration &&
                functionDeclaration.ParentClassDeclaration is
                    { GenericTypeNames.Length: > 0 } genericOwner &&
                !functionDeclaration.IsStatic &&
                !IsClosedGenericArrayGetter(functionDeclaration, genericOwner,
                    genericOwner.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray()) &&
                !IsClosedGenericArraySetter(functionDeclaration, genericOwner,
                    genericOwner.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray()))
            {
                continue;
            }
            if (functionDeclaration.GenericTypeNames.Length > 0)
            {
                continue;
            }
            if (functionDeclaration.IsExtension &&
                functionDeclaration.ParentClassDeclaration?.GenericTypeNames.Length > 0 &&
                functionDeclaration.ExtensionTargetType is { } openArrayExtensionTarget &&
                UnwrapConst(openArrayExtensionTarget) is ArrayType)
            {
                continue;
            }
            if (IsClosedClassConceptMethodReturnBody(functionDeclaration) ||
                IsClosedClassConceptVoidCallBody(functionDeclaration))
            {
                continue;
            }
            if (functionDeclaration.OperatorToken is not null &&
                functionDeclaration.ParentClassDeclaration?.GenericTypeNames.Length > 0)
            {
                continue;
            }
            if (functionDeclaration is ConstructorDeclaration &&
                functionDeclaration.ParentClassDeclaration is
                {
                    ClassType: ClassType.Struct,
                    GenericTypeNames.Length: > 0,
                } genericStruct &&
                functionDeclaration.Parameters.Any(parameter =>
                    ContainsGenericParameter(
                        parameter.ParameterType, genericStruct.GenericTypeNames)))
            {
                continue;
            }

            if (functionDeclaration.ParentClassDeclaration is
                    { GenericTypeNames.Length: > 0 } genericArrayOwner &&
                (IsClosedGenericArrayGetter(
                    functionDeclaration,
                    genericArrayOwner,
                    genericArrayOwner.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) ||
                 IsClosedGenericArraySetter(
                    functionDeclaration,
                    genericArrayOwner,
                    genericArrayOwner.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray())))
            {
                var fields = genericArrayOwner.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray();
                var isGetter = IsClosedGenericArrayGetter(
                    functionDeclaration, genericArrayOwner, fields);
                var arrayAccess = isGetter
                    ? (ArrayAccessExpression)((ReturnStatement)
                        functionDeclaration.Body![0]).Expression!
                    : (ArrayAccessExpression)((AssignmentExpression)((ExpressionStatement)
                        functionDeclaration.Body![0]).Expression).Target;
                var functionName = functionDeclaration.ToCIdentifier(
                    moduleName, GetNameOverrideIndex(functionDeclaration, declarations));
                WriteSourceLocation(writer, functionDeclaration.SourceSpan);
                writer.Write($"{functionName}(");
                writer.Write(
                    $"{(functionDeclaration.Const ? "const " : string.Empty)}" +
                    $"{genericArrayOwner.ToCIdentifier(
                        genericArrayOwner.ProjectName ?? moduleName)}* __this, cx_uint index" +
                    (isGetter ? string.Empty : ", void* value"));
                writer.WriteLine(") {");
                writer.IncreaseIndent();
                writer.WriteLine($"__cx_register_static_roots_{GetModuleToken(moduleName)}();");
                writer.WriteLine("#if !defined(CX_STATIC_LINK)");
                writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
                writer.WriteLine("#endif");
                var access = ToCGenericArrayElementAccess(
                    arrayAccess, "void*", functionDeclaration, moduleName);
                writer.WriteLine(isGetter ? $"return {access};" : $"{access} = value;");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.WriteLine();
                continue;
            }

            var nameOverrideIndex = GetNameOverrideIndex(functionDeclaration, declarations);
            WriteSourceLocation(writer, functionDeclaration.SourceSpan);
            writer.Write($"{(functionDeclaration.LocalCName is null ? string.Empty : "static ")}" +
                $"{functionDeclaration.ToCIdentifier(moduleName, nameOverrideIndex)}(");

            var parameters = new List<string>();
            if (!functionDeclaration.IsStatic)
            {
                var receiverConst = functionDeclaration.Const ? "const " : string.Empty;
                var parentClass = functionDeclaration.ParentClassDeclaration!;
                parameters.Add(functionDeclaration.ExtensionTargetType is { } extensionTarget &&
                    IsPrimitiveExtensionReceiverType(extensionTarget)
                        ? $"{receiverConst}{ToCParameterType(extensionTarget)} __this"
                    : functionDeclaration.IsExtension && parentClass.ClassType == ClassType.Interface
                        ? $"{receiverConst}struct cx_iface_ref __this"
                        : $"{ToCReceiverType(parentClass, parentClass.ProjectName ?? moduleName, functionDeclaration.Const)} __this");
            }
            parameters.AddRange(functionDeclaration.Parameters.Select(
                parameter => $"{ToCParameterType(parameter.ParameterType)} {parameter.Name}"));
            if (functionDeclaration.LambdaOwner is not null)
            {
                parameters.Insert(0, "void *__cx_environment");
            }

            writer.Write(string.Join(", ", parameters));
            writer.WriteLine(") {");
            writer.IncreaseIndent();
            writer.WriteLine($"__cx_register_static_roots_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#if !defined(CX_STATIC_LINK)");
            writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#endif");
            var context = new StatementWriteContext(functionDeclaration);
            context.WriteDeclarations(writer);

            if (functionDeclaration.LambdaOwner is { } lambdaOwner)
            {
                if (lambdaOwner.Captures.Count == 0)
                {
                    writer.WriteLine("(void)__cx_environment;");
                }
                else
                {
                    writer.WriteLine($"struct {GetLambdaEnvironmentCIdentifier(lambdaOwner)} *__cx_closure = " +
                        $"(struct {GetLambdaEnvironmentCIdentifier(lambdaOwner)}*)__cx_environment;");
                }
            }
            foreach (var parameter in functionDeclaration.Parameters.Where(parameter =>
                parameter.LocalSymbol?.IsCaptured == true))
            {
                var symbol = parameter.LocalSymbol!;
                var cType = symbol.Type.ToCIdentifier(false);
                writer.WriteLine(
                    $"{cType} *{symbol.CellName} = ({cType}*)CX_ID_4(cxcore, System, Memory, Alloc)(" +
                    $"(cx_uint)sizeof({cType}));");
                writer.WriteLine($"*{symbol.CellName} = {parameter.Name};");
            }

            if (functionDeclaration is ConstructorDeclaration { IsStatic: false } constructor)
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
                        if (field.Initializer is LiteralExpression { SourceText: "default" } &&
                            ContainsGenericParameter(
                                field.Type,
                                constructor.ParentClassDeclaration.GenericTypeNames))
                        {
                            continue;
                        }

                        writer.WriteLine(
                            $"__this->{field.Name} = " +
                            $"{ToCExpressionAsType(
                                field.Initializer!,
                                field.Type,
                                functionDeclaration,
                                moduleName)};");
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

    private static IEnumerable<FunctionDeclaration> EnumerateLocalFunctions(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var function in EnumerateFunctions(declarations))
        {
            if (function.Body is null)
                continue;
            foreach (var local in EnumerateLocalFunctions(function.Body))
                yield return local;
        }
    }

    private static IEnumerable<FunctionDeclaration> EnumerateLocalFunctions(
        IEnumerable<StatementBase> statements)
    {
        foreach (var statement in statements)
        {
            foreach (var expression in GetDirectExpressions(statement))
            {
                foreach (var lambda in EnumerateOperatorExpressions<LambdaExpression>(expression))
                {
                    if (lambda.GeneratedFunction is not { } generated)
                        continue;
                    yield return generated;
                    if (generated.Body is { } generatedBody)
                    {
                        foreach (var nested in EnumerateLocalFunctions(generatedBody))
                            yield return nested;
                    }
                }
            }
            switch (statement)
            {
                case LocalFunctionDeclarationStatement local:
                    yield return local.Function;
                    if (local.Function.Body is { } localBody)
                    {
                        foreach (var nested in EnumerateLocalFunctions(localBody))
                            yield return nested;
                    }
                    break;
                case BlockStatement block:
                    foreach (var nested in EnumerateLocalFunctions(block.Statements))
                        yield return nested;
                    break;
                case IfStatement conditional:
                    foreach (var nested in EnumerateLocalFunctions([conditional.ThenStatement]))
                        yield return nested;
                    if (conditional.ElseStatement is { } alternate)
                    {
                        foreach (var nested in EnumerateLocalFunctions([alternate]))
                            yield return nested;
                    }
                    break;
                case WhileStatement loop:
                    foreach (var nested in EnumerateLocalFunctions([loop.Body]))
                        yield return nested;
                    break;
                case DoWhileStatement loop:
                    foreach (var nested in EnumerateLocalFunctions([loop.Body]))
                        yield return nested;
                    break;
                case ForStatement loop:
                    foreach (var nested in EnumerateLocalFunctions([loop.Body]))
                        yield return nested;
                    break;
                case ForeachStatement loop:
                    foreach (var nested in EnumerateLocalFunctions([loop.Body]))
                        yield return nested;
                    break;
                case SwitchStatement switchStatement:
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var nested in EnumerateLocalFunctions(section.Statements))
                            yield return nested;
                    }
                    break;
                case TryStatement tryStatement:
                    foreach (var nested in EnumerateLocalFunctions([tryStatement.Body]))
                        yield return nested;
                    foreach (var clause in tryStatement.CatchClauses)
                    {
                        foreach (var nested in EnumerateLocalFunctions([clause.Body]))
                            yield return nested;
                    }
                    if (tryStatement.FinallyBody is { } finallyBody)
                    {
                        foreach (var nested in EnumerateLocalFunctions([finallyBody]))
                            yield return nested;
                    }
                    break;
            }
        }
    }

    private static void WriteGenericFunctionDefinitions(
        IndentingWriter writer,
        IEnumerable<FunctionSymbol> instances,
        string moduleName)
    {
        foreach (var instance in instances
            .Where(item => item.ImplementationDeclaration?.Body is not null ||
                item.Declaration?.Body is not null)
            .OrderBy(item => item.SpecializationName, StringComparer.Ordinal))
        {
            var declaration = instance.Declaration!;
            if (GetGenericConstrainedReceiver(declaration) is { } genericReceiver)
            {
                var parameterIndex = declaration.Parameters
                    .Select((parameter, index) => (parameter, index))
                    .Where(item => item.parameter.Name ==
                        genericReceiver.Identifier.ToString())
                    .Select(item => item.index)
                    .SingleOrDefault(-1);
                if (parameterIndex >= 0 && parameterIndex < instance.ParameterTypes.Count)
                {
                    genericReceiver.SetClosedGenericType(
                        instance.ParameterTypes[parameterIndex]);
                }
            }
            var parameters = GetGenericFunctionParameters(instance);
            var implementationDeclaration = instance.ImplementationDeclaration;
            if (implementationDeclaration is not null)
            {
                parameters = GetGenericFunctionParameters(instance, implementationDeclaration);
            }
            writer.WriteLine($"{instance.ReturnType.ToCIdentifier(false)} " +
                $"{instance.SpecializationName}({string.Join(", ", parameters)})");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine($"__cx_register_static_roots_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#if !defined(CX_STATIC_LINK)");
            writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#endif");
            if (!declaration.IsStatic && declaration is not ConstructorDeclaration)
            {
                writer.WriteLine("(void)__this;");
            }
            if ((implementationDeclaration ?? declaration).LambdaOwner is
                { Captures.Count: 0 })
            {
                writer.WriteLine("(void)__cx_environment;");
            }
            if (instance.ClosedContainingType is not null &&
                declaration.IsExtension &&
                declaration.ExtensionTargetType is { } closedArrayExtensionTarget &&
                UnwrapConst(closedArrayExtensionTarget) is ArrayType &&
                (implementationDeclaration ?? declaration).Body is
                    [ReturnStatement { Expression: ArrayAccessExpression genericArrayAccess }])
            {
                writer.WriteLine(
                    $"return {ToCGenericArrayReturnAccess(
                        genericArrayAccess, instance.GenericTypeArguments,
                        implementationDeclaration ?? declaration, moduleName)};");
            }
            else if (implementationDeclaration is { Body: { } specializedBody })
            {
                var restoreClosedOwner = implementationDeclaration.ParentClassDeclaration is
                    { GenericTypeNames.Length: > 0 } implementationOwner &&
                    HasClosedGenericOwnerArguments(instance, implementationOwner)
                    ? ApplyClosedGenericOwnerTypes(
                        instance, implementationDeclaration, implementationOwner)
                    : null;
                try
                {
                    var context = new StatementWriteContext(implementationDeclaration);
                    context.WriteDeclarations(writer);
                    foreach (var statement in specializedBody)
                    {
                        WriteStatement(
                            writer, statement, implementationDeclaration, moduleName, context);
                    }
                    context.WriteEpilogue(writer);
                }
                finally
                {
                    restoreClosedOwner?.Invoke();
                }
            }
            else if (instance.ClosedContainingType is not null &&
                (IsClosedClassConceptMethodReturnBody(declaration) ||
                 IsClosedClassConceptVoidCallBody(declaration)))
            {
                if (IsClosedClassConceptMethodReturnBody(declaration))
                {
                    var expression = ((ReturnStatement)declaration.Body![0]).Expression!;
                    writer.WriteLine(
                        $"return {ToCGenericConceptPropertyExpression(
                            expression, instance, declaration, moduleName)};");
                }
                else
                {
                    foreach (var statement in declaration.Body!)
                    {
                        WriteClosedClassConceptVoidStatement(
                            writer, statement, instance, declaration, moduleName);
                    }
                }
            }
            else if (declaration is not ConstructorDeclaration &&
                declaration.ParentClassDeclaration is
                    { GenericTypeNames.Length: > 0 } closedOwner &&
                HasClosedGenericOwnerArguments(instance, closedOwner) &&
                IsClosedGenericOwnerMethod(declaration, closedOwner))
            {
                var restore = ApplyClosedGenericOwnerTypes(
                    instance, declaration, closedOwner);
                try
                {
                    var context = new StatementWriteContext(declaration);
                    context.WriteDeclarations(writer);
                    foreach (var statement in declaration.Body ?? [])
                    {
                        WriteStatement(writer, statement, declaration, moduleName, context);
                    }
                    context.WriteEpilogue(writer);
                }
                finally
                {
                    restore();
                }
            }
            else if (declaration is ConstructorDeclaration closedConstructor)
            {
                if (closedConstructor.Initializer is { } initializer)
                {
                    var target = initializer.Target ?? throw new InternalCompilerException(
                        "Closed constructor initializer is not bound.");
                    var receiver = initializer.Kind == ConstructorInitializerKind.Base
                        ? "&__this->__base"
                        : "__this";
                    var arguments = new[] { receiver }.Concat(
                        initializer.Arguments.Zip(target.ParameterTypes).Select(pair =>
                            ToCExpressionAsType(
                                pair.First, pair.Second, declaration, moduleName)));
                    writer.WriteLine(
                        $"{ToCIdentifier(target)}({string.Join(", ", arguments)});");
                }
                foreach (var field in declaration.ParentClassDeclaration!
                    .MemberDeclarations.Declarations.OfType<FieldDeclaration>()
                    .Where(field => field.Initializer is not null))
                {
                    if (field.Initializer is LiteralExpression { SourceText: "default" } &&
                        ContainsGenericParameter(
                            field.Type, declaration.ParentClassDeclaration.GenericTypeNames))
                    {
                        continue;
                    }

                    writer.WriteLine(
                        $"__this->{field.Name} = " +
                        $"{ToCExpressionAsType(field.Initializer!, field.Type, declaration, moduleName)};");
                }
                foreach (var statement in closedConstructor.Body ?? [])
                {
                    if (statement is ExpressionStatement
                        {
                            Expression: AssignmentExpression
                            {
                                Operator: "=",
                                Target: IdentifierExpression field,
                                Value: { } value,
                            },
                        })
                    {
                        var closedField = declaration.ParentClassDeclaration!
                            .MemberDeclarations.Declarations.OfType<FieldDeclaration>()
                            .Single(candidate => candidate.Name ==
                                field.Identifier.Parts[^1]);
                        if (instance.ClosedContainingType is not null &&
                            IsClosedGenericArrayAllocation(
                                closedField, value, closedConstructor,
                                declaration.ParentClassDeclaration!))
                        {
                            var typeArguments = declaration.ParentClassDeclaration!
                                .GenericTypeNames
                                .Zip(instance.ClosedContainingType.TypeArguments)
                                .ToDictionary(pair => pair.First, pair => pair.Second);
                            var elementType = GenericTypeSubstitution.Substitute(
                                ((ArrayCreationExpression)value).ElementType,
                                typeArguments).ToCIdentifier(false);
                            writer.WriteLine(
                                $"__this->{closedField.Name} = cx_array_new(" +
                                $"(cx_uint)({ToCExpression(((ArrayCreationExpression)value).Length, declaration, moduleName)}), " +
                                $"(cx_uint)sizeof({elementType}));");
                            continue;
                        }
                        if (value is ObjectCreationExpression objectCreation &&
                            instance.SpecializationName is { } callerSpecialization &&
                            objectCreation.TryGetClosedConstruction(
                                callerSpecialization, out var closedRequestedType,
                                out var closedObjectConstructor))
                        {
                            var temporaryName = objectCreation.TemporaryName ??
                                throw new InternalCompilerException(
                                    "Closed object creation temporary is not bound.");
                            writer.WriteLine(
                                $"{closedRequestedType.ToCIdentifier(false)} {temporaryName};");
                            var closedCreation = new ObjectCreationExpression(
                                closedRequestedType, objectCreation.Arguments);
                            closedCreation.BindConstructor(
                                closedObjectConstructor,
                                objectCreation.ClassType ?? ClassType.Class,
                                temporaryName);
                            writer.WriteLine(
                                $"__this->{closedField.Name} = " +
                                $"({closedField.Type.ToCIdentifier(false)})" +
                                $"{ToCExpression(closedCreation, declaration, moduleName)};");
                            continue;
                        }
                        writer.WriteLine(
                            $"__this->{closedField.Name} = " +
                            $"{ToCExpression(value, declaration, moduleName)};");
                    }
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericDefaultReturnBody(declaration))
            {
                writer.WriteLine($"return {ToCDefaultValue(instance.ReturnType)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericForwardCallReturnBody(declaration))
            {
                var invocation = (InvocationExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                var target = invocation.GetClosedGenericTarget(
                    instance.SpecializationName!) ?? throw new InternalCompilerException(
                    $"Missing closed forwarding target for '{instance.SpecializationName}'.");
                if (target.SpecializationName is null)
                {
                    throw new InternalCompilerException(
                        "Generic forwarding requires a closed target function.");
                }
                var receiver = string.Empty;
                if (target.Declaration is { IsStatic: false })
                {
                    if (invocation.Receiver is not null and not ThisExpression &&
                        (invocation.Receiver.InferredType is not { } receiverType ||
                         target.Declaration.ParentClassDeclaration?.ClassType switch
                         {
                             ClassType.Class => receiverType is not NamedType
                                 { ClassType: ClassType.Class },
                             ClassType.Struct => receiverType is not NamedType
                                 { ClassType: ClassType.Struct },
                             _ => true,
                         }))
                    {
                        throw new InternalCompilerException(
                            "Instance generic forwarding receiver does not match the supported owner type.");
                    }
                    if (invocation.Receiver is null)
                    {
                        receiver = "__this";
                    }
                    else
                    {
                        var receiverExpression = ToCExpression(
                            invocation.Receiver, declaration, moduleName);
                        if (target.Declaration.ParentClassDeclaration?.ClassType ==
                                ClassType.Struct &&
                            !IsCStorageLocation(invocation.Receiver) &&
                            invocation.Receiver is not ThisExpression)
                        {
                            var computedReceiverType = invocation.Receiver.InferredType ??
                                throw new InternalCompilerException(
                                    "Generic struct receiver has no inferred type.");
                            const string receiverTemporary = "__cx_forward_receiver";
                            writer.WriteLine(
                                $"{computedReceiverType.ToCIdentifier(false)} {receiverTemporary} = " +
                                $"{receiverExpression};");
                            receiver = $"&{receiverTemporary}";
                        }
                        else
                        {
                            receiver = ToCBaseReceiver(
                                receiverExpression,
                                IsCPointerReceiver(invocation.Receiver),
                                invocation.ReceiverBaseDepth);
                        }
                    }
                    receiver += ", ";
                }
                var arguments = invocation.Arguments.Zip(target.ParameterTypes)
                    .Select(pair => ToCExpressionAsType(
                        pair.First, pair.Second, declaration, moduleName));
                writer.WriteLine(
                    $"return {target.SpecializationName}({receiver}{string.Join(", ", arguments)});");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericDefaultLocalReturnBody(declaration))
            {
                var local = (LocalVariableDeclarationStatement)declaration.Body![0];
                var declarator = local.Declarators.Single();
                writer.WriteLine(
                    $"{instance.ReturnType.ToCIdentifier(false)} {declarator.Name} = " +
                    $"{ToCDefaultValue(instance.ReturnType)};");
                writer.WriteLine($"return {declarator.Name};");
            }
            else if (declaration.IsExtension && declaration.Body is
                [ReturnStatement { Expression: { } returnExpression }])
            {
                writer.WriteLine(
                    $"return {ToCExpression(returnExpression, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptConditionalReturnBody(declaration))
            {
                var conditional = (ConditionalExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                var comparison = (BinaryExpression)conditional.Condition;
                var left = (IdentifierExpression)comparison.Left;
                var right = (IdentifierExpression)comparison.Right;
                if (!instance.ConceptOperatorBindings.TryGetValue(
                    comparison.Operator, out var operatorSymbol))
                {
                    throw new InternalCompilerException(
                        $"Missing concept operator binding for '{comparison.Operator}'.");
                }
                var receiverType = instance.ParameterTypes[0];
                var receiver = receiverType is NamedType { ClassType: ClassType.Struct }
                    ? $"&{left.Identifier.Parts[^1]}"
                    : left.Identifier.Parts[^1];
                var argument = ToCExpressionAsType(
                    right,
                    operatorSymbol.ParameterTypes.Single(),
                    declaration,
                    moduleName);
                writer.WriteLine(
                    $"return ({ToCIdentifier(operatorSymbol)}({receiver}, {argument}) ? " +
                    $"{left.Identifier.Parts[^1]} : {right.Identifier.Parts[^1]});");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptOperatorReturnBody(declaration))
            {
                var comparison = (BinaryExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                var left = (IdentifierExpression)comparison.Left;
                var right = (IdentifierExpression)comparison.Right;
                if (!instance.ConceptOperatorBindings.TryGetValue(
                    comparison.Operator, out var operatorSymbol))
                {
                    throw new InternalCompilerException(
                        $"Missing concept operator binding for '{comparison.Operator}'.");
                }
                var receiverType = instance.ParameterTypes[0];
                var receiver = receiverType is NamedType { ClassType: ClassType.Struct }
                    ? $"&{left.Identifier.Parts[^1]}"
                    : left.Identifier.Parts[^1];
                var argument = ToCExpressionAsType(
                    right,
                    operatorSymbol.ParameterTypes.Single(),
                    declaration,
                    moduleName);
                writer.WriteLine(
                    $"return {ToCIdentifier(operatorSymbol)}({receiver}, {argument});");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptMethodReturnBody(declaration))
            {
                var invocation = (InvocationExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCGenericConceptMethodInvocation(
                        invocation, instance, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptMethodCallBody(declaration))
            {
                foreach (var statement in declaration.Body!)
                {
                    var invocation = (InvocationExpression)((ExpressionStatement)statement)
                        .Expression;
                    var member = (MemberAccessExpression)invocation.Target;
                    var receiverExpression = (IdentifierExpression)member.Target;
                    var requirement = invocation.TargetSymbol?.Declaration ??
                        throw new InternalCompilerException(
                            $"Missing concept method declaration for '{member.MemberName}'.");
                    var bindingKey = GetConceptMethodBindingKey(
                        invocation, requirement, instance);
                    if (!instance.ConceptOperatorBindings.TryGetValue(
                        bindingKey, out var methodSymbol))
                    {
                        throw new InternalCompilerException(
                            $"Missing concept method binding for '{member.MemberName}'.");
                    }
                    var receiver = instance.ParameterTypes[0] is
                        NamedType { ClassType: ClassType.Struct }
                        ? $"&{receiverExpression.Identifier.Parts[0]}"
                        : receiverExpression.Identifier.Parts[0];
                    var arguments = invocation.Arguments.Zip(methodSymbol.ParameterTypes)
                        .Select(pair => ToCExpressionAsType(
                            pair.First,
                            pair.Second,
                            declaration,
                            moduleName));
                    var argumentList = string.Join(", ", arguments);
                    var argumentSuffix = argumentList.Length == 0
                        ? string.Empty
                        : $", {argumentList}";
                    writer.WriteLine(
                        $"{ToCIdentifier(methodSymbol)}({receiver}{argumentSuffix});");
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptPropertyReturnBody(declaration))
            {
                var expression = ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCGenericConceptPropertyExpression(
                        expression, instance, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptPropertyAssignmentBody(declaration))
            {
                var compoundAssignmentIndex = 0;
                foreach (var statement in declaration.Body!)
                {
                    var assignment = (AssignmentExpression)((ExpressionStatement)
                        statement).Expression;
                    var member = (MemberAccessExpression)assignment.Target;
                    var receiverExpression = (IdentifierExpression)member.Target;
                    var bindingKey = $"property:{member.MemberName}:set";
                    if (!instance.ConceptOperatorBindings.TryGetValue(
                        bindingKey, out var setterSymbol))
                    {
                        throw new InternalCompilerException(
                            $"Missing concept property setter binding for '{member.MemberName}'.");
                    }
                    var receiver = instance.ParameterTypes[0] is
                        NamedType { ClassType: ClassType.Struct }
                        ? $"&{receiverExpression.Identifier.Parts[0]}"
                        : receiverExpression.Identifier.Parts[0];
                    if (assignment.Operator != "=")
                    {
                        var getterBindingKey = $"property:{member.MemberName}";
                        if (!instance.ConceptOperatorBindings.TryGetValue(
                            getterBindingKey, out var getterSymbol))
                        {
                            throw new InternalCompilerException(
                                $"Missing concept property getter binding for '{member.MemberName}'.");
                        }

                        var temporaryName =
                            $"__cx_concept_property_value_{compoundAssignmentIndex++}";
                        var propertyType = setterSymbol.ParameterTypes.Single();
                        var getterCall =
                            $"{ToCIdentifier(getterSymbol)}({receiver})";
                        var operation = assignment.Operator[..^1];
                        var compoundValue = ContainsGenericConceptPropertyAccess(
                                assignment.Value, receiverExpression.Identifier.Parts[0])
                            ? ToCGenericConceptPropertyExpression(
                                assignment.Value, instance, declaration, moduleName)
                            : ToCExpressionAsType(
                                assignment.Value,
                                propertyType,
                                declaration,
                                moduleName);
                        writer.WriteLine(
                            $"{propertyType.ToCIdentifier(false)} {temporaryName} = {getterCall};");
                        writer.WriteLine(
                            $"{temporaryName} = {temporaryName} {operation} {compoundValue};");
                        writer.WriteLine(
                            $"{ToCIdentifier(setterSymbol)}({receiver}, {temporaryName});");
                        continue;
                    }

                    var value = ContainsGenericConceptPropertyAccess(
                            assignment.Value, receiverExpression.Identifier.Parts[0])
                        ? ToCGenericConceptPropertyExpression(
                            assignment.Value, instance, declaration, moduleName)
                        : ToCExpressionAsType(
                            assignment.Value,
                            setterSymbol.ParameterTypes.Single(),
                            declaration,
                            moduleName);
                    writer.WriteLine(
                        $"{ToCIdentifier(setterSymbol)}({receiver}, {value});");
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptMixedMutationBody(declaration))
            {
                foreach (var statement in declaration.Body!)
                {
                    WriteGenericConceptMutationStatement(
                        writer, statement, instance, declaration, moduleName);
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConstrainedMemberReturnBody(declaration))
            {
                var constrainedReturnExpression =
                    ((ReturnStatement)declaration.Body![0]).Expression!;
                foreach (var memberExpression in EnumerateAllExpressions(
                    constrainedReturnExpression))
                {
                    var receiver = memberExpression switch
                    {
                        InvocationExpression
                        {
                            Target: MemberAccessExpression
                            {
                                Target: IdentifierExpression identifier,
                            },
                        } => identifier,
                        MemberAccessExpression
                        {
                            Target: IdentifierExpression identifier,
                            TargetProperty: not null,
                        } => identifier,
                        ArrayAccessExpression
                        {
                            Target: MemberAccessExpression
                            {
                                Target: IdentifierExpression identifier,
                            },
                            TargetProperty: not null,
                        } => identifier,
                        _ => null,
                    };
                    if (receiver is null)
                    {
                        continue;
                    }
                    var parameterIndex = declaration.Parameters
                        .Select((parameter, index) => (parameter, index))
                        .Where(item => item.parameter.Name ==
                            receiver.Identifier.ToString())
                        .Select(item => item.index)
                        .SingleOrDefault(-1);
                    if (parameterIndex >= 0 &&
                        parameterIndex < instance.ParameterTypes.Count)
                    {
                        receiver.SetClosedGenericType(instance.ParameterTypes[parameterIndex]);
                    }
                }
                foreach (var temporaryName in EnumerateAllExpressions(
                    constrainedReturnExpression).Select(expression => expression switch
                    {
                        InvocationExpression invocation => invocation.ReceiverTemporaryName,
                        MemberAccessExpression member => member.InterfaceReceiverTemporaryName,
                        ArrayAccessExpression access => access.InterfaceReceiverTemporaryName,
                        _ => null,
                    }).OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    writer.WriteLine($"struct cx_iface_ref {temporaryName};");
                }
                writer.WriteLine(
                    $"return {ToCExpression(constrainedReturnExpression, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConstrainedMemberLocalReturnBody(declaration))
            {
                var local = (LocalVariableDeclarationStatement)declaration.Body![0];
                var declarator = local.Declarators.Single();
                var initializer = declarator.Initializer!;
                foreach (var memberExpression in EnumerateAllExpressions(initializer))
                {
                    var receiverExpression = memberExpression switch
                    {
                        InvocationExpression
                        {
                            Target: MemberAccessExpression
                            {
                                Target: IdentifierExpression identifier,
                            },
                        } => identifier,
                        MemberAccessExpression
                        {
                            Target: IdentifierExpression identifier,
                            TargetProperty: not null,
                        } => identifier,
                        ArrayAccessExpression
                        {
                            Target: MemberAccessExpression
                            {
                                Target: IdentifierExpression identifier,
                            },
                            TargetProperty: not null,
                        } => identifier,
                        _ => null,
                    };
                    if (receiverExpression is null)
                    {
                        continue;
                    }
                    var parameterIndex = Array.FindIndex(
                        declaration.Parameters.ToArray(),
                        parameter => parameter.Name ==
                            receiverExpression.Identifier.ToString());
                    if (parameterIndex >= 0 && parameterIndex < instance.ParameterTypes.Count)
                    {
                        receiverExpression.SetClosedGenericType(
                            instance.ParameterTypes[parameterIndex]);
                    }
                }

                foreach (var temporaryName in EnumerateAllExpressions(initializer)
                    .Select(expression => expression switch
                    {
                        InvocationExpression invocation => invocation.ReceiverTemporaryName,
                        MemberAccessExpression member => member.InterfaceReceiverTemporaryName,
                        ArrayAccessExpression access => access.InterfaceReceiverTemporaryName,
                        _ => null,
                    })
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal))
                {
                    writer.WriteLine($"struct cx_iface_ref {temporaryName};");
                }

                writer.WriteLine(
                    $"{local.DeclaredType.ToCIdentifier(false)} {declarator.Name} = " +
                    $"{ToCExpression(initializer, declaration, moduleName)};");
                var returned = (IdentifierExpression)
                    ((ReturnStatement)declaration.Body[1]).Expression!;
                writer.WriteLine($"return {returned.Identifier.Parts[0]};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConstrainedFieldReturnBody(declaration))
            {
                var constrainedFieldExpression =
                    ((ReturnStatement)declaration.Body![0]).Expression!;
                var receiverTemporaryName = constrainedFieldExpression switch
                {
                    MemberAccessExpression
                    {
                        InterfaceDispatchSlotIndex: not null,
                        InterfaceReceiverTemporaryName: { } temporaryName,
                    } => temporaryName,
                    ArrayAccessExpression
                    {
                        InterfaceDispatchSlotIndex: not null,
                        InterfaceReceiverTemporaryName: { } temporaryName,
                    } => temporaryName,
                    _ => null,
                };
                if (receiverTemporaryName is not null)
                {
                    writer.WriteLine($"struct cx_iface_ref {receiverTemporaryName};");
                }
                writer.WriteLine(
                    $"return {ToCExpression(constrainedFieldExpression, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConstrainedPropertyAssignmentBody(declaration))
            {
                foreach (var statement in declaration.Body!)
                {
                    var assignment = (AssignmentExpression)((ExpressionStatement)
                        statement).Expression;
                    var assignmentReceiver = assignment.Target switch
                    {
                        MemberAccessExpression memberAccess => memberAccess.Target,
                        ArrayAccessExpression
                        {
                            Target: MemberAccessExpression memberAccess,
                        } => memberAccess.Target,
                        _ => null,
                    };
                    if (assignmentReceiver is IdentifierExpression identifier)
                    {
                        var parameterIndex = declaration.Parameters
                            .Select((parameter, index) => (parameter, index))
                            .Where(item => item.parameter.Name ==
                                identifier.Identifier.ToString())
                            .Select(item => item.index)
                            .SingleOrDefault(-1);
                        if (parameterIndex >= 0 && parameterIndex < instance.ParameterTypes.Count)
                        {
                            assignmentReceiver.SetClosedGenericType(
                                instance.ParameterTypes[parameterIndex]);
                        }
                    }
                    if (assignment.TargetProperty is { } property &&
                        assignment.TemporaryName is { } temporaryName)
                    {
                        writer.WriteLine(
                            $"{property.Type.ToCIdentifier(false)} {temporaryName};");
                    }
                    if (assignment.PropertyReceiverTemporaryName is { } propertyReceiverTemporary)
                    {
                        var receiverType = assignmentReceiver?.ClosedGenericType ??
                            assignmentReceiver?.InferredType ?? throw new InternalCompilerException(
                                "Generic property assignment receiver type is not bound.");
                        var unwrappedReceiverType = UnwrapConst(receiverType);
                        var isStructReceiver = unwrappedReceiverType is
                            NamedType { ClassType: ClassType.Struct };
                        var receiverPointer = isStructReceiver &&
                            (assignmentReceiver is ThisExpression ||
                                IsCStorageLocation(assignmentReceiver!))
                            ? "*"
                            : string.Empty;
                        writer.WriteLine(
                            $"{unwrappedReceiverType.ToCIdentifier(false)}{receiverPointer} " +
                            $"{propertyReceiverTemporary};");
                    }
                    if (assignment.PropertyIndexTemporaryNames.Count > 0)
                    {
                        var setter = assignment.PropertySetter ?? throw new InternalCompilerException(
                            "Generic property index temporaries have no setter.");
                        for (var index = 0; index < assignment.PropertyIndexTemporaryNames.Count; index++)
                        {
                            var indexType = GenericTypeSubstitution.Substitute(
                                setter.ParameterTypes[index],
                                instance.GenericTypeArguments);
                            writer.WriteLine(
                                $"{indexType.ToCIdentifier(false)} " +
                                $"{assignment.PropertyIndexTemporaryNames[index]};");
                        }
                    }
                    foreach (var receiverTemporaryName in EnumerateAllExpressions(assignment)
                        .SelectMany(expression => expression switch
                        {
                            InvocationExpression invocation =>
                                new string?[] { invocation.ReceiverTemporaryName },
                            MemberAccessExpression member =>
                                new string?[] { member.InterfaceReceiverTemporaryName },
                            ArrayAccessExpression access =>
                                new string?[] { access.InterfaceReceiverTemporaryName },
                            AssignmentExpression propertyAssignment =>
                                new string?[]
                                {
                                    propertyAssignment.InterfaceReceiverTemporaryName,
                                    propertyAssignment.PropertyGetterInterfaceReceiverTemporaryName,
                                },
                            _ => Array.Empty<string?>(),
                        })
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal))
                    {
                        writer.WriteLine($"struct cx_iface_ref {receiverTemporaryName};");
                    }
                    writer.WriteLine(
                        $"{ToCExpression(assignment, declaration, moduleName)};");
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConstrainedVoidMethodCallBody(declaration))
            {
                foreach (var statement in declaration.Body!)
                {
                    WriteGenericConstrainedVoidMethodCallStatement(
                        writer, statement, instance, declaration, moduleName);
                }
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.Name == "get" &&
                declaration.ParentClassDeclaration is { } arrayLengthParent &&
                IsClosedGenericArrayLengthMethod(
                    declaration,
                    arrayLengthParent,
                    arrayLengthParent.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()))
            {
                var expression = ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCExpression(expression, declaration, moduleName)};");
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.ParentClassDeclaration is { } arrayGetterParent &&
                IsClosedGenericArrayGetter(
                    declaration,
                    arrayGetterParent,
                    arrayGetterParent.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()))
            {
                var arrayAccess = (ArrayAccessExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                var typeArguments = arrayGetterParent.GenericTypeNames
                    .Zip(instance.ClosedContainingType.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                writer.WriteLine(
                    $"return {ToCGenericArrayReturnAccess(
                        arrayAccess, typeArguments, declaration, moduleName)};");
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.ParentClassDeclaration is { } arraySetterParent &&
                IsClosedGenericArraySetter(
                    declaration,
                    arraySetterParent,
                    arraySetterParent.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()))
            {
                var assignment = (AssignmentExpression)((ExpressionStatement)
                    declaration.Body![0]).Expression;
                var arrayAccess = (ArrayAccessExpression)assignment.Target;
                var typeArguments = arraySetterParent.GenericTypeNames
                    .Zip(instance.ClosedContainingType.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                var elementType = GenericTypeSubstitution.Substitute(
                    arrayAccess.InferredType!, typeArguments);
                writer.WriteLine(
                    $"{ToCGenericArrayReturnAccess(
                        arrayAccess, typeArguments, declaration, moduleName)} = " +
                    $"{ToCExpressionAsType(
                        assignment.Value, elementType, declaration, moduleName)};");
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
            else if (instance.ClosedContainingType is { } closedContainingType &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.ParentClassDeclaration is { } parentClass &&
                GetClosedGenericDefaultFieldSetter(
                    declaration,
                    parentClass,
                    parentClass.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } defaultField)
            {
                var typeArguments = parentClass.GenericTypeNames
                    .Zip(closedContainingType.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                var fieldType = GenericTypeSubstitution.Substitute(
                    defaultField.Type, typeArguments);
                writer.WriteLine(
                    $"__this->{defaultField.Name} = {ToCDefaultValue(fieldType)};");
            }
            else if (instance.ClosedContainingType is { } conditionalClosedType &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.ParentClassDeclaration is { } conditionalParentClass &&
                GetClosedGenericConditionalDefaultFieldReset(
                    declaration,
                    conditionalParentClass,
                    conditionalParentClass.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } conditionalReset)
            {
                var typeArguments = conditionalParentClass.GenericTypeNames
                    .Zip(conditionalClosedType.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    conditionalReset.Condition,
                    parameterNames,
                    declaration,
                    conditionalParentClass.ToCIdentifier(moduleName),
                    moduleName,
                    "__this");
                var trueType = GenericTypeSubstitution.Substitute(
                    conditionalReset.WhenTrue.Type, typeArguments);
                var falseType = GenericTypeSubstitution.Substitute(
                    conditionalReset.WhenFalse.Type, typeArguments);
                writer.WriteLine($"if ({condition}) {{");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"__this->{conditionalReset.WhenTrue.Name} = {ToCDefaultValue(trueType)};");
                writer.DecreaseIndent();
                writer.WriteLine("} else {");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"__this->{conditionalReset.WhenFalse.Name} = {ToCDefaultValue(falseType)};");
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.ParentClassDeclaration is { } conditionalParameterParent &&
                GetClosedGenericConditionalFieldParameterAssignments(
                    declaration,
                    conditionalParameterParent,
                    conditionalParameterParent.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } conditionalAssignments)
            {
                var typeArguments = conditionalParameterParent.GenericTypeNames
                    .Zip(instance.ClosedContainingType!.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    conditionalAssignments.Condition,
                    parameterNames,
                    declaration,
                    conditionalParameterParent.ToCIdentifier(moduleName),
                    moduleName,
                    "__this");
                writer.WriteLine($"if ({condition}) {{");
                writer.IncreaseIndent();
                foreach (var assignment in conditionalAssignments.WhenTrue)
                {
                    var value = assignment.ParameterIndex is { } parameterIndex
                        ? declaration.Parameters[parameterIndex].Name
                        : ToCDefaultValue(GenericTypeSubstitution.Substitute(
                            assignment.Field.Type, typeArguments));
                    writer.WriteLine(
                        $"__this->{assignment.Field.Name} = " +
                        $"{value};");
                }
                writer.DecreaseIndent();
                if (conditionalAssignments.WhenFalse.Count > 0)
                {
                    writer.WriteLine("} else {");
                    writer.IncreaseIndent();
                    foreach (var assignment in conditionalAssignments.WhenFalse)
                    {
                        var value = assignment.ParameterIndex is { } parameterIndex
                            ? declaration.Parameters[parameterIndex].Name
                            : ToCDefaultValue(GenericTypeSubstitution.Substitute(
                                assignment.Field.Type, typeArguments));
                        writer.WriteLine(
                            $"__this->{assignment.Field.Name} = " +
                            $"{value};");
                    }
                    writer.DecreaseIndent();
                    writer.WriteLine("}");
                }
                else
                {
                    writer.WriteLine("}");
                }
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.GenericTypeNames.Length == 0 &&
                declaration.ParentClassDeclaration is { } conditionalConcreteParent &&
                GetClosedGenericConditionalConcreteFieldAssignments(
                    declaration,
                    conditionalConcreteParent,
                    conditionalConcreteParent.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } concreteAssignments)
            {
                var classArguments = conditionalConcreteParent.GenericTypeNames
                    .Zip(instance.ClosedContainingType!.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    concreteAssignments.Condition,
                    parameterNames,
                    declaration,
                    conditionalConcreteParent.ToCIdentifier(moduleName),
                    moduleName,
                    "__this");
                writer.WriteLine($"if ({condition}) {{");
                writer.IncreaseIndent();
                foreach (var assignment in concreteAssignments.WhenTrue)
                {
                    var value = ToCClosedGenericMixedFieldAssignmentValue(
                        assignment.Field,
                        assignment.Assignment,
                        declaration,
                        classArguments,
                        parameterNames,
                        conditionalConcreteParent.ToCIdentifier(moduleName),
                        moduleName,
                        "__this");
                    writer.WriteLine($"__this->{assignment.Field.Name} = {value};");
                }
                writer.DecreaseIndent();
                writer.WriteLine("} else {");
                writer.IncreaseIndent();
                foreach (var assignment in concreteAssignments.WhenFalse)
                {
                    var value = ToCClosedGenericMixedFieldAssignmentValue(
                        assignment.Field,
                        assignment.Assignment,
                        declaration,
                        classArguments,
                        parameterNames,
                        conditionalConcreteParent.ToCIdentifier(moduleName),
                        moduleName,
                        "__this");
                    writer.WriteLine($"__this->{assignment.Field.Name} = {value};");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            else if (instance.ClosedContainingType is not null &&
                declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Class, GenericTypeNames.Length: > 0 } genericClass &&
                IsClosedGenericConcreteExpressionMethod(
                    declaration,
                    genericClass,
                    genericClass.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()))
            {
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                if (declaration.Body is [ReturnStatement { Expression: { } classExpression }])
                {
                    var classExpressionC = ToCClosedGenericConcreteMethodExpression(
                        classExpression,
                        parameterNames,
                        declaration,
                        genericClass.ToCIdentifier(moduleName),
                        moduleName,
                        "__this");
                    writer.WriteLine($"return {classExpressionC};");
                }
                else if (TryGetClosedGenericConcreteResultParts(
                    declaration,
                    out var conditionExpression,
                    out var trueExpression,
                    out var falseExpression))
                {
                    var concreteCondition = ToCClosedGenericConcreteMethodExpression(
                        conditionExpression,
                        parameterNames,
                        declaration,
                        genericClass.ToCIdentifier(moduleName),
                        moduleName,
                        "__this");
                    var concreteWhenTrue = ToCClosedGenericConcreteMethodExpression(
                        trueExpression,
                        parameterNames,
                        declaration,
                        genericClass.ToCIdentifier(moduleName),
                        moduleName,
                        "__this");
                    var concreteWhenFalse = ToCClosedGenericConcreteMethodExpression(
                        falseExpression,
                        parameterNames,
                        declaration,
                        genericClass.ToCIdentifier(moduleName),
                        moduleName,
                        "__this");
                    writer.WriteLine(
                        $"return ({concreteCondition} ? {concreteWhenTrue} : " +
                        $"{concreteWhenFalse});");
                }
                else
                {
                    throw new InternalCompilerException(
                        $"Supported concrete-expression method '{declaration.FullName}' " +
                        "has no supported return expression.");
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericArrayCopyBody(declaration))
            {
                var copyLocal = (LocalVariableDeclarationStatement)declaration.Body![0];
                var copyDeclarator = copyLocal.Declarators.Single();
                var creation = (ArrayCreationExpression)copyDeclarator.Initializer!;
                var loop = (ForStatement)declaration.Body[1];
                var indexDeclaration = loop.DeclarationInitializer!;
                var indexDeclarator = indexDeclaration.Declarators.Single();
                var indexName = indexDeclarator.Name;
                var elementType = ((ArrayType)instance.ReturnType).ElementType;
                var elementCType = elementType.ToCIdentifier(false);
                var lengthExpression = ToCExpression(creation.Length, declaration, moduleName);
                writer.WriteLine(
                    $"{instance.ReturnType.ToCIdentifier(false)} {copyDeclarator.Name} = " +
                    $"cx_array_new((cx_uint)({lengthExpression}), " +
                    $"(cx_uint)sizeof({elementCType}));");
                writer.WriteLine(
                    $"for ({indexDeclaration.DeclaredType.ToCIdentifier(false)} {indexName} = " +
                    $"{ToCExpression(indexDeclarator.Initializer!, declaration, moduleName)}; " +
                    $"{ToCExpression(loop.Condition!, declaration, moduleName)}; " +
                    $"{ToCExpression(loop.Iterators.Single(), declaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                var assignment = (AssignmentExpression)((ExpressionStatement)
                    ((BlockStatement)loop.Body).Statements.Single()).Expression;
                var target = (ArrayAccessExpression)assignment.Target;
                var value = (ArrayAccessExpression)assignment.Value;
                writer.WriteLine(
                    $"{ToCGenericArrayElementAccess(target, elementCType, declaration, moduleName)} = " +
                    $"{ToCGenericArrayElementAccess(value, elementCType, declaration, moduleName)};");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.WriteLine($"return {copyDeclarator.Name};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericArrayCopyWhileBody(declaration))
            {
                var copyLocal = (LocalVariableDeclarationStatement)declaration.Body![0];
                var copyDeclarator = copyLocal.Declarators.Single();
                var creation = (ArrayCreationExpression)copyDeclarator.Initializer!;
                var indexDeclaration = (LocalVariableDeclarationStatement)declaration.Body[1];
                var indexDeclarator = indexDeclaration.Declarators.Single();
                var loop = (WhileStatement)declaration.Body[2];
                var elementType = ((ArrayType)instance.ReturnType).ElementType;
                var elementCType = elementType.ToCIdentifier(false);
                var lengthExpression = ToCExpression(creation.Length, declaration, moduleName);
                writer.WriteLine(
                    $"{instance.ReturnType.ToCIdentifier(false)} {copyDeclarator.Name} = " +
                    $"cx_array_new((cx_uint)({lengthExpression}), " +
                    $"(cx_uint)sizeof({elementCType}));");
                writer.WriteLine(
                    $"{indexDeclaration.DeclaredType.ToCIdentifier(false)} {indexDeclarator.Name} = " +
                    $"{ToCExpression(indexDeclarator.Initializer!, declaration, moduleName)};");
                writer.WriteLine($"while ({ToCExpression(loop.Condition!, declaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                var loopBody = (BlockStatement)loop.Body;
                var assignment = (AssignmentExpression)((ExpressionStatement)
                    loopBody.Statements[0]).Expression;
                var target = (ArrayAccessExpression)assignment.Target;
                var value = (ArrayAccessExpression)assignment.Value;
                writer.WriteLine(
                    $"{ToCGenericArrayElementAccess(target, elementCType, declaration, moduleName)} = " +
                    $"{ToCGenericArrayElementAccess(value, elementCType, declaration, moduleName)};");
                var increment = ((ExpressionStatement)
                    loopBody.Statements[1]).Expression;
                writer.WriteLine(
                    $"{ToCExpression(increment, declaration, moduleName)};");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.WriteLine($"return {copyDeclarator.Name};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericArrayCopyForeachBody(declaration))
            {
                var copyLocal = (LocalVariableDeclarationStatement)declaration.Body![0];
                var copyDeclarator = copyLocal.Declarators.Single();
                var creation = (ArrayCreationExpression)copyDeclarator.Initializer!;
                var indexLocal = (LocalVariableDeclarationStatement)declaration.Body[1];
                var indexDeclarator = indexLocal.Declarators.Single();
                var elementType = ((ArrayType)instance.ReturnType).ElementType;
                var lengthExpression = ToCExpression(creation.Length, declaration, moduleName);
                writer.WriteLine(
                    $"{instance.ReturnType.ToCIdentifier(false)} {copyDeclarator.Name} = " +
                    $"cx_array_new((cx_uint)({lengthExpression}), " +
                    $"(cx_uint)sizeof({elementType.ToCIdentifier(false)}));");
                writer.WriteLine(
                    $"{indexLocal.DeclaredType.ToCIdentifier(false)} {indexDeclarator.Name} = " +
                    $"{ToCExpression(indexDeclarator.Initializer!, declaration, moduleName)};");
                WriteGenericConceptMutationStatement(
                    writer, declaration.Body[2], instance, declaration, moduleName);
                writer.WriteLine($"return {copyDeclarator.Name};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                declaration.Body is
                [LocalVariableDeclarationStatement
                {
                    Declarators:
                    [
                        {
                            Initializer: ArrayCreationExpression creation,
                        } createArrayLocal,
                    ],
                },
                ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: ArrayAccessExpression
                        {
                            Target: IdentifierExpression,
                            Indices: [var createArrayIndex],
                        } createArrayAccess,
                        Value: IdentifierExpression createArrayValue,
                    },
                },
                ReturnStatement])
            {
                var localName = createArrayLocal.Name;
                var elementType = ((ArrayType)instance.ReturnType).ElementType;
                var elementCType = elementType.ToCIdentifier(false);
                var lengthExpression = ToCExpression(creation.Length, declaration, moduleName);
                writer.WriteLine(
                    $"{instance.ReturnType.ToCIdentifier(false)} {localName} = " +
                    $"cx_array_new((cx_uint)({lengthExpression}), " +
                    $"(cx_uint)sizeof({elementCType}));");

                var valueParameterIndex = declaration.Parameters.ToList().FindIndex(parameter =>
                    parameter.Name == createArrayValue.Identifier.ToString());
                var arrayExpression = ToCExpression(createArrayAccess.Target, declaration, moduleName);
                var indexExpression = ToCExpression(createArrayIndex, declaration, moduleName);
                var valueExpression = ToCExpressionAsType(
                    createArrayValue,
                    instance.ParameterTypes[valueParameterIndex],
                    declaration,
                    moduleName);
                writer.WriteLine(
                    $"(*({elementCType}*)cx_array_at({arrayExpression}, " +
                    $"(cx_uint)({indexExpression}), (cx_uint)sizeof({elementCType}))) = " +
                    $"{valueExpression};");
                writer.WriteLine($"return {localName};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                declaration.Body is
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: ArrayAccessExpression copyTargetAccess,
                        Value: ArrayAccessExpression copyValueAccess,
                    },
                }])
            {
                writer.WriteLine(
                    $"{ToCGenericArrayReturnAccess(copyTargetAccess, instance.GenericTypeArguments, declaration, moduleName)} = " +
                    $"{ToCGenericArrayReturnAccess(copyValueAccess, instance.GenericTypeArguments, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                declaration.Body is
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: ArrayAccessExpression setAccess,
                        Value: IdentifierExpression value,
                    },
                }])
            {
                var arrayRoot = setAccess;
                while (arrayRoot.Target is ArrayAccessExpression nestedAccess)
                {
                    arrayRoot = nestedAccess;
                }
                var arrayParameterName =
                    ((IdentifierExpression)arrayRoot.Target).Identifier.ToString();
                var arrayParameterIndex = declaration.Parameters.ToList().FindIndex(parameter =>
                    parameter.Name == arrayParameterName);
                var valueParameterIndex = declaration.Parameters.ToList().FindIndex(parameter =>
                    parameter.Name == ((IdentifierExpression)value).Identifier.ToString());
                var valueExpression = ToCExpressionAsType(
                    value,
                    instance.ParameterTypes[valueParameterIndex],
                    declaration,
                    moduleName);
                writer.WriteLine(
                    $"{ToCGenericArrayReturnAccess(setAccess, instance.GenericTypeArguments, declaration, moduleName)} = " +
                    $"{valueExpression};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                declaration.Body is
                [ReturnStatement
                {
                    Expression: MemberAccessExpression
                    {
                        Target: { } array,
                        MemberName: "Length",
                    },
                }])
            {
                var lengthGetter = new QualifiedIdentifier(
                    "cxcore", "System", "Collections", "Array", "Length", "__const_get").ToCIdentifier();
                writer.WriteLine(
                    $"return {lengthGetter}({ToCExpression(array, declaration, moduleName)});");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericArrayLengthComparisonBody(declaration))
            {
                var comparison = ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCExpression(comparison, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericReferenceNullComparisonBody(declaration))
            {
                var comparison = ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCExpression(comparison, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericNullCoalescingReturnBody(declaration))
            {
                var coalescing = ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCExpression(coalescing, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                declaration.Body is
                [ReturnStatement
                {
                    Expression: ArrayAccessExpression
                    {
                    } access,
                }])
            {
                writer.WriteLine(
                    $"return {ToCGenericArrayReturnAccess(access, instance.GenericTypeArguments, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericArrayElementLocalReturnBody(declaration))
            {
                var body = declaration.Body ?? throw new InternalCompilerException(
                    "Generic array-element local body is not bound.");
                foreach (var statement in body.Take(body.Count - 1))
                {
                    switch (statement)
                    {
                        case LocalVariableDeclarationStatement local:
                        {
                            var localType = local.DeclaredType is AutoType
                                ? instance.ReturnType.ToCIdentifier(false)
                                : GenericTypeSubstitution.Substitute(
                                    local.DeclaredType!,
                                    instance.GenericTypeArguments).ToCIdentifier(false);
                            foreach (var declarator in local.Declarators)
                            {
                                var initializer = declarator.Initializer switch
                                {
                                    ArrayAccessExpression arrayAccess =>
                                        ToCGenericArrayReturnAccess(
                                            arrayAccess,
                                            instance.GenericTypeArguments,
                                            declaration,
                                            moduleName),
                                    IdentifierExpression source => source.Identifier.Parts[0],
                                    _ => throw new InternalCompilerException(
                                        "Generic array-element local has an unsupported initializer."),
                                };
                                writer.WriteLine(
                                    $"{localType} {declarator.Name} = {initializer};");
                            }
                            break;
                        }
                        case ExpressionStatement
                        {
                            Expression: AssignmentExpression
                            {
                                Target: IdentifierExpression target,
                                Value: IdentifierExpression source,
                            },
                        }:
                            writer.WriteLine(
                                $"{target.Identifier.Parts[0]} = {source.Identifier.Parts[0]};");
                            break;
                    }
                }
                var returned = (IdentifierExpression)((ReturnStatement)
                    body[^1]).Expression!;
                writer.WriteLine($"return {returned.Identifier.Parts[0]};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericLocalSequenceBody(declaration))
            {
                foreach (var statement in declaration.Body!)
                {
                    switch (statement)
                    {
                        case LocalVariableDeclarationStatement local:
                            foreach (var declarator in local.Declarators)
                            {
                                var source = (IdentifierExpression)declarator.Initializer!;
                                writer.WriteLine($"{instance.ReturnType.ToCIdentifier(false)} " +
                                    $"{declarator.Name} = {source.Identifier.Parts[0]};");
                            }
                            break;
                        case ExpressionStatement
                        {
                            Expression: AssignmentExpression
                            {
                                Target: IdentifierExpression target,
                                Value: IdentifierExpression replacement,
                            },
                        }:
                            writer.WriteLine(
                                $"{target.Identifier.Parts[0]} = {replacement.Identifier.Parts[0]};");
                            break;
                        case ReturnStatement { Expression: IdentifierExpression returned }:
                            writer.WriteLine($"return {returned.Identifier.Parts[0]};");
                            break;
                    }
                }
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConditionalLocalReturnBody(declaration))
            {
                var conditionalLocal = (LocalVariableDeclarationStatement)declaration.Body![0];
                var declarator = conditionalLocal.Declarators.Single();
                writer.WriteLine(
                    $"{instance.ReturnType.ToCIdentifier(false)} {declarator.Name} = " +
                    $"{ToCExpression(declarator.Initializer!, declaration, moduleName)};");
                writer.WriteLine($"return {declarator.Name};");
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
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConditionalReturnBody(declaration))
            {
                var body = declaration.Body!;
                var flowStart = 0;
                while (flowStart < body.Count &&
                    body[flowStart] is LocalVariableDeclarationStatement conditionLocal)
                {
                    foreach (var declarator in conditionLocal.Declarators)
                    {
                        writer.WriteLine($"cx_bool {declarator.Name} = " +
                            $"{ToCExpression(declarator.Initializer!, declaration, moduleName)};");
                    }
                    flowStart++;
                }
                foreach (var statement in body.Skip(flowStart))
                {
                    WriteGenericReturnFlow(writer, statement, declaration, moduleName);
                }
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
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptOperatorReturnBody(declaration))
            {
                var operation = (BinaryExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                var left = (IdentifierExpression)operation.Left;
                var right = (IdentifierExpression)operation.Right;
                if (!instance.ConceptOperatorBindings.TryGetValue(
                    operation.Operator, out var operatorSymbol))
                {
                    throw new InternalCompilerException(
                        $"Missing concept operator binding for '{operation.Operator}'.");
                }
                var receiver = instance.ParameterTypes[0] is
                    NamedType { ClassType: ClassType.Struct }
                    ? $"&{left.Identifier.Parts[0]}"
                    : left.Identifier.Parts[0];
                var argument = ToCExpressionAsType(
                    right,
                    operatorSymbol.ParameterTypes.Single(),
                    declaration,
                    moduleName);
                writer.WriteLine(
                    $"return {ToCIdentifier(operatorSymbol)}({receiver}, {argument});");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptUnaryReturnBody(declaration))
            {
                var unary = (UnaryExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                var operand = (IdentifierExpression)unary.Operand;
                if (!instance.ConceptOperatorBindings.TryGetValue(
                    unary.Operator, out var operatorSymbol))
                {
                    throw new InternalCompilerException(
                        $"Missing concept operator binding for '{unary.Operator}'.");
                }
                var receiver = instance.ParameterTypes[0] is
                    NamedType { ClassType: ClassType.Struct }
                    ? $"&{operand.Identifier.Parts[0]}"
                    : operand.Identifier.Parts[0];
                writer.WriteLine($"return {ToCIdentifier(operatorSymbol)}({receiver});");
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } mixedGenericStruct &&
                GetClosedGenericMixedFieldAssignments(
                    declaration,
                    mixedGenericStruct,
                    mixedGenericStruct.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } mixedAssignments &&
                instance.ClosedContainingType?.ConstructedIdentity is { } mixedClosedIdentity)
            {
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                foreach (var assignment in mixedAssignments)
                {
                    var mixedTargetField = ((IdentifierExpression)assignment.Target)
                        .TargetField!.Declaration;
                    string mixedValue;
                    if (ContainsGenericParameter(
                            mixedTargetField.Type, mixedGenericStruct.GenericTypeNames))
                    {
                        mixedValue = ((IdentifierExpression)assignment.Value)
                            .Identifier.Parts[0];
                    }
                    else
                    {
                        mixedValue = ToCClosedGenericConcreteMethodExpression(
                            assignment.Value,
                            parameterNames,
                            declaration,
                            mixedClosedIdentity.CIdentifier,
                            moduleName,
                            "__this");
                    }
                    writer.WriteLine($"__this->{mixedTargetField.Name} = {mixedValue};");
                }
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } whileGenericStruct &&
                instance.ClosedContainingType?.ConstructedIdentity is
                    { } whileClosedIdentity &&
                GetClosedGenericWhileConcreteFieldAssignments(
                    declaration,
                    whileGenericStruct,
                    whileGenericStruct.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } whileAssignments)
            {
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                var loopCondition = ToCClosedGenericConcreteMethodExpression(
                    whileAssignments.Condition,
                    parameterNames,
                    declaration,
                    whileClosedIdentity.CIdentifier,
                    moduleName,
                    "__this");
                var assignments = string.Join(" ", whileAssignments.Assignments.Select(assignment =>
                    $"__this->{assignment.Field.Name} {assignment.Assignment.Operator} " +
                    ToCClosedGenericConcreteMethodExpression(
                        assignment.Assignment.Value,
                        parameterNames,
                        declaration,
                        whileClosedIdentity.CIdentifier,
                        moduleName,
                        "__this") + ";"));
                writer.WriteLine($"while ({loopCondition}) {{ {assignments} }}");
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } doWhileGenericStruct &&
                instance.ClosedContainingType?.ConstructedIdentity is
                    { } doWhileClosedIdentity &&
                GetClosedGenericDoWhileConcreteFieldAssignments(
                    declaration,
                    doWhileGenericStruct,
                    doWhileGenericStruct.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } doWhileAssignments)
            {
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                var assignments = string.Join(" ", doWhileAssignments.Assignments.Select(assignment =>
                    $"__this->{assignment.Field.Name} {assignment.Assignment.Operator} " +
                    ToCClosedGenericConcreteMethodExpression(
                        assignment.Assignment.Value,
                        parameterNames,
                        declaration,
                        doWhileClosedIdentity.CIdentifier,
                        moduleName,
                        "__this") + ";"));
                var doWhileCondition = ToCClosedGenericConcreteMethodExpression(
                    doWhileAssignments.Condition,
                    parameterNames,
                    declaration,
                    doWhileClosedIdentity.CIdentifier,
                    moduleName,
                    "__this");
                writer.WriteLine($"do {{ {assignments} }} while ({doWhileCondition});");
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } forGenericStruct &&
                instance.ClosedContainingType?.ConstructedIdentity is
                    { } forClosedIdentity &&
                GetClosedGenericForConcreteFieldAssignments(
                    declaration,
                    forGenericStruct,
                    forGenericStruct.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) is { } forAssignments)
            {
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                string RenderAssignment(AssignmentExpression assignment)
                {
                    var field = ((IdentifierExpression)assignment.Target)
                        .TargetField!.Declaration;
                    var value = ToCClosedGenericConcreteMethodExpression(
                        assignment.Value,
                        parameterNames,
                        declaration,
                        forClosedIdentity.CIdentifier,
                        moduleName,
                        "__this");
                    return $"__this->{field.Name} {assignment.Operator} {value}";
                }

                var forCondition = ToCClosedGenericConcreteMethodExpression(
                    forAssignments.Condition,
                    parameterNames,
                    declaration,
                    forClosedIdentity.CIdentifier,
                    moduleName,
                    "__this");
                var assignments = string.Join(" ", forAssignments.Assignments.Select(assignment =>
                    RenderAssignment(assignment.Assignment) + ";"));
                writer.WriteLine(
                    $"for ({RenderAssignment(forAssignments.Initializer)}; {forCondition}; " +
                    $"{RenderAssignment(forAssignments.Iterator)}) {{ {assignments} }}");
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } concreteGenericStruct &&
                IsClosedGenericConcreteExpressionMethod(
                    declaration,
                    concreteGenericStruct,
                    concreteGenericStruct.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) &&
                instance.ClosedContainingType?.ConstructedIdentity is
                    { } concreteClosedIdentity)
            {
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                if (declaration.Body is [ReturnStatement { Expression: { } expression }])
                {
                    var expressionC = ToCClosedGenericConcreteMethodExpression(
                        expression,
                        parameterNames,
                        declaration,
                        concreteClosedIdentity.CIdentifier,
                        moduleName,
                        "__this");
                    writer.WriteLine($"return {expressionC};");
                }
                else if (TryGetClosedGenericConcreteResultParts(
                    declaration,
                    out var conditionExpression,
                    out var trueExpression,
                    out var falseExpression))
                {
                    var concreteCondition = ToCClosedGenericConcreteMethodExpression(
                        conditionExpression,
                        parameterNames,
                        declaration,
                        concreteClosedIdentity.CIdentifier,
                        moduleName,
                        "__this");
                    var concreteWhenTrue = ToCClosedGenericConcreteMethodExpression(
                        trueExpression,
                        parameterNames,
                        declaration,
                        concreteClosedIdentity.CIdentifier,
                        moduleName,
                        "__this");
                    var concreteWhenFalse = ToCClosedGenericConcreteMethodExpression(
                        falseExpression,
                        parameterNames,
                        declaration,
                        concreteClosedIdentity.CIdentifier,
                        moduleName,
                        "__this");
                    writer.WriteLine(
                        $"return ({concreteCondition} ? {concreteWhenTrue} : " +
                        $"{concreteWhenFalse});");
                }
                else
                {
                    throw new InternalCompilerException(
                        $"Supported concrete-expression method '{declaration.FullName}' " +
                        "has no supported return expression.");
                }
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } genericStruct &&
                IsClosedGenericGenericResultExpressionMethod(
                    declaration,
                    genericStruct,
                    genericStruct.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) &&
                instance.ClosedContainingType?.ConstructedIdentity is { } closedIdentity)
            {
                if (!TryGetClosedGenericGenericResultParts(
                        declaration,
                        out var conditionExpression,
                        out var trueExpression,
                        out var falseExpression))
                {
                    throw new InternalCompilerException(
                        $"Supported generic-result method '{declaration.FullName}' " +
                        "has no conditional or return-branch expression.");
                }
                var closedInstance = $"((struct {closedIdentity.CIdentifier}*)__this)";
                var parameterNames = declaration.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => parameter.Name,
                    StringComparer.Ordinal);
                var conditionExpressionC = ToCClosedGenericConcreteMethodExpression(
                    conditionExpression,
                    parameterNames,
                    declaration,
                    closedIdentity.CIdentifier,
                    moduleName,
                    closedInstance);
                writer.WriteLine("return (" + conditionExpressionC + " ? " +
                    ToCClosedGenericGenericResultArm(
                        trueExpression, instance.ReturnType, closedInstance) + " : " +
                    ToCClosedGenericGenericResultArm(
                        falseExpression, instance.ReturnType, closedInstance) + ");");
            }
            else if (declaration.ParentClassDeclaration is
                    { ClassType: ClassType.Struct, GenericTypeNames.Length: > 0 } defaultGenericStruct &&
                IsClosedGenericDefaultResultMethod(declaration, defaultGenericStruct))
            {
                writer.WriteLine($"return {ToCDefaultValue(instance.ReturnType)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericConceptPropertyReturnBody(declaration))
            {
                var expression = ((ReturnStatement)declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCGenericConceptPropertyExpression(
                        expression, instance, declaration, moduleName)};");
            }
            else if (declaration.GenericTypeNames.Length > 0 &&
                IsGenericArrayGetReturnBody(declaration))
            {
                var invocation = (InvocationExpression)((ReturnStatement)
                    declaration.Body![0]).Expression!;
                writer.WriteLine(
                    $"return {ToCExpression(invocation, declaration, moduleName)};");
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

    private static bool IsGenericArrayGetReturnBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not
                [ReturnStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression
                        {
                            MemberName: "Get",
                            Target: IdentifierExpression arrayIdentifier,
                        },
                        Arguments.Count: 1,
                    },
                } invocation] ||
            declaration.ReturnType is not GenericType returnType)
        {
            return false;
        }

        var parameter = declaration.Parameters.SingleOrDefault(item =>
            item.Name == arrayIdentifier.Identifier.ToString());
        return parameter is not null &&
            ArrayType.TryGetElementType(parameter.ParameterType, out var elementType) &&
            elementType is GenericType genericElementType &&
            genericElementType.Name == returnType.Name;
    }

    private static Action ApplyClosedGenericOwnerTypes(
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        ClassDeclaration owner)
    {
        var ownerArguments = GetClosedGenericOwnerArguments(instance, owner) ??
            throw new InternalCompilerException(
                $"Generic function '{instance.FullName}' has no closed owner arguments.");
        var callerSpecialization = instance.SpecializationName ??
            throw new InternalCompilerException(
                $"Generic function '{instance.FullName}' has no specialization identity.");
        TypeBase Close(TypeBase type)
        {
            var closed = GenericTypeSubstitution.Substitute(type, ownerArguments);
            EnsureConstructedGenericIdentities(closed);
            return closed;
        }

        var originalReturnType = declaration.ReturnType;
        var originalParameterTypes = declaration.Parameters
            .Select(parameter => parameter.ParameterType).ToArray();
        var localDeclarations = EnumerateBodyStatements(declaration.Body ?? [])
            .OfType<LocalVariableDeclarationStatement>()
            .Select(local => (Local: local, DeclaredType: local.DeclaredType,
                DeclaratorTypes: local.Declarators.Select(item => item.Type).ToArray()))
            .ToArray();
        var expressionStates = EnumerateBodyExpressions(declaration.Body ?? [])
            .Select(expression => (Expression: expression,
                InferredType: expression.InferredType,
                ClosedGenericType: expression.ClosedGenericType,
                InvocationTarget: expression is InvocationExpression invocation
                    ? invocation.TargetSymbol
                    : null,
                DispatchInterfaceType: expression is InvocationExpression dispatchInvocation
                    ? dispatchInvocation.DispatchInterfaceType
                    : null,
                CreationType: expression is ObjectCreationExpression creation
                    ? creation.RequestedType
                    : null,
                CreationConstructor: expression is ObjectCreationExpression objectCreation
                    ? objectCreation.Constructor
                    : null))
            .ToArray();

        void Restore()
        {
            declaration.ReturnType = originalReturnType;
            foreach (var (parameter, type) in declaration.Parameters.Zip(originalParameterTypes))
            {
                parameter.ParameterType = type;
            }
            foreach (var (local, declaredType, declaratorTypes) in localDeclarations)
            {
                local.DeclaredType = declaredType;
                foreach (var (declarator, type) in local.Declarators.Zip(declaratorTypes))
                {
                    if (type is not null)
                    {
                        declarator.CloseType(type);
                    }
                }
            }
            foreach (var state in expressionStates)
            {
                state.Expression.RestoreTypes(
                    state.InferredType, state.ClosedGenericType);
                if (state.Expression is InvocationExpression invocation)
                {
                    invocation.RestoreTargetSymbol(state.InvocationTarget);
                    invocation.SetClosedDispatchInterfaceType(state.DispatchInterfaceType);
                }
                else if (state.Expression is ObjectCreationExpression creation &&
                    state.CreationType is { } creationType)
                {
                    creation.RestoreConstruction(
                        creationType, state.CreationConstructor);
                }
            }
        }

        declaration.ReturnType = Close(declaration.ReturnType);
        foreach (var parameter in declaration.Parameters)
        {
            parameter.ParameterType = Close(parameter.ParameterType);
        }
        foreach (var local in EnumerateBodyStatements(declaration.Body ?? [])
            .OfType<LocalVariableDeclarationStatement>())
        {
            local.DeclaredType = Close(local.DeclaredType);
            foreach (var declarator in local.Declarators)
            {
                if (declarator.Type is { } localType)
                {
                    declarator.CloseType(Close(localType));
                }
            }
        }
        foreach (var expression in EnumerateBodyExpressions(declaration.Body ?? []))
        {
            if (expression.InferredType is { } inferredType)
            {
                expression.SetInferredType(Close(inferredType));
            }
            if (expression is InvocationExpression invocation)
            {
                if (invocation.OpenDispatchInterfaceType is { } openInterfaceType)
                {
                    invocation.SetClosedDispatchInterfaceType(
                        Close(openInterfaceType) as NamedType);
                }
                invocation.ApplyClosedGenericTarget(callerSpecialization);
            }
            else if (expression is ObjectCreationExpression creation)
            {
                creation.ApplyClosedConstruction(callerSpecialization);
            }
        }

        return Restore;
    }

    private static bool HasClosedGenericOwnerArguments(
        FunctionSymbol instance,
        ClassDeclaration owner) =>
        GetClosedGenericOwnerArguments(instance, owner) is not null;

    private static IReadOnlyDictionary<string, TypeBase>? GetClosedGenericOwnerArguments(
        FunctionSymbol instance,
        ClassDeclaration owner)
    {
        if (instance.ClosedContainingType is { } closedOwner &&
            closedOwner.TypeArguments.Count == owner.GenericTypeNames.Length)
        {
            return owner.GenericTypeNames.Zip(closedOwner.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second,
                    StringComparer.Ordinal);
        }

        if (owner.GenericTypeNames.All(instance.GenericTypeArguments.ContainsKey))
        {
            return owner.GenericTypeNames.ToDictionary(
                name => name,
                name => instance.GenericTypeArguments[name],
                StringComparer.Ordinal);
        }

        return null;
    }

    private static bool IsClosedClassConceptMethodReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.ParentClassDeclaration is not
                { ClassType: ClassType.Class or ClassType.Struct, ConceptConstraints.Count: > 0 } parent ||
            declaration.Body is not [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        return IsClosedClassConceptExpression(expression, parent) &&
            EnumerateAllExpressions(expression).Any(candidate => candidate switch
            {
                InvocationExpression { IsConceptMethodInvocation: true } => true,
                MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                    TargetProperty: not null,
                } => receiver.InferredType is GenericType generic &&
                    parent.ConceptConstraints.Any(constraint =>
                        constraint.ParameterName == generic.Name),
                BinaryExpression binary => IsClassConceptTypeParameter(
                    binary.Left, declaration),
                UnaryExpression unary => IsClassConceptTypeParameter(
                    unary.Operand, declaration),
                _ => false,
            });
    }

    private static bool IsClosedClassConceptVoidCallBody(
        FunctionDeclaration declaration)
    {
        if (declaration.ReturnType is not VoidType ||
            declaration.ParentClassDeclaration is not
                { ClassType: ClassType.Class or ClassType.Struct, ConceptConstraints.Count: > 0 } parent ||
            declaration.Body is not { Count: > 0 } body)
        {
            return false;
        }

        return body.All(statement => IsClosedClassConceptVoidStatement(
            statement, parent, declaration));
    }

    private static bool IsClosedClassConceptVoidStatement(
        StatementBase statement,
        ClassDeclaration parent,
        FunctionDeclaration declaration) => statement switch
    {
        BlockStatement block => block.Statements.All(nested =>
            IsClosedClassConceptVoidStatement(nested, parent, declaration)),
        IfStatement conditional =>
            IsClosedClassConceptExpression(conditional.Condition, parent) &&
            IsClosedClassConceptVoidStatement(
                conditional.ThenStatement, parent, declaration) &&
            (conditional.ElseStatement is null ||
             IsClosedClassConceptVoidStatement(
                conditional.ElseStatement, parent, declaration)),
        WhileStatement loop =>
            IsClosedClassConceptExpression(loop.Condition, parent) &&
            IsClosedClassConceptVoidStatement(loop.Body, parent, declaration),
        DoWhileStatement loop =>
            IsClosedClassConceptExpression(loop.Condition, parent) &&
            IsClosedClassConceptVoidStatement(loop.Body, parent, declaration),
        ForStatement loop => IsClosedClassConceptForStatement(loop, parent, declaration),
        ForeachStatement loop => IsClosedClassConceptForeachStatement(
            loop, parent, declaration),
        SwitchStatement switchStatement =>
            IsClosedClassConceptExpression(switchStatement.Expression, parent) &&
            switchStatement.Expression.InferredType is not null &&
            switchStatement.Sections.SelectMany(section => section.Labels)
                .All(label => label.Filter is null ||
                    IsClosedClassConceptExpression(label.Filter, parent)) &&
            switchStatement.Sections.SelectMany(section => section.Statements)
                .All(nested => IsClosedClassConceptVoidStatement(nested, parent, declaration)),
        LocalVariableDeclarationStatement local =>
            (local.DeclaredType is IntTypeBase or BoolType or StringType ||
             local.DeclaredType is GenericType genericType &&
                parent.ConceptConstraints.Any(constraint =>
                    constraint.ParameterName == genericType.Name)) &&
            local.Declarators.Count == 1 &&
            local.Declarators[0].Initializer is { } initializer &&
            IsClosedClassConceptExpression(initializer, parent),
        ReturnStatement { Expression: null } => true,
        BreakStatement or ContinueStatement => true,
        ExpressionStatement
        {
            Expression: InvocationExpression
            {
                IsConceptMethodInvocation: true,
                Target: MemberAccessExpression { Target: IdentifierExpression },
            } invocation,
        } => invocation.Arguments.All(argument =>
            IsClosedClassConceptExpression(argument, parent)),
        ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                    TargetProperty: not null,
                },
                Value: { } value,
            } assignment,
        } => IsClassConceptTypeParameter(receiver, declaration) &&
            (assignment.Operator == "="
                ? IsClosedClassConceptExpression(value, parent)
                : assignment.OperatorSymbol is null &&
                    IsClosedClassConceptSimpleValue(value, declaration)),
        ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                Target: IdentifierExpression { TargetLocal: not null },
                Value: { } value,
            } assignment,
        } => assignment.Operator == "="
            ? IsClosedClassConceptExpression(value, parent)
            : assignment.OperatorSymbol is null &&
                IsClosedClassConceptSimpleValue(value, declaration),
        ExpressionStatement
        {
            Expression: UnaryExpression
            {
                Operator: "++" or "--",
                OperatorSymbol: null,
                Operand: IdentifierExpression { TargetLocal: not null },
            } unary,
        } => unary.Operand.InferredType is IntTypeBase,
        _ => false,
    };

    private static bool IsClosedClassConceptForStatement(
        ForStatement loop,
        ClassDeclaration parent,
        FunctionDeclaration declaration)
    {
        string? counterName;
        ExpressionBase? startValue;
        if (loop.DeclarationInitializer is
            { Declarators.Count: 1, DeclaredType: IntType } initializer &&
            loop.InitializerExpressions.Count == 0 &&
            initializer.Declarators[0] is { Initializer: { } declaredStart } counter)
        {
            counterName = counter.Name;
            startValue = declaredStart;
        }
        else if (loop.DeclarationInitializer is null &&
            loop.InitializerExpressions is
                [AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression
                    {
                        TargetLocal: not null,
                        InferredType: IntTypeBase,
                    } existingCounter,
                    Value: { } existingStart,
                }])
        {
            counterName = existingCounter.Identifier.ToString();
            startValue = existingStart;
        }
        else
        {
            return false;
        }

        if (loop.Condition is null || loop.Iterators.Count != 1 ||
            loop.Iterators[0] is not UnaryExpression
            {
                Operator: "++" or "--",
                OperatorSymbol: null,
                Operand: IdentifierExpression iterator,
            } || iterator.Identifier.ToString() != counterName)
        {
            return false;
        }

        return IsClosedClassConceptExpression(startValue, parent) &&
            IsClosedClassConceptExpression(loop.Condition, parent) &&
            IsClosedClassConceptVoidStatement(loop.Body, parent, declaration);
    }

    private static bool IsClosedClassConceptForeachStatement(
        ForeachStatement loop,
        ClassDeclaration parent,
        FunctionDeclaration declaration)
    {
        if (loop.Collection.InferredType is not ArrayType arrayType ||
            loop.VariableType is not { } variableType ||
            loop.VariableSymbol?.IsCaptured == true ||
            !IsSameGenericTypePattern(variableType, arrayType.ElementType))
        {
            return false;
        }

        var constrainedTypeParameter = arrayType.ElementType is GenericType genericElement &&
            parent.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == genericElement.Name);
        var concreteElementType =
            !ContainsGenericParameter(arrayType.ElementType, parent.GenericTypeNames);
        return (constrainedTypeParameter || concreteElementType) &&
            IsClosedClassConceptExpression(loop.Collection, parent) &&
            IsClosedClassConceptVoidStatement(loop.Body, parent, declaration);
    }

    private static void WriteClosedClassConceptVoidStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        switch (statement)
        {
            case BlockStatement block:
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var nested in block.Statements)
                {
                    WriteClosedClassConceptVoidStatement(
                        writer, nested, instance, declaration, moduleName);
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                break;
            case IfStatement conditional:
                writer.WriteLine(
                    $"if ({ToCGenericConceptPropertyExpression(
                        conditional.Condition, instance, declaration, moduleName)})");
                WriteClosedClassConceptVoidControlled(
                    writer, conditional.ThenStatement, instance, declaration, moduleName);
                if (conditional.ElseStatement is { } elseStatement)
                {
                    writer.WriteLine("else");
                    WriteClosedClassConceptVoidControlled(
                        writer, elseStatement, instance, declaration, moduleName);
                }
                break;
            case WhileStatement loop:
                writer.WriteLine(
                    $"while ({ToCGenericConceptPropertyExpression(
                        loop.Condition, instance, declaration, moduleName)})");
                WriteClosedClassConceptVoidControlled(
                    writer, loop.Body, instance, declaration, moduleName);
                break;
            case DoWhileStatement loop:
                writer.WriteLine("do");
                WriteClosedClassConceptVoidControlled(
                    writer, loop.Body, instance, declaration, moduleName);
                writer.WriteLine(
                    $"while ({ToCGenericConceptPropertyExpression(
                        loop.Condition, instance, declaration, moduleName)});");
                break;
            case ForStatement loop:
                var initializer = loop.DeclarationInitializer is { } forDeclaration
                    ? ToCForDeclaration(forDeclaration, declaration, moduleName)
                    : string.Join(", ", loop.InitializerExpressions.Select(expression =>
                        ToCGenericConceptPropertyExpression(
                            expression, instance, declaration, moduleName)));
                var condition = ToCGenericConceptPropertyExpression(
                    loop.Condition!, instance, declaration, moduleName);
                var iterators = string.Join(", ", loop.Iterators.Select(expression =>
                    ToCExpression(expression, declaration, moduleName)));
                writer.WriteLine($"for ({initializer}; {condition}; {iterators})");
                WriteClosedClassConceptVoidControlled(
                    writer, loop.Body, instance, declaration, moduleName);
                break;
            case ForeachStatement loop:
            {
                var variableType = GenericTypeSubstitution.Substitute(
                    loop.VariableType ?? throw new InternalCompilerException(
                        $"Foreach variable '{loop.VariableName}' is not bound."),
                    instance.GenericTypeArguments);
                if (loop.VariableSymbol?.IsCaptured == true)
                {
                    throw new InternalCompilerException(
                        "Captured foreach variables are not supported in class concept bodies.");
                }

                var collectionName = $"__cx_class_concept_foreach_collection_{loop.VariableName}";
                var indexName = $"__cx_class_concept_foreach_index_{loop.VariableName}";
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"struct CX_ID_4(cxcore, System, Collections, Array)* {collectionName} = " +
                    $"{ToCExpression(loop.Collection, declaration, moduleName)};");
                writer.WriteLine(
                    $"for (cx_uint {indexName} = 0; " +
                    $"{indexName} < {collectionName}->_length; {indexName}++)");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"{variableType.ToCIdentifier(false)} {loop.VariableName} = " +
                    $"(({variableType.ToCIdentifier(false)}*){collectionName}->_data)[{indexName}];");
                if (loop.Body is BlockStatement block)
                {
                    foreach (var nested in block.Statements)
                    {
                        WriteClosedClassConceptVoidStatement(
                            writer, nested, instance, declaration, moduleName);
                    }
                }
                else
                {
                    WriteClosedClassConceptVoidStatement(
                        writer, loop.Body, instance, declaration, moduleName);
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                break;
            }
            case LocalVariableDeclarationStatement local:
            {
                var declarator = local.Declarators.Single();
                var type = declarator.Type ?? throw new InternalCompilerException(
                    $"Local '{declarator.Name}' is not bound.");
                type = GenericTypeSubstitution.Substitute(
                    type, instance.GenericTypeArguments);
                var localInitializer = declarator.Initializer ?? throw new InternalCompilerException(
                    $"Local '{declarator.Name}' has no initializer.");
                writer.WriteLine(
                    $"{type.ToCIdentifier(false)} {declarator.Name} = " +
                    $"{ToCGenericConceptPropertyExpression(
                        localInitializer, instance, declaration, moduleName)};");
                break;
            }
            case SwitchStatement switchStatement:
                if (switchStatement.SelectorTemporaryName is not null ||
                    switchStatement.Sections.SelectMany(section => section.Labels)
                        .Any(label => label.Filter is not null))
                {
                    WriteClosedClassConceptFilteredSwitch(
                        writer, switchStatement, instance, declaration, moduleName);
                    break;
                }

                writer.WriteLine(
                    $"switch ({ToCGenericConceptPropertyExpression(
                        switchStatement.Expression, instance, declaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var label in section.Labels)
                    {
                        writer.WriteLine(label.IsDefault
                            ? "default:"
                            : $"case {ToCExpression(label.Value!, declaration, moduleName)}:");
                    }
                    writer.WriteLine("{");
                    writer.IncreaseIndent();
                    foreach (var nested in section.Statements)
                    {
                        WriteClosedClassConceptVoidStatement(
                            writer, nested, instance, declaration, moduleName);
                    }
                    writer.DecreaseIndent();
                    writer.WriteLine("}");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                break;
            case BreakStatement:
                writer.WriteLine("break;");
                break;
            case ContinueStatement:
                writer.WriteLine("continue;");
                break;
            case ReturnStatement { Expression: null }:
                writer.WriteLine("return;");
                break;
            case ExpressionStatement { Expression: { } expression }:
                writer.WriteLine(
                    $"{ToCGenericConceptPropertyExpression(
                        expression, instance, declaration, moduleName)};");
                break;
            default:
                throw new InternalCompilerException(
                    $"Unsupported statement in closed class concept body: {statement.GetType().Name}.");
        }
    }

    private static void WriteClosedClassConceptFilteredSwitch(
        IndentingWriter writer,
        SwitchStatement statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        var selectorType = statement.Expression.InferredType ??
            throw new InternalCompilerException("Switch selector is not bound.");
        var selectorTemporary = statement.SelectorTemporaryName ??
            throw new InternalCompilerException("Filtered switch selector has no temporary.");
        var caseIndexTemporary = statement.CaseIndexTemporaryName ??
            throw new InternalCompilerException("Filtered switch has no case-index temporary.");

        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine(
            $"{selectorType.ToCIdentifier(false)} {selectorTemporary} = " +
            $"{ToCGenericConceptPropertyExpression(
                statement.Expression, instance, declaration, moduleName)};");
        writer.WriteLine($"cx_int {caseIndexTemporary} = -1;");
        for (var sectionIndex = 0; sectionIndex < statement.Sections.Count; sectionIndex++)
        {
            foreach (var label in statement.Sections[sectionIndex].Labels.Where(label =>
                !label.IsDefault))
            {
                var match = ToCSwitchLabelMatch(
                    selectorTemporary,
                    selectorType,
                    label.Value!,
                    declaration,
                    moduleName);
                if (label.Filter is not null)
                {
                    match = $"({match}) && " +
                        $"({ToCGenericConceptPropertyExpression(
                            label.Filter, instance, declaration, moduleName)})";
                }
                writer.WriteLine(
                    $"if ({caseIndexTemporary} == -1 && ({match})) " +
                    $"{caseIndexTemporary} = {sectionIndex};");
            }
        }

        var defaultIndex = statement.Sections
            .Select((section, index) => (section, index))
            .FirstOrDefault(pair => pair.section.Labels.Any(label => label.IsDefault));
        if (defaultIndex.section is not null)
        {
            writer.WriteLine(
                $"if ({caseIndexTemporary} == -1) " +
                $"{caseIndexTemporary} = {defaultIndex.index};");
        }

        writer.WriteLine($"switch ({caseIndexTemporary})");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        for (var sectionIndex = 0; sectionIndex < statement.Sections.Count; sectionIndex++)
        {
            writer.WriteLine($"case {sectionIndex}:");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            foreach (var nested in statement.Sections[sectionIndex].Statements)
            {
                WriteClosedClassConceptVoidStatement(
                    writer, nested, instance, declaration, moduleName);
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteClosedClassConceptVoidControlled(
        IndentingWriter writer,
        StatementBase statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        if (statement is BlockStatement)
        {
            WriteClosedClassConceptVoidStatement(
                writer, statement, instance, declaration, moduleName);
            return;
        }

        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteClosedClassConceptVoidStatement(
            writer, statement, instance, declaration, moduleName);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static bool IsClosedClassConceptSimpleValue(
        ExpressionBase expression,
        FunctionDeclaration declaration) =>
        expression is LiteralExpression ||
        expression is IdentifierExpression identifier &&
            declaration.Parameters.Any(parameter => parameter.Name ==
                identifier.Identifier.ToString());

    private static bool IsClosedClassConceptExpression(
        ExpressionBase expression,
        ClassDeclaration declaration) => expression switch
    {
        LiteralExpression or IdentifierExpression => true,
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            TargetProperty: not null,
        } => receiver.InferredType is GenericType generic &&
            declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == generic.Name),
        InvocationExpression
        {
            IsConceptMethodInvocation: true,
            Target: MemberAccessExpression { Target: IdentifierExpression },
        } invocation => invocation.Arguments.All(argument =>
            IsClosedClassConceptExpression(argument, declaration)),
        BinaryExpression { OperatorSymbol: null } binary =>
            IsClosedClassConceptExpression(binary.Left, declaration) &&
            IsClosedClassConceptExpression(binary.Right, declaration),
        UnaryExpression { OperatorSymbol: null, Postfix: false } unary =>
            IsClosedClassConceptExpression(unary.Operand, declaration),
        ConditionalExpression conditional =>
            IsClosedClassConceptExpression(conditional.Condition, declaration) &&
            IsClosedClassConceptExpression(conditional.WhenTrue, declaration) &&
            IsClosedClassConceptExpression(conditional.WhenFalse, declaration),
        _ => false,
    };

    private static bool IsGenericArrayLengthComparisonBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not
            [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        bool IsArrayLength(ExpressionBase expression) =>
            expression is MemberAccessExpression
            {
                Target: { } target,
                MemberName: "Length",
            } && IsGenericArrayLengthTarget(target, declaration);

        bool IsUnsignedComparisonOperand(ExpressionBase expression) =>
            expression is LiteralExpression literal &&
                (literal.SourceText.EndsWith('u') ||
                 uint.TryParse(literal.SourceText, out _)) ||
            expression is IdentifierExpression identifier &&
                declaration.Parameters.Any(parameter =>
                    parameter.Name == identifier.Identifier.ToString() &&
                    parameter.ParameterType is UIntType);

        bool IsLengthComparison(ExpressionBase candidate) =>
            candidate is BinaryExpression
            {
                Operator: "==" or "!=" or "<" or "<=" or ">" or ">=",
                OperatorSymbol: null,
            } comparison &&
            (IsArrayLength(comparison.Left) &&
                IsUnsignedComparisonOperand(comparison.Right) ||
             IsUnsignedComparisonOperand(comparison.Left) &&
                IsArrayLength(comparison.Right));

        bool IsSupportedBooleanExpression(ExpressionBase candidate) => candidate switch
        {
            UnaryExpression { Operator: "!", OperatorSymbol: null } unary =>
                IsSupportedBooleanExpression(unary.Operand),
            BinaryExpression
            {
                Operator: "&&" or "||",
                OperatorSymbol: null,
            } logical => IsSupportedBooleanExpression(logical.Left) &&
                IsSupportedBooleanExpression(logical.Right),
            ConditionalExpression conditional =>
                IsSupportedBooleanExpression(conditional.Condition) &&
                IsSupportedBooleanExpression(conditional.WhenTrue) &&
                IsSupportedBooleanExpression(conditional.WhenFalse),
            _ => IsLengthComparison(candidate),
        };

        return IsSupportedBooleanExpression(expression);
    }

    private static bool IsGenericDefaultReturnBody(FunctionDeclaration declaration) =>
        declaration.Body is
            [ReturnStatement { Expression: LiteralExpression { SourceText: "default" } }] &&
        ContainsGenericParameter(declaration.ReturnType, declaration.GenericTypeNames);

    private static bool IsGenericForwardCallReturnBody(FunctionDeclaration declaration)
    {
        if (declaration.ParentClassDeclaration is { } parent &&
                (parent.ClassType is not (ClassType.Class or ClassType.Struct) ||
                    parent.GenericTypeNames.Length != 0 ||
                    parent.IsStatic) ||
            declaration.LambdaOwner is not null || declaration.IsExtension ||
            declaration.Body is not
            [ReturnStatement { Expression: InvocationExpression invocation }] ||
            invocation.Arguments.Count == 0 ||
            invocation.Arguments.Any(argument =>
                !IsPotentialGenericForwardArgument(argument, declaration)) ||
            declaration.ReturnType is not GenericType returnType ||
            !declaration.GenericTypeNames.Contains(returnType.Name, StringComparer.Ordinal))
        {
            return false;
        }

        return invocation.Arguments.OfType<IdentifierExpression>()
            .Select(argument => declaration.Parameters.SingleOrDefault(candidate =>
                candidate.Name == argument.Identifier.ToString()))
            .Any(parameter => parameter?.ParameterType is GenericType
            {
                Name: var parameterTypeName,
            } && parameterTypeName == returnType.Name);
    }

    private static bool IsPotentialGenericForwardArgument(
        ExpressionBase expression,
        FunctionDeclaration declaration) => expression switch
    {
        IdentifierExpression identifier => declaration.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString()),
        LiteralExpression { SourceText: not "default" } => true,
        UnaryExpression unary when !unary.Postfix && unary.Operator is not ("++" or "--") =>
            IsPotentialGenericForwardArgument(unary.Operand, declaration),
        BinaryExpression binary =>
            IsPotentialGenericForwardArgument(binary.Left, declaration) &&
            IsPotentialGenericForwardArgument(binary.Right, declaration),
        ConditionalExpression conditional =>
            IsPotentialGenericForwardArgument(conditional.Condition, declaration) &&
            IsPotentialGenericForwardArgument(conditional.WhenTrue, declaration) &&
            IsPotentialGenericForwardArgument(conditional.WhenFalse, declaration),
        _ => false,
    };

    private static bool IsGenericDefaultLocalReturnBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not
                [LocalVariableDeclarationStatement local,
                    ReturnStatement { Expression: IdentifierExpression returned }] ||
            local.DeclaredType is null ||
            local.Declarators is not [var declarator] ||
            declarator.Initializer is not LiteralExpression { SourceText: "default" } ||
            !ContainsGenericParameter(declaration.ReturnType, declaration.GenericTypeNames) ||
            returned.Identifier.ToString() != declarator.Name ||
            !IsSameGenericTypePattern(
                declaration.ReturnType,
                GenericTypeSubstitution.BindParameters(
                    local.DeclaredType, declaration.GenericTypeNames)))
        {
            return false;
        }
        return true;
    }

    private static bool IsGenericArrayLengthTarget(
        ExpressionBase expression,
        FunctionDeclaration declaration)
    {
        TypeBase? arrayType = expression switch
        {
            IdentifierExpression identifier => declaration.Parameters.SingleOrDefault(parameter =>
                parameter.Name == identifier.Identifier.ToString())?.ParameterType,
            ArrayAccessExpression access => access.InferredType,
            _ => null,
        };
        return arrayType is ArrayType array &&
            ContainsGenericParameter(array.ElementType, declaration.GenericTypeNames);
    }

    private static bool IsGenericConceptConditionalReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length != 1 ||
            declaration.ReturnType is not GenericType returnType ||
            !declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == returnType.Name) ||
            declaration.Body is not
                [ReturnStatement
                {
                    Expression: ConditionalExpression
                    {
                        Condition: BinaryExpression
                        {
                            Left: IdentifierExpression left,
                            Right: IdentifierExpression right,
                        },
                        WhenTrue: IdentifierExpression whenTrue,
                        WhenFalse: IdentifierExpression whenFalse,
                    },
                }])
        {
            return false;
        }

        var parameters = declaration.Parameters
            .Where(parameter => IsSameGenericTypePattern(
                returnType, parameter.ParameterType))
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var leftName = left.Identifier.ToString();
        var rightName = right.Identifier.ToString();
        return leftName != rightName && parameters.Contains(leftName) &&
            parameters.Contains(rightName) &&
            whenTrue.Identifier.ToString() == leftName &&
            whenFalse.Identifier.ToString() == rightName &&
            declaration.Parameters.Count == 2;
    }

    private static bool IsGenericConceptOperatorReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length != 1 ||
            declaration.Body is not
                [ReturnStatement
                {
                    Expression: BinaryExpression
                    {
                        Left: IdentifierExpression left,
                        Right: IdentifierExpression right,
                    },
                }])
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        return (declaration.ReturnType is BoolType ||
                declaration.ReturnType is GenericType returnType &&
                returnType.Name == typeParameter) &&
            declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            declaration.Parameters.Count == 2 &&
            declaration.Parameters.All(parameter =>
                parameter.ParameterType is GenericType generic &&
                generic.Name == typeParameter) &&
            left.Identifier.ToString() != right.Identifier.ToString() &&
            declaration.Parameters.Any(parameter =>
                parameter.Name == left.Identifier.ToString()) &&
            declaration.Parameters.Any(parameter =>
                parameter.Name == right.Identifier.ToString());
    }

    private static bool IsGenericConceptUnaryReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length != 1 ||
            declaration.Body is not
                [ReturnStatement { Expression: UnaryExpression
                {
                    Operand: IdentifierExpression operand,
                } unary }])
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        return (unary.Operator is "!" or "~" or "-") &&
            declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            declaration.Parameters.Count == 1 &&
            declaration.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            declaration.Parameters[0].Name == operand.Identifier.ToString() &&
            (unary.Operator == "!"
                ? declaration.ReturnType is BoolType
                : declaration.ReturnType is GenericType returnType &&
                    returnType.Name == typeParameter);
    }

    private static bool IsGenericConceptMethodReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length == 0 ||
            declaration.Body is not
                [ReturnStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression
                        {
                            Target: IdentifierExpression receiver,
                        } member,
                        Arguments: _,
                    },
                }])
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        return declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            declaration.Parameters.Count >= 1 &&
            declaration.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            declaration.Parameters[0].Name == receiver.Identifier.ToString() &&
            member.MemberName.Length > 0 && declaration.ReturnType is not VoidType;
    }

    private static string GetConceptMethodBindingKey(
        InvocationExpression invocation,
        FunctionDeclaration requirement,
        FunctionSymbol instance) => ConceptDeclaration.GetMethodBindingKey(
            requirement,
            invocation.ConceptMethodTypeArguments.Select(type =>
                GenericTypeSubstitution.Substitute(type, instance.GenericTypeArguments))
                .ToArray());

    private static bool IsGenericConceptMethodCallBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length == 0 ||
            declaration.ReturnType is not VoidType ||
            declaration.Body is not { Count: > 0 } body ||
            body.Any(statement => statement is not ExpressionStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression
                        {
                            Target: IdentifierExpression,
                        },
                    },
                }))
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        var receiverMatches = body.Cast<ExpressionStatement>()
            .Select(statement => (InvocationExpression)statement.Expression)
            .All(invocation => invocation.Target is MemberAccessExpression
                { Target: IdentifierExpression receiver } &&
                declaration.Parameters.Count > 0 &&
                declaration.Parameters[0].Name == receiver.Identifier.ToString());
        return declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            declaration.Parameters.Count >= 1 &&
            declaration.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            receiverMatches;
    }

    private static bool IsGenericConceptPropertyReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length != 1 ||
            declaration.Body is not
                [ReturnStatement
                {
                    Expression: { } expression,
                }])
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        var receiverName = declaration.Parameters.FirstOrDefault()?.Name;
        return declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            declaration.Parameters.Count >= 1 &&
            declaration.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            declaration.Parameters.Skip(1).All(parameter =>
                !ContainsGenericParameter(
                    parameter.ParameterType, declaration.GenericTypeNames)) &&
            receiverName is not null &&
            IsGenericConceptPropertyExpression(expression, receiverName) &&
            ContainsGenericConceptPropertyAccess(expression, receiverName) &&
            declaration.ReturnType is not VoidType;
    }

    private static bool IsGenericConceptPropertyExpression(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            MemberName.Length: > 0,
        } => receiver.Identifier.ToString() == receiverName,
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
                MemberName.Length: > 0,
            },
        } invocation => receiver.Identifier.ToString() == receiverName &&
            invocation.Arguments.All(argument =>
                IsGenericConceptMethodArgumentExpression(argument, receiverName)),
        LiteralExpression => true,
        IdentifierExpression => true,
        ConditionalExpression conditional =>
            IsGenericConceptPropertyExpression(conditional.Condition, receiverName) &&
            IsGenericConceptPropertyExpression(conditional.WhenTrue, receiverName) &&
            IsGenericConceptPropertyExpression(conditional.WhenFalse, receiverName),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary => IsGenericConceptPropertyExpression(binary.Left, receiverName) &&
            IsGenericConceptPropertyExpression(binary.Right, receiverName),
        _ => false,
    };

    private static bool IsGenericConceptMethodArgumentExpression(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        LiteralExpression or IdentifierExpression => true,
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            MemberName.Length: > 0,
        } => receiver.Identifier.ToString() == receiverName,
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
                MemberName.Length: > 0,
            },
        } invocation => receiver.Identifier.ToString() == receiverName &&
            invocation.Arguments.All(argument =>
                IsGenericConceptMethodArgumentExpression(argument, receiverName)),
        UnaryExpression
        {
            OperatorSymbol: null,
            Postfix: false,
            Operator: "+" or "-" or "~" or "!",
        } unary => IsGenericConceptMethodArgumentExpression(unary.Operand, receiverName),
        BinaryExpression
        {
            OperatorSymbol: null,
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
        } binary => IsGenericConceptMethodArgumentExpression(binary.Left, receiverName) &&
            IsGenericConceptMethodArgumentExpression(binary.Right, receiverName),
        ConditionalExpression conditional =>
            IsGenericConceptMethodArgumentExpression(conditional.Condition, receiverName) &&
            IsGenericConceptMethodArgumentExpression(conditional.WhenTrue, receiverName) &&
            IsGenericConceptMethodArgumentExpression(conditional.WhenFalse, receiverName),
        _ => false,
    };

    private static bool ContainsGenericConceptPropertyAccess(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        MemberAccessExpression { Target: IdentifierExpression receiver } =>
            receiver.Identifier.ToString() == receiverName,
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
        } invocation => receiver.Identifier.ToString() == receiverName ||
            invocation.Arguments.Any(argument =>
                ContainsGenericConceptPropertyAccess(argument, receiverName)),
        ConditionalExpression conditional =>
            ContainsGenericConceptPropertyAccess(conditional.Condition, receiverName) ||
            ContainsGenericConceptPropertyAccess(conditional.WhenTrue, receiverName) ||
            ContainsGenericConceptPropertyAccess(conditional.WhenFalse, receiverName),
        BinaryExpression binary =>
            ContainsGenericConceptPropertyAccess(binary.Left, receiverName) ||
            ContainsGenericConceptPropertyAccess(binary.Right, receiverName),
        _ => false,
    };

    private static string ToCGenericConceptPropertyExpression(
        ExpressionBase expression,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName) => expression switch
    {
        MemberAccessExpression
        {
            Target: IdentifierExpression receiverExpression,
        } member => ToCGenericConceptPropertyAccess(
            member, receiverExpression, instance, declaration),
        InvocationExpression invocation => ToCGenericConceptMethodInvocation(
            invocation, instance, declaration, moduleName),
        AssignmentExpression
        {
            Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiverExpression,
                TargetProperty: not null,
            } member,
        } assignment => ToCGenericConceptPropertyAssignment(
            assignment, member, receiverExpression, instance, declaration, moduleName),
        ConditionalExpression conditional =>
            $"({ToCGenericConceptPropertyExpression(conditional.Condition, instance, declaration, moduleName)} ? " +
            $"{ToCGenericConceptPropertyExpression(conditional.WhenTrue, instance, declaration, moduleName)} : " +
            $"{ToCGenericConceptPropertyExpression(conditional.WhenFalse, instance, declaration, moduleName)})",
        BinaryExpression binary => ToCGenericConceptBinaryExpression(
            binary, instance, declaration, moduleName),
        UnaryExpression unary => ToCGenericConceptUnaryExpression(
            unary, instance, declaration, moduleName),
        _ => ToCExpression(expression, declaration, moduleName),
    };

    private static string ToCGenericConceptBinaryExpression(
        BinaryExpression binary,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        if (binary.OperatorSymbol is null &&
            IsClassConceptTypeParameter(binary.Left, declaration) &&
            binary.Left is IdentifierExpression left &&
            instance.ConceptOperatorBindings.TryGetValue(
                binary.Operator, out var operation))
        {
            var receiverType = GetConceptReceiverType(left, instance, declaration);
            var receiver = receiverType is NamedType { ClassType: ClassType.Struct }
                ? $"&{left.Identifier.Parts[^1]}"
                : left.Identifier.Parts[^1];
            var argument = ToCExpressionAsType(
                binary.Right,
                operation.ParameterTypes.Single(),
                declaration,
                moduleName);
            return $"{ToCIdentifier(operation)}({receiver}, {argument})";
        }

        return $"({ToCGenericConceptPropertyExpression(binary.Left, instance, declaration, moduleName)} " +
            $"{binary.Operator} " +
            $"{ToCGenericConceptPropertyExpression(binary.Right, instance, declaration, moduleName)})";
    }

    private static string ToCGenericConceptUnaryExpression(
        UnaryExpression unary,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        if (unary.OperatorSymbol is null && unary.Operand is IdentifierExpression operand &&
            IsClassConceptTypeParameter(unary.Operand, declaration) &&
            instance.ConceptOperatorBindings.TryGetValue(
                unary.Operator, out var operation))
        {
            var receiverType = GetConceptReceiverType(operand, instance, declaration);
            var receiver = receiverType is NamedType { ClassType: ClassType.Struct }
                ? $"&{operand.Identifier.Parts[^1]}"
                : operand.Identifier.Parts[^1];
            return $"{ToCIdentifier(operation)}({receiver})";
        }
        return ToCExpression(unary, declaration, moduleName);
    }

    private static bool IsClassConceptTypeParameter(
        ExpressionBase expression,
        FunctionDeclaration declaration) =>
        expression.InferredType is GenericType generic &&
        (declaration.ParentClassDeclaration?.ConceptConstraints.Any(constraint =>
            constraint.ParameterName == generic.Name) == true ||
         declaration.ConceptConstraints.Any(constraint =>
            constraint.ParameterName == generic.Name));

    private static string ToCGenericConceptMethodInvocation(
        InvocationExpression invocation,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        var member = invocation.Target as MemberAccessExpression ??
            throw new InternalCompilerException(
                "A concept method expression must target a member.");
        var receiverExpression = member.Target as IdentifierExpression ??
            throw new InternalCompilerException(
                "A concept method expression must use the constrained receiver.");
        var requirement = invocation.TargetSymbol?.Declaration ??
            throw new InternalCompilerException(
                $"Missing concept method declaration for '{member.MemberName}'.");
        var bindingKey = GetConceptMethodBindingKey(invocation, requirement, instance);
        if (!instance.ConceptOperatorBindings.TryGetValue(bindingKey, out var methodSymbol))
        {
            throw new InternalCompilerException(
                $"Missing concept method binding for '{member.MemberName}'.");
        }

        var receiverType = GetConceptReceiverType(
            receiverExpression, instance, declaration);
        var receiver = receiverType is
            NamedType { ClassType: ClassType.Struct }
            ? $"&{receiverExpression.Identifier.Parts[0]}"
            : receiverExpression.Identifier.Parts[0];
        var receiverName = receiverExpression.Identifier.ToString();
        var arguments = invocation.Arguments.Zip(methodSymbol.ParameterTypes)
            .Select(pair => ContainsGenericConceptPropertyAccess(
                    pair.First, receiverName)
                ? ToCGenericConceptPropertyExpression(
                    pair.First, instance, declaration, moduleName)
                : ToCExpressionAsType(
                    pair.First, pair.Second, declaration, moduleName));
        var argumentList = string.Join(", ", new[] { receiver }.Concat(arguments));
        return $"{ToCIdentifier(methodSymbol)}({argumentList})";
    }

    private static string ToCGenericConceptPropertyAccess(
        MemberAccessExpression member,
        IdentifierExpression receiverExpression,
        FunctionSymbol instance,
        FunctionDeclaration declaration)
    {
        var bindingKey = $"property:{member.MemberName}";
        if (!instance.ConceptOperatorBindings.TryGetValue(bindingKey, out var getterSymbol))
        {
            throw new InternalCompilerException(
                $"Missing concept property binding for '{member.MemberName}'.");
        }

        var receiverType = GetConceptReceiverType(
            receiverExpression, instance, declaration);
        var receiver = receiverType is
            NamedType { ClassType: ClassType.Struct }
            ? $"&{receiverExpression.Identifier.Parts[0]}"
            : receiverExpression.Identifier.Parts[0];
        return $"{ToCIdentifier(getterSymbol)}({receiver})";
    }

    private static string ToCGenericConceptPropertyAssignment(
        AssignmentExpression assignment,
        MemberAccessExpression member,
        IdentifierExpression receiverExpression,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        var bindingKey = $"property:{member.MemberName}:set";
        if (!instance.ConceptOperatorBindings.TryGetValue(bindingKey, out var setterSymbol))
        {
            throw new InternalCompilerException(
                $"Missing concept property setter binding for '{member.MemberName}'.");
        }

        var receiverType = GetConceptReceiverType(
            receiverExpression, instance, declaration);
        var receiver = receiverType is NamedType { ClassType: ClassType.Struct }
            ? $"&{receiverExpression.Identifier.Parts[0]}"
            : receiverExpression.Identifier.Parts[0];
        var value = ToCGenericConceptPropertyExpression(
            assignment.Value, instance, declaration, moduleName);
        if (assignment.Operator != "=")
        {
            var getterKey = $"property:{member.MemberName}";
            if (!instance.ConceptOperatorBindings.TryGetValue(getterKey, out var getterSymbol))
            {
                throw new InternalCompilerException(
                    $"Missing concept property getter binding for '{member.MemberName}'.");
            }
            var currentValue = ToCGenericConceptPropertyAccess(
                member, receiverExpression, instance, declaration);
            value = $"({currentValue} {assignment.Operator[..^1]} {value})";
        }
        return $"{ToCIdentifier(setterSymbol)}({receiver}, {value})";
    }

    private static TypeBase GetConceptReceiverType(
        IdentifierExpression receiver,
        FunctionSymbol instance,
        FunctionDeclaration declaration)
    {
        var name = receiver.Identifier.ToString();
        var index = declaration.Parameters.Select((parameter, index) =>
                (parameter, index))
            .Where(item => item.parameter.Name == name)
            .Select(item => item.index)
            .SingleOrDefault(-1);
        return index >= 0 && index < instance.ParameterTypes.Count
            ? instance.ParameterTypes[index]
            : instance.ParameterTypes[0];
    }

    private static bool IsGenericConceptPropertyAssignmentBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length != 1 ||
            declaration.ReturnType is not VoidType ||
            declaration.Body is not { Count: > 0 } body ||
            body.Any(statement => statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                        Target: MemberAccessExpression
                        {
                            Target: IdentifierExpression,
                        },
                    },
                }))
        {
            return false;
        }

        var assignments = body.Cast<ExpressionStatement>()
            .Select(statement => (AssignmentExpression)statement.Expression).ToArray();
        if (assignments.Any(assignment =>
            assignment.Target is not MemberAccessExpression
                { Target: IdentifierExpression receiver } member ||
            member.MemberName.Length == 0 ||
            declaration.Parameters.Count == 0 ||
            declaration.Parameters[0].Name != receiver.Identifier.ToString()))
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        return declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            declaration.Parameters.Count >= 1 &&
            declaration.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            assignments.Length > 0;
    }

    private static bool IsGenericConceptMixedMutationBody(
        FunctionDeclaration declaration)
    {
        if (declaration.GenericTypeNames.Length != 1 ||
            declaration.ReturnType is not VoidType ||
            declaration.Body is not { Count: > 0 } body)
        {
            return false;
        }

        var typeParameter = declaration.GenericTypeNames[0];
        var receiverName = declaration.Parameters.FirstOrDefault()?.Name;
        if (receiverName is null ||
            !declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) ||
            declaration.Parameters[0].ParameterType is not GenericType parameterType ||
            parameterType.Name != typeParameter)
        {
            return false;
        }

        bool IsMutationStatement(
            StatementBase statement,
            bool insideLoop = false,
            bool insideSwitch = false) =>
            statement switch
        {
            ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: MemberAccessExpression
                    {
                        Target: IdentifierExpression receiver,
                    } member,
                } assignment,
            } => receiver.Identifier.ToString() == receiverName &&
                member.MemberName.Length > 0 &&
                (!ContainsGenericConceptPropertyAccess(assignment.Value, receiverName) ||
                 IsGenericConceptPropertyExpression(assignment.Value, receiverName)),
            ExpressionStatement
            {
                Expression: InvocationExpression
                {
                    Target: MemberAccessExpression
                    {
                        Target: IdentifierExpression receiver,
                    },
                },
            } => receiver.Identifier.ToString() == receiverName,
            ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression,
                } assignment,
            } => IsGenericConceptPropertyExpression(
                assignment.Value, receiverName),
            LocalVariableDeclarationStatement local =>
                local.DeclaredType is not GenericType &&
                local.Declarators.Count > 0 &&
                local.Declarators.All(declarator =>
                    declarator.Initializer is { } initializer &&
                    IsGenericConceptPropertyExpression(initializer, receiverName)),
            BlockStatement block => block.Statements.All(
                nested => IsMutationStatement(nested, insideLoop, insideSwitch)),
            IfStatement conditional =>
                IsGenericConceptMutationCondition(conditional.Condition, declaration) &&
                IsMutationStatement(
                    conditional.ThenStatement, insideLoop, insideSwitch) &&
                (conditional.ElseStatement is null ||
                 IsMutationStatement(
                    conditional.ElseStatement, insideLoop, insideSwitch)),
            WhileStatement loop =>
                IsGenericConceptMutationCondition(loop.Condition, declaration) &&
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch),
            DoWhileStatement loop =>
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch) &&
                IsGenericConceptMutationCondition(loop.Condition, declaration),
            ForStatement loop =>
                loop.DeclarationInitializer is { } initializer &&
                loop.InitializerExpressions.Count == 0 &&
                loop.Condition is { } condition &&
                loop.Iterators.Count > 0 &&
                loop.Iterators.All(IsGenericConceptMutationLocalAssignment) &&
                IsMutationStatement(initializer, insideLoop, insideSwitch) &&
                IsGenericConceptMutationCondition(condition, declaration) &&
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch),
            ForeachStatement loop =>
                IsGenericConceptMutationForeachCollection(
                    loop.Collection, declaration) &&
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch),
            SwitchStatement switchStatement =>
                IsGenericConceptMutationSwitchSelector(
                    switchStatement.Expression, declaration) &&
                switchStatement.Sections.Count > 0 &&
                switchStatement.Sections.All(section =>
                    section.Labels.Count > 0 &&
                    section.Labels.All(label =>
                        label.Filter is null &&
                        (label.IsDefault ||
                         label.Value is LiteralExpression)) &&
                    section.Statements.All(nested =>
                        IsMutationStatement(nested, insideLoop, insideSwitch: true))),
            BreakStatement => insideLoop || insideSwitch,
            ContinueStatement => insideLoop,
            ReturnStatement { Expression: null } => true,
            _ => false,
        };

        return body.All(statement => IsMutationStatement(statement));

        bool IsGenericConceptMutationLocalAssignment(ExpressionBase expression) =>
            expression is AssignmentExpression
            {
                Operator: "=",
                Target: IdentifierExpression,
            } assignment &&
            IsGenericConceptPropertyExpression(
                assignment.Value, declaration.Parameters[0].Name);

        static bool IsGenericConceptMutationSwitchSelector(
            ExpressionBase expression,
            FunctionDeclaration function) =>
            expression is IdentifierExpression identifier &&
            function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                (parameter.ParameterType is IntTypeBase or BoolType or CharType ||
                 parameter.ParameterType is NamedType { ClassType: ClassType.Enum }));

        static bool IsGenericConceptMutationForeachCollection(
            ExpressionBase expression,
            FunctionDeclaration function)
        {
            static bool IsSupportedElementType(
                TypeBase elementType,
                IReadOnlyList<string> genericTypeNames) =>
                !ContainsGenericParameter(elementType, genericTypeNames) ||
                elementType is GenericType genericElement &&
                    genericElement.Name == genericTypeNames[0] ||
                elementType is ArrayType nestedArray &&
                    IsSupportedElementType(nestedArray.ElementType, genericTypeNames);

            return expression is IdentifierExpression identifier &&
                function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                parameter.ParameterType is ArrayType array &&
                IsSupportedElementType(array.ElementType, function.GenericTypeNames));
        }
    }

    private static bool IsGenericConceptMutationCondition(
        ExpressionBase expression,
        FunctionDeclaration declaration)
    {
        var localName = expression is IdentifierExpression identifierExpression
            ? identifierExpression.Identifier.ToString()
            : string.Empty;

        bool DeclaresBooleanLocal(StatementBase statement) => statement switch
        {
            LocalVariableDeclarationStatement
            {
                DeclaredType: BoolType,
            } local => local.Declarators.Any(declarator =>
                declarator.Name == localName),
            BlockStatement block => block.Statements.Any(DeclaresBooleanLocal),
            IfStatement conditional =>
                DeclaresBooleanLocal(conditional.ThenStatement) ||
                conditional.ElseStatement is { } elseStatement &&
                DeclaresBooleanLocal(elseStatement),
            WhileStatement loop => DeclaresBooleanLocal(loop.Body),
            DoWhileStatement loop => DeclaresBooleanLocal(loop.Body),
            ForStatement loop =>
                loop.DeclarationInitializer is { } initializer &&
                DeclaresBooleanLocal(initializer) ||
                DeclaresBooleanLocal(loop.Body),
            _ => false,
        };

        return expression switch
        {
            LiteralExpression { SourceText: "true" or "false" } => true,
            IdentifierExpression identifier =>
                declaration.Parameters.Any(parameter =>
                    parameter.Name == identifier.Identifier.ToString() &&
                    parameter.ParameterType is BoolType) ||
                declaration.Body?.Any(DeclaresBooleanLocal) == true,
            UnaryExpression { Operator: "!", OperatorSymbol: null } unary =>
                IsGenericConceptMutationCondition(unary.Operand, declaration),
            BinaryExpression
            {
                Operator: "&&" or "||",
                OperatorSymbol: null,
            } binary =>
                IsGenericConceptMutationCondition(binary.Left, declaration) &&
                IsGenericConceptMutationCondition(binary.Right, declaration),
            BinaryExpression
            {
                Operator: "==" or "!=" or "<" or "<=" or ">" or ">=",
                OperatorSymbol: null,
            } binary =>
                IsGenericConceptPropertyExpression(
                    binary.Left, declaration.Parameters[0].Name) &&
                IsGenericConceptPropertyExpression(
                    binary.Right, declaration.Parameters[0].Name),
            _ => false,
        };
    }

    private static void WriteGenericConceptMutationStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        switch (statement)
        {
            case ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Target: MemberAccessExpression,
                } assignment,
            }:
            {
                var member = (MemberAccessExpression)assignment.Target;
                var receiverExpression = (IdentifierExpression)member.Target;
                var propertyBindingKey = $"property:{member.MemberName}:set";
                if (!instance.ConceptOperatorBindings.TryGetValue(
                    propertyBindingKey, out var setterSymbol))
                {
                    throw new InternalCompilerException(
                        $"Missing concept property setter binding for '{member.MemberName}'.");
                }
                var receiver = instance.ParameterTypes[0] is
                    NamedType { ClassType: ClassType.Struct }
                    ? $"&{receiverExpression.Identifier.Parts[0]}"
                    : receiverExpression.Identifier.Parts[0];
                var value = ContainsGenericConceptPropertyAccess(
                        assignment.Value, receiverExpression.Identifier.Parts[0])
                    ? ToCGenericConceptPropertyExpression(
                        assignment.Value, instance, declaration, moduleName)
                    : ToCExpressionAsType(
                        assignment.Value,
                        setterSymbol.ParameterTypes.Single(),
                        declaration,
                        moduleName);
                writer.WriteLine($"{ToCIdentifier(setterSymbol)}({receiver}, {value});");
                return;
            }
            case ExpressionStatement { Expression: InvocationExpression invocation }:
            {
                var methodMember = (MemberAccessExpression)invocation.Target;
                var methodReceiver = (IdentifierExpression)methodMember.Target;
                var requirement = invocation.TargetSymbol?.Declaration ??
                    throw new InternalCompilerException(
                        $"Missing concept method declaration for '{methodMember.MemberName}'.");
                var bindingKey = GetConceptMethodBindingKey(
                    invocation, requirement, instance);
                if (!instance.ConceptOperatorBindings.TryGetValue(
                    bindingKey, out var methodSymbol))
                {
                    throw new InternalCompilerException(
                        $"Missing concept method binding for '{methodMember.MemberName}'.");
                }
                var receiverArgument = instance.ParameterTypes[0] is
                    NamedType { ClassType: ClassType.Struct }
                    ? $"&{methodReceiver.Identifier.Parts[0]}"
                    : methodReceiver.Identifier.Parts[0];
                var arguments = invocation.Arguments.Zip(methodSymbol.ParameterTypes)
                    .Select(pair => ToCExpressionAsType(
                        pair.First, pair.Second, declaration, moduleName));
                var argumentList = string.Join(", ", arguments);
                var argumentSuffix = argumentList.Length == 0
                    ? string.Empty
                    : $", {argumentList}";
                writer.WriteLine(
                    $"{ToCIdentifier(methodSymbol)}({receiverArgument}{argumentSuffix});");
                return;
            }
            case ExpressionStatement
            {
                Expression: UnaryExpression
                {
                    OperatorSymbol: null,
                    Operand: IdentifierExpression,
                } unary,
            }:
                writer.WriteLine(
                    $"{ToCExpression(unary, declaration, moduleName)};");
                return;
            case ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: ArrayAccessExpression access,
                } assignment,
            }:
                writer.WriteLine(
                    $"{ToCGenericArrayReturnAccess(
                        access, instance.GenericTypeArguments, declaration, moduleName)} = " +
                    $"{ToCExpression(assignment.Value, declaration, moduleName)};");
                return;
            case ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression,
                } assignment,
            }:
            {
                var receiverName = declaration.Parameters[0].Name;
                var value = ContainsGenericConceptPropertyAccess(
                        assignment.Value, receiverName)
                    ? ToCGenericConceptPropertyExpression(
                        assignment.Value, instance, declaration, moduleName)
                    : ToCExpression(assignment.Value, declaration, moduleName);
                writer.WriteLine(
                    $"{ToCExpression(assignment.Target, declaration, moduleName)} = {value};");
                return;
            }
            case LocalVariableDeclarationStatement localDeclaration:
                foreach (var declarator in localDeclaration.Declarators)
                {
                    var type = declarator.Type ?? throw new InternalCompilerException(
                        $"Local '{declarator.Name}' is not bound.");
                    var symbol = declarator.Symbol ?? throw new InternalCompilerException(
                        $"Local '{declarator.Name}' has no bound symbol.");
                    if (symbol.IsCaptured)
                    {
                        throw new InternalCompilerException(
                            $"Concept mutation local '{declarator.Name}' cannot be captured.");
                    }

                    var initializer = declarator.Initializer ?? throw new InternalCompilerException(
                        $"Concept mutation local '{declarator.Name}' has no initializer.");
                    var value = ContainsGenericConceptPropertyAccess(
                            initializer, declaration.Parameters[0].Name)
                        ? ToCGenericConceptPropertyExpression(
                            initializer, instance, declaration, moduleName)
                        : ToCExpressionAsType(initializer, type, declaration, moduleName);
                    writer.WriteLine($"{type.ToCIdentifier(false)} {declarator.Name} = {value};");
                }
                return;
            case BlockStatement block:
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var nested in block.Statements)
                {
                    WriteGenericConceptMutationStatement(
                        writer, nested, instance, declaration, moduleName);
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                return;
            case IfStatement conditional:
                writer.WriteLine(
                    $"if ({ToCExpression(conditional.Condition, declaration, moduleName)})");
                WriteGenericConceptMutationControlled(
                    writer, conditional.ThenStatement, instance, declaration, moduleName);
                if (conditional.ElseStatement is { } elseStatement)
                {
                    writer.WriteLine("else");
                    WriteGenericConceptMutationControlled(
                        writer, elseStatement, instance, declaration, moduleName);
                }
                return;
            case WhileStatement loop:
            {
                var receiverName = declaration.Parameters[0].Name;
                var condition = ContainsGenericConceptPropertyAccess(
                        loop.Condition, receiverName)
                    ? ToCGenericConceptPropertyExpression(
                        loop.Condition, instance, declaration, moduleName)
                    : ToCExpression(loop.Condition, declaration, moduleName);
                writer.WriteLine($"while ({condition})");
                WriteGenericConceptMutationControlled(
                    writer, loop.Body, instance, declaration, moduleName);
                return;
            }
            case ForStatement loop:
            {
                var receiverName = declaration.Parameters[0].Name;
                var initializer = loop.DeclarationInitializer is { } declarationInitializer
                    ? ToCForDeclaration(declarationInitializer, declaration, moduleName)
                    : string.Join(", ", loop.InitializerExpressions.Select(expression =>
                        ToCExpression(expression, declaration, moduleName)));
                var condition = loop.Condition is { } loopCondition
                    ? ContainsGenericConceptPropertyAccess(loopCondition, receiverName)
                        ? ToCGenericConceptPropertyExpression(
                            loopCondition, instance, declaration, moduleName)
                        : ToCExpression(loopCondition, declaration, moduleName)
                    : string.Empty;
                var iterators = string.Join(", ", loop.Iterators.Select(expression =>
                    ToCExpression(expression, declaration, moduleName)));
                writer.WriteLine($"for ({initializer}; {condition}; {iterators})");
                WriteGenericConceptMutationControlled(
                    writer, loop.Body, instance, declaration, moduleName);
                return;
            }
            case DoWhileStatement loop:
            {
                var receiverName = declaration.Parameters[0].Name;
                var condition = ContainsGenericConceptPropertyAccess(
                        loop.Condition, receiverName)
                    ? ToCGenericConceptPropertyExpression(
                        loop.Condition, instance, declaration, moduleName)
                    : ToCExpression(loop.Condition, declaration, moduleName);
                writer.WriteLine("do");
                WriteGenericConceptMutationControlled(
                    writer, loop.Body, instance, declaration, moduleName);
                writer.WriteLine($"while ({condition});");
                return;
            }
            case BreakStatement:
                writer.WriteLine("break;");
                return;
            case ContinueStatement:
                writer.WriteLine("continue;");
                return;
            case ReturnStatement { Expression: null }:
                writer.WriteLine("return;");
                return;
            case SwitchStatement switchStatement:
                writer.WriteLine(
                    $"switch ({ToCExpression(switchStatement.Expression, declaration, moduleName)})");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var label in section.Labels)
                    {
                        writer.WriteLine(label.IsDefault
                            ? "default:"
                            : $"case {ToCExpression(label.Value!, declaration, moduleName)}:");
                    }
                    writer.WriteLine("{");
                    writer.IncreaseIndent();
                    foreach (var nested in section.Statements)
                    {
                        WriteGenericConceptMutationStatement(
                            writer, nested, instance, declaration, moduleName);
                    }
                    writer.DecreaseIndent();
                    writer.WriteLine("}");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                return;
            case ForeachStatement foreachStatement:
            {
                var variableType = foreachStatement.VariableType is { } openVariableType
                    ? GenericTypeSubstitution.Substitute(
                        openVariableType, instance.GenericTypeArguments)
                    :
                    throw new InternalCompilerException(
                        $"Foreach variable '{foreachStatement.VariableName}' is not bound.");
                if (foreachStatement.VariableSymbol?.IsCaptured == true)
                {
                    throw new InternalCompilerException(
                        "Captured foreach variables are not supported in concept mutation bodies.");
                }

                var collectionName =
                    $"__cx_concept_foreach_collection_{foreachStatement.VariableName}";
                var indexName =
                    $"__cx_concept_foreach_index_{foreachStatement.VariableName}";
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"struct CX_ID_4(cxcore, System, Collections, Array)* {collectionName} = " +
                    $"{ToCExpression(foreachStatement.Collection, declaration, moduleName)};");
                writer.WriteLine(
                    $"for (cx_uint {indexName} = 0; " +
                    $"{indexName} < {collectionName}->_length; {indexName}++)");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"{variableType.ToCIdentifier(false)} {foreachStatement.VariableName} = " +
                    $"(({variableType.ToCIdentifier(false)}*){collectionName}->_data)[{indexName}];");
                WriteGenericConceptMutationStatement(
                    writer, foreachStatement.Body, instance, declaration, moduleName);
                writer.DecreaseIndent();
                writer.WriteLine("}");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                return;
            }
            default:
                throw new InternalCompilerException(
                    $"Unsupported statement in generic concept mutation: {statement.GetType().Name}.");
        }
    }

    private static void WriteGenericConceptMutationControlled(
        IndentingWriter writer,
        StatementBase statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        if (statement is BlockStatement)
        {
            WriteGenericConceptMutationStatement(
                writer, statement, instance, declaration, moduleName);
            return;
        }

        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteGenericConceptMutationStatement(
            writer, statement, instance, declaration, moduleName);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static bool IsGenericReferenceNullComparisonBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not
                [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        bool IsGenericParameter(ExpressionBase expression) =>
            expression is IdentifierExpression identifier &&
            declaration.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                ContainsGenericParameter(
                    parameter.ParameterType, declaration.GenericTypeNames));

        static bool IsNull(ExpressionBase expression) =>
            expression is LiteralExpression { SourceText: "null" };

        bool IsNullComparison(ExpressionBase candidate) =>
            candidate is BinaryExpression
            {
                Operator: "==" or "!=",
                OperatorSymbol: null,
            } comparison &&
            (IsGenericParameter(comparison.Left) && IsNull(comparison.Right) ||
             IsNull(comparison.Left) && IsGenericParameter(comparison.Right));

        bool IsGenericArrayLength(ExpressionBase candidate) =>
            candidate is MemberAccessExpression
            {
                Target: { } target,
                MemberName: "Length",
            } && IsGenericArrayLengthTarget(target, declaration);

        bool IsUnsignedOperand(ExpressionBase candidate) =>
            candidate is LiteralExpression literal &&
                (literal.SourceText.EndsWith('u') ||
                 uint.TryParse(literal.SourceText, out _)) ||
            candidate is IdentifierExpression identifier &&
                declaration.Parameters.Any(parameter =>
                    parameter.Name == identifier.Identifier.ToString() &&
                    parameter.ParameterType is UIntType);

        bool IsGenericArrayLengthComparison(ExpressionBase candidate) =>
            candidate is BinaryExpression
            {
                Operator: "==" or "!=" or "<" or "<=" or ">" or ">=",
                OperatorSymbol: null,
            } comparison &&
            (IsGenericArrayLength(comparison.Left) &&
                IsUnsignedOperand(comparison.Right) ||
             IsUnsignedOperand(comparison.Left) &&
                IsGenericArrayLength(comparison.Right));

        bool IsSupportedBooleanExpression(ExpressionBase candidate) => candidate switch
        {
            UnaryExpression { Operator: "!", OperatorSymbol: null } unary =>
                IsSupportedBooleanExpression(unary.Operand),
            BinaryExpression
            {
                Operator: "&&" or "||",
                OperatorSymbol: null,
            } logical => IsSupportedBooleanExpression(logical.Left) &&
                IsSupportedBooleanExpression(logical.Right),
            ConditionalExpression conditional =>
                IsSupportedBooleanExpression(conditional.Condition) &&
                IsSupportedBooleanExpression(conditional.WhenTrue) &&
                IsSupportedBooleanExpression(conditional.WhenFalse),
            _ => IsNullComparison(candidate) ||
                IsGenericArrayLengthComparison(candidate),
        };

        return IsSupportedBooleanExpression(expression);
    }

    private static bool IsGenericNullCoalescingReturnBody(
        FunctionDeclaration declaration)
    {
        if (!ContainsGenericParameter(declaration.ReturnType, declaration.GenericTypeNames) ||
            declaration.Body is not
                [ReturnStatement { Expression: NullCoalescingExpression coalescing }])
        {
            return false;
        }

        bool IsMatchingParameter(ExpressionBase expression) =>
            expression is IdentifierExpression identifier &&
            declaration.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                IsSameGenericTypePattern(declaration.ReturnType, parameter.ParameterType));

        bool IsSupportedOperand(ExpressionBase expression) => expression switch
        {
            LiteralExpression { SourceText: "null" } => true,
            NullCoalescingExpression nested =>
                IsSupportedOperand(nested.Left) && IsSupportedOperand(nested.Right),
            _ => IsMatchingParameter(expression),
        };

        bool ContainsMatchingParameter(ExpressionBase expression) => expression switch
        {
            NullCoalescingExpression nested =>
                ContainsMatchingParameter(nested.Left) ||
                ContainsMatchingParameter(nested.Right),
            _ => IsMatchingParameter(expression),
        };

        return IsSupportedOperand(coalescing) && ContainsMatchingParameter(coalescing);
    }

    private static bool IsGenericConstrainedMemberReturnBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not
            [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        return ContainsGenericConstrainedMember(expression, declaration) &&
            IsGenericConstrainedMemberReturnExpression(expression, declaration);
    }

    private static bool IsGenericConstrainedMemberLocalReturnBody(
        FunctionDeclaration declaration)
    {
        if (declaration.Body is not
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: var localType,
                    Declarators:
                    [
                        { Name: var localName, Initializer: { } initializer },
                    ],
                },
                ReturnStatement
                {
                    Expression: IdentifierExpression returned,
                },
            ] || returned.Identifier.ToString() != localName ||
            localType is AutoType ||
            ContainsGenericParameter(localType, declaration.GenericTypeNames))
        {
            return false;
        }

        return ContainsGenericConstrainedMember(initializer, declaration) &&
            IsGenericConstrainedMemberReturnExpression(initializer, declaration);
    }

    private static bool IsGenericConstrainedReceiver(
        ExpressionBase expression,
        FunctionDeclaration declaration) =>
        expression is IdentifierExpression receiver &&
        declaration.Parameters.Any(parameter =>
            parameter.Name == receiver.Identifier.ToString() &&
            parameter.ParameterType is GenericType genericParameter &&
            declaration.GenericTypeNames.Contains(genericParameter.Name));

    private static bool ContainsGenericConstrainedMember(
        ExpressionBase expression,
        FunctionDeclaration declaration) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
        } => IsGenericConstrainedReceiver(receiver, declaration),
        MemberAccessExpression { Target: var receiver } =>
            IsGenericConstrainedReceiver(receiver, declaration),
        ArrayAccessExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
        } => IsGenericConstrainedReceiver(receiver, declaration),
        BinaryExpression binary =>
            ContainsGenericConstrainedMember(binary.Left, declaration) ||
            ContainsGenericConstrainedMember(binary.Right, declaration),
        ConditionalExpression conditional =>
            ContainsGenericConstrainedMember(conditional.Condition, declaration) ||
            ContainsGenericConstrainedMember(conditional.WhenTrue, declaration) ||
            ContainsGenericConstrainedMember(conditional.WhenFalse, declaration),
        _ => false,
    };

    private static bool IsGenericConstrainedMemberReturnExpression(
        ExpressionBase expression,
        FunctionDeclaration declaration) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
            Arguments: var arguments,
        } => IsGenericConstrainedReceiver(receiver, declaration) &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, declaration)),
        MemberAccessExpression { Target: var receiver } =>
            IsGenericConstrainedReceiver(receiver, declaration),
        ArrayAccessExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
            Indices: var indices,
        } => IsGenericConstrainedReceiver(receiver, declaration) &&
            indices.All(index =>
                IsSupportedGenericConstrainedValueExpression(index, declaration)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsGenericConstrainedMemberReturnExpression(binary.Left, declaration) &&
            IsGenericConstrainedMemberReturnExpression(binary.Right, declaration),
        ConditionalExpression conditional =>
            IsGenericConstrainedMemberReturnExpression(conditional.Condition, declaration) &&
            IsGenericConstrainedMemberReturnExpression(conditional.WhenTrue, declaration) &&
            IsGenericConstrainedMemberReturnExpression(conditional.WhenFalse, declaration),
        _ => false,
    };

    private static string? GetGenericConstrainedMemberReceiverName(ExpressionBase expression) =>
        expression switch
        {
            InvocationExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
            } => receiver.Identifier.ToString(),
            MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
                TargetProperty: not null,
            } => receiver.Identifier.ToString(),
            ArrayAccessExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
                TargetProperty: not null,
            } => receiver.Identifier.ToString(),
            BinaryExpression binary =>
                GetGenericConstrainedMemberReceiverName(binary.Left) ??
                GetGenericConstrainedMemberReceiverName(binary.Right),
            ConditionalExpression conditional =>
                GetGenericConstrainedMemberReceiverName(conditional.WhenTrue) ??
                GetGenericConstrainedMemberReceiverName(conditional.WhenFalse),
            _ => null,
        };

    private static bool ContainsGenericConstrainedMemberInvocation(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
        } => receiver.Identifier.ToString() == receiverName,
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            TargetProperty: not null,
        } => receiver.Identifier.ToString() == receiverName,
        ArrayAccessExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            TargetProperty: not null,
        } => receiver.Identifier.ToString() == receiverName,
        BinaryExpression binary =>
            ContainsGenericConstrainedMemberInvocation(binary.Left, receiverName) ||
            ContainsGenericConstrainedMemberInvocation(binary.Right, receiverName),
        ConditionalExpression conditional =>
            ContainsGenericConstrainedMemberInvocation(conditional.Condition, receiverName) ||
            ContainsGenericConstrainedMemberInvocation(conditional.WhenTrue, receiverName) ||
            ContainsGenericConstrainedMemberInvocation(conditional.WhenFalse, receiverName),
        _ => false,
    };

    private static bool IsGenericConstrainedMemberReturnExpression(
        ExpressionBase expression,
        FunctionDeclaration declaration,
        string receiverName) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Arguments: var arguments,
        } => receiver.Identifier.ToString() == receiverName &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, declaration)),
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            TargetProperty: not null,
        } => receiver.Identifier.ToString() == receiverName,
        ArrayAccessExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            TargetProperty: not null,
            Indices: var indices,
        } => receiver.Identifier.ToString() == receiverName &&
            indices.All(index =>
                IsSupportedGenericConstrainedValueExpression(index, declaration)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsGenericConstrainedMemberReturnExpression(binary.Left, declaration, receiverName) &&
            IsGenericConstrainedMemberReturnExpression(binary.Right, declaration, receiverName),
        ConditionalExpression conditional =>
            IsGenericConstrainedMemberReturnExpression(
                conditional.Condition, declaration, receiverName) &&
            IsGenericConstrainedMemberReturnExpression(
                conditional.WhenTrue, declaration, receiverName) &&
            IsGenericConstrainedMemberReturnExpression(
                conditional.WhenFalse, declaration, receiverName),
        _ => false,
    };

    private static IdentifierExpression? GetGenericConstrainedReceiver(
        FunctionDeclaration declaration)
    {
        var bodyExpression = declaration.Body switch
        {
            [ReturnStatement { Expression: { } expression }] => expression,
            _ => declaration.Body?.OfType<ExpressionStatement>()
                .Select(statement => statement.Expression)
                .OfType<AssignmentExpression>()
                .Select(assignment => assignment.Target)
                .FirstOrDefault(),
        };
        var receiver = bodyExpression switch
        {
            InvocationExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression identifier,
                },
            } => identifier,
            MemberAccessExpression
            {
                Target: IdentifierExpression identifier,
            } => identifier,
            ArrayAccessExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression identifier,
                },
            } => identifier,
            _ => null,
        };
        return receiver is not null && declaration.Parameters.Any(parameter =>
            parameter.Name == receiver.Identifier.ToString() &&
            parameter.ParameterType is GenericType genericParameter &&
            declaration.GenericTypeNames.Contains(genericParameter.Name))
                ? receiver
                : null;
    }

    private static bool IsGenericConstrainedFieldReturnBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        var receiverName = expression switch
        {
            MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            } => receiver.Identifier.ToString(),
            ArrayAccessExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
            } => receiver.Identifier.ToString(),
            _ => null,
        };
        return declaration.Parameters.Any(parameter =>
            parameter.Name == receiverName &&
            parameter.ParameterType is GenericType genericParameter &&
            declaration.GenericTypeNames.Contains(genericParameter.Name));
    }

    private static bool IsGenericConstrainedPropertyAssignmentBody(
        FunctionDeclaration declaration)
    {
        if (declaration.Body is not { Count: > 0 } body)
        {
            return false;
        }

        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                    Target: var target,
                    Value: var value,
                },
            })
            {
                return false;
            }

            var receiverName = target switch
            {
                MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                } => receiver.Identifier.ToString(),
                ArrayAccessExpression
                {
                    Target: MemberAccessExpression
                    {
                        Target: IdentifierExpression receiver,
                    },
                } => receiver.Identifier.ToString(),
                _ => null,
            };
            if (receiverName is null || !declaration.Parameters.Any(parameter =>
                    parameter.Name == receiverName &&
                    parameter.ParameterType is GenericType genericParameter &&
                    declaration.GenericTypeNames.Contains(genericParameter.Name)) ||
                !IsSupportedGenericConstrainedAssignmentValueExpression(
                    value, declaration, receiverName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsGenericConstrainedVoidMethodCallBody(
        FunctionDeclaration declaration)
    {
        if (declaration.ReturnType is not VoidType ||
            declaration.Body is not { Count: > 0 } body ||
            body.Any(statement => !IsGenericConstrainedVoidMethodCallStatement(
                statement, declaration)))
        {
            return false;
        }

        return true;
    }

    private static bool IsGenericConstrainedVoidMethodCallStatement(
        StatementBase statement,
        FunctionDeclaration declaration) => statement switch
    {
        BlockStatement block => block.Statements.Count > 0 && block.Statements.All(
            nested => IsGenericConstrainedVoidMethodCallStatement(nested, declaration)),
        IfStatement conditional =>
            IsSupportedGenericConstrainedValueExpression(conditional.Condition, declaration) &&
            IsGenericConstrainedVoidMethodCallStatement(
                conditional.ThenStatement, declaration) &&
            (conditional.ElseStatement is null ||
             IsGenericConstrainedVoidMethodCallStatement(
                 conditional.ElseStatement, declaration)),
        ExpressionStatement
        {
            Expression: InvocationExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
                Receiver: not null,
                TargetSymbol.ReturnType: VoidType,
                Arguments: var arguments,
            },
        } => declaration.Parameters.Any(parameter =>
                parameter.Name == receiver.Identifier.ToString() &&
                parameter.ParameterType is GenericType genericParameter &&
                declaration.GenericTypeNames.Contains(genericParameter.Name) &&
                declaration.GenericConstraints.Any(constraint =>
                    constraint.ParameterName == genericParameter.Name &&
                    constraint.Kind == GenericConstraintKind.Type &&
                    constraint.Types.Count > 0)) &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, declaration)),
        _ => false,
    };

    private static void WriteGenericConstrainedVoidMethodCallStatement(
        IndentingWriter writer,
        StatementBase statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        switch (statement)
        {
            case BlockStatement block:
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var nested in block.Statements)
                {
                    WriteGenericConstrainedVoidMethodCallStatement(
                        writer, nested, instance, declaration, moduleName);
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                break;
            case IfStatement conditional:
                writer.WriteLine(
                    $"if ({ToCExpression(conditional.Condition, declaration, moduleName)})");
                WriteGenericConstrainedVoidMethodCallControlled(
                    writer, conditional.ThenStatement, instance, declaration, moduleName);
                if (conditional.ElseStatement is { } elseStatement)
                {
                    writer.WriteLine("else");
                    WriteGenericConstrainedVoidMethodCallControlled(
                        writer, elseStatement, instance, declaration, moduleName);
                }
                break;
            case ExpressionStatement { Expression: InvocationExpression invocation }:
                var receiver = ((MemberAccessExpression)invocation.Target).Target;
                var receiverName = ((IdentifierExpression)receiver).Identifier.ToString();
                var parameterIndex = Array.FindIndex(
                    declaration.Parameters.ToArray(),
                    parameter => parameter.Name == receiverName);
                if (parameterIndex >= 0 && parameterIndex < instance.ParameterTypes.Count)
                {
                    receiver.SetClosedGenericType(instance.ParameterTypes[parameterIndex]);
                }

                foreach (var temporaryName in EnumerateAllExpressions(invocation)
                    .OfType<InvocationExpression>()
                    .Select(nested => nested.ReceiverTemporaryName)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal))
                {
                    writer.WriteLine($"struct cx_iface_ref {temporaryName};");
                }

                writer.WriteLine(
                    $"{ToCExpression(invocation, declaration, moduleName)};");
                break;
            default:
                throw new InternalCompilerException(
                    $"Unsupported statement in generic constrained call body: {statement.GetType().Name}.");
        }
    }

    private static void WriteGenericConstrainedVoidMethodCallControlled(
        IndentingWriter writer,
        StatementBase statement,
        FunctionSymbol instance,
        FunctionDeclaration declaration,
        string moduleName)
    {
        if (statement is BlockStatement)
        {
            WriteGenericConstrainedVoidMethodCallStatement(
                writer, statement, instance, declaration, moduleName);
            return;
        }

        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteGenericConstrainedVoidMethodCallStatement(
            writer, statement, instance, declaration, moduleName);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static bool IsSupportedGenericConstrainedValueExpression(
        ExpressionBase expression,
        FunctionDeclaration declaration) => expression switch
    {
        LiteralExpression => true,
        IdentifierExpression identifier => declaration.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString() &&
            !ContainsGenericParameter(
                parameter.ParameterType, declaration.GenericTypeNames)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsSupportedGenericConstrainedValueExpression(binary.Left, declaration) &&
            IsSupportedGenericConstrainedValueExpression(binary.Right, declaration),
        ConditionalExpression conditional =>
            IsSupportedGenericConstrainedValueExpression(conditional.Condition, declaration) &&
            IsSupportedGenericConstrainedValueExpression(conditional.WhenTrue, declaration) &&
            IsSupportedGenericConstrainedValueExpression(conditional.WhenFalse, declaration),
        _ => false,
    };

    private static bool IsSupportedGenericConstrainedAssignmentValueExpression(
        ExpressionBase expression,
        FunctionDeclaration declaration,
        string receiverName) => expression switch
    {
        LiteralExpression => true,
        IdentifierExpression => IsSupportedGenericConstrainedValueExpression(
            expression, declaration),
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Arguments: var arguments,
        } => receiver.Identifier.ToString() == receiverName &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, declaration)),
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
        } => receiver.Identifier.ToString() == receiverName,
        ArrayAccessExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Indices: var indices,
        } => receiver.Identifier.ToString() == receiverName &&
            indices.All(index =>
                IsSupportedGenericConstrainedValueExpression(index, declaration)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsSupportedGenericConstrainedAssignmentValueExpression(
                binary.Left, declaration, receiverName) &&
            IsSupportedGenericConstrainedAssignmentValueExpression(
                binary.Right, declaration, receiverName),
        ConditionalExpression conditional =>
            IsSupportedGenericConstrainedAssignmentValueExpression(
                conditional.Condition, declaration, receiverName) &&
            IsSupportedGenericConstrainedAssignmentValueExpression(
                conditional.WhenTrue, declaration, receiverName) &&
            IsSupportedGenericConstrainedAssignmentValueExpression(
                conditional.WhenFalse, declaration, receiverName),
        _ => false,
    };

    private static bool ContainsGenericParameter(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) =>
        type switch
        {
            ConstType constant => ContainsGenericParameter(
                constant.UnderlyingType, genericTypeNames),
            GenericType generic => genericTypeNames.Contains(generic.Name),
            ArrayType array => ContainsGenericParameter(array.ElementType, genericTypeNames),
            NullableType nullable => ContainsGenericParameter(
                nullable.UnderlyingType, genericTypeNames),
            NamedType named => named.TypeArguments.Any(argument =>
                ContainsGenericParameter(argument, genericTypeNames)),
            FunctionType function => ContainsGenericParameter(
                    function.ReturnType, genericTypeNames) ||
                function.ParameterTypes.Any(parameter =>
                    ContainsGenericParameter(parameter, genericTypeNames)),
            _ => false,
        };

    private static bool IsGenericConditionalReturnBody(FunctionDeclaration declaration)
    {
        var body = declaration.Body;
        if (body is not { Count: > 0 })
        {
            return false;
        }

        var booleanLocals = new HashSet<string>(StringComparer.Ordinal);
        var flowStart = 0;
        while (flowStart < body.Count &&
            body[flowStart] is LocalVariableDeclarationStatement local)
        {
            if (local.DeclaredType is not BoolType || local.Declarators.Count == 0)
            {
                return false;
            }
            foreach (var declarator in local.Declarators)
            {
                if (declarator.Initializer is not { } initializer ||
                    !IsGenericReturnFlowBooleanExpression(initializer, declaration, booleanLocals) ||
                    !booleanLocals.Add(declarator.Name))
                {
                    return false;
                }
            }
            flowStart++;
        }

        var returnFlow = body.Skip(flowStart).ToArray();
        return returnFlow.Length > 0 &&
            returnFlow.All(statement =>
                IsGenericReturnFlow(statement, declaration, booleanLocals)) &&
            IsGenericReturnFlowAlwaysReturns(returnFlow);
    }

    private static bool IsGenericConditionalLocalReturnBody(FunctionDeclaration declaration) =>
        declaration.Body is
        [
            LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ConditionalExpression }],
            },
            ReturnStatement { Expression: IdentifierExpression },
        ];

    private static bool IsGenericReturnFlowAlwaysReturns(IReadOnlyList<StatementBase> statements) =>
        statements.Count > 0 && IsGenericReturnFlowAlwaysReturns(statements[^1]);

    private static bool IsGenericReturnFlowAlwaysReturns(StatementBase statement) => statement switch
    {
        ReturnStatement => true,
        BlockStatement block => IsGenericReturnFlowAlwaysReturns(block.Statements),
        IfStatement { ElseStatement: not null } conditional =>
            IsGenericReturnFlowAlwaysReturns(conditional.ThenStatement) &&
            IsGenericReturnFlowAlwaysReturns(conditional.ElseStatement),
        _ => false,
    };

    private static bool IsGenericReturnFlow(
        StatementBase statement,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? booleanLocals = null) => statement switch
    {
        ReturnStatement { Expression: { } expression } =>
            IsGenericValueSelectionExpression(
                expression, declaration, booleanLocals),
        BlockStatement block => block.Statements.All(item =>
            IsGenericReturnFlow(item, declaration, booleanLocals)),
        IfStatement conditional =>
            IsGenericReturnFlowBooleanExpression(
                conditional.Condition, declaration, booleanLocals) &&
            IsGenericReturnFlow(conditional.ThenStatement, declaration, booleanLocals) &&
            (conditional.ElseStatement is null ||
                IsGenericReturnFlow(conditional.ElseStatement, declaration, booleanLocals)),
        _ => false,
    };

    private static bool IsGenericValueSelectionExpression(
        ExpressionBase expression,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? booleanLocals) =>
        expression switch
        {
            IdentifierExpression identifier => declaration.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                IsSameGenericTypePattern(declaration.ReturnType, parameter.ParameterType)),
            ConditionalExpression conditional =>
                IsGenericReturnFlowBooleanExpression(
                    conditional.Condition, declaration, booleanLocals) &&
                IsGenericValueSelectionExpression(
                    conditional.WhenTrue, declaration, booleanLocals) &&
                IsGenericValueSelectionExpression(
                    conditional.WhenFalse, declaration, booleanLocals),
            _ => false,
        };

    private static bool IsGenericReturnFlowBooleanExpression(
        ExpressionBase expression,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? booleanLocals = null) => expression switch
    {
        IdentifierExpression identifier => declaration.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString() && parameter.ParameterType is BoolType) ||
            booleanLocals?.Contains(identifier.Identifier.ToString()) == true,
        LiteralExpression { SourceText: "true" or "false" } => true,
        UnaryExpression { Operator: "!", Operand: var operand } =>
            IsGenericReturnFlowBooleanExpression(operand, declaration, booleanLocals),
        BinaryExpression { Operator: "&&" or "||", Left: var left, Right: var right } =>
            IsGenericReturnFlowBooleanExpression(left, declaration, booleanLocals) &&
            IsGenericReturnFlowBooleanExpression(right, declaration, booleanLocals),
        BinaryExpression { Operator: "==" or "!=", Left: var left, Right: var right } =>
            IsGenericReturnFlowBooleanExpression(left, declaration, booleanLocals) &&
            IsGenericReturnFlowBooleanExpression(right, declaration, booleanLocals),
        _ => false,
    };

    private static bool IsSameGenericTypePattern(TypeBase left, TypeBase right)
    {
        var leftIsArray = ArrayType.TryGetElementType(left, out var leftElementType);
        var rightIsArray = ArrayType.TryGetElementType(right, out var rightElementType);
        if (leftIsArray || rightIsArray)
        {
            return leftIsArray && rightIsArray &&
                IsSameGenericTypePattern(leftElementType, rightElementType);
        }

        return (left, right) switch
        {
            (ConstType first, ConstType second) =>
                IsSameGenericTypePattern(first.UnderlyingType, second.UnderlyingType),
            (GenericType first, GenericType second) => first.Name == second.Name,
            (NullableType first, NullableType second) =>
                IsSameGenericTypePattern(first.UnderlyingType, second.UnderlyingType),
            (NamedType first, NamedType second) =>
                first.ResolvedTypeFullName == second.ResolvedTypeFullName &&
                first.TypeArguments.Count == second.TypeArguments.Count &&
                first.TypeArguments.Zip(second.TypeArguments)
                    .All(pair => IsSameGenericTypePattern(pair.First, pair.Second)),
            _ => left.FullName == right.FullName,
        };
    }

    private static bool IsGenericArrayCopyBody(FunctionDeclaration declaration) =>
        declaration.Body is
        [
            LocalVariableDeclarationStatement
            {
                DeclaredType: ArrayType,
                Declarators: [{ Initializer: ArrayCreationExpression }],
            },
            ForStatement
            {
                DeclarationInitializer: not null,
                Condition: not null,
                Iterators.Count: 1,
                Body: BlockStatement { Statements.Count: 1 },
            },
            ReturnStatement { Expression: IdentifierExpression },
        ];

    private static bool IsGenericArrayElementLocalReturnBody(
        FunctionDeclaration declaration)
    {
        return declaration.Body is { Count: >= 2 } body &&
            body[0] is LocalVariableDeclarationStatement
            {
                DeclaredType: { },
                Declarators: [{ Initializer: ArrayAccessExpression }],
            } &&
            body[^1] is ReturnStatement { Expression: IdentifierExpression } &&
            body.Take(body.Count - 1).Skip(1).All(statement => statement switch
            {
                LocalVariableDeclarationStatement
                {
                    DeclaredType: not null,
                    Declarators.Count: > 0,
                } => true,
                ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: IdentifierExpression,
                        Operator: "=",
                        Value: IdentifierExpression,
                    },
                } => true,
                _ => false,
            });
    }

    private static bool IsGenericArrayCopyWhileBody(FunctionDeclaration declaration) =>
        declaration.Body is
        [
            LocalVariableDeclarationStatement
            {
                DeclaredType: ArrayType,
                Declarators: [{ Initializer: ArrayCreationExpression }],
            },
            LocalVariableDeclarationStatement
            {
                DeclaredType: UIntType,
                Declarators: [{ Initializer: LiteralExpression { SourceText: "0u" } }],
            },
            WhileStatement
            {
                Condition: not null,
                Body: BlockStatement { Statements.Count: 2 },
            },
            ReturnStatement { Expression: IdentifierExpression },
        ];

    private static bool IsGenericArrayCopyForeachBody(FunctionDeclaration declaration) =>
        declaration.Body is
        [
            LocalVariableDeclarationStatement
            {
                DeclaredType: ArrayType,
                Declarators: [{ Initializer: ArrayCreationExpression }],
            },
            LocalVariableDeclarationStatement
            {
                DeclaredType: UIntType,
                Declarators: [{ Initializer: LiteralExpression { SourceText: "0u" } }],
            },
            ForeachStatement
            {
                Collection: IdentifierExpression,
                Body: BlockStatement { Statements.Count: 2 },
            },
            ReturnStatement { Expression: IdentifierExpression },
        ];

    private static bool IsGenericLocalSequenceBody(FunctionDeclaration declaration)
    {
        if (declaration.Body is not { Count: >= 2 } body ||
            body[^1] is not ReturnStatement { Expression: IdentifierExpression returned } ||
            !body.Take(body.Count - 1).OfType<LocalVariableDeclarationStatement>()
                .Any(local => local.Declarators.Count > 0))
        {
            return false;
        }

        var values = declaration.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.ParameterType,
            StringComparer.Ordinal);
        var locals = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < body.Count - 1; index++)
        {
            switch (body[index])
            {
                case LocalVariableDeclarationStatement local:
                    var localType = local.DeclaredType is AutoType
                        ? declaration.ReturnType
                        : GenericTypeSubstitution.BindParameters(
                            local.DeclaredType, declaration.GenericTypeNames);
                    if (local.Declarators.Count == 0 ||
                        !IsSameGenericTypePattern(declaration.ReturnType, localType))
                    {
                        return false;
                    }
                    foreach (var declarator in local.Declarators)
                    {
                        if (declarator.Initializer is not IdentifierExpression source ||
                            !values.TryGetValue(source.Identifier.ToString(), out var sourceType) ||
                            !IsSameGenericTypePattern(declaration.ReturnType, sourceType) ||
                            values.ContainsKey(declarator.Name))
                        {
                            return false;
                        }
                        values.Add(declarator.Name, declaration.ReturnType);
                        locals.Add(declarator.Name);
                    }
                    break;
                case ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: IdentifierExpression target,
                        Operator: "=",
                        Value: IdentifierExpression replacement,
                    },
                } when locals.Contains(target.Identifier.ToString()) &&
                    values.TryGetValue(replacement.Identifier.ToString(), out var replacementType) &&
                    IsSameGenericTypePattern(declaration.ReturnType, replacementType):
                    break;
                default:
                    return false;
            }
        }

        return locals.Contains(returned.Identifier.ToString()) &&
            values.TryGetValue(returned.Identifier.ToString(), out var returnType) &&
            IsSameGenericTypePattern(declaration.ReturnType, returnType);
    }

    private static void WriteGenericReturnFlow(
        IndentingWriter writer,
        StatementBase statement,
        FunctionDeclaration declaration,
        string moduleName)
    {
        switch (statement)
        {
            case LocalFunctionDeclarationStatement:
                break;

            case ReturnStatement { Expression: { } expression }:
                writer.WriteLine(
                    $"return {ToCExpression(expression, declaration, moduleName)};");
                break;
            case BlockStatement block:
                writer.WriteLine("{");
                writer.IncreaseIndent();
                foreach (var nested in block.Statements)
                {
                    WriteGenericReturnFlow(writer, nested, declaration, moduleName);
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
                break;
            case IfStatement conditional:
                writer.WriteLine($"if ({ToCExpression(conditional.Condition, declaration, moduleName)})");
                WriteGenericReturnFlowControlled(writer, conditional.ThenStatement,
                    declaration, moduleName);
                if (conditional.ElseStatement is not null)
                {
                    writer.WriteLine("else");
                    WriteGenericReturnFlowControlled(writer, conditional.ElseStatement,
                        declaration, moduleName);
                }
                break;
        }
    }

    private static void WriteGenericReturnFlowControlled(
        IndentingWriter writer,
        StatementBase statement,
        FunctionDeclaration declaration,
        string moduleName)
    {
        if (statement is BlockStatement)
        {
            WriteGenericReturnFlow(writer, statement, declaration, moduleName);
            return;
        }
        writer.WriteLine("{");
        writer.IncreaseIndent();
        WriteGenericReturnFlow(writer, statement, declaration, moduleName);
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WritePropertyAccessorDefinitions(
        IndentingWriter writer,
        PropertyDeclaration property,
        string moduleName)
    {
        foreach (var accessor in property.PropertyAccessorDeclarations
            .Where(accessor => accessor.BodyFunction is not null &&
                !IsClosedGenericArrayPropertyGetter(
                    accessor,
                    property.ParentClassDeclaration,
                    property.ParentClassDeclaration.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()) &&
                !(property.ParentClassDeclaration.GenericTypeNames.Length > 0 &&
                    accessor.BodyFunction is { } genericBody &&
                    EnumerateBodyExpressions(genericBody.Body ?? [])
                        .OfType<ObjectCreationExpression>()
                        .Any(creation => ContainsGenericParameter(
                            creation.RequestedType,
                            property.ParentClassDeclaration.GenericTypeNames)))))
        {
            var function = accessor.BodyFunction!;
            writer.Write($"{accessor.ToCIdentifier(moduleName)}(");

            var parameters = new List<string>();
            if (!property.IsStatic)
            {
                var receiverConst = accessor.Const ? "const " : string.Empty;
                parameters.Add(property.ExtensionTargetType is { } extensionTarget &&
                    IsPrimitiveExtensionReceiverType(extensionTarget)
                        ? $"{receiverConst}{ToCParameterType(extensionTarget)} __this"
                    : accessor.BodyFunction!.IsExtension &&
                        property.ParentClassDeclaration.ClassType == ClassType.Interface
                            ? $"{receiverConst}struct cx_iface_ref __this"
                            : $"{ToCReceiverType(property.ParentClassDeclaration, property.ParentClassDeclaration.ProjectName ?? moduleName, accessor.Const)} __this");
            }
            parameters.AddRange(function.Parameters.Select(
                parameter => $"{ToCParameterType(parameter.ParameterType)} {parameter.Name}"));

            writer.Write(string.Join(", ", parameters));
            writer.WriteLine(") {");
            writer.IncreaseIndent();
            writer.WriteLine("#if !defined(CX_STATIC_LINK)");
            writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
            writer.WriteLine("#endif");
            if (accessor.Name == "get" &&
                property.ParentClassDeclaration is { GenericTypeNames.Length: > 0 } genericOwner &&
                IsClosedGenericGenericFieldLocalAccessor(
                    accessor,
                    genericOwner,
                    genericOwner.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()))
            {
                var initializer = GetClosedGenericGenericFieldLocalInitializer(accessor);
                if (initializer is ConditionalExpression)
                {
                    writer.WriteLine(
                        $"return {ToCExpression(initializer, function, moduleName)};");
                }
                else
                {
                    var field = GetReferenceFieldDeclaration(initializer) ??
                        throw new InternalCompilerException(
                            $"Supported generic property '{accessor.FullName}' has no bound field.");
                    writer.WriteLine($"return (__this)->{field.Name};");
                }
            }
            else
            {
                var context = new StatementWriteContext(function);
                context.WriteDeclarations(writer);

                foreach (var statement in function.Body!)
                {
                    WriteStatement(writer, statement, function, moduleName, context);
                }
                context.WriteEpilogue(writer);
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
        string moduleName,
        StatementWriteContext context)
    {
        WriteSourceLocation(writer, statement.SourceSpan);
        WriteExpressionTemporaries(writer, GetDirectExpressions(statement));

        switch (statement)
        {
            case LocalFunctionDeclarationStatement:
                break;

            case ExpressionStatement expressionStatement:
                if (functionDeclaration is ConstructorDeclaration constructor &&
                    constructor.ParentClassDeclaration is
                        { GenericTypeNames.Length: > 0 } genericOwner &&
                    expressionStatement.Expression is AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression target,
                        Value: ArrayCreationExpression allocation,
                    } &&
                    genericOwner.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>()
                        .SingleOrDefault(field => field.Name ==
                            target.Identifier.Parts[^1]) is { } arrayField &&
                    IsClosedGenericArrayAllocation(
                        arrayField, allocation, constructor, genericOwner))
                {
                    writer.WriteLine(
                        $"__this->{arrayField.Name} = cx_array_new(" +
                        $"(cx_uint)({ToCExpression(allocation.Length, functionDeclaration, moduleName)}), " +
                        "(cx_uint)sizeof(void*));");
                }
                else
                {
                    writer.WriteLine(
                        $"{ToCExpression(expressionStatement.Expression, functionDeclaration, moduleName)};");
                }
                break;

            case ReturnStatement returnStatement:
                WriteReturnStatement(writer, returnStatement, functionDeclaration, moduleName, context);
                break;

            case LocalVariableDeclarationStatement localDeclaration:
                foreach (var declarator in localDeclaration.Declarators)
                {
                    var type = declarator.Type ?? throw new InternalCompilerException(
                        $"Local '{declarator.Name}' is not bound.");
                    var symbol = declarator.Symbol ?? throw new InternalCompilerException(
                        $"Local '{declarator.Name}' has no bound symbol.");
                    if (symbol.IsCaptured)
                    {
                        var cType = type.ToCIdentifier(false);
                        writer.WriteLine(
                            $"{cType} *{symbol.CellName} = ({cType}*)CX_ID_4(cxcore, System, Memory, Alloc)(" +
                            $"(cx_uint)sizeof({cType}));");
                        if (declarator.Initializer is not null)
                        {
                            writer.WriteLine(
                                $"*{symbol.CellName} = " +
                                $"{ToCExpressionAsType(declarator.Initializer, type, functionDeclaration, moduleName)};");
                        }
                        continue;
                    }
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
                var capturedForLocals = forStatement.DeclarationInitializer?.Declarators
                    .Where(declarator => declarator.Symbol?.IsCaptured == true)
                    .ToArray() ?? [];
                if (capturedForLocals.Length > 0)
                {
                    writer.WriteLine("{");
                    writer.IncreaseIndent();
                    foreach (var declarator in forStatement.DeclarationInitializer!.Declarators)
                    {
                        var localType = declarator.Type ?? throw new InternalCompilerException(
                            $"Local '{declarator.Name}' is not bound.");
                        var localSymbol = declarator.Symbol ?? throw new InternalCompilerException(
                            $"Local '{declarator.Name}' has no bound symbol.");
                        if (localSymbol.IsCaptured)
                        {
                            var cType = localType.ToCIdentifier(false);
                            writer.WriteLine(
                                $"{cType} *{localSymbol.CellName} = ({cType}*)CX_ID_4(cxcore, System, Memory, Alloc)(" +
                                $"(cx_uint)sizeof({cType}));");
                            if (declarator.Initializer is not null)
                            {
                                writer.WriteLine(
                                    $"*{localSymbol.CellName} = " +
                                    $"{ToCExpressionAsType(declarator.Initializer, localType, functionDeclaration, moduleName)};");
                            }
                        }
                        else
                        {
                            var initializer = declarator.Initializer is null
                                ? string.Empty
                                : $" = {ToCExpressionAsType(declarator.Initializer, localType, functionDeclaration, moduleName)}";
                            writer.WriteLine($"{localType.ToCIdentifier(false)} {declarator.Name}{initializer};");
                        }
                    }
                }
                var forInitializer = forStatement.DeclarationInitializer is not null
                    ? capturedForLocals.Length > 0
                        ? string.Empty
                        : ToCForDeclaration(
                            forStatement.DeclarationInitializer,
                            functionDeclaration,
                            moduleName)
                    :
                    string.Join(", ", forStatement.InitializerExpressions.Select(
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
                if (capturedForLocals.Length > 0)
                {
                    writer.DecreaseIndent();
                    writer.WriteLine("}");
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
        foreach (var invocation in expressionList
            .SelectMany(EnumerateOperatorExpressions<InvocationExpression>)
            .Where(invocation => invocation.FunctionType is not null &&
                invocation.FunctionValueTemporaryName is not null))
        {
            writer.WriteLine(
                $"{GetFunctionTypeCIdentifier(invocation.FunctionType!)} " +
                $"{invocation.FunctionValueTemporaryName};");
        }
        foreach (var assignment in expressionList
            .SelectMany(EnumerateAllExpressions)
            .OfType<AssignmentExpression>()
            .Where(assignment => assignment.DelegateTemporaryName is not null))
        {
            var functionType = GetFunctionType(assignment.Target.InferredType) ??
                throw new InternalCompilerException("Delegate assignment target is not a function type.");
            writer.WriteLine(
                $"{functionType.ToCIdentifier(false)} *{assignment.DelegateTemporaryName};");
        }
        foreach (var assignment in expressionList
            .SelectMany(EnumerateAllExpressions)
            .OfType<AssignmentExpression>()
            .Where(assignment => assignment.PropertyReceiverTemporaryName is not null))
        {
            var propertyExpression = assignment.Target is ArrayAccessExpression indexed
                ? indexed.Target
                : assignment.Target;
            var receiver = propertyExpression is MemberAccessExpression memberAccess
                ? memberAccess.Target
                : null;
            var receiverType = receiver?.InferredType ?? throw new InternalCompilerException(
                "Property receiver type is not bound.");
            receiverType = receiver?.ClosedGenericType ?? receiverType;
            var valueType = UnwrapConst(receiverType);
            var isStructReceiver = valueType is NamedType { ClassType: ClassType.Struct };
            var receiverPointer = isStructReceiver &&
                (receiver is ThisExpression || IsCStorageLocation(receiver!))
                ? "*"
                : string.Empty;
            writer.WriteLine(
                $"{valueType.ToCIdentifier(false)}{receiverPointer} " +
                $"{assignment.PropertyReceiverTemporaryName};");
        }
        foreach (var assignment in expressionList
            .SelectMany(EnumerateAllExpressions)
            .OfType<AssignmentExpression>()
            .Where(assignment => assignment.PropertyIndexTemporaryNames.Count > 0))
        {
            var indexParameterTypes = assignment.PropertyGetter?.ParameterTypes ??
                assignment.PropertySetter?.ParameterTypes ?? throw new InternalCompilerException(
                    "Property index temporaries have no accessor.");
            for (var index = 0; index < assignment.PropertyIndexTemporaryNames.Count; index++)
            {
                writer.WriteLine(
                    $"{indexParameterTypes[index].ToCIdentifier(false)} " +
                    $"{assignment.PropertyIndexTemporaryNames[index]};");
            }
        }
        foreach (var assignment in expressionList
            .SelectMany(EnumerateAllExpressions)
            .OfType<AssignmentExpression>()
            .Where(assignment => assignment.PropertyGetterInterfaceReceiverTemporaryName is not null))
        {
            writer.WriteLine(
                $"struct cx_iface_ref {assignment.PropertyGetterInterfaceReceiverTemporaryName};");
        }
        foreach (var unary in expressionList
            .SelectMany(EnumerateOperatorExpressions<UnaryExpression>)
            .Where(unary => unary.PropertySetter is not null))
        {
            var propertyType = unary.TargetProperty!.Type;
            var valueType = propertyType is ConstType constType
                ? constType.UnderlyingType
                : propertyType;
            writer.WriteLine(
                $"{valueType.ToCIdentifier(false)} {unary.OperatorTemporaryName};");
            if (unary.OperatorOldValueTemporaryName is { } oldValueTemporary)
            {
                writer.WriteLine($"{valueType.ToCIdentifier(false)} {oldValueTemporary};");
            }
            if (unary.PropertyReceiverTemporaryName is { } receiverTemporary)
            {
                var propertyExpression = unary.Operand is ArrayAccessExpression indexed
                    ? indexed.Target
                    : unary.Operand;
                var receiver = propertyExpression is MemberAccessExpression memberAccess
                    ? memberAccess.Target
                    : null;
                var receiverType = receiver?.InferredType ?? throw new InternalCompilerException(
                    "Property mutation receiver type is not bound.");
                var unwrappedReceiverType = receiverType is ConstType receiverConst
                    ? receiverConst.UnderlyingType
                    : receiverType;
                var receiverPointer = receiver is ThisExpression &&
                    !IsCReferenceType(unwrappedReceiverType)
                    ? "*"
                    : string.Empty;
                writer.WriteLine(
                    $"{unwrappedReceiverType.ToCIdentifier(false)}{receiverPointer} " +
                    $"{receiverTemporary};");
            }
            for (var index = 0; index < unary.PropertyIndexTemporaryNames.Count; index++)
            {
                var getter = unary.PropertyGetter ?? throw new InternalCompilerException(
                    "Property mutation index temporaries have no getter.");
                writer.WriteLine(
                    $"{getter.ParameterTypes[index].ToCIdentifier(false)} " +
                    $"{unary.PropertyIndexTemporaryNames[index]};");
            }
            if (unary.PropertyGetterInterfaceReceiverTemporaryName is { } getterInterfaceTemporary)
            {
                writer.WriteLine($"struct cx_iface_ref {getterInterfaceTemporary};");
            }
            if (unary.PropertySetterInterfaceReceiverTemporaryName is { } setterInterfaceTemporary)
            {
                writer.WriteLine($"struct cx_iface_ref {setterInterfaceTemporary};");
            }
        }
        foreach (var assignment in expressionList
            .SelectMany(EnumerateAllExpressions)
            .OfType<AssignmentExpression>()
            .Where(assignment => assignment.TemporaryName is not null &&
                assignment.OperatorSymbol?.Declaration?.IsExtension == true))
        {
            var type = assignment.Target.InferredType ?? throw new InternalCompilerException(
                "Extension operator assignment target type is not bound.");
            var valueType = type is ConstType constType ? constType.UnderlyingType : type;
            writer.WriteLine(
                $"{valueType.ToCIdentifier(false)}* {assignment.TemporaryName};");
        }
        var allExpressions = expressionList.SelectMany(EnumerateAllExpressions).ToArray();
        foreach (var methodValue in allExpressions
            .OfType<MemberAccessExpression>()
            .Concat(allExpressions
                .OfType<IdentifierExpression>()
                .Select(identifier => identifier.TargetMethodValue)
                .OfType<MemberAccessExpression>())
            .Where(memberAccess => memberAccess.FunctionValueSymbol is not null &&
                !memberAccess.FunctionValueIsStatic)
            .DistinctBy(memberAccess => memberAccess.FunctionValueReceiverTemporaryName))
        {
            var receiverType = GetUnwrappedCaptureType(methodValue.Target.InferredType ??
                throw new InternalCompilerException("Method value receiver is not bound."));
            writer.WriteLine(
                $"{receiverType.ToCIdentifier(false)} {methodValue.FunctionValueReceiverTemporaryName};");
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
        foreach (var binary in expressionList.SelectMany(EnumerateOperatorExpressions<BinaryExpression>)
            .Where(expression => expression.OperatorSymbol is not null &&
                expression.OperatorReceiverTemporaryName is not null))
        {
            var type = binary.Left.InferredType ?? throw new InternalCompilerException(
                "Operator receiver type is not bound.");
            var temporaryType = type is ConstType constType ? constType.UnderlyingType : type;
            writer.WriteLine(
                $"{temporaryType.ToCIdentifier(false)} {binary.OperatorReceiverTemporaryName};");
        }
        foreach (var unary in expressionList.SelectMany(EnumerateOperatorExpressions<UnaryExpression>)
            .Where(expression => expression.OperatorSymbol is not null &&
                expression.OperatorTemporaryName is not null &&
                expression.PropertySetter is null))
        {
            var type = unary.Operand.InferredType ?? throw new InternalCompilerException(
                "Operator operand type is not bound.");
            var valueType = type is ConstType constType ? constType.UnderlyingType : type;
            var classType = valueType is NamedType namedType ? namedType.ClassType : (ClassType?)null;
            var cType = valueType.ToCIdentifier(false);
            if (unary.Operator is "++" or "--")
            {
                if (classType == ClassType.Struct ||
                    unary.OperatorSymbol?.Declaration?.IsExtension == true)
                {
                    cType += "*";
                }
            }
            writer.WriteLine($"{cType} {unary.OperatorTemporaryName};");
            if (unary.OperatorOldValueTemporaryName is { } oldValueTemporary)
            {
                writer.WriteLine($"{valueType.ToCIdentifier(false)} {oldValueTemporary};");
            }
        }
        foreach (var switchExpression in expressionList
            .SelectMany(EnumerateOperatorExpressions<SwitchExpression>))
        {
            var selectorType = switchExpression.Selector.InferredType ??
                throw new InternalCompilerException("Switch expression selector is not bound.");
            writer.WriteLine(
                $"{selectorType.ToCIdentifier(false)} {switchExpression.TemporaryName};");
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
                if (clause.VariableSymbol?.IsCaptured == true)
                {
                    var symbol = clause.VariableSymbol;
                    var type = clause.ExceptionType.ToCIdentifier(false);
                    writer.WriteLine(
                        $"{type} *{symbol.CellName} = ({type}*)CX_ID_4(cxcore, System, Memory, Alloc)(" +
                        $"(cx_uint)sizeof({type}));");
                    writer.WriteLine(
                        $"*{symbol.CellName} = ({type})cx_exception_current();");
                }
                else
                {
                    writer.WriteLine(
                        $"{clause.ExceptionType.ToCIdentifier(false)} {clause.VariableName} = " +
                        $"({clause.ExceptionType.ToCIdentifier(false)})cx_exception_current();");
                }
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
        if (statement.EnumeratorSymbol is { } enumeratorSymbol)
        {
            WriteEnumeratorForeachStatement(
                writer,
                statement,
                variableType,
                enumeratorSymbol,
                functionDeclaration,
                moduleName,
                context);
            return;
        }

        var collectionName = $"__cx_foreach_collection_{statement.VariableName}";
        var indexName = $"__cx_foreach_index_{statement.VariableName}";

        writer.WriteLine("{");
        writer.IncreaseIndent();
        var loopTarget = context.PushLoop();
        writer.WriteLine(
            $"struct CX_ID_4(cxcore, System, Collections, Array)* {collectionName} = " +
            $"{ToCExpression(statement.Collection, functionDeclaration, moduleName)};");
        writer.WriteLine(
            $"for (cx_uint {indexName} = 0; " +
            $"{indexName} < {collectionName}->_length; {indexName}++)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        if (statement.VariableSymbol?.IsCaptured == true)
        {
            var symbol = statement.VariableSymbol;
            writer.WriteLine(
                $"{variableType.ToCIdentifier(false)} *{symbol.CellName} = " +
                $"({variableType.ToCIdentifier(false)}*)CX_ID_4(cxcore, System, Memory, Alloc)(" +
                $"(cx_uint)sizeof({variableType.ToCIdentifier(false)}));");
            writer.WriteLine(
                $"*{symbol.CellName} = (({variableType.ToCIdentifier(false)}*)" +
                $"{collectionName}->_data)[{indexName}];");
        }
        else
        {
            writer.WriteLine(
                $"{variableType.ToCIdentifier(false)} {statement.VariableName} = " +
                $"(({variableType.ToCIdentifier(false)}*){collectionName}->_data)[{indexName}];");
        }
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

    private static void WriteEnumeratorForeachStatement(
        IndentingWriter writer,
        ForeachStatement statement,
        TypeBase variableType,
        LocalVariableSymbol enumeratorSymbol,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context)
    {
        var getEnumerator = statement.GetEnumeratorInvocation ??
            throw new InternalCompilerException("Foreach enumerator factory is not bound.");
        var moveNext = statement.MoveNextInvocation ??
            throw new InternalCompilerException("Foreach MoveNext call is not bound.");
        var current = statement.CurrentExpression ??
            throw new InternalCompilerException("Foreach Current expression is not bound.");

        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine(
            $"{enumeratorSymbol.Type.ToCIdentifier(false)} {enumeratorSymbol.Name} = " +
            $"{ToCExpression(getEnumerator, functionDeclaration, moduleName)};");
        var loopTarget = context.PushLoop();
        writer.WriteLine($"while ({ToCExpression(moveNext, functionDeclaration, moduleName)})");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        if (statement.VariableSymbol?.IsCaptured == true)
        {
            var symbol = statement.VariableSymbol;
            writer.WriteLine(
                $"{variableType.ToCIdentifier(false)} *{symbol.CellName} = " +
                $"({variableType.ToCIdentifier(false)}*)CX_ID_4(cxcore, System, Memory, Alloc)(" +
                $"(cx_uint)sizeof({variableType.ToCIdentifier(false)}));");
            writer.WriteLine(
                $"*{symbol.CellName} = {ToCExpression(current, functionDeclaration, moduleName)};");
        }
        else
        {
            writer.WriteLine(
                $"{variableType.ToCIdentifier(false)} {statement.VariableName} = " +
                $"{ToCExpression(current, functionDeclaration, moduleName)};");
        }
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
        if (statement.SelectorTemporaryName is { } selectorTemporary &&
            statement.CaseIndexTemporaryName is { } caseIndexTemporary)
        {
            var selectorType = statement.Expression.InferredType ??
                throw new InternalCompilerException("Switch selector is not bound.");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine(
                $"{selectorType.ToCIdentifier(false)} {selectorTemporary} = " +
                $"{ToCExpressionAsType(statement.Expression, selectorType, functionDeclaration, moduleName)};");
            writer.WriteLine($"cx_int {caseIndexTemporary} = -1;");
            for (var sectionIndex = 0; sectionIndex < statement.Sections.Count; sectionIndex++)
            {
                var section = statement.Sections[sectionIndex];
                foreach (var label in section.Labels.Where(label => !label.IsDefault))
                {
                    var match = ToCSwitchLabelMatch(
                        selectorTemporary,
                        selectorType,
                        label.Value!,
                        functionDeclaration,
                        moduleName);
                    if (label.Filter is not null)
                    {
                        match = $"({match}) && ({ToCExpression(label.Filter, functionDeclaration, moduleName)})";
                    }
                    writer.WriteLine(
                        $"if ({caseIndexTemporary} == -1 && ({match})) {caseIndexTemporary} = {sectionIndex};");
                }
            }
            var defaultIndex = statement.Sections
                .Select((section, index) => (section, index))
                .FirstOrDefault(pair => pair.section.Labels.Any(label => label.IsDefault));
            if (defaultIndex.section is not null)
            {
                writer.WriteLine(
                    $"if ({caseIndexTemporary} == -1) {caseIndexTemporary} = {defaultIndex.index};");
            }
            writer.WriteLine($"switch ({caseIndexTemporary})");
            WriteSwitchSections(writer, statement, functionDeclaration, moduleName, context, useIndices: true);
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
        else
        {
            writer.WriteLine(
                $"switch ({ToCExpression(statement.Expression, functionDeclaration, moduleName)})");
            WriteSwitchSections(writer, statement, functionDeclaration, moduleName, context, useIndices: false);
        }

        if (switchTarget.BreakLabelUsed)
        {
            writer.WriteLine($"{switchTarget.BreakLabel}:;");
        }
        context.PopSwitch(switchTarget);
    }

    private static void WriteSwitchSections(
        IndentingWriter writer,
        SwitchStatement statement,
        FunctionDeclaration functionDeclaration,
        string moduleName,
        StatementWriteContext context,
        bool useIndices)
    {
        writer.WriteLine("{");
        writer.IncreaseIndent();
        for (var sectionIndex = 0; sectionIndex < statement.Sections.Count; sectionIndex++)
        {
            var section = statement.Sections[sectionIndex];
            if (useIndices)
            {
                writer.WriteLine($"case {sectionIndex}:");
            }
            foreach (var label in section.Labels)
            {
                if (useIndices)
                {
                    continue;
                }
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
    }

    private static string ToCSwitchLabelMatch(
        string selector,
        TypeBase selectorType,
        ExpressionBase label,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var labelExpression = ToCExpression(label, functionDeclaration, moduleName);
        if (selectorType is ConstType constType)
        {
            selectorType = constType.UnderlyingType;
        }
        if (selectorType is StringType)
        {
            var selectorReference = $"({selector})";
            if (label is LiteralExpression { SourceText: "null" })
            {
                return $"{selectorReference} == CX_NULL";
            }
            var labelReference = $"({labelExpression})";
            return $"(({selectorReference} == {labelReference}) || " +
                $"({selectorReference} != CX_NULL && {labelReference} != CX_NULL && " +
                $"{selectorReference}->_length == {labelReference}->_length && " +
                $"({selectorReference}->_length == 0 || memcmp({selectorReference}->_data, " +
                $"{labelReference}->_data, {selectorReference}->_length) == 0)))";
        }
        return $"({selector} == {labelExpression})";
    }

    private static string ToCSwitchExpression(
        SwitchExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var selectorType = expression.Selector.InferredType ??
            throw new InternalCompilerException("Switch expression selector is not bound.");
        var resultType = expression.InferredType ??
            throw new InternalCompilerException("Switch expression result type is not bound.");
        var temporary = expression.TemporaryName ??
            throw new InternalCompilerException("Switch expression temporary is not bound.");
        var result = ToCExpressionAsType(
            expression.Arms[^1].Value, resultType, functionDeclaration, moduleName);
        for (var index = expression.Arms.Count - 2; index >= 0; index--)
        {
            var arm = expression.Arms[index];
            var match = ToCSwitchLabelMatch(
                temporary, selectorType, arm.Label!, functionDeclaration, moduleName);
            if (arm.Filter is not null)
            {
                match = $"({match}) && ({ToCExpression(arm.Filter, functionDeclaration, moduleName)})";
            }
            var value = ToCExpressionAsType(arm.Value, resultType, functionDeclaration, moduleName);
            result = $"(({match}) ? {value} : {result})";
        }
        return $"({temporary} = " +
            $"{ToCExpressionAsType(expression.Selector, selectorType, functionDeclaration, moduleName)}, {result})";
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
                "default" => ToCDefaultLiteral(literal),
                _ => literal.SourceText,
            },
            LambdaExpression { GeneratedFunction.LocalCName: { } localName } lambda =>
                ToCLambdaValue(lambda, functionDeclaration, moduleName),
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
            IdentifierExpression { TargetMethodValue: not null } identifier =>
                ToCMethodGroupValue(
                    identifier.TargetMethodValue,
                    functionDeclaration,
                    moduleName),
            IdentifierExpression { FunctionValueSymbol: not null } identifier =>
                ToCFunctionValue(identifier, functionDeclaration, moduleName),
            IdentifierExpression { TargetCapture: not null } identifier =>
                ToCCapturedLocal(identifier.TargetCapture, functionDeclaration),
            IdentifierExpression { TargetLocal: not null } identifier =>
                ToCLocal(identifier.TargetLocal),
            IdentifierExpression identifier => identifier.TargetField is null
                ? identifier.Identifier.ToCIdentifier()
                : ToCFieldAccess(
                    identifier.TargetField,
                    null,
                    identifier.ReceiverBaseDepth,
                    functionDeclaration,
                    moduleName),
            ThisExpression { TargetCapture: not null } thisExpression =>
                ToCCapturedThis(thisExpression.TargetCapture, functionDeclaration),
            ThisExpression => "__this",
            BinaryExpression binary =>
                ToCBinaryExpression(binary, functionDeclaration, moduleName),
            SwitchExpression switchExpression =>
                ToCSwitchExpression(switchExpression, functionDeclaration, moduleName),
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
            UnaryExpression { PropertySetter: not null } unary =>
                ToCPropertyMutation(unary, functionDeclaration, moduleName),
            UnaryExpression unary when unary.Postfix =>
                unary.OperatorSymbol is not null
                    ? ToCOperatorUnaryExpression(unary, functionDeclaration, moduleName)
                    : $"({ToCExpression(unary.Operand, functionDeclaration, moduleName)}{unary.Operator})",
            UnaryExpression unary =>
                unary.OperatorSymbol is not null
                    ? ToCOperatorUnaryExpression(unary, functionDeclaration, moduleName)
                    : $"({unary.Operator}{ToCExpression(unary.Operand, functionDeclaration, moduleName)})",
            AssignmentExpression { PropertySetter: not null } assignment =>
                ToCPropertyAssignment(assignment, functionDeclaration, moduleName),
            AssignmentExpression { DelegateTemporaryName: not null } assignment =>
                ToCDelegateCompoundAssignment(assignment, functionDeclaration, moduleName),
            AssignmentExpression { OperatorSymbol: not null } assignment =>
                ToCOperatorAssignment(assignment, functionDeclaration, moduleName),
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
                    moduleName,
                    memberAccess.InterfaceDispatchType),
            MemberAccessExpression { TargetField: not null } memberAccess =>
                ToCFieldAccess(
                    memberAccess.TargetField,
                    memberAccess.Target,
                    memberAccess.ReceiverBaseDepth,
                    functionDeclaration,
                    moduleName),
            MemberAccessExpression { FunctionValueIsStatic: true } memberAccess =>
                ToCStaticMethodGroupValue(memberAccess),
            MemberAccessExpression { FunctionValueSymbol: not null } memberAccess =>
                ToCMethodGroupValue(memberAccess, functionDeclaration, moduleName),
            MemberAccessExpression memberAccess =>
                FlattenIdentifier(memberAccess).ToCIdentifier(),
            _ => throw new InternalCompilerException(
                $"Expression '{expression.GetType().Name}' is not yet supported by the C generator."),
        };
    }

    private static string ToCDefaultLiteral(LiteralExpression literal)
    {
        var type = literal.InferredType ?? throw new InternalCompilerException(
            "Default literal type is not bound.");
        return type is GenericType ? "CX_NULL" : ToCDefaultValue(type);
    }

    private static string ToCDefaultValue(TypeBase type) =>
        $"(({type.ToCIdentifier(false)}){{ 0 }})";

    private static void WriteClosureEnvironmentDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        IReadOnlyCollection<FunctionDeclaration> functions)
    {
        foreach (var methodValue in EnumerateMethodValueExpressions(declarations, functions)
            .Where(methodValue => !methodValue.FunctionValueIsStatic))
        {
            var boundReceiverType = methodValue.Target.InferredType ??
                throw new InternalCompilerException("Method value receiver is not bound.");
            var receiverType = GetUnwrappedCaptureType(boundReceiverType);
            writer.WriteLine($"struct {methodValue.FunctionValueEnvironmentName} {{");
            writer.IncreaseIndent();
            var receiverCType = receiverType is NamedType { ClassType: ClassType.Interface }
                ? "struct cx_iface_ref"
                : receiverType is NamedType { ClassType: ClassType.Struct }
                    ? receiverType.ToCIdentifier(false)
                    : boundReceiverType.ToCIdentifier(false);
            writer.WriteLine($"{receiverCType} receiver;");
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }

        foreach (var lambda in functions
            .Select(function => function.LambdaOwner)
            .OfType<LambdaExpression>()
            .Where(lambda => lambda.Captures.Count > 0)
            .Distinct())
        {
            writer.WriteLine($"struct {GetLambdaEnvironmentCIdentifier(lambda)} {{");
            writer.IncreaseIndent();
            foreach (var capture in lambda.Captures)
            {
                if (capture.Local.IsThis &&
                    GetUnwrappedCaptureType(capture.Local.Type) is NamedType
                    {
                        ClassType: ClassType.Struct,
                    } valueType)
                {
                    writer.WriteLine(
                        $"{valueType.ToCIdentifier(false)} {capture.EnvironmentFieldName};");
                }
                else
                {
                    writer.WriteLine($"void *{capture.EnvironmentFieldName};");
                }
            }
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }
    }

    private static IEnumerable<MemberAccessExpression> EnumerateMethodValueExpressions(
        IReadOnlyCollection<DeclarationBase> declarations,
        IReadOnlyCollection<FunctionDeclaration> localFunctions)
    {
        var expressions = EnumerateFunctions(declarations)
            .Concat(localFunctions)
            .Distinct()
            .SelectMany(function => EnumerateStatements(function.Body ?? [])
                .SelectMany(GetDirectExpressions)
                .SelectMany(EnumerateAllExpressions))
            .ToArray();
        return expressions
            .OfType<MemberAccessExpression>()
            .Concat(expressions
                .OfType<IdentifierExpression>()
                .Select(identifier => identifier.TargetMethodValue)
                .OfType<MemberAccessExpression>())
            .Where(memberAccess => memberAccess.FunctionValueSymbol is not null)
            .DistinctBy(memberAccess => memberAccess.FunctionValueAdapterName);
    }

    private static void WriteFunctionValueAdapters(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        IReadOnlyCollection<FunctionDeclaration> localFunctions)
    {
        var methodValues = EnumerateMethodValueExpressions(declarations, localFunctions).ToArray();
        var functions = EnumerateFunctions(declarations)
            .Concat(localFunctions)
            .Distinct()
            .SelectMany(function => EnumerateStatements(function.Body ?? [])
                .SelectMany(GetDirectExpressions)
                .SelectMany(EnumerateAllExpressions))
            .OfType<IdentifierExpression>()
            .Where(identifier => identifier.FunctionValueSymbol is not null)
            .Select(identifier => identifier.FunctionValueSymbol!)
            .Concat(methodValues
                .Where(methodValue => methodValue.FunctionValueIsStatic)
                .Select(methodValue => methodValue.FunctionValueSymbol!))
            .DistinctBy(ToCIdentifier)
            .ToArray();
        foreach (var function in functions)
        {
            var adapterName = GetFunctionValueAdapterCIdentifier(function);
            var parameters = function.ParameterTypes.Select((type, index) =>
                $"{ToCParameterType(type)} __cx_arg_{index}").ToArray();
            var signature = new[] { "void *__cx_environment" }.Concat(parameters);
            writer.WriteLine($"static {function.ReturnType.ToCReturnType(false)} {adapterName}(");
            writer.IncreaseIndent();
            writer.WriteLine(string.Join(",\n", signature));
            writer.DecreaseIndent();
            writer.WriteLine(") {");
            writer.IncreaseIndent();
            var localEnvironment =
                (function.ImplementationDeclaration ?? function.Declaration)?.LambdaOwner is not null
                ? new[] { "__cx_environment" }
                : Array.Empty<string>();
            if (localEnvironment.Length == 0)
            {
                writer.WriteLine("(void)__cx_environment;");
            }
            var callArguments = localEnvironment.Concat(
                parameters.Select((_, index) => $"__cx_arg_{index}"));
            var call = $"{ToCIdentifier(function)}({string.Join(", ", callArguments)})";
            writer.WriteLine(function.ReturnType is VoidType ? $"{call};" : $"return {call};");
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
        var instanceMethodValues = methodValues
            .Where(methodValue => !methodValue.FunctionValueIsStatic)
            .ToArray();
        foreach (var methodValue in instanceMethodValues)
        {
            WriteMethodValueAdapter(writer, methodValue, moduleName);
        }
        if (functions.Length > 0)
        {
            writer.WriteLine();
        }
        if (instanceMethodValues.Length > 0)
        {
            writer.WriteLine();
        }
        var functionTypes = EnumerateFunctionTypesFromDeclarations(declarations)
            .DistinctBy(GetFunctionTypeCIdentifier)
            .ToArray();
        foreach (var functionType in functionTypes)
        {
            var cType = GetFunctionTypeCIdentifier(functionType);
            WriteFunctionValueHelpers(writer, functionType);
        }
        if (functionTypes.Length > 0)
        {
            writer.WriteLine();
        }
    }

    private static void WriteFunctionValueHelpers(
        IndentingWriter writer,
        FunctionType functionType)
    {
        var cType = GetFunctionTypeCIdentifier(functionType);
        var nodeType = GetFunctionTypeNodeCIdentifier(functionType);
        var nullName = GetFunctionTypeHelperName(functionType, "is_null");
        var countName = GetFunctionTypeHelperName(functionType, "count");
        var itemName = GetFunctionTypeHelperName(functionType, "item");
        var invocationListName = GetFunctionTypeHelperName(functionType, "get_invocation_list");
        var appendName = GetFunctionTypeHelperName(functionType, "append");
        var appendValueName = GetFunctionTypeHelperName(functionType, "append_value");
        var combineName = GetFunctionTypeHelperName(functionType, "combine");
        var removeName = GetFunctionTypeHelperName(functionType, "remove");
        var equalName = GetFunctionTypeHelperName(functionType, "equal");
        var invokeName = GetFunctionTypeHelperName(functionType, "invoke");
        var callArguments = string.Join(", ", functionType.ParameterTypes
            .Select((type, index) => $"__cx_arg_{index}"));
        var parameters = functionType.ParameterTypes
            .Select((type, index) => $"{ToCParameterType(type)} __cx_arg_{index}")
            .ToArray();
        var callSuffix = callArguments.Length == 0 ? string.Empty : $", {callArguments}";

        writer.WriteLine($"struct {nodeType} {{ {cType} value; struct {nodeType} *next; }};");
        writer.WriteLine($"static inline cx_bool {nullName}({cType} value) {{");
        writer.IncreaseIndent();
        writer.WriteLine("return value.__function == NULL && value.__invocation_list == NULL ? CX_TRUE : CX_FALSE;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline cx_uint {countName}({cType} value) {{");
        writer.IncreaseIndent();
        writer.WriteLine("cx_uint count = 0;");
        writer.WriteLine($"if (value.__invocation_list != NULL) for (struct {nodeType} *item = value.__invocation_list; item != NULL; item = item->next) ++count;");
        writer.WriteLine("else if (value.__function != NULL) count = 1;");
        writer.WriteLine("return count;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline {cType} {itemName}({cType} value, cx_uint index) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"if (value.__invocation_list != NULL) {{ for (struct {nodeType} *item = value.__invocation_list; item != NULL; item = item->next) {{ if (index == 0) return item->value; --index; }} }}");
        writer.WriteLine("else if (index == 0 && value.__function != NULL) return value;");
        writer.WriteLine($"return ({cType}){{ 0 }};");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline {new ArrayType(functionType).ToCIdentifier(false)} {invocationListName}({cType} value) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"cx_uint count = {countName}(value);");
        writer.WriteLine($"struct CX_ID_4(cxcore, System, Collections, Array) *result = cx_array_new(count, (cx_uint)sizeof({cType}));");
        writer.WriteLine("for (cx_uint index = 0; index < count; ++index)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"*({cType}*)cx_array_at(result, index, (cx_uint)sizeof({cType})) = {itemName}(value, index);");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("return result;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline void {appendName}({cType} value, struct {nodeType} **head, struct {nodeType} **tail) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"struct {nodeType} *source = value.__invocation_list;");
        writer.WriteLine($"cx_uint count = source == NULL ? (value.__function == NULL ? 0 : 1) : {countName}(value);");
        writer.WriteLine("for (cx_uint index = 0; index < count; ++index)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"{cType} entry = source == NULL ? value : source->value;");
        writer.WriteLine("if (source != NULL) source = source->next;");
        writer.WriteLine("entry.__invocation_list = NULL;");
        writer.WriteLine($"struct {nodeType} *copy = (struct {nodeType}*)CX_ID_4(cxcore, System, Memory, Alloc)((cx_uint)sizeof(struct {nodeType}));");
        writer.WriteLine("copy->value = entry;");
        writer.WriteLine("copy->next = NULL;");
        writer.WriteLine("if (*tail == NULL) *head = copy; else (*tail)->next = copy;");
        writer.WriteLine("*tail = copy;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline void {appendValueName}({cType} value, struct {nodeType} **head, struct {nodeType} **tail) {{");
        writer.IncreaseIndent();
        writer.WriteLine("value.__invocation_list = NULL;");
        writer.WriteLine("if (value.__function == NULL) return;");
        writer.WriteLine($"struct {nodeType} *copy = (struct {nodeType}*)CX_ID_4(cxcore, System, Memory, Alloc)((cx_uint)sizeof(struct {nodeType}));");
        writer.WriteLine("copy->value = value;");
        writer.WriteLine("copy->next = NULL;");
        writer.WriteLine("if (*tail == NULL) *head = copy; else (*tail)->next = copy;");
        writer.WriteLine("*tail = copy;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline {cType} {combineName}({cType} left, {cType} right) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"struct {nodeType} *head = NULL, *tail = NULL;");
        writer.WriteLine($"{appendName}(left, &head, &tail);");
        writer.WriteLine($"{appendName}(right, &head, &tail);");
        writer.WriteLine($"if (head == NULL) return ({cType}){{ 0 }};");
        writer.WriteLine($"if (head == tail) return head->value;");
        writer.WriteLine($"{cType} result = {{ 0 }};");
        writer.WriteLine("result.__invocation_list = head;");
        writer.WriteLine("return result;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline {cType} {removeName}({cType} source, {cType} value) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"cx_uint sourceCount = {countName}(source), valueCount = {countName}(value);");
        writer.WriteLine("if (valueCount == 0 || valueCount > sourceCount) return source;");
        writer.WriteLine("cx_uint removeAt = sourceCount;");
        writer.WriteLine("for (cx_uint start = 0; start <= sourceCount - valueCount; ++start)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("cx_bool matches = CX_TRUE;");
        writer.WriteLine("for (cx_uint offset = 0; offset < valueCount; ++offset)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"{cType} sourceItem = {itemName}(source, start + offset);");
        writer.WriteLine($"{cType} valueItem = {itemName}(value, offset);");
        writer.WriteLine("if (sourceItem.__function != valueItem.__function || sourceItem.__environment != valueItem.__environment) matches = CX_FALSE;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("if (matches) removeAt = start;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("if (removeAt == sourceCount) return source;");
        writer.WriteLine($"struct {nodeType} *head = NULL, *tail = NULL;");
        writer.WriteLine("for (cx_uint index = 0; index < sourceCount; ++index)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("if (index >= removeAt && index < removeAt + valueCount) continue;");
        writer.WriteLine($"{appendValueName}({itemName}(source, index), &head, &tail);");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("if (head == NULL) return (" + cType + "){ 0 };");
        writer.WriteLine("if (head == tail) return head->value;");
        writer.WriteLine($"{cType} result = {{ 0 }};");
        writer.WriteLine("result.__invocation_list = head;");
        writer.WriteLine("return result;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        writer.WriteLine($"static inline cx_bool {equalName}({cType} left, {cType} right) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"cx_uint count = {countName}(left);");
        writer.WriteLine($"if (count != {countName}(right)) return CX_FALSE;");
        writer.WriteLine("for (cx_uint index = 0; index < count; ++index)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"{cType} leftItem = {itemName}(left, index);");
        writer.WriteLine($"{cType} rightItem = {itemName}(right, index);");
        writer.WriteLine("if (leftItem.__function != rightItem.__function || leftItem.__environment != rightItem.__environment) return CX_FALSE;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("return CX_TRUE;");
        writer.DecreaseIndent();
        writer.WriteLine("}");

        var invokeParameters = new[] { $"{cType} value" }.Concat(parameters);
        writer.WriteLine($"static inline {functionType.ReturnType.ToCReturnType(false)} {invokeName}(");
        writer.IncreaseIndent();
        writer.WriteLine(string.Join(",\n", invokeParameters));
        writer.DecreaseIndent();
        writer.WriteLine(") {");
        writer.IncreaseIndent();
        writer.WriteLine($"if ({nullName}(value)) abort();");
        if (functionType.ReturnType is VoidType)
        {
            writer.WriteLine($"if (value.__invocation_list != NULL) for (struct {nodeType} *item = value.__invocation_list; item != NULL; item = item->next) item->value.__function(item->value.__environment{callSuffix});");
            writer.WriteLine($"else value.__function(value.__environment{callSuffix});");
        }
        else
        {
            var returnType = functionType.ReturnType.ToCIdentifier(false);
            writer.WriteLine($"{returnType} result = {{ 0 }};");
            writer.WriteLine($"if (value.__invocation_list != NULL) for (struct {nodeType} *item = value.__invocation_list; item != NULL; item = item->next) result = item->value.__function(item->value.__environment{callSuffix});");
            writer.WriteLine($"else result = value.__function(value.__environment{callSuffix});");
            writer.WriteLine("return result;");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private static string GetFunctionTypeNodeCIdentifier(FunctionType functionType) =>
        $"{GetFunctionTypeCIdentifier(functionType)}_invocation";

    private static FunctionType? GetFunctionType(TypeBase? type) => type switch
    {
        ConstType constant => GetFunctionType(constant.UnderlyingType),
        NullableType nullable => GetFunctionType(nullable.UnderlyingType),
        FunctionType function => function,
        NamedType { IsDelegate: true, DelegateSignature: { } signature } => signature,
        _ => null,
    };

    private static string GetFunctionTypeHelperName(
        FunctionType functionType,
        string operation) =>
        $"__cx_delegate_{operation}_{GetFunctionTypeCIdentifier(functionType)}";

    private static void WriteMethodValueAdapter(
        IndentingWriter writer,
        MemberAccessExpression methodValue,
        string moduleName)
    {
        var method = methodValue.FunctionValueSymbol ?? throw new InternalCompilerException(
            "Method value has no bound function.");
        var receiverType = GetUnwrappedCaptureType(methodValue.Target.InferredType ??
            throw new InternalCompilerException("Method value receiver is not bound."));
        var parameters = method.ParameterTypes.Select((type, index) =>
            $"{ToCParameterType(type)} __cx_arg_{index}").ToArray();
        var signature = new[] { "void *__cx_environment" }.Concat(parameters);
        writer.WriteLine($"static {method.ReturnType.ToCReturnType(false)} " +
            $"{methodValue.FunctionValueAdapterName}(");
        writer.IncreaseIndent();
        writer.WriteLine(string.Join(",\n", signature));
        writer.DecreaseIndent();
        writer.WriteLine(") {");
        writer.IncreaseIndent();
        writer.WriteLine($"struct {methodValue.FunctionValueEnvironmentName} *__cx_closure = " +
            $"(struct {methodValue.FunctionValueEnvironmentName}*)__cx_environment;");

        string call;
        if (receiverType is NamedType { ClassType: ClassType.Interface } &&
            method.Declaration?.IsExtension != true)
        {
            var dispatchSlot = methodValue.InterfaceDispatchSlotIndex ??
                throw new InternalCompilerException("Interface method value has no dispatch slot.");
            var parameterTypes = new[] { "cx_ptr" }
                .Concat(method.ParameterTypes.Select(type => type.ToCIdentifier(false)));
            var functionPointer =
                $"({method.ReturnType.ToCReturnType(false)} (*)({string.Join(", ", parameterTypes)}))";
            var arguments = new[] { "__cx_closure->receiver.instance" }
                .Concat(parameters.Select((_, index) => $"__cx_arg_{index}"));
            call = $"({functionPointer}((union cx_vtable_entry*)" +
                "__cx_closure->receiver.vtable)[" + dispatchSlot + "].function)(" +
                string.Join(", ", arguments) + ")";
        }
        else if (receiverType is NamedType { ClassType: ClassType.Interface })
        {
            var arguments = new[] { "__cx_closure->receiver" }
                .Concat(parameters.Select((_, index) => $"__cx_arg_{index}"));
            call = $"{ToCIdentifier(method)}({string.Join(", ", arguments)})";
        }
        else
        {
            var receiver = receiverType is NamedType { ClassType: ClassType.Struct }
                ? "&__cx_closure->receiver"
                : "__cx_closure->receiver";
            var receiverArgument = ToCBaseReceiver(receiver, true, methodValue.ReceiverBaseDepth);
            var arguments = new[] { receiverArgument }
                .Concat(parameters.Select((_, index) => $"__cx_arg_{index}"))
                .ToArray();
            if (method.Declaration is { VirtualSlotIndex: { } slotIndex } declaration)
            {
                var contract = declaration.VirtualContract ?? declaration;
                var parentClass = contract.ParentClassDeclaration!;
                var parameterTypes = new[]
                {
                    ToCReceiverType(parentClass, parentClass.ProjectName ?? moduleName, contract.Const),
                }.Concat(contract.Parameters.Select(parameter =>
                    parameter.ParameterType.ToCIdentifier(false)));
                var functionPointer =
                    $"({contract.ReturnType.ToCReturnType(false)} (*)({string.Join(", ", parameterTypes)}))";
                call = $"({functionPointer}((union cx_vtable_entry*)CX_GET_VTABLE({receiverArgument}))" +
                    $"[{slotIndex}].function)({string.Join(", ", arguments)})";
            }
            else
            {
                call = $"{ToCIdentifier(method)}({string.Join(", ", arguments)})";
            }
        }

        writer.WriteLine(method.ReturnType is VoidType ? $"{call};" : $"return {call};");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static string ToCLambdaValue(
        LambdaExpression lambda,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var generated = lambda.GeneratedFunction ?? throw new InternalCompilerException(
            "Lambda expression is not bound.");
        var functionType = lambda.InferredType as FunctionType ?? throw new InternalCompilerException(
            "Lambda function type is not bound.");
        var environment = ToCCaptureEnvironment(lambda, functionDeclaration);
        return $"(({GetFunctionTypeCIdentifier(functionType)}){{ " +
            $".__function = &{new QualifiedIdentifier(moduleName, generated.LocalCName!).ToCIdentifier()}, " +
            $".__environment = {environment} }})";
    }

    private static TypeBase GetUnwrappedCaptureType(TypeBase type) =>
        type is ConstType constType ? constType.UnderlyingType : type;

    private static string ToCCaptureEnvironment(
        LambdaExpression lambda,
        FunctionDeclaration functionDeclaration)
    {
        if (lambda.Captures.Count == 0)
        {
            return "NULL";
        }
        var environmentType = GetLambdaEnvironmentCIdentifier(lambda);
        var fields = lambda.Captures.Select(capture =>
        {
            var pointer = GetCaptureCellPointer(capture.Local, functionDeclaration);
            if (capture.Local.IsThis &&
                GetUnwrappedCaptureType(capture.Local.Type) is NamedType
                {
                    ClassType: ClassType.Struct,
                })
            {
                return $".{capture.EnvironmentFieldName} = *({pointer})";
            }
            return $".{capture.EnvironmentFieldName} = (void*)({pointer})";
        });
        return $"memcpy(CX_ID_4(cxcore, System, Memory, Alloc)((cx_uint)sizeof(struct {environmentType})), " +
            $"&(struct {environmentType}){{ {string.Join(", ", fields)} }}, " +
            $"sizeof(struct {environmentType}))";
    }

    private static string ToCFunctionValue(
        IdentifierExpression identifier,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var functionType = identifier.InferredType as FunctionType ?? throw new InternalCompilerException(
            "Function value type is not bound.");
        var symbol = identifier.FunctionValueSymbol!;
        var adapterName = GetFunctionValueAdapterCIdentifier(symbol);
        var environment = (symbol.ImplementationDeclaration ?? symbol.Declaration)?.LambdaOwner
            is { } lambdaOwner
            ? ToCCaptureEnvironment(lambdaOwner, functionDeclaration)
            : "NULL";
        return $"(({GetFunctionTypeCIdentifier(functionType)}){{ " +
            $".__function = &{adapterName}, .__environment = {environment} }})";
    }

    private static string ToCMethodGroupValue(
        MemberAccessExpression methodValue,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var functionType = methodValue.InferredType as FunctionType ??
            throw new InternalCompilerException("Method value type is not bound.");
        var adapterName = methodValue.FunctionValueAdapterName ??
            throw new InternalCompilerException("Method value adapter is not bound.");
        var environmentType = methodValue.FunctionValueEnvironmentName ??
            throw new InternalCompilerException("Method value environment is not bound.");
        var receiverTemporary = methodValue.FunctionValueReceiverTemporaryName ??
            throw new InternalCompilerException("Method value receiver temporary is not bound.");
        var receiverType = GetUnwrappedCaptureType(methodValue.Target.InferredType ??
            throw new InternalCompilerException("Method value receiver is not bound."));
        var receiver = ToCExpression(methodValue.Target, functionDeclaration, moduleName);
        if (receiverType is NamedType { ClassType: ClassType.Struct } &&
            methodValue.Target is ThisExpression)
        {
            receiver = $"*({receiver})";
        }
        var environment = $"memcpy(CX_ID_4(cxcore, System, Memory, Alloc)(" +
            $"(cx_uint)sizeof(struct {environmentType})), " +
            $"&(struct {environmentType}){{ .receiver = {receiverTemporary} }}, " +
            $"sizeof(struct {environmentType}))";
        var closure = $"(({GetFunctionTypeCIdentifier(functionType)}){{ " +
            $".__function = &{adapterName}, .__environment = {environment} }})";
        return $"({receiverTemporary} = {receiver}, {closure})";
    }

    private static string ToCStaticMethodGroupValue(MemberAccessExpression methodValue)
    {
        var method = methodValue.FunctionValueSymbol ?? throw new InternalCompilerException(
            "Static method value has no bound function.");
        var functionType = methodValue.InferredType as FunctionType ??
            throw new InternalCompilerException("Static method value type is not bound.");
        var adapterName = GetFunctionValueAdapterCIdentifier(method);
        return $"(({GetFunctionTypeCIdentifier(functionType)}){{ " +
            $".__function = &{adapterName}, .__environment = NULL }})";
    }

    private static string ToCLocal(LocalVariableSymbol local)
    {
        return local.IsCaptured ? $"(*{local.CellName})" : local.Name;
    }

    private static string ToCCapturedLocal(
        LambdaCapture capture,
        FunctionDeclaration functionDeclaration)
    {
        var environmentType = GetLambdaEnvironmentCIdentifier(
            functionDeclaration.LambdaOwner ?? throw new InternalCompilerException(
                "A captured local is referenced outside a lambda helper."));
        var type = capture.Local.Type.ToCIdentifier(false);
        return $"(*(({type}*)((struct {environmentType}*)__cx_environment)->" +
            $"{capture.EnvironmentFieldName}))";
    }

    private static string ToCCapturedThis(
        LambdaCapture capture,
        FunctionDeclaration functionDeclaration) =>
        GetCapturedThisPointer(capture.Local, functionDeclaration);

    private static string GetCapturedThisPointer(
        LocalVariableSymbol thisSymbol,
        FunctionDeclaration functionDeclaration)
    {
        if (functionDeclaration.LambdaOwner is { } lambdaOwner)
        {
            var capture = lambdaOwner.Captures.FirstOrDefault(item =>
                ReferenceEquals(item.Local, thisSymbol));
            if (capture is null)
            {
                throw new InternalCompilerException("Lambda uses 'this' without capturing it.");
            }
            var environmentType = GetLambdaEnvironmentCIdentifier(lambdaOwner);
            if (GetUnwrappedCaptureType(thisSymbol.Type) is NamedType
                {
                    ClassType: ClassType.Struct,
                })
            {
                return $"(({thisSymbol.Type.ToCIdentifier(false)}*)&((struct {environmentType}*)" +
                    $"__cx_environment)->{capture.EnvironmentFieldName})";
            }
            var cType = thisSymbol.Type.ToCIdentifier(false);
            return $"(({cType})((struct {environmentType}*)__cx_environment)->" +
                capture.EnvironmentFieldName + ")";
        }
        if (functionDeclaration.ParentClassDeclaration is not null)
        {
            return "__this";
        }
        throw new InternalCompilerException("'this' has no enclosing instance.");
    }

    private static string GetCaptureCellPointer(
        LocalVariableSymbol local,
        FunctionDeclaration functionDeclaration)
    {
        if (local.IsThis)
        {
            return GetCapturedThisPointer(local, functionDeclaration);
        }
        if (ReferenceEquals(local.OwnerLambda, functionDeclaration.LambdaOwner))
        {
            return local.CellName;
        }
        if (functionDeclaration.LambdaOwner is { } enclosingLambda)
        {
            var parentCapture = enclosingLambda.Captures.FirstOrDefault(capture =>
                ReferenceEquals(capture.Local, local));
            if (parentCapture is not null)
            {
                var environmentType = GetLambdaEnvironmentCIdentifier(enclosingLambda);
                return $"((struct {environmentType}*)__cx_environment)->{parentCapture.EnvironmentFieldName}";
            }
        }
        return local.CellName;
    }

    private static string GetLambdaEnvironmentCIdentifier(LambdaExpression lambda)
    {
        var functionName = lambda.GeneratedFunction?.LocalCName ??
            throw new InternalCompilerException("Lambda environment has no generated function.");
        return $"{functionName}_environment";
    }

    private static string GetFunctionValueAdapterCIdentifier(FunctionSymbol function)
    {
        var identifier = ToCIdentifier(function);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)));
        return $"__cx_function_adapter_{hash[..16]}";
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
            moduleName,
            expression.InterfaceDispatchType);
    }

    private static string ToCInvocation(
        InvocationExpression invocation,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (invocation.FunctionType is not null)
        {
            var temporaryName = invocation.FunctionValueTemporaryName ??
                throw new InternalCompilerException("Function value invocation has no temporary.");
            if (invocation.IsDelegateInvocationList)
            {
                var receiver = invocation.DelegateInvocationListReceiver ??
                    throw new InternalCompilerException(
                        "Delegate invocation-list access has no receiver.");
                var helper = GetFunctionTypeHelperName(
                    invocation.FunctionType,
                    "get_invocation_list");
                return $"({temporaryName} = " +
                    $"{ToCExpression(receiver, functionDeclaration, moduleName)}, " +
                    $"{helper}({temporaryName}))";
            }

            var callArguments = string.Join(", ", invocation.Arguments
                .Zip(invocation.FunctionType.ParameterTypes)
                .Select(pair => ToCExpressionAsType(
                    pair.First,
                    pair.Second,
                    functionDeclaration,
                    moduleName)));
            var call = $"{GetFunctionTypeHelperName(invocation.FunctionType, "invoke")}" +
                $"({temporaryName}{(callArguments.Length == 0 ? string.Empty : $", {callArguments}")})";
            return $"({temporaryName} = {ToCExpression(invocation.Target, functionDeclaration, moduleName)}, {call})";
        }
        var target = invocation.TargetSymbol ?? throw new InternalCompilerException(
            "Invocation target is not bound.");
        if (target.Declaration is { } arrayMethod &&
            arrayMethod.ParentClassDeclaration is { } arrayOwner &&
            IsPublicArrayDeclaration(arrayOwner) &&
            arrayMethod.Name is "Get" or "Set" &&
            invocation.Receiver is { } arrayReceiver &&
            invocation.Arguments.Count == (arrayMethod.Name == "Get" ? 1 : 2))
        {
            var elementType = arrayMethod.Name == "Get"
                ? invocation.InferredType ?? target.ReturnType
                : invocation.Arguments[1].InferredType ?? target.ParameterTypes[1];
            var elementCType = elementType is GenericType
                ? "cx_ptr"
                : elementType.ToCIdentifier(false);
            var receiver = ToCExpression(arrayReceiver, functionDeclaration, moduleName);
            var index = ToCExpression(invocation.Arguments[0], functionDeclaration, moduleName);
            var access = $"(*({elementCType}*)cx_array_at({receiver}, " +
                $"(cx_uint)({index}), (cx_uint)sizeof({elementCType})))";
            if (arrayMethod.Name == "Get")
            {
                return access;
            }

            var value = ToCExpressionAsType(
                invocation.Arguments[1], elementType, functionDeclaration, moduleName);
            return $"(({access} = {value}), (void)0)";
        }
        if (invocation.DispatchSlotIndex is { } interfaceSlotIndex)
        {
            var interfaceReceiver = invocation.Receiver ?? throw new InternalCompilerException(
                "Interface invocation has no receiver.");
            var receiver = ToCExpression(
                interfaceReceiver,
                functionDeclaration,
                moduleName);
            var constrainedInterface = invocation.DispatchInterfaceType;
            var closedReceiverType = interfaceReceiver.ClosedGenericType is
                ConstType closedReceiverConst
                    ? closedReceiverConst.UnderlyingType
                    : interfaceReceiver.ClosedGenericType;
            if (constrainedInterface is not null &&
                closedReceiverType is NamedType
                {
                    ClassType: ClassType.Struct,
                } structReceiverType)
            {
                var vtable = structReceiverType.ConstructedIdentity is { } identity
                    ? GetClosedGenericInterfaceVTableIdentifier(
                        identity.CIdentifier,
                        moduleName,
                        constrainedInterface.ResolvedTypeFullName).ToCIdentifier()
                    : GetInterfaceVTableIdentifier(
                        structReceiverType.ResolvedTypeFullName,
                        constrainedInterface.ResolvedTypeFullName).ToCIdentifier();
                receiver = $"(struct cx_iface_ref){{ {vtable}, (cx_ptr)&({receiver}) }}";
            }
            else if (constrainedInterface is not null &&
                (interfaceReceiver.InferredType is GenericType ||
                    interfaceReceiver.InferredType is
                        ConstType { UnderlyingType: GenericType }))
            {
                receiver = $"cx_checked_cast_object_to_interface((cx_ptr)({receiver}), " +
                    $"&{GetTypeInfoIdentifier(constrainedInterface).ToCIdentifier()})";
            }
            var receiverTemporary = invocation.ReceiverTemporaryName ??
                throw new InternalCompilerException(
                    "Interface invocation receiver temporary is not bound.");
            var interfaceArgumentsByName = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
            if (constrainedInterface is not null &&
                target.Declaration?.ParentClassDeclaration is
                    { ClassType: ClassType.Interface } contractInterface &&
                contractInterface.Name == constrainedInterface.Name &&
                contractInterface.GenericTypeNames.Length ==
                    constrainedInterface.TypeArguments.Count)
            {
                interfaceArgumentsByName = contractInterface.GenericTypeNames
                    .Zip(constrainedInterface.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second,
                        StringComparer.Ordinal);
            }
            if (constrainedInterface is not null &&
                invocation.OpenDispatchInterfaceType is { } openDispatchInterface &&
                openDispatchInterface.TypeArguments.Count ==
                    constrainedInterface.TypeArguments.Count)
            {
                foreach (var pair in openDispatchInterface.TypeArguments
                    .Zip(constrainedInterface.TypeArguments))
                {
                    if (pair.First is GenericType openGenericArgument)
                    {
                        interfaceArgumentsByName[openGenericArgument.Name] = pair.Second;
                    }
                }
            }
            var interfaceParameterTypes = target.ParameterTypes.Select(parameterType =>
                GenericTypeSubstitution.Substitute(
                    parameterType, interfaceArgumentsByName)).ToArray();
            var interfaceReturnType = GenericTypeSubstitution.Substitute(
                target.ReturnType, interfaceArgumentsByName);
            var convertedArguments = invocation.Arguments.Zip(interfaceParameterTypes).Select(pair =>
                ToCExpressionAsType(pair.First, pair.Second, functionDeclaration, moduleName));
            var interfaceParameterDeclarations = new[] { "cx_ptr" }.Concat(
                interfaceParameterTypes.Select(type => type.ToCIdentifier(false)));
            var interfaceFunctionPointer =
                $"({interfaceReturnType.ToCReturnType(false)} (*)({string.Join(", ", interfaceParameterDeclarations)}))";
            var interfaceArguments = new[] { $"({receiverTemporary}).instance" }
                .Concat(convertedArguments);
            var call = $"({interfaceFunctionPointer}((union cx_vtable_entry*)" +
                $"({receiverTemporary}).vtable)[{interfaceSlotIndex}].function)" +
                $"({string.Join(", ", interfaceArguments)})";
            return $"({receiverTemporary} = {receiver}, {call})";
        }

        var arguments = new List<string>();
        if ((target.ImplementationDeclaration ?? target.Declaration)?.LambdaOwner is
            { } targetLambdaOwner)
        {
            arguments.Add(ToCCaptureEnvironment(targetLambdaOwner, functionDeclaration));
        }
        if (target.Declaration is { IsStatic: false })
        {
            if (invocation.Receiver is null)
            {
                var implicitReceiver = functionDeclaration.LambdaOwner?.ThisSymbol is { } thisSymbol
                    ? GetCapturedThisPointer(thisSymbol, functionDeclaration)
                    : "__this";
                arguments.Add(target.Declaration is
                    { IsExtension: true, ExtensionTargetType: { } extensionTarget } &&
                    IsPrimitiveExtensionReceiverType(extensionTarget)
                        ? implicitReceiver
                        : ToCBaseReceiver(
                            implicitReceiver,
                            true,
                            invocation.ReceiverBaseDepth));
            }
            else
            {
                var receiver = ToCExpression(
                    invocation.Receiver,
                    functionDeclaration,
                    moduleName);
                var constrainedReceiverParameter = invocation.Receiver.InferredType switch
                {
                    GenericType generic => generic,
                    ConstType { UnderlyingType: GenericType generic } => generic,
                    _ => null,
                };
                var constrainedReceiverType = constrainedReceiverParameter is null
                    ? null
                    : GetSimpleFunctionClassConstraint(
                        functionDeclaration, constrainedReceiverParameter.Name);
                var receiverArgument = constrainedReceiverType is null
                    ? ToCBaseReceiver(
                        receiver,
                        IsCPointerReceiver(invocation.Receiver),
                        invocation.ReceiverBaseDepth)
                    : ToCBaseReceiver(
                        $"(({constrainedReceiverType.ToCIdentifier(false)})({receiver}))",
                        true,
                        invocation.ReceiverBaseDepth);
                arguments.Add(target.Declaration is
                    { IsExtension: true, ExtensionTargetType: { } extensionTarget } &&
                    IsPrimitiveExtensionReceiverType(extensionTarget)
                        ? ToCExpressionAsType(
                            invocation.Receiver,
                            extensionTarget,
                            functionDeclaration,
                            moduleName)
                    : target.Declaration is
                    { IsExtension: true, ParentClassDeclaration.ClassType: ClassType.Interface }
                        ? receiver
                        : receiverArgument);
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
        var parentClass = contract.ParentClassDeclaration!;
        var parameterTypes = new List<string>
        {
            ToCReceiverType(parentClass, parentClass.ProjectName ?? moduleName, contract.Const),
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
        if (expression.PropertyGetter is { } getter)
        {
            var receiverExpression = receiver is null
                ? null
                : ToCExpression(receiver, functionDeclaration, moduleName);
            var compoundReceiverType = receiver?.ClosedGenericType ?? receiver?.InferredType;
            var compoundEffectiveReceiverType = compoundReceiverType is null
                ? null
                : UnwrapConst(compoundReceiverType);
            var compoundReceiverIsStruct = compoundEffectiveReceiverType is
                NamedType { ClassType: ClassType.Struct };
            var compoundReceiverIsStructStorage = compoundReceiverIsStruct &&
                receiver is not null &&
                (receiver is ThisExpression || IsCStorageLocation(receiver));
            var compoundReceiverExpression = expression.PropertyReceiverTemporaryName is
                { } compoundReceiverTemporary
                    ? compoundReceiverIsStructStorage
                        ? $"*{compoundReceiverTemporary}"
                        : compoundReceiverTemporary
                    : receiverExpression;
            var getterCall = ToCPropertyCall(
                expression.TargetProperty!,
                getter,
                receiver,
                [],
                null,
                expression.ReceiverBaseDepth,
                expression.PropertyGetterInterfaceDispatchSlotIndex,
                expression.PropertyGetterInterfaceReceiverTemporaryName,
                functionDeclaration,
                moduleName,
                expression.PropertyGetterInterfaceDispatchType,
                compoundReceiverExpression,
                expression.PropertyIndexTemporaryNames);
            var propertyFunctionType = GetFunctionType(expression.TargetProperty!.Type);
            var operation = propertyFunctionType is not null &&
                expression.Operator is ("+=" or "-=")
                ? $"{temporaryName} = {GetFunctionTypeHelperName(propertyFunctionType, expression.Operator == "+=" ? "combine" : "remove")}(" +
                    $"{temporaryName}, {ToCExpressionAsType(expression.Value, propertyFunctionType, functionDeclaration, moduleName)})"
                : expression.OperatorSymbol is { } symbol
                ? ToCPropertyCompoundOperator(
                    expression,
                    symbol,
                    functionDeclaration,
                    moduleName)
                : $"{temporaryName} {expression.Operator} " +
                    ToCExpression(expression.Value, functionDeclaration, moduleName);
            var compoundSetterCall = ToCPropertyCall(
                expression.TargetProperty!,
                expression.PropertySetter!,
                receiver,
                [],
                temporaryName,
                expression.ReceiverBaseDepth,
                expression.InterfaceDispatchSlotIndex,
                expression.InterfaceReceiverTemporaryName,
                functionDeclaration,
                moduleName,
                expression.InterfaceDispatchType,
                compoundReceiverExpression,
                expression.PropertyIndexTemporaryNames);
            var evaluationSetups = new List<string>();
            if (expression.PropertyReceiverTemporaryName is { } receiverTemporary)
            {
                var receiverSetupValue = compoundReceiverIsStructStorage &&
                    receiver is not ThisExpression
                        ? $"&({receiverExpression})"
                        : receiverExpression;
                evaluationSetups.Add($"{receiverTemporary} = {receiverSetupValue}");
            }
            for (var index = 0; index < indexExpressions.Count; index++)
            {
                var parameterType = getter.ParameterTypes[index];
                evaluationSetups.Add(
                    $"{expression.PropertyIndexTemporaryNames[index]} = " +
                    ToCExpressionAsType(
                        indexExpressions[index],
                        parameterType,
                        functionDeclaration,
                        moduleName));
            }
            var evaluationSetup = evaluationSetups.Count == 0
                ? string.Empty
                : string.Join(", ", evaluationSetups) + ", ";
            var assignmentResult = propertyFunctionType is null
                ? temporaryName
                : "(void)0";
            return $"({evaluationSetup}{temporaryName} = {getterCall}, {operation}, {compoundSetterCall}, " +
                $"{assignmentResult})";
        }

        var setterEvaluationSetups = new List<string>();
        var receiverType = receiver?.ClosedGenericType ?? receiver?.InferredType;
        var effectiveReceiverType = receiverType is null ? null : UnwrapConst(receiverType);
        var receiverIsStruct = effectiveReceiverType is NamedType { ClassType: ClassType.Struct };
        var receiverIsStructStorage = receiverIsStruct && receiver is not null &&
            (receiver is ThisExpression || IsCStorageLocation(receiver));
        if (expression.PropertyReceiverTemporaryName is { } setterReceiverTemporary)
        {
            var receiverValue = ToCExpression(receiver!, functionDeclaration, moduleName);
            var receiverSetupValue = receiverIsStructStorage && receiver is not ThisExpression
                ? $"&({receiverValue})"
                : receiverValue;
            setterEvaluationSetups.Add(
                $"{setterReceiverTemporary} = {receiverSetupValue}");
        }
        for (var index = 0; index < indexExpressions.Count; index++)
        {
            var parameterType = expression.PropertySetter!.ParameterTypes[index];
            setterEvaluationSetups.Add(
                $"{expression.PropertyIndexTemporaryNames[index]} = " +
                ToCExpressionAsType(
                    indexExpressions[index],
                    parameterType,
                    functionDeclaration,
                    moduleName));
        }
        setterEvaluationSetups.Add(
            $"{temporaryName} = " + ToCExpressionAsType(
                expression.Value,
                expression.TargetProperty!.Type,
                functionDeclaration,
                moduleName));
        var capturedReceiver = expression.PropertyReceiverTemporaryName is { } capturedReceiverTemporary
            ? receiverIsStructStorage
                ? $"*{capturedReceiverTemporary}"
                : capturedReceiverTemporary
            : receiver is null ? null : ToCExpression(receiver, functionDeclaration, moduleName);
        var setterCall = ToCPropertyCall(
            expression.TargetProperty!,
            expression.PropertySetter!,
            receiver,
            [],
            temporaryName,
            expression.ReceiverBaseDepth,
            expression.InterfaceDispatchSlotIndex,
            expression.InterfaceReceiverTemporaryName,
            functionDeclaration,
            moduleName,
            expression.InterfaceDispatchType,
            capturedReceiver,
            expression.PropertyIndexTemporaryNames);
        setterEvaluationSetups.Add(setterCall);
        setterEvaluationSetups.Add(temporaryName);
        return $"({string.Join(", ", setterEvaluationSetups)})";
    }

    private static string ToCPropertyMutation(
        UnaryExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var property = expression.TargetProperty ?? throw new InternalCompilerException(
            "Property mutation has no target property.");
        var getter = expression.PropertyGetter ?? throw new InternalCompilerException(
            "Property mutation has no getter.");
        var setter = expression.PropertySetter ?? throw new InternalCompilerException(
            "Property mutation has no setter.");
        var valueTemporary = expression.OperatorTemporaryName ?? throw new InternalCompilerException(
            "Property mutation value temporary is not bound.");
        var indexExpressions = expression.Operand is ArrayAccessExpression indexed
            ? indexed.Indices
            : [];
        var propertyExpression = expression.Operand is ArrayAccessExpression arrayAccess
            ? arrayAccess.Target
            : expression.Operand;
        var receiver = propertyExpression is MemberAccessExpression memberAccess && !property.IsStatic
            ? memberAccess.Target
            : null;
        var receiverExpression = receiver is null
            ? null
            : ToCExpression(receiver, functionDeclaration, moduleName);
        var capturedReceiver = expression.PropertyReceiverTemporaryName ?? receiverExpression;
        var indexOverrides = expression.PropertyIndexTemporaryNames;
        var setups = new List<string>();
        if (expression.PropertyReceiverTemporaryName is { } receiverTemporary)
        {
            setups.Add($"{receiverTemporary} = {receiverExpression}");
        }
        for (var index = 0; index < indexExpressions.Count; index++)
        {
            setups.Add($"{indexOverrides[index]} = " + ToCExpressionAsType(
                indexExpressions[index],
                getter.ParameterTypes[index],
                functionDeclaration,
                moduleName));
        }
        var getterCall = ToCPropertyCall(
            property,
            getter,
            receiver,
            [],
            null,
            expression.PropertyReceiverBaseDepth,
            expression.PropertyGetterInterfaceDispatchSlotIndex,
            expression.PropertyGetterInterfaceReceiverTemporaryName,
            functionDeclaration,
            moduleName,
            expression.PropertyGetterInterfaceDispatchType,
            capturedReceiver,
            indexOverrides);
        setups.Add($"{valueTemporary} = {getterCall}");

        var postfixResult = expression.Postfix
            ? expression.OperatorOldValueTemporaryName ?? throw new InternalCompilerException(
                "Postfix property mutation has no old-value temporary.")
            : null;
        if (postfixResult is not null)
        {
            setups.Add($"{postfixResult} = {valueTemporary}");
        }

        if (expression.OperatorSymbol is { } operatorSymbol)
        {
            var operatorCall = operatorSymbol.Declaration?.IsExtension == true
                ? $"{ToCIdentifier(operatorSymbol)}({valueTemporary})"
                : $"{ToCIdentifier(operatorSymbol)}(" +
                    $"{ToCBaseReceiver(valueTemporary, IsCReferenceType(property.Type), expression.OperatorBaseDepth)})";
            if (operatorSymbol.Declaration?.IsExtension == true ||
                operatorSymbol.ReturnType is not VoidType)
            {
                setups.Add($"{valueTemporary} = {operatorCall}");
            }
            else
            {
                setups.Add(operatorCall);
            }
        }
        else
        {
            setups.Add($"{valueTemporary}{expression.Operator}");
        }

        var setterCall = ToCPropertyCall(
            property,
            setter,
            receiver,
            [],
            valueTemporary,
            expression.PropertyReceiverBaseDepth,
            expression.PropertySetterInterfaceDispatchSlotIndex,
            expression.PropertySetterInterfaceReceiverTemporaryName,
            functionDeclaration,
            moduleName,
            expression.PropertySetterInterfaceDispatchType,
            capturedReceiver,
            indexOverrides);
        setups.Add(setterCall);
        setups.Add(postfixResult ?? valueTemporary);
        return $"({string.Join(", ", setups)})";
    }

    private static string ToCPropertyCompoundOperator(
        AssignmentExpression expression,
        FunctionSymbol symbol,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var temporaryName = expression.TemporaryName ?? throw new InternalCompilerException(
            "Compound property assignment temporary is not bound.");
        var type = expression.TargetProperty!.Type;
        var value = ToCExpressionAsType(
            expression.Value,
            symbol.ParameterTypes.Single(),
            functionDeclaration,
            moduleName);
        var call = symbol.Declaration?.IsExtension == true
            ? $"{ToCIdentifier(symbol)}({temporaryName}, {value})"
            : $"{ToCIdentifier(symbol)}(" +
                $"{ToCBaseReceiver(temporaryName, IsCReferenceType(type), expression.OperatorBaseDepth)}, " +
                $"{value})";
        return symbol.ReturnType is VoidType
            ? call
            : $"{temporaryName} = {call}";
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
        string moduleName,
        NamedType? interfaceDispatchType = null,
        string? receiverExpressionOverride = null,
        IReadOnlyList<string>? indexExpressionOverrides = null)
    {
        var receiverExpression = receiver is null
            ? null
            : receiverExpressionOverride ?? ToCExpression(receiver, functionDeclaration, moduleName);
        if (interfaceDispatchSlotIndex is { } slotIndex)
        {
            if (receiver is null || interfaceReceiverTemporaryName is null)
            {
                throw new InternalCompilerException("Interface property dispatch is not bound.");
            }
            var closedReceiverType = receiver.ClosedGenericType is ConstType closedReceiverConst
                ? closedReceiverConst.UnderlyingType
                : receiver.ClosedGenericType;
            if (interfaceDispatchType is not null &&
                closedReceiverType is NamedType
                {
                    ClassType: ClassType.Struct,
                } structReceiverType)
            {
                var vtable = structReceiverType.ConstructedIdentity is { } identity
                    ? GetClosedGenericInterfaceVTableIdentifier(
                        identity.CIdentifier,
                        moduleName,
                        interfaceDispatchType.ResolvedTypeFullName).ToCIdentifier()
                    : GetInterfaceVTableIdentifier(
                        structReceiverType.ResolvedTypeFullName,
                        interfaceDispatchType.ResolvedTypeFullName).ToCIdentifier();
                receiverExpression =
                    $"(struct cx_iface_ref){{ {vtable}, (cx_ptr)&({receiverExpression}) }}";
            }
            else if (interfaceDispatchType is not null &&
                (receiver.InferredType is GenericType ||
                    receiver.InferredType is ConstType { UnderlyingType: GenericType }))
            {
                receiverExpression =
                    $"cx_checked_cast_object_to_interface((cx_ptr)({receiverExpression}), " +
                    $"&{GetTypeInfoIdentifier(interfaceDispatchType).ToCIdentifier()})";
            }
            var convertedIndices = indexExpressionOverrides ?? indexArguments
                .Zip(accessor.ParameterTypes).Select(pair =>
                    ToCExpressionAsType(pair.First, pair.Second, functionDeclaration, moduleName))
                .ToArray();
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
            return $"({interfaceReceiverTemporaryName} = {receiverExpression!}, {call})";
        }

        if (value is null && receiver is not null && IsCStorageLocation(receiver) &&
            receiver.InferredType is NamedType
            {
                ClassType: ClassType.Struct,
                ConstructedIdentity: { } closedGenericFieldLocalIdentity,
            } && accessor.Declaration is { } genericFieldLocalAccessor &&
            genericFieldLocalAccessor.ParentPropertyDeclaration.ParentClassDeclaration is
                { GenericTypeNames.Length: > 0 } genericFieldLocalDeclaration &&
            IsClosedGenericGenericFieldLocalAccessor(
                genericFieldLocalAccessor,
                genericFieldLocalDeclaration,
                genericFieldLocalDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            var initializer = GetClosedGenericGenericFieldLocalInitializer(
                genericFieldLocalAccessor);
            var function = genericFieldLocalAccessor.BodyFunction ??
                throw new InternalCompilerException(
                    $"Supported generic-result property '{genericFieldLocalAccessor.FullName}' has no body function.");
            var closedResultType = GenericTypeSubstitution.Substitute(
                genericFieldLocalAccessor.ParentPropertyDeclaration.Type,
                genericFieldLocalDeclaration.GenericTypeNames
                    .Zip(((NamedType)receiver.InferredType).TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
            var instance = $"((struct {closedGenericFieldLocalIdentity.CIdentifier}*)&({receiverExpression}))";
            return ToCClosedGenericGenericResultExpression(
                initializer,
                closedResultType,
                function,
                closedGenericFieldLocalIdentity.CIdentifier,
                moduleName,
                instance);
        }

        var closedPropertyReceiverType = receiver?.ClosedGenericType is ConstType receiverConst
            ? receiverConst.UnderlyingType
            : receiver?.ClosedGenericType ?? receiver?.InferredType;
        if (value is null && receiver is not null && IsCStorageLocation(receiver) &&
            closedPropertyReceiverType is NamedType
            {
                ClassType: ClassType.Class,
                ConstructedIdentity: { } closedArrayGetterIdentity,
            } closedArrayGetterReceiver &&
            accessor.Declaration is { } closedArrayGetterAccessor &&
            closedArrayGetterAccessor.ParentPropertyDeclaration.ParentClassDeclaration is
                { GenericTypeNames.Length: > 0 } closedArrayGetterOwner &&
            IsClosedGenericArrayPropertyGetter(
                closedArrayGetterAccessor,
                closedArrayGetterOwner,
                closedArrayGetterOwner.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            var function = closedArrayGetterAccessor.BodyFunction ??
                throw new InternalCompilerException(
                    $"Supported array property '{closedArrayGetterAccessor.FullName}' " +
                    "has no body function.");
            var arrayAccess = (ArrayAccessExpression)((ReturnStatement)function.Body![0]).Expression!;
            var closedTypeIdentifier = RequiresClosedValueLayout(closedArrayGetterReceiver)
                ? closedArrayGetterIdentity.CIdentifier
                : closedArrayGetterOwner.ToCIdentifier(
                    closedArrayGetterOwner.ProjectName ?? moduleName, false);
            var instance = $"((struct {closedTypeIdentifier}*)({receiverExpression}))";
            var substitutions = closedArrayGetterOwner.GenericTypeNames
                .Zip(closedArrayGetterReceiver.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            var array = ToCClosedGenericConcreteMethodExpression(
                arrayAccess.Target,
                new Dictionary<string, string>(StringComparer.Ordinal),
                function,
                closedTypeIdentifier,
                moduleName,
                instance);
            var index = ToCClosedGenericConcreteMethodExpression(
                arrayAccess.Indices.Single(),
                new Dictionary<string, string>(StringComparer.Ordinal),
                function,
                closedTypeIdentifier,
                moduleName,
                instance);
            var elementType = GenericTypeSubstitution.Substitute(
                closedArrayGetterAccessor.ParentPropertyDeclaration.Type, substitutions);
            var elementCType = elementType.ToCIdentifier(false);
            return $"(*({elementCType}*)cx_array_at({array}, " +
                $"(cx_uint)({index}), (cx_uint)sizeof({elementCType})))";
        }

        if (value is null && receiver is not null && IsCStorageLocation(receiver) &&
            receiver.InferredType is NamedType
            {
                ClassType: ClassType.Struct,
                ConstructedIdentity: { } closedIdentity,
            } && accessor.Declaration is { } accessorDeclaration &&
            accessorDeclaration.ParentPropertyDeclaration.ParentClassDeclaration is
                { GenericTypeNames.Length: > 0 } genericDeclaration &&
            accessorDeclaration.BodyFunction is { } accessorFunction &&
            IsClosedGenericConcreteExpressionAccessor(
                accessorDeclaration,
                genericDeclaration,
                genericDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            var expression = accessorFunction.Body is
                [ReturnStatement { Expression: { } returnedExpression }]
                    ? returnedExpression
                    : throw new InternalCompilerException(
                        $"Supported computed property '{accessorDeclaration.FullName}' has no return expression.");
            var instance = $"((struct {closedIdentity.CIdentifier}*)&({receiverExpression}))";
            return ToCClosedGenericConcreteMethodExpression(
                expression,
                new Dictionary<string, string>(StringComparer.Ordinal),
                accessorFunction,
                closedIdentity.CIdentifier,
                moduleName,
                instance);
        }

        if (value is null && receiver is not null && IsCStorageLocation(receiver) &&
            receiver.InferredType is NamedType
            {
                ClassType: ClassType.Struct,
                ConstructedIdentity: { } closedGenericResultIdentity,
            } closedGenericResultReceiverType && accessor.Declaration is { } genericAccessor &&
            genericAccessor.ParentPropertyDeclaration.ParentClassDeclaration is
                { GenericTypeNames.Length: > 0 } genericResultDeclaration &&
            IsClosedGenericGenericResultExpressionAccessor(
                genericAccessor,
                genericResultDeclaration,
                genericResultDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            var function = genericAccessor.BodyFunction ?? throw new InternalCompilerException(
                $"Supported generic-result property '{genericAccessor.FullName}' has no body function.");
            var conditional = function.Body is
                [ReturnStatement { Expression: ConditionalExpression expression }]
                    ? expression
                    : throw new InternalCompilerException(
                        $"Supported generic-result property '{genericAccessor.FullName}' has no conditional expression.");
            var substitutions = genericResultDeclaration.GenericTypeNames
                .Zip(closedGenericResultReceiverType.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            var closedResultType = GenericTypeSubstitution.Substitute(
                genericAccessor.ParentPropertyDeclaration.Type, substitutions);
            var instance = $"((struct {closedGenericResultIdentity.CIdentifier}*)&({receiverExpression}))";
            var condition = ToCClosedGenericConcreteMethodExpression(
                conditional.Condition,
                new Dictionary<string, string>(StringComparer.Ordinal),
                function,
                closedGenericResultIdentity.CIdentifier,
                moduleName,
                instance);
            var whenTrue = ToCClosedGenericGenericResultArm(
                conditional.WhenTrue, closedResultType, instance);
            var whenFalse = ToCClosedGenericGenericResultArm(
                conditional.WhenFalse, closedResultType, instance);
            return $"({condition} ? {whenTrue} : {whenFalse})";
        }

        if (value is not null && receiver is not null && IsCStorageLocation(receiver) &&
            receiver.InferredType is NamedType
            {
                ClassType: ClassType.Struct,
                ConstructedIdentity: { } closedGenericSetterIdentity,
            } && accessor.Declaration is { } setterDeclaration &&
            setterDeclaration.ParentPropertyDeclaration.ParentClassDeclaration is
                { GenericTypeNames.Length: > 0 } setterOwner &&
            IsClosedGenericConcreteExpressionSetter(
                setterDeclaration,
                setterOwner,
                setterOwner.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            var setterFunction = setterDeclaration.BodyFunction ??
                throw new InternalCompilerException(
                    $"Supported computed setter '{setterDeclaration.FullName}' has no body function.");
            var assignment = setterFunction.Body is
                [ExpressionStatement { Expression: AssignmentExpression bodyAssignment }]
                    ? bodyAssignment
                    : throw new InternalCompilerException(
                        $"Supported computed setter '{setterDeclaration.FullName}' has no assignment.");
            var targetField = ((IdentifierExpression)assignment.Target)
                .TargetField!.Declaration;
            var instance = $"((struct {closedGenericSetterIdentity.CIdentifier}*)&({receiverExpression}))";
            var parameterNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [setterFunction.Parameters[0].Name] = value,
            };
            var assignedValue = ToCClosedGenericConcreteMethodExpression(
                assignment.Value,
                parameterNames,
                setterFunction,
                closedGenericSetterIdentity.CIdentifier,
                moduleName,
                instance);
            return $"({instance})->{targetField.Name} = {assignedValue}";
        }

        var arguments = new List<string>();
        if (!property.IsStatic)
        {
            if (receiver is null)
            {
                var implicitReceiverExpression = functionDeclaration.LambdaOwner?.ThisSymbol is { } thisSymbol
                    ? GetCapturedThisPointer(thisSymbol, functionDeclaration)
                    : "__this";
                arguments.Add(functionDeclaration.ExtensionTargetType is { } extensionTarget &&
                    IsPrimitiveExtensionReceiverType(extensionTarget)
                        ? implicitReceiverExpression
                        : ToCBaseReceiver(implicitReceiverExpression, true, receiverBaseDepth));
            }
            else
            {
                if (IsPrimitiveExtensionReceiverType(property.ContainingType))
                {
                    arguments.Add(ToCExpressionAsType(
                        receiver,
                        property.ContainingType,
                        functionDeclaration,
                        moduleName));
                }
                else if (property.ContainingClassType == ClassType.Interface)
                {
                    arguments.Add(receiverExpression!);
                }
                else
                {
                    var genericReceiver = receiver.InferredType switch
                    {
                        GenericType generic => generic,
                        ConstType { UnderlyingType: GenericType generic } => generic,
                        _ => null,
                    };
                    var classConstraint = genericReceiver is null
                        ? null
                        : GetSimpleFunctionClassConstraint(
                            functionDeclaration, genericReceiver.Name);
                    arguments.Add(classConstraint is null
                        ? ToCBaseReceiver(
                            receiverExpression!,
                            IsCPointerReceiver(receiver),
                            receiverBaseDepth)
                        : ToCBaseReceiver(
                            $"(({classConstraint.ToCIdentifier(false)})({receiverExpression!}))",
                            true,
                            receiverBaseDepth));
                }
            }
        }
        arguments.AddRange(indexExpressionOverrides ?? indexArguments.Select(argument =>
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
            if (functionDeclaration.LambdaOwner?.ThisSymbol is { } thisSymbol &&
                !field.Declaration.IsStatic)
            {
                return $"({GetCapturedThisPointer(thisSymbol, functionDeclaration)})->" +
                    field.Declaration.Name;
            }
            if (receiverBaseDepth == 0)
            {
                return $"__this->{field.Declaration.Name}";
            }
            return $"{ToCBaseValue("__this", true, receiverBaseDepth)}.{field.Declaration.Name}";
        }

        var receiverExpression = ToCExpression(receiver, functionDeclaration, moduleName);
        var genericReceiver = receiver.InferredType switch
        {
            GenericType generic => generic,
            ConstType { UnderlyingType: GenericType generic } => generic,
            _ => null,
        };
        if (genericReceiver is not null &&
            GetSimpleFunctionClassConstraint(
                functionDeclaration, genericReceiver.Name) is { } constraintType)
        {
            var constrainedReceiver =
                $"(({constraintType.ToCIdentifier(false)})({receiverExpression}))";
            var fieldReceiver = receiverBaseDepth == 0
                ? constrainedReceiver
                : ToCBaseValue(constrainedReceiver, true, receiverBaseDepth);
            return receiverBaseDepth == 0
                ? $"{fieldReceiver}->{field.Declaration.Name}"
                : $"{fieldReceiver}.{field.Declaration.Name}";
        }
        if (receiverBaseDepth > 0)
        {
            return $"{ToCBaseValue(receiverExpression, true, receiverBaseDepth)}.{field.Declaration.Name}";
        }
        if (receiver.InferredType is NamedType { } closedType &&
            RequiresClosedValueLayout(closedType))
        {
            return field.ContainingClassType == ClassType.Class
                ? $"((struct {closedType.ConstructedIdentity!.CIdentifier}*)" +
                    $"({receiverExpression}))->" + field.Declaration.Name
                : $"({receiverExpression})." + field.Declaration.Name;
        }
        var accessOperator = field.ContainingClassType == ClassType.Class ? "->" : ".";
        return $"({receiverExpression}){accessOperator}{field.Declaration.Name}";
    }

    private static NamedType? GetSimpleFunctionClassConstraint(
        FunctionDeclaration function,
        string genericParameterName) =>
        function.GenericConstraints.Concat(
                function.ParentClassDeclaration?.GenericConstraints ?? [])
            .Where(constraint => constraint.ParameterName == genericParameterName &&
                constraint.Kind == GenericConstraintKind.Type)
            .SelectMany(constraint => constraint.Types)
            .OfType<NamedType>()
            .FirstOrDefault(type => type.ClassType == ClassType.Class);

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

    private static TypeBase UnwrapConst(TypeBase type) =>
        type is ConstType constant ? constant.UnderlyingType : type;

    private static bool IsBoxableValueType(TypeBase type) =>
        type is ValueTypeBase and not PtrType ||
        type is NamedType { ClassType: ClassType.Struct or ClassType.Enum };

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

    private static bool IsCStorageLocation(ExpressionBase expression)
    {
        return expression switch
        {
            ThisExpression => true,
            IdentifierExpression identifier => identifier.TargetProperty is null &&
                identifier.FunctionValueSymbol is null && identifier.TargetMethodValue is null,
            MemberAccessExpression { TargetField: not null } => true,
            ArrayAccessExpression { TargetProperty: null } => true,
            _ => false,
        };
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
        if (GetFunctionType(unwrappedTarget) is not null && unwrappedSource is NullType)
        {
            return $"({unwrappedTarget.ToCIdentifier(false)}){{ 0 }}";
        }
        if (unwrappedTarget is ObjectType && IsBoxableValueType(unwrappedSource))
        {
            var valueType = unwrappedSource.ToCIdentifier(false);
            return $"(struct CX_ID_3(cxcore, System, Object)*)cx_box_value(" +
                $"(const void*)&(({valueType}[]){{ {value} }})[0], " +
                $"(cx_uint)sizeof({valueType}), {ToTypeInfoPointer(unwrappedSource)})";
        }
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
            var vtable = sourceClass.ConstructedIdentity is { } identity
                ? GetClosedGenericInterfaceVTableIdentifier(
                    identity.CIdentifier,
                    targetInterface.ResolvedTypeFullName.Parts[0],
                    targetInterface.ResolvedTypeFullName).ToCIdentifier()
                : GetInterfaceVTableIdentifier(
                    sourceClass.ResolvedTypeFullName,
                    targetInterface.ResolvedTypeFullName).ToCIdentifier();
            return $"({targetType.ToCIdentifier(false)}){{ {vtable}, (cx_ptr)({value}) }}";
        }
        if (unwrappedTarget is NamedType { ClassType: ClassType.Interface } &&
            unwrappedSource is NamedType { ClassType: ClassType.Struct })
        {
            return ToCBoxedInterfaceExpression(value, unwrappedSource, unwrappedTarget);
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
        if (GetFunctionType(targetType) is not null && expression.Operand.InferredType is NullType)
        {
            return $"({targetType.ToCIdentifier(false)}){{ 0 }}";
        }
        var targetCType = targetType.ToCIdentifier(false);
        var sourceType = expression.Operand.InferredType!;
        var value = ToCExpression(expression.Operand, functionDeclaration, moduleName);
        if (UnwrapConst(sourceType) is ObjectType && IsBoxableValueType(UnwrapConst(targetType)))
        {
            var valueType = UnwrapConst(targetType).ToCIdentifier(false);
            return $"*((const {valueType}*)cx_unbox_value((cx_ptr)({value}), " +
                $"{ToTypeInfoPointer(UnwrapConst(targetType))}))";
        }
        if (UnwrapConst(targetType) is NamedType { ClassType: ClassType.Interface } targetInterface &&
            UnwrapConst(sourceType) is NamedType { ClassType: ClassType.Struct } sourceStruct)
        {
            return ToCBoxedInterfaceExpression(value, sourceStruct, targetInterface);
        }
        var targetClassType = GetReferenceClassType(targetType);
        if (expression.Operand.InferredType is NullType)
        {
            return targetClassType == ClassType.Interface
                ? $"({targetCType}){{ CX_NULL, CX_NULL }}"
                : $"({targetCType})CX_NULL";
        }

        var sourceClassType = GetReferenceClassType(sourceType);
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

    private static string ToCBoxedInterfaceExpression(
        string value,
        TypeBase sourceType,
        TypeBase targetType)
    {
        var valueType = sourceType.ToCIdentifier(false);
        return $"cx_checked_cast_object_to_interface(" +
            $"(cx_ptr)cx_box_value((const void*)&(({valueType}[]){{ {value} }})[0], " +
            $"(cx_uint)sizeof({valueType}), {ToTypeInfoPointer(sourceType)}), " +
            $"{ToTypeInfoPointer(targetType)})";
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
            NamedType namedArray when ArrayType.TryGetElementType(namedArray, out _) =>
                new QualifiedIdentifier("cxcore", "System", "Collections", "Array"),
            NamedType { ConstructedIdentity: { } identity } =>
                new QualifiedIdentifier(identity.CIdentifier),
            NamedType namedType => namedType.ResolvedTypeFullName,
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
        if (ArrayType.TryGetElementType(expression.RequestedType, out var arrayElementType))
        {
            if (expression.Arguments.Count != 1)
            {
                throw new InternalCompilerException(
                    "Array<T> construction must have one length argument.");
            }
            var elementCType = arrayElementType is GenericType
                ? "cx_ptr"
                : arrayElementType.ToCIdentifier(false);
            return $"cx_array_new((cx_uint)({ToCExpression(expression.Arguments[0], functionDeclaration, moduleName)}), " +
                $"(cx_uint)sizeof({elementCType}))";
        }

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
        var leftFunctionTypeOuter = GetFunctionType(expression.Left.InferredType);
        var rightFunctionTypeOuter = GetFunctionType(expression.Right.InferredType);
        if (expression.Operator is "+" or "-" &&
            (leftFunctionTypeOuter ?? rightFunctionTypeOuter) is { } delegateType)
        {
            var helper = GetFunctionTypeHelperName(
                delegateType,
                expression.Operator == "+" ? "combine" : "remove");
            return $"{helper}(" +
                $"{ToCExpressionAsType(expression.Left, delegateType, functionDeclaration, moduleName)}, " +
                $"{ToCExpressionAsType(expression.Right, delegateType, functionDeclaration, moduleName)})";
        }

        if (expression.OperatorSymbol is { } operatorSymbol)
        {
            var receiverTemporary = expression.OperatorReceiverTemporaryName;
            var receiverType = expression.Left.InferredType ?? throw new InternalCompilerException(
                "Operator receiver type is not bound.");
            var receiverIsPointer = IsCReferenceType(receiverType);
            var receiverExpression = receiverTemporary ??
                ToCExpression(expression.Left, functionDeclaration, moduleName);
            var primitiveExtensionTarget = operatorSymbol.Declaration?.ExtensionTargetType;
            var receiverArgument = primitiveExtensionTarget is { } primitiveType &&
                IsPrimitiveExtensionReceiverType(primitiveType)
                    ? ToCExpressionAsType(
                        expression.Left,
                        primitiveType,
                        functionDeclaration,
                        moduleName)
                    : ToCBaseReceiver(
                        receiverExpression,
                        receiverTemporary is null && receiverIsPointer,
                        expression.OperatorBaseDepth);
            var argumentType = operatorSymbol.ParameterTypes.Single();
            var argument = ToCExpressionAsType(
                expression.Right, argumentType, functionDeclaration, moduleName);
            var call = $"{ToCIdentifier(operatorSymbol)}({receiverArgument}, {argument})";
            return receiverTemporary is null
                ? call
                : $"({receiverTemporary} = " +
                    $"{ToCExpressionAsType(expression.Left, receiverType, functionDeclaration, moduleName)}, {call})";
        }

        if (expression.Operator is "==" or "!=")
        {
            var leftType = expression.Left.InferredType is ConstType leftConst
                ? leftConst.UnderlyingType
                : expression.Left.InferredType;
            var rightType = expression.Right.InferredType is ConstType rightConst
                ? rightConst.UnderlyingType
                : expression.Right.InferredType;
            var leftFunctionType = GetFunctionType(leftType);
            var rightFunctionType = GetFunctionType(rightType);
            if (leftFunctionType is not null)
            {
                if (rightType is NullType)
                {
                    var isNull = $"{GetFunctionTypeHelperName(leftFunctionType, "is_null")}" +
                        $"({ToCExpression(expression.Left, functionDeclaration, moduleName)})";
                    return expression.Operator == "==" ? isNull : $"(!{isNull})";
                }
                if (rightFunctionType is not null)
                {
                    var comparison = $"{GetFunctionTypeHelperName(leftFunctionType, "equal")}(" +
                        $"{ToCExpressionAsType(expression.Left, leftFunctionType, functionDeclaration, moduleName)}, " +
                        $"{ToCExpressionAsType(expression.Right, leftFunctionType, functionDeclaration, moduleName)})";
                    return expression.Operator == "==" ? comparison : $"(!{comparison})";
                }
            }
            if (rightFunctionType is not null && leftType is NullType)
            {
                var isNull = $"{GetFunctionTypeHelperName(rightFunctionType, "is_null")}" +
                    $"({ToCExpression(expression.Right, functionDeclaration, moduleName)})";
                return expression.Operator == "==" ? isNull : $"(!{isNull})";
            }
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

    private static string ToCOperatorUnaryExpression(
        UnaryExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var symbol = expression.OperatorSymbol ?? throw new InternalCompilerException(
            "Operator unary expression is not bound.");
        var temporary = expression.OperatorTemporaryName;
        var operandType = expression.Operand.InferredType ?? throw new InternalCompilerException(
            "Operator operand type is not bound.");
        var unwrappedOperandType = operandType is ConstType constType
            ? constType.UnderlyingType
            : operandType;
        var classType = unwrappedOperandType is NamedType named
            ? named.ClassType
            : (ClassType?)null;

        if (expression.Operator is "++" or "--")
        {
            if (temporary is null)
            {
                throw new InternalCompilerException("Mutating operator has no receiver temporary.");
            }
            if (symbol.Declaration?.IsExtension == true)
            {
                var extensionReceiverSetup = $"{temporary} = &(" +
                    $"{ToCExpression(expression.Operand, functionDeclaration, moduleName)})";
                var extensionCall = $"{ToCIdentifier(symbol)}(*{temporary})";
                if (expression.Postfix && expression.OperatorOldValueTemporaryName is { } oldExtensionValue)
                {
                    return $"({extensionReceiverSetup}, {oldExtensionValue} = *{temporary}, " +
                        $"*{temporary} = {extensionCall}, {oldExtensionValue})";
                }
                return $"({extensionReceiverSetup}, *{temporary} = {extensionCall}, *{temporary})";
            }
            var receiverSetup = classType == ClassType.Struct
                ? $"{temporary} = &({ToCExpression(expression.Operand, functionDeclaration, moduleName)})"
                : $"{temporary} = {ToCExpression(expression.Operand, functionDeclaration, moduleName)}";
            var call = $"{ToCIdentifier(symbol)}(" +
                $"{ToCBaseReceiver(temporary, true, expression.OperatorBaseDepth)})";
            var result = classType == ClassType.Struct ? $"*{temporary}" : temporary;
            if (expression.Postfix && expression.OperatorOldValueTemporaryName is { } oldValue)
            {
                return $"({receiverSetup}, {oldValue} = *{temporary}, {call}, {oldValue})";
            }
            return $"({receiverSetup}, {call}, {result})";
        }

        var receiverExpression = temporary ?? ToCExpression(
            expression.Operand, functionDeclaration, moduleName);
        var primitiveExtensionTarget = symbol.Declaration?.ExtensionTargetType;
        var receiverArgument = primitiveExtensionTarget is { } primitiveType &&
            IsPrimitiveExtensionReceiverType(primitiveType)
                ? ToCExpressionAsType(
                    expression.Operand,
                    primitiveType,
                    functionDeclaration,
                    moduleName)
                : ToCBaseReceiver(
                    receiverExpression,
                    temporary is null && IsCReferenceType(operandType),
                    expression.OperatorBaseDepth);
        var callExpression = $"{ToCIdentifier(symbol)}({receiverArgument})";
        return temporary is null
            ? callExpression
            : $"({temporary} = " +
                $"{ToCExpressionAsType(expression.Operand, operandType, functionDeclaration, moduleName)}, {callExpression})";
    }

    private static string ToCOperatorAssignment(
        AssignmentExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var symbol = expression.OperatorSymbol ?? throw new InternalCompilerException(
            "Operator assignment is not bound.");
        var receiverType = expression.Target.InferredType ?? throw new InternalCompilerException(
            "Operator assignment target type is not bound.");
        var receiver = ToCExpression(expression.Target, functionDeclaration, moduleName);
        if (symbol.Declaration?.IsExtension == true)
        {
            var temporary = expression.TemporaryName ?? throw new InternalCompilerException(
                "Extension operator assignment has no location temporary.");
            var extensionValue = ToCExpressionAsType(
                expression.Value,
                symbol.ParameterTypes.Single(),
                functionDeclaration,
                moduleName);
            return $"({temporary} = &({receiver}), *{temporary} = " +
                $"{ToCIdentifier(symbol)}(*{temporary}, {extensionValue}), (void)0)";
        }
        var receiverArgument = ToCBaseReceiver(
            receiver,
            IsCReferenceType(receiverType),
            expression.OperatorBaseDepth);
        var value = ToCExpressionAsType(
            expression.Value, symbol.ParameterTypes.Single(), functionDeclaration, moduleName);
        return $"{ToCIdentifier(symbol)}({receiverArgument}, {value})";
    }

    private static string ToCDelegateCompoundAssignment(
        AssignmentExpression expression,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var functionType = GetFunctionType(expression.Target.InferredType) ??
            throw new InternalCompilerException("Delegate assignment target is not a function type.");
        var temporaryName = expression.DelegateTemporaryName ??
            throw new InternalCompilerException("Delegate assignment has no lvalue temporary.");
        var helper = GetFunctionTypeHelperName(
            functionType,
            expression.Operator == "+=" ? "combine" : "remove");
        var target = ToCExpression(expression.Target, functionDeclaration, moduleName);
        var value = ToCExpressionAsType(
            expression.Value,
            functionType,
            functionDeclaration,
            moduleName);
        return $"({temporaryName} = &({target}), " +
            $"*{temporaryName} = {helper}(*{temporaryName}, {value}), (void)0)";
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

    private static string ToCGenericArrayElementAccess(
        ArrayAccessExpression expression,
        string elementCType,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (expression.Indices.Count != 1)
        {
            throw new InternalCompilerException(
                "Only one-dimensional generic array indexing can be emitted.");
        }

        return $"(*({elementCType}*)cx_array_at(" +
            $"{ToCExpression(expression.Target, functionDeclaration, moduleName)}, " +
            $"(cx_uint)({ToCExpression(expression.Indices[0], functionDeclaration, moduleName)}), " +
            $"(cx_uint)sizeof({elementCType})))";
    }

    private static string ToCGenericArrayReturnAccess(
        ArrayAccessExpression expression,
        IReadOnlyDictionary<string, TypeBase> typeArguments,
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        if (expression.Indices.Count != 1 || expression.InferredType is not { } inferredType)
        {
            throw new InternalCompilerException(
                "Generic array element returns require bound single-index accesses.");
        }

        var elementType = GenericTypeSubstitution.Substitute(inferredType, typeArguments)
            .ToCIdentifier(false);
        var target = expression.Target is ArrayAccessExpression nested
            ? ToCGenericArrayReturnAccess(
                nested,
                typeArguments,
                functionDeclaration,
                moduleName)
            : ToCExpression(expression.Target, functionDeclaration, moduleName);
        return $"(*({elementType}*)cx_array_at({target}, " +
            $"(cx_uint)({ToCExpression(expression.Indices[0], functionDeclaration, moduleName)}), " +
            $"(cx_uint)sizeof({elementType})))";
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
        if (symbol.Declaration?.LocalCName is { } localCName)
        {
            return new QualifiedIdentifier(symbol.ModuleName, localCName).ToCIdentifier();
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
            else if (declaration is ExtensionDeclaration extensionDeclaration)
            {
                foreach (var member in EnumerateFunctions(
                    extensionDeclaration.Members.MemberDeclarations.Declarations))
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

            case CastExpression cast:
                foreach (var literal in EnumerateStringLiterals(cast.Operand))
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

            case SwitchExpression switchExpression:
                foreach (var literal in EnumerateStringLiterals(switchExpression.Selector))
                {
                    yield return literal;
                }
                foreach (var arm in switchExpression.Arms)
                {
                    if (arm.Label is not null)
                    {
                        foreach (var literal in EnumerateStringLiterals(arm.Label))
                        {
                            yield return literal;
                        }
                    }
                    if (arm.Filter is not null)
                    {
                        foreach (var literal in EnumerateStringLiterals(arm.Filter))
                        {
                            yield return literal;
                        }
                    }
                    foreach (var literal in EnumerateStringLiterals(arm.Value))
                    {
                        yield return literal;
                    }
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
            SwitchExpression switchExpression => [switchExpression.Selector,
                .. switchExpression.Arms.SelectMany(arm => new[] { arm.Label, arm.Filter, arm.Value }
                    .Where(child => child is not null)
                    .Select(child => child!))],
            NullCoalescingExpression coalescing => [coalescing.Left, coalescing.Right],
            UnaryExpression unary => [unary.Operand],
            AssignmentExpression assignment => [assignment.Target, assignment.Value],
            ArrayCreationExpression arrayCreation => [arrayCreation.Length],
            ArrayAccessExpression arrayAccess => [arrayAccess.Target, .. arrayAccess.Indices],
            ObjectCreationExpression creation => creation.Arguments,
            LambdaExpression => [],
            _ => [],
        };
    }

    private static IEnumerable<TExpression> EnumerateOperatorExpressions<TExpression>(
        ExpressionBase expression)
        where TExpression : ExpressionBase
    {
        if (expression is TExpression matchingExpression)
        {
            yield return matchingExpression;
        }
        foreach (var child in GetExpressionChildren(expression))
        {
            foreach (var nested in EnumerateOperatorExpressions<TExpression>(child))
            {
                yield return nested;
            }
        }
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
            ForeachStatement foreachStatement => new ExpressionBase?[]
            {
                foreachStatement.Collection,
                foreachStatement.GetEnumeratorInvocation,
                foreachStatement.MoveNextInvocation,
                foreachStatement.CurrentExpression,
            }.OfType<ExpressionBase>(),
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
