parser grammar CxParser;

options {
    tokenVocab = CxLexer;
}

// Top-level statements

compilationUnit
    : importStatements? namespaceDeclaration? topLevelDeclarations? EOF
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
    | enumDeclaration
    | delegateDeclaration
    | aliasDeclaration
    | extensionDeclaration
    | conceptDeclaration
    ;

delegateDeclaration
    : annotations? visibilityModifier? Delegate returnType = typeNameOrVoid name = Identifier
        LeftParen parameters = functionParameters? RightParen Semicolon
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
    : annotations? classModifiers partial = Partial? classType name = Identifier genericTypeParameters?
        conceptConstraintClause* genericConstraintClauses? base = classBase?
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
    | Unsafe
    ;

memberDeclaration
    : fieldsDeclaration
    | propertyDeclaration
    | constructorDeclaration
    | functionDeclaration
    | classDeclaration
    ;

fieldsDeclaration
    : annotations? memberModifiers typeName fieldInitializers Semicolon
    ;

fieldInitializers
    : fieldInitializer
    | fieldInitializers Comma fieldInitializer
    ;

fieldInitializer
    : Identifier (Assign expression)?
    ;

propertyDeclaration
    : annotations? memberModifiers typeName Identifier LeftBrace propertyAccessorDeclarations RightBrace
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
    : annotations? modifiers = memberModifiers Constructor LeftParen functionParameters? RightParen constructorOrBaseInvocation? functionBody
    ;

constructorOrBaseInvocation
    : Colon (This | Base) functionInvocation
    ;

functionDeclaration
    : annotations? modifiers = memberModifiers async = Async? returnType = typeNameOrVoid name = Identifier
        functionGenericTypeList? LeftParen functionParameters? RightParen Const? conceptConstraintClause* genericConstraintClauses? functionBody
    ;

functionGenericTypeList
    : Less functionGenericTypeArgumentList Greater
    ;

functionGenericTypeArgumentList
    : typeName
    | functionGenericTypeArgumentList Comma typeName
    ;

conceptConstraintClause
    : Colon parameter = Identifier Assign concepts += qualifiedIdentifier
        (Comma concepts += qualifiedIdentifier)*
    ;

genericConstraintClauses
    : genericConstraintClause+
    ;

genericConstraintClause
    : Where parameter = Identifier Colon
        ( Class (Comma constraints += typeName)*
        | Struct (Comma constraints += typeName)*
        | constraints += typeName (Comma constraints += typeName)*
        )
    ;

functionParameters
    : functionParameter
    | functionParameters Comma functionParameter
    ;

functionParameter
    : annotations? Params? typeName name = Identifier
        (defaultValue = Assign (literal | sign = (Plus | Minus) (IntegerLiteral | FloatingLiteral) | qualifiedIdentifier))?
    ;

functionBody
    : LeftBrace statements? RightBrace
    | Arrow statement
    | Semicolon
    ;

enumDeclaration
    : annotations? visibilityModifier? Enum name = Identifier (Colon underlying = integerType)? enumDeclarationBody
    ;

enumDeclarationBody
    : LeftBrace (enumMemberDeclaration (Comma enumMemberDeclaration)* Comma?)? RightBrace
    | Semicolon
    ;

enumMemberDeclaration
    : Identifier (value = Assign literal)?
    // TODO: allow memberDeclaration to build rich enums
    ;

aliasDeclaration
    : Alias Identifier genericTypeParameters? Assign target = typeName Semicolon
    ;

extensionDeclaration
    : visibilityModifier? Extension name = Identifier genericTypeParameters? base = classBase body = classDeclarationBody
    ;

conceptDeclaration
    : visibilityModifier? Concept name = Identifier base = conceptBaseList? LeftBrace
        (requirements += functionDeclaration
        | propertyRequirements += conceptPropertyRequirement)*
        RightBrace
    ;

conceptBaseList
    : Colon names += qualifiedIdentifier (Comma names += qualifiedIdentifier)*
    ;

conceptPropertyRequirement
    : typeName Identifier LeftBrace conceptPropertyAccessorRequirement+ RightBrace
    ;

conceptPropertyAccessorRequirement
    : Const? Get Semicolon
    | Set Semicolon
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
    | localFunctionDeclarationStatement
    ;

localFunctionDeclarationStatement
    : functionDeclaration
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
    | breakStatement
    | continueStatement
    | throwStatement
    | tryStatement
    | usingStatement
    | unsafeStatement
    | LeftBrace statements? RightBrace
    | Semicolon
    ;

expressionStatement
    : expression Semicolon
    ;

unsafeStatement
    : Unsafe LeftBrace statements? RightBrace
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

breakStatement
    : Break Semicolon
    ;

continueStatement
    : Continue Semicolon
    ;

throwStatement
    : Throw expression? Semicolon
    ;

tryStatement
    : Try embeddedStatement (catchClauses finallyClause? | finallyClause)
    ;

catchClauses
    : catchClause+
    ;

catchClause
    : Catch LeftParen typeName Identifier? RightParen exceptionFilter? embeddedStatement
    ;

exceptionFilter
    : When LeftParen expression RightParen
    ;

finallyClause
    : Finally embeddedStatement
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
    : primaryExpression assignOperator expression
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
    : lambdaExpression
    | conditionalExpression
    ;

lambdaExpression
    : lambdaParameters Arrow lambdaBody
    ;

lambdaParameters
    : Identifier
    | LeftParen (lambdaParameter (Comma lambdaParameter)*)? RightParen
    ;

lambdaParameter
    : typeName? Identifier
    ;

lambdaBody
    : expression
    | LeftBrace statements? RightBrace
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
        | Is typeName
    )*
    ;

shiftExpression
    : additiveExpression (
        (LeftShift | Greater Greater) additiveExpression
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
    | And unaryExpression
    | Star unaryExpression
    | LeftParen typeName RightParen unaryExpression
    | Await unaryExpression
    ;

primaryExpression
    : primaryExpressionStart arrayExpression* (
        (
            memberAccess
            | genericTypeMemberAccess
            | functionInvocation
            | genericFunctionInvocation
            | switchExpression
            | PlusPlus
            | MinusMinus
        ) arrayExpression*
    )*
    ;

switchExpression
    : Switch LeftBrace switchExpressionArm (Comma switchExpressionArm)* Comma? RightBrace
    ;

switchExpressionArm
    : expression switchLabelFilter? Arrow expression
    ;

primaryExpressionStart
    : nativeTypeQuery
    | nativeStackAllocation
    | literal
    | Identifier
    | This
    | LeftParen expression RightParen
    | arrayCreationExpression
    | New typeName functionInvocation
    ;

nativeTypeQuery
    : (Sizeof | Alignof) LeftParen typeName RightParen
    | Offsetof LeftParen typeName Comma Identifier RightParen
    ;

nativeStackAllocation
    : Stackalloc Less typeName Greater LeftParen expression RightParen
    ;

arrayCreationExpression
    : New arrayElementType LeftBracket expression RightBracket arrayDimension*
    ;

arrayElementType
    : builtInType
    | qualifiedIdentifier genericTypeArguments?
    ;

arrayExpression
    : LeftBracket expression (Comma expression)* RightBracket
    ;

memberAccess
    : Dot Identifier
    ;

genericTypeMemberAccess
    : genericTypeArguments Dot Identifier
    ;

functionInvocation
    : LeftParen functionInvocationArguments? RightParen
    ;

genericFunctionInvocation
    : genericTypeArguments functionInvocation
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
    : At (target = (Return | Identifier) Colon)? qualifiedIdentifier
        (LeftParen (annotationArgument (Comma annotationArgument)*)? RightParen)?
    ;

annotationArgument
    : (name = Identifier Assign)? expression
    ;

// Types

typeNameOrVoid
    : typeName
    | Void
    ;

typeName
    : Const? builtInType arrayDimension* Question?
    | Const? namedType = qualifiedIdentifier namedTypeGenericArguments = genericTypeArguments? arrayDimension* Question?
    | autoVarType = Var
    | autoConstType = Const
    | functionType arrayDimension* Question?
    ;

functionType
    : Delegate LeftParen functionTypeParameters? RightParen Arrow typeNameOrVoid
    ;

functionTypeParameters
    : typeName (Comma typeName)*
    ;

arrayDimension
    : LeftBracket IntegerLiteral? RightBracket
    ;

genericTypeParameters
    : Less genericParamList Greater
    ;

genericParamList
    : Identifier
    | genericParamList Comma Identifier
    ;

genericTypeArguments
    : Less genericTypeArgumentList Greater
    ;

genericTypeArgumentList
    : typeName
    | genericTypeArgumentList Comma typeName
    ;

builtInType
    : integerType
    | floatingType
    | textualType
    | objectType = Object
    | ptrType = Ptr (genericTypeArguments | Less Const? Void Greater)?
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
    | Uint8
    | Uint16
    | Uint32
    | Uint64
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
    | Wchar
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
    | Default
    ;

booleanLiteral
    : True
    | False
    ;
