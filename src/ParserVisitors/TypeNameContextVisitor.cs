using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model;
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
            underlyingType = new NamedType(context.namedType.GetText());
        }
        else if (context.autoVarType != null)
        {
            underlyingType = new AutoType(false);
        }
        else if (context.autoConstType != null)
        {
            underlyingType = new AutoType(true);
        }
        else
        {
            underlyingType = base.VisitTypeName(context);
        }

        if (context.arrayDimension() != null && context.arrayDimension().Length > 0)
        {
            underlyingType = new ArrayType(underlyingType);
        }

        if (context.Question() != null)
        {
            underlyingType = new NullableType(underlyingType);
        }

        return underlyingType;
    }

    public override TypeBase VisitBuiltInType([NotNull] CxParser.BuiltInTypeContext context)
    {
        if (context.objectType != null)
        {
            return BuiltInSystemTypes.Object;
        }

        if (context.ptrType != null)
        {
            // TODO: Handle generic params if present
            return BuiltInSystemTypes.Ptr;
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
            CxLexer.UInt8 => BuiltInSystemTypes.Byte,
            CxLexer.UInt16 => BuiltInSystemTypes.UShort,
            CxLexer.UInt32 => BuiltInSystemTypes.UInt,
            CxLexer.UInt64 => BuiltInSystemTypes.ULong,
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
            CxLexer.String => BuiltInSystemTypes.String,
            _ => throw new InternalCompilerException($"Unknown textual type: {context.GetText()}"),
        };
    }
}