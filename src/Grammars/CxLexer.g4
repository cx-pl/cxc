lexer grammar CxLexer;

// $antlr-format allowShortRulesOnASingleLine true, alignSemicolons none

// Keywords

Abstract: 'abstract';
Alias: 'alias';
As: 'as';
Async: 'async';
Await: 'await';
Base: 'base';
Bool: 'bool';
Break: 'break';
Byte: 'byte';
Case: 'case';
Catch: 'catch';
Char: 'char';
Class: 'class';
Concept: 'concept';
Const: 'const';
Constructor: 'constructor';
Continue: 'continue';
Decimal: 'decimal';
Default: 'default';
Delegate: 'delegate';
Do: 'do';
Double: 'double';
Else: 'else';
Enum: 'enum';
Extension: 'extension';
Extern: 'extern';
False: 'false';
Final: 'final';
Finally: 'finally';
Float: 'float';
Float32: 'float32';
Float64: 'float64';
For: 'for';
Foreach: 'foreach';
Get: 'get';
If: 'if';
In: 'in';
Int: 'int';
Int8: 'int8';
Int16: 'int16';
Int32: 'int32';
Int64: 'int64';
Interface: 'interface';
Internal: 'internal';
Import: 'import';
Is: 'is';
Long: 'long';
Nameof: 'nameof';
Namespace: 'namespace';
New: 'new';
Null: 'null';
Object: 'object';
Operator: 'operator';
Out: 'out';
Override: 'override';
Params: 'params';
Partial: 'partial';
Private: 'private';
Protected: 'protected';
Ptr: 'ptr';
Public: 'public';
Ref: 'ref';
Return: 'return';
Sbyte: 'sbyte';
Set: 'set';
Short: 'short';
Sizeof: 'sizeof';
Static: 'static';
String: 'string';
Struct: 'struct';
Switch: 'switch';
This: 'this';
Throw: 'throw';
True: 'true';
Try: 'try';
Typedef: 'typedef';
Typeof: 'typeof';
Uint: 'uint';
UInt8: 'uint8';
UInt16: 'uint16';
UInt32: 'uint32';
UInt64: 'uint64';
Ulong: 'ulong';
Ushort: 'ushort';
Using: 'using';
Var: 'var';
Virtual: 'virtual';
Void: 'void';
When: 'when';
While: 'while';

// Operators

LeftParen: '(';
RightParen: ')';
LeftBracket: '[';
RightBracket: ']';
LeftBrace: '{';
RightBrace: '}';
Less: '<';
LessEqual: '<=';
Greater: '>';
GreaterEqual: '>=';
LeftShift: '<<';
RightShift: '>>';
Plus: '+';
PlusPlus: '++';
Minus: '-';
MinusMinus: '--';
Star: '*';
Div: '/';
Mod: '%';
And: '&';
AndAnd: '&&';
Or: '|';
OrOr: '||';
Xor: '^';
Not: '!';
At: '@';
Tilde: '~';
Question: '?';
Colon: ':';
Semicolon: ';';
Comma: ',';
Assign: '=';
StarAssign: '*=';
DivAssign: '/=';
ModAssign: '%=';
PlusAssign: '+=';
MinusAssign: '-=';
LeftShiftAssign: '<<=';
RightShiftAssign: '>>=';
AndAssign: '&=';
OrAssign: '|=';
XorAssign: '^=';
Equal: '==';
NotEqual: '!=';
QuestionQuestion: '??';
Arrow: '=>';
PipeThen: '|>';
PipeError: '->';
Dot: '.';

// $antlr-format allowShortRulesOnASingleLine false, alignSemicolons hanging

// Identifiers

Identifier
	: Nondigit (Nondigit | Digit)*
	;

// Literals

fragment Digit
	: [0-9]
	;
fragment NonzeroDigit
	: [1-9]
	;
fragment BinaryDigit
	: [0-1]
	;
fragment OctalDigit
	: [0-7]
	;
fragment HexadecimalDigit
	: [0-9a-fA-F]
	;
fragment Nondigit
	: [a-zA-Z_]
	;
fragment Sign
	: [+-]
	;
fragment ExponentPrefix
	: [eE]
	;
fragment OctalPrefix
	: '0'
	;
fragment HexadecimalPrefix
	: '0' [xX]
	;
fragment BinaryPrefix
	: '0' [bB]
	;
fragment UnsignedSuffix
	: [uU]
	;
fragment LongSuffix
	: [lL]
	;
fragment FloatSuffix
	: [fF]
	;
fragment DoubleSuffix
	: [dD]
	;
fragment DecimalSuffix
	: [mM]
	;
fragment DigitSequence
	: Digit+
	;
fragment EscapeSequence
	: '\\' ['"\\nrtb0]
	;
fragment CharCharacter
	: ~['\\\r\n]
	;
fragment StringCharacter
	: ~["\\\r\n]
	;

IntegerLiteral
	: DecimalLiteral IntegerSuffix?
	| OctalLiteral IntegerSuffix?
	| HexadecimalLiteral IntegerSuffix?
	| BinaryLiteral IntegerSuffix?
	;
fragment DecimalLiteral
	: Digit+
	;
fragment OctalLiteral
	: OctalPrefix OctalDigit+
	;
fragment HexadecimalLiteral
	: HexadecimalPrefix HexadecimalDigit+
	;
fragment BinaryLiteral
	: BinaryPrefix BinaryDigit+
	;
fragment IntegerSuffix
	: UnsignedSuffix? LongSuffix?
	;

FloatingLiteral
	: FloatingFractionalPart FloatingExponentPart? FloatingSuffix?
	;
fragment FloatingFractionalPart
	: DigitSequence? '.' DigitSequence
	| DigitSequence '.'
	;
fragment FloatingExponentPart
	: ExponentPrefix Sign? DigitSequence
	;
fragment FloatingSuffix
	: FloatSuffix
	| DoubleSuffix
	| DecimalSuffix
	;

CharLiteral
	: '\'' (CharCharacter | EscapeSequence) '\''
	;
StringLiteral
	: '"' (StringCharacter | EscapeSequence)* '"'
	;

// Whitespace and comments

Whitespace
	: [ \r\t\n]+ -> channel(HIDDEN)
	;
BlockComment
	: '/*' .*? '*/' -> channel(HIDDEN)
	;
LineComment
	: '//' ~[\r\n]* -> channel(HIDDEN)
	;
