namespace ShockwaveFlash.Avm1.Compilation.Syntax;

public readonly record struct Avm1ExpressionIndex(int Value)
{
    public static readonly Avm1ExpressionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1ExpressionList(int Start, int Count)
{
    public static readonly Avm1ExpressionList Empty = new(0, 0);
}

public enum Avm1ExpressionSyntaxKind : byte
{
    Missing,
    Omitted,
    Skipped,
    IdentifierName,
    ThisExpression,
    SuperExpression,
    LiteralExpression,
    ParenthesizedExpression,
    ArrayLiteralExpression,
    ObjectLiteralExpression,
    ObjectProperty,
    FunctionExpression,
    UnaryExpression,
    PostfixUnaryExpression,
    BinaryExpression,
    ConditionalExpression,
    AssignmentExpression,
    CommaExpression,
    MemberAccessExpression,
    ElementAccessExpression,
    InvocationExpression,
    NewExpression
}

public readonly record struct Avm1ExpressionSyntax(
    Avm1ExpressionIndex Index,
    Avm1ExpressionSyntaxKind Kind,
    Avm1SyntaxTokenIndex Token,
    Avm1SyntaxTokenIndex OperatorToken,
    Avm1ExpressionList Children,
    Avm1SyntaxTokenList AuxiliaryTokens,
    Avm1ParameterList Parameters,
    Avm1QualifiedNameSyntax DeclaredType,
    Avm1StatementIndex Body,
    Avm1TextSpan BodySpan,
    Avm1TextSpan Span);
