parser grammar CxParser;

options {
	tokenVocab = CxLexer;
}

// Top-level statements

compilationUnit
	: importStatements? namespaceDeclaration? topLevelDeclarations?
	;

importStatements
	: importStatement+
	;

importStatement
	: Import name = qualifiedIdentifier Semicolon
	;

namespaceDeclaration
	: Namespace name = qualifiedIdentifier Semicolon
	;

topLevelDeclarations
	: topLevelDeclaration+
	;

topLevelDeclaration
	: classDeclaration
	| functionDeclaration
	// enumDeclaration
	| typedefDeclaration
	| extensionDeclaration
	;

// Classes

classModifiers
	: classModifier*
	;

classModifier
	: visibilityModifier
	| Abstract
	| Final
	| Static
	;

visibilityModifier
	: Public
	| Protected
	| Private
	| Internal
	;

classBase
	: Colon list = classBaseList
	;

classBaseList
	: name = typeName
	| list = classBaseList Comma name = typeName
	;

classType
	: Class
	| Struct
	| Interface
	| Identifier
	;

classDeclaration
	: modifiers = classModifiers partial = Partial? classType name = Identifier genericParams? base = classBase?
		body = classDeclarationBody
	;

classDeclarationBody
	: LeftBrace members = memberDeclaration* RightBrace
	| Semicolon
	;

// Members

memberModifiers
	: memberModifier*
	;

memberModifier
	: visibilityModifier
	| Abstract
	| Virtual
	| Override
	| Final
	| Static
	| Extern
	;

memberDeclaration
	: fieldsDeclaration
	| propertyDeclaration
	| constructorDeclaration
	| functionDeclaration
	| classDeclaration
	;

fieldsDeclaration
	: modifiers = memberModifiers typeName fieldInitializers Semicolon
	;

fieldInitializers
	: fieldInitializer
	| fieldInitializers Comma fieldInitializer
	;

fieldInitializer
	: Identifier (Equal literal)?
	;

propertyDeclaration
	: modifiers = memberModifiers typeName Identifier LeftBrace propertyAccessorDeclarations RightBrace
	;

propertyAccessorDeclarations
	: propertyAccessorDeclaration+
	;

propertyAccessorDeclaration
	: Extern? Const? Get propertyParams? propertyAccessorBody
	| Extern? Set propertyParams? propertyAccessorBody
	;

propertyParams
	: LeftParen propertyParam (Comma propertyParam)* RightParen
	;

propertyParam
	: typeName? Identifier
	;

propertyAccessorBody
	: LeftBrace statements? RightBrace
	| Arrow statement
	| Semicolon
	;

constructorDeclaration
	: modifiers = memberModifiers Constructor LeftParen functionParameters? RightParen constructorOrBaseInvocation? functionBody
	;

constructorOrBaseInvocation
	: Colon (This | Base) functionInvocation
	;

functionDeclaration
	: modifiers = memberModifiers async = Async? returnType = typeNameOrVoid name = Identifier genericParams? LeftParen functionParameters? RightParen Const? functionBody
	;

functionParameters
	: functionParameter
	| functionParameters Comma functionParameter
	;

functionParameter
	: Params? typeName name = Identifier (defaultValue = Assign literal)?
	;

functionBody
	: LeftBrace statements? RightBrace
	| Arrow statement
	| Semicolon
	;

typedefDeclaration
	: Typedef Identifier Assign qualifiedIdentifier Semicolon
	;

extensionDeclaration
	: visibilityModifier? Extension name = Identifier base = classBase body = classDeclarationBody
	;

// Statements

statements
	: statement
	| statements statement
	;

statement
	: declarationStatement
	| embeddedStatement
	;

declarationStatement
	: localVariableDeclarationStatement
	// TODO: | localFunctionDeclarationStatement
	;

localVariableDeclarationStatement
	: localVariableDeclaration Semicolon
	;

localVariableDeclaration
	: typeName variableDeclarations
	;

variableDeclarations
	: variableDeclaration
	| variableDeclarations Comma variableDeclaration
	;

variableDeclaration
	: Identifier
	| Identifier Assign expression
	;

embeddedStatement
	: expressionStatement
	| ifStatement
	| switchStatement
	| whileStatement
	| doStatement
	| forStatement
	| foreachStatement
	| returnStatement
	| throwStatement
	| tryStatement
	| usingStatement
	| LeftBrace statements? RightBrace
	| Semicolon
	;

expressionStatement
	: expression Semicolon
	;

ifStatement
	: If LeftParen expression RightParen embeddedStatement (Else embeddedStatement)?
	;

switchStatement
	: Switch LeftParen expression RightParen LeftBrace switchSection* RightBrace
	;

switchSection
	: switchLabel+ statements
	;

switchLabel
	: Case expression switchLabelFilter? Colon
	| Default Colon
	;

switchLabelFilter
	: When expression
	;

whileStatement
	: While LeftParen expression RightParen embeddedStatement
	;

doStatement
	: Do embeddedStatement While LeftParen expression RightParen Semicolon
	;

forStatement
	: For LeftParen forInitializer? Semicolon forCheck? Semicolon forIterator? RightParen embeddedStatement
	;

forInitializer
	: localVariableDeclaration
	| expression (Comma expression)*
	;

forCheck
	: expression
	;

forIterator
	: expression (Comma expression)*
	;

foreachStatement
	: Foreach LeftParen typeName Identifier In expression RightParen embeddedStatement
	;

returnStatement
	: Return expression? Semicolon
	;

throwStatement
	: Throw expression? Semicolon
	;

tryStatement
	: Try embeddedStatement (catchClauses finallyClause? | finallyClause)
	;

catchClauses
	: Catch LeftParen typeName Identifier? RightParen exceptionFilter? statements
	;

exceptionFilter
	: When LeftParen expression RightParen
	;

finallyClause
	: Finally statements
	;

usingStatement
	: Using LeftParen expression RightParen embeddedStatement
	;

// Expressions

expression
	: assignmentExpression
	| nonAssignmentExpression
	;

assignmentExpression
	: qualifiedIdentifier assignOperator expression
	;

assignOperator
	: Assign
	| PlusAssign
	| MinusAssign
	| StarAssign
	| DivAssign
	| ModAssign
	| AndAssign
	| OrAssign
	| XorAssign
	| LeftShiftAssign
	| RightShiftAssign
	;

nonAssignmentExpression
	: conditionalExpression
	// TODO: lambdaExpression
	;

conditionalExpression
	: nullCoalescingExpression (
		Question throwableExpression Colon throwableExpression
	)?
	;

nullCoalescingExpression
	: conditionalOrExpression (
		QuestionQuestion nullCoalescingExpression
	)?
	;

conditionalOrExpression
	: conditionalAndExpression (OrOr conditionalAndExpression)*
	;

conditionalAndExpression
	: inclusiveOrExpression (AndAnd inclusiveOrExpression)*
	;

inclusiveOrExpression
	: exclusiveOrExpression (Or exclusiveOrExpression)*
	;

exclusiveOrExpression
	: andExpression (Xor andExpression)*
	;

andExpression
	: equalityExpression (And equalityExpression)*
	;

equalityExpression
	: relationalExpression (
		(Equal | NotEqual) relationalExpression
	)*
	;

relationalExpression
	: shiftExpression (
		(Less | LessEqual | Greater | GreaterEqual) shiftExpression
	)*
	;

shiftExpression
	: additiveExpression (
		(LeftShift | RightShift) additiveExpression
	)*
	;

additiveExpression
	: multiplicativeExpression (
		(Plus | Minus) multiplicativeExpression
	)*
	;

multiplicativeExpression
	: unaryExpression ((Star | Div | Mod) unaryExpression)*
	;

unaryExpression
	: primaryExpression
	| Plus unaryExpression
	| Minus unaryExpression
	| Not unaryExpression
	| Tilde unaryExpression
	| PlusPlus unaryExpression
	| MinusMinus unaryExpression
	| LeftParen typeName RightParen unaryExpression
	| Await unaryExpression
	;

primaryExpression
	: primaryExpressionStart arrayExpression* (
		(
			memberAccess
			| functionInvocation
			| PlusPlus
			| MinusMinus
		) arrayExpression*
	)*
	;

primaryExpressionStart
	: literal
	| qualifiedIdentifier
	| New typeName functionInvocation
	;

arrayExpression
	: LeftBracket expression (Comma expression)* RightBracket
	;

memberAccess
	: Dot Identifier
	;

functionInvocation
	: LeftParen functionInvocationArguments? RightParen
	;

functionInvocationArguments
	: functionInvocationArgument
	| functionInvocationArguments Comma functionInvocationArgument
	;

functionInvocationArgument
	: (Identifier Colon)? expression
	;

throwableExpression
	: expression
	| throwExpression
	;

throwExpression
	: Throw expression?
	;


// Annotations
annotations
	: annotation+
	;

annotation
	: At qualifiedIdentifier functionInvocation?
	;

// Types

typeNameOrVoid
	: typeName
	| Void
	;

typeName
	: Const? builtInType arrayDimension* Question?
	| Const? namedType = qualifiedIdentifier genericParams? arrayDimension* Question?
	| autoVarType = Var
	| autoConstType = Const
	// TODO: functionType
	;

arrayDimension
	: LeftBracket RightBracket
	;

genericParams
	: Less genericParamList Greater
	;

genericParamList
	: Identifier
	| genericParamList Comma Identifier
	;

builtInType
	: integerType
	| floatingType
	| textualType
	| objectType = Object
	| ptrType = Ptr genericParams?
	;

integerType
	: Bool
	| Byte
	| Sbyte
	| Short
	| Ushort
	| Int
	| Uint
	| Long
	| Ulong
	| Int8
	| Int16
	| Int32
	| Int64
	| UInt8
	| UInt16
	| UInt32
	| UInt64
	;

floatingType
	: Float
	| Double
	// | Decimal
	| Float32
	| Float64
	;

textualType
	: Char
	| String
	;

// Identifiers and literals

qualifiedIdentifier
	: identifier = Identifier													# simpleIdentifier
	| baseQualifiedIdentifier = qualifiedIdentifier Dot identifier = Identifier	# combinedQualifiedIdentifier
	;

literal
	: booleanLiteral
	| IntegerLiteral
	| FloatingLiteral
	| CharLiteral
	| StringLiteral
	| Null
	;

booleanLiteral
	: True
	| False
	;