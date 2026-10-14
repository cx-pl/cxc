using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.ParserVisitors;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    private void AddCoreTextDeclarations()
    {
        // Standalone consumers need the same core declarations as source-project
        // references. Parse them through the ordinary binder rather than inventing
        // a separate representation for constructors, virtual slots or parameters.
        Add("""
            namespace System;
            public class Exception {
                private string _message;
                private Exception? _innerException;
                public string Message { extern const get; }
                public extern constructor(string message);
                public extern constructor(string message, Exception innerException);
                public extern constructor();
            }
            public final class WideString : Object {
                private uint _length; private ptr _data;
                public static WideString Empty { extern const get; }
                public uint Length { extern const get; }
                public wchar Item { extern const get(uint index); }
                public uint ScalarCount { extern const get; }
                public extern char ScalarAt(uint index) const;
                public extern constructor();
                public unsafe extern constructor(ptr<const wchar> data, uint length);
                public override extern string ToString() const;
                public override extern bool Equals(object other) const;
                public override extern int GetHashCode() const;
                public extern int CompareOrdinal(WideString other) const;
                public extern bool operator ==(WideString other) const;
                public extern bool operator !=(WideString other) const;
            }
            public class EncodingException : Exception {
                private uint _position;
                public uint Position { extern const get; }
                public extern constructor(uint position);
            }
            """);
        Add("""
            namespace System.Interop;
            public final class NativeBuffer {
                private ptr _data; private ulong _byteLength; private ptr _allocation;
                public ptr Data { extern const get; }
                public ulong ByteLength { extern const get; }
                public static extern NativeBuffer Allocate(ulong byteLength);
                public static extern NativeBuffer AllocateAligned(ulong byteLength, uint alignment);
                public static extern NativeBuffer FromUtf8(string value, bool terminated = true);
                public static extern NativeBuffer FromUtf16(System.WideString value, bool terminated = true);
                public extern string ToUtf8String(uint byteLength) const;
                public extern System.WideString ToWideString(uint codeUnitLength) const;
                public extern void Dispose();
                public static unsafe extern void Copy(ptr destination, ptr source, ulong byteLength);
            }
            public static class NativeCallback {
                public static extern System.Exception GetLastException();
                public static extern void ClearLastException();
                public static unsafe extern NativeCallbackRegistration Register<T>(ptr callback);
            }
            public final class NativeCallbackRegistration {
                private ptr _state;
                private System.Exception _lastException;
                private extern constructor();
                public ptr Context { extern const get; }
                public System.Exception LastException { extern const get; }
                public unsafe extern T Function<T>() const;
                public extern void Unregister(delegate() => void unregister);
            }
            public final class NativeLibrary {
                private ptr _handle;
                private extern constructor();
                public static extern NativeLibrary Load(string path);
                public unsafe extern T Resolve<T>(string name) const;
            }
            """);
        void Add(string source)
        {
            var parser = new CxParser(new CommonTokenStream(new OperatorAwareCxLexer(new AntlrInputStream(source))));
            var parsed = new CompilationUnitParserVisitor().Visit(parser.compilationUnit());
            var context = new CompilationContext();
            context.SetNamespace(parsed.Namespace);
            foreach (var declaration in parsed.DeclarationScope.Declarations.OfType<ClassDeclaration>())
                if (FindCoreClass(declaration.FullName.ToString()) is null)
                    context.DeclarationScope.AddDeclaration(declaration);
            if (context.DeclarationScope.Declarations.Count != 0)
                _project!.AddReferencedCompilationContext(context, "cxcore", syntheticCore: true);
        }
    }

    private void AddCoreStringTranscoding()
    {
        if (FindCoreClass("System.String") is not null) return;
        var owner = _types.Single(type => type.Declaration.FullName.ToString() == "System.String").Declaration;
        var result = new NamedType("System.WideString", []);
        result.SetResolvedType(new QualifiedIdentifier("System", "WideString"), "cxcore", ClassType.Class);
        var function = new FunctionDeclaration("ToWideString", owner.FullName, result,
            [MemberModifier.Public, MemberModifier.Extern], owner, @const: true);
        owner.MemberDeclarations.AddDeclaration(function);
        _symbols.Add(new FunctionSymbol("cxcore", function.FullName, [], result, declaration: function));
    }
}
