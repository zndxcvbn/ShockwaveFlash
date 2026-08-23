namespace ShockwaveFlash.Avm1.Compilation.Syntax;

[Flags]
public enum Avm1SyntaxModifiers : ushort
{
    None = 0,
    Public = 1 << 0,
    Private = 1 << 1,
    Protected = 1 << 2,
    Static = 1 << 3,
    Final = 1 << 4,
    Override = 1 << 5,
    Dynamic = 1 << 6,
    Intrinsic = 1 << 7
}

public enum Avm1TypeDeclarationKind : byte
{
    Class,
    Interface
}

public enum Avm1MemberDeclarationKind : byte
{
    Field,
    Method,
    Constructor,
    Getter,
    Setter,
    Directive,
    Unknown
}

public readonly record struct Avm1QualifiedNameList(int Start, int Count)
{
    public static readonly Avm1QualifiedNameList Empty = new(0, 0);
}

public readonly record struct Avm1MemberDeclarationList(int Start, int Count)
{
    public static readonly Avm1MemberDeclarationList Empty = new(0, 0);
}

public readonly record struct Avm1ParameterList(int Start, int Count)
{
    public static readonly Avm1ParameterList Empty = new(0, 0);
}

public readonly record struct Avm1VariableDeclaratorList(int Start, int Count)
{
    public static readonly Avm1VariableDeclaratorList Empty = new(0, 0);
}

public readonly record struct Avm1DirectiveIndex(int Value)
{
    public static readonly Avm1DirectiveIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1QualifiedNameSyntax(
    Avm1SyntaxTokenList Tokens,
    Avm1TextSpan Span)
{
    public static readonly Avm1QualifiedNameSyntax Missing = new(
        Avm1SyntaxTokenList.Empty,
        Avm1TextSpan.Invalid);

    public bool IsValid => Span.IsValid && Tokens.Count > 0;
}

public readonly record struct Avm1ImportDirectiveSyntax(
    Avm1SyntaxTokenIndex ImportKeyword,
    Avm1QualifiedNameSyntax Name,
    Avm1TextSpan Span);

public readonly record struct Avm1DirectiveSyntax(
    Avm1SyntaxTokenIndex HashToken,
    Avm1SyntaxTokenIndex NameToken,
    Avm1TextSpan ArgumentSpan,
    Avm1TextSpan Span);

public readonly record struct Avm1VariableDeclaratorSyntax(
    Avm1SyntaxTokenIndex NameToken,
    Avm1QualifiedNameSyntax DeclaredType,
    Avm1ExpressionIndex Initializer,
    Avm1TextSpan InitializerSpan,
    Avm1TextSpan Span);

public readonly record struct Avm1ParameterSyntax(
    Avm1SyntaxTokenIndex NameToken,
    Avm1QualifiedNameSyntax DeclaredType,
    Avm1ExpressionIndex DefaultValue,
    Avm1TextSpan DefaultValueSpan,
    Avm1TextSpan Span);

public readonly record struct Avm1MemberDeclarationSyntax(
    Avm1MemberDeclarationKind Kind,
    Avm1SyntaxModifiers Modifiers,
    Avm1SyntaxTokenIndex Keyword,
    Avm1SyntaxTokenIndex NameToken,
    Avm1VariableDeclaratorList Declarators,
    Avm1ParameterList Parameters,
    Avm1QualifiedNameSyntax DeclaredType,
    Avm1DirectiveIndex Directive,
    Avm1StatementIndex Body,
    Avm1TextSpan BodySpan,
    Avm1TextSpan Span);

public readonly record struct Avm1TypeDeclarationSyntax(
    Avm1TypeDeclarationKind Kind,
    Avm1SyntaxModifiers Modifiers,
    Avm1SyntaxTokenIndex Keyword,
    Avm1QualifiedNameSyntax Name,
    Avm1QualifiedNameList BaseTypes,
    Avm1QualifiedNameList ImplementedTypes,
    Avm1MemberDeclarationList Members,
    Avm1TextSpan BodySpan,
    Avm1TextSpan Span);
