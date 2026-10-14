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
        var generatedName = GetLambdaCName(lambda);
        var helper = new FunctionDeclaration(
            generatedName,
            currentNamespace,
            placeholderReturnType,
            enclosingFunction.MemberModifiers.Contains(MemberModifier.Unsafe) || _unsafeDepth > 0 ? [MemberModifier.Unsafe] : [],
            null);
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
                targetType?.ReturnType);
            if (targetType is not null &&
                !CanAssign(targetType.ReturnType, returnType))
            {
                throw new CompilationErrorException(
                    $"Lambda returns '{GetTypeName(returnType)}', but its target requires " +
                    $"'{GetTypeName(targetType.ReturnType)}'.",
                    lambda.SourceSpan);
            }
            returnType = targetType?.ReturnType ?? returnType;
            helper = new FunctionDeclaration(
                generatedName,
                currentNamespace,
                targetType?.ReturnType ?? returnType,
                enclosingFunction.MemberModifiers.Contains(MemberModifier.Unsafe) || _unsafeDepth > 0 ? [MemberModifier.Unsafe] : [],
                null);
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

        BindFunction(
            helper,
            imports,
            captureScope: enclosingScope,
            lambdaOwner: lambda);
        var functionType = new FunctionType(
            QualifiedIdentifier.Empty,
            returnType,
            parameterTypes);
        if (targetType is not null && !IsType(functionType, targetType))
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
