using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.ParserVisitors;

public class TypeNameContextVisitor : CxParserBaseVisitor<TypeBase>
{
    public override TypeBase VisitTypeNameOrVoid([NotNull] CxParser.TypeNameOrVoidContext context)
    {
        if (context.Void() != null)
        {
            return BuiltInSystemTypes.Void;
        }

        return base.VisitTypeNameOrVoid(context);
    }

    public override TypeBase VisitTypeName([NotNull] CxParser.TypeNameContext context)
    {
        TypeBase underlyingType;

        if (context.namedType != null)
        {
            var genericArguments = context.genericTypeArguments() is { } typeArguments
                ? new GenericTypeArgumentsParserVisitor().VisitGenericTypeArguments(typeArguments)
                : [];
            underlyingType = NamedType.CreateWithTypeArguments(
                context.namedType.GetText(), genericArguments);
        }
        else if (context.autoVarType != null)
        {
            underlyingType = new AutoType(false);
        }
        else if (context.autoConstType != null)
        {
            underlyingType = new AutoType(true);
        }
        else if (context.functionType() is { } functionType)
        {
            underlyingType = Visit(functionType);
        }
        else
        {
            underlyingType = Visit(context.builtInType());
        }

        if (context.arrayDimension() != null && context.arrayDimension().Length > 0)
        {
            foreach (var dimension in context.arrayDimension())
            {
                if (dimension.IntegerLiteral() is { } count)
                {
                    if (!int.TryParse(count.GetText(), out var length) || length <= 0)
                        throw new CxCompiler.Model.Errors.CompilationErrorException("Fixed native array length must be a positive decimal integer.", SourceSpan.From(dimension));
                    underlyingType = new FixedArrayType(underlyingType, length);
                }
                else underlyingType = new ArrayType(underlyingType);
            }
        }

        if (context.Question() != null)
        {
            underlyingType = new NullableType(underlyingType);
        }

        if (context.Const() != null && context.autoConstType == null)
        {
            underlyingType = new ConstType(underlyingType);
        }

        return underlyingType;
    }

    public override TypeBase VisitFunctionType([NotNull] CxParser.FunctionTypeContext context)
    {
        var parameters = context.functionTypeParameters()?.typeName()
            .Select(type => Visit(type))
            .ToArray() ?? [];
        return new FunctionType(
            CxCompiler.Model.Common.QualifiedIdentifier.Empty,
            Visit(context.typeNameOrVoid()),
            parameters);
    }

    public override TypeBase VisitBuiltInType([NotNull] CxParser.BuiltInTypeContext context)
    {
        if (context.objectType != null)
        {
            return BuiltInSystemTypes.Object;
        }

        if (context.ptrType != null)
        {
            if (context.Void() is not null)
                return context.Const() is null ? BuiltInSystemTypes.Ptr
                    : new PtrType(BuiltInSystemTypes.Ptr.Namespace, new ConstType(BuiltInSystemTypes.Void));
            var arguments = context.genericTypeArguments() is { } list
                ? new GenericTypeArgumentsParserVisitor().VisitGenericTypeArguments(list) : [];
            if (arguments.Length > 1)
                throw new CxCompiler.Model.Errors.CompilationErrorException("ptr accepts exactly one pointee type.", SourceSpan.From(context));
            return arguments.Length == 0 ? BuiltInSystemTypes.Ptr : new PtrType(BuiltInSystemTypes.Ptr.Namespace, arguments[0]);
        }

        return base.VisitBuiltInType(context);
    }

    public override TypeBase VisitIntegerType([NotNull] CxParser.IntegerTypeContext context)
    {
        var terminalNode = (ITerminalNode)context.GetChild(0);
        var symbolType = terminalNode.Symbol.Type;

        return symbolType switch
        {
            CxLexer.Bool => BuiltInSystemTypes.Bool,
            CxLexer.Byte => BuiltInSystemTypes.Byte,
            CxLexer.Sbyte => BuiltInSystemTypes.SByte,
            CxLexer.Short => BuiltInSystemTypes.Short,
            CxLexer.Ushort => BuiltInSystemTypes.UShort,
            CxLexer.Int => BuiltInSystemTypes.Int,
            CxLexer.Uint => BuiltInSystemTypes.UInt,
            CxLexer.Long => BuiltInSystemTypes.Long,
            CxLexer.Ulong => BuiltInSystemTypes.ULong,
            CxLexer.Int8 => BuiltInSystemTypes.SByte,
            CxLexer.Int16 => BuiltInSystemTypes.Short,
            CxLexer.Int32 => BuiltInSystemTypes.Int,
            CxLexer.Int64 => BuiltInSystemTypes.Long,
            CxLexer.Uint8 => BuiltInSystemTypes.Byte,
            CxLexer.Uint16 => BuiltInSystemTypes.UShort,
            CxLexer.Uint32 => BuiltInSystemTypes.UInt,
            CxLexer.Uint64 => BuiltInSystemTypes.ULong,
            _ => throw new InternalCompilerException($"Unknown integer type: {context.GetText()}"),
        };
    }

    public override TypeBase VisitFloatingType([NotNull] CxParser.FloatingTypeContext context)
    {
        var terminalNode = (ITerminalNode)context.GetChild(0);
        var symbolType = terminalNode.Symbol.Type;
        return symbolType switch
        {
            CxLexer.Float => BuiltInSystemTypes.Float,
            CxLexer.Double => BuiltInSystemTypes.Double,
            // CxLexer.Decimal => BuiltInSystemTypes.Decimal, // TODO
            CxLexer.Float32 => BuiltInSystemTypes.Float,
            CxLexer.Float64 => BuiltInSystemTypes.Double,
            _ => throw new InternalCompilerException($"Unknown floating type: {context.GetText()}"),
        };
    }

    public override TypeBase VisitTextualType([NotNull] CxParser.TextualTypeContext context)
    {
        var terminalNode = (ITerminalNode)context.GetChild(0);
        var symbolType = terminalNode.Symbol.Type;
        return symbolType switch
        {
            CxLexer.Char => BuiltInSystemTypes.Char,
            CxLexer.Wchar => BuiltInSystemTypes.WChar,
            CxLexer.String => BuiltInSystemTypes.String,
            _ => throw new InternalCompilerException($"Unknown textual type: {context.GetText()}"),
        };
    }
}
