namespace ShockwaveFlash.Avm1.Compilation.Syntax;

public readonly record struct Avm1StatementIndex(int Value)
{
    public static readonly Avm1StatementIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1StatementList(int Start, int Count)
{
    public static readonly Avm1StatementList Empty = new(0, 0);
}

public enum Avm1StatementSyntaxKind : byte
{
    Missing,
    Skipped,
    Block,
    Empty,
    Expression,
    VariableDeclaration,
    FunctionDeclaration,
    Return,
    Throw,
    If,
    IfFrameLoaded,
    While,
    DoWhile,
    For,
    ForIn,
    Switch,
    SwitchSection,
    Break,
    Continue,
    With,
    TellTarget,
    Try,
    CatchClause,
    FinallyClause,
    Labeled,
    Directive
}

[Flags]
public enum Avm1StatementSyntaxFlags : byte
{
    None = 0,
    HasDefaultLabel = 1 << 0
}

public readonly record struct Avm1StatementSyntax(
    Avm1StatementIndex Index,
    Avm1StatementSyntaxKind Kind,
    Avm1StatementSyntaxFlags Flags,
    Avm1SyntaxTokenIndex Keyword,
    Avm1SyntaxTokenIndex NameToken,
    Avm1ExpressionIndex Expression,
    Avm1ExpressionIndex SecondaryExpression,
    Avm1ExpressionList Initializers,
    Avm1ExpressionList Expressions,
    Avm1VariableDeclaratorList Declarators,
    Avm1ParameterList Parameters,
    Avm1QualifiedNameSyntax DeclaredType,
    Avm1StatementList Children,
    Avm1TextSpan Span);
