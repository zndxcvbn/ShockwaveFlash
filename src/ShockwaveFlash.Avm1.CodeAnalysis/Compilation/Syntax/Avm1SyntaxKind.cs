namespace ShockwaveFlash.Avm1.Compilation.Syntax;

public enum Avm1SyntaxKind : ushort
{
    BadToken,
    EndOfFileToken,
    IdentifierToken,
    NumericLiteralToken,
    StringLiteralToken,

    OpenBraceToken,
    CloseBraceToken,
    OpenParenthesisToken,
    CloseParenthesisToken,
    OpenBracketToken,
    CloseBracketToken,
    DotToken,
    CommaToken,
    ColonToken,
    SemicolonToken,
    QuestionToken,
    HashToken,
    DoubleColonToken,

    PlusToken,
    PlusPlusToken,
    PlusEqualsToken,
    MinusToken,
    MinusMinusToken,
    MinusEqualsToken,
    AsteriskToken,
    AsteriskEqualsToken,
    SlashToken,
    SlashEqualsToken,
    PercentToken,
    PercentEqualsToken,
    EqualsToken,
    EqualsEqualsToken,
    EqualsEqualsEqualsToken,
    ExclamationToken,
    ExclamationEqualsToken,
    ExclamationEqualsEqualsToken,
    LessThanToken,
    LessThanEqualsToken,
    LessThanLessThanToken,
    LessThanLessThanEqualsToken,
    GreaterThanToken,
    GreaterThanEqualsToken,
    GreaterThanGreaterThanToken,
    GreaterThanGreaterThanEqualsToken,
    GreaterThanGreaterThanGreaterThanToken,
    GreaterThanGreaterThanGreaterThanEqualsToken,
    AmpersandToken,
    AmpersandAmpersandToken,
    AmpersandEqualsToken,
    PipeToken,
    PipePipeToken,
    PipeEqualsToken,
    CaretToken,
    CaretEqualsToken,
    TildeToken,

    BreakKeyword,
    CaseKeyword,
    CatchKeyword,
    ClassKeyword,
    ConstKeyword,
    ContinueKeyword,
    DefaultKeyword,
    DeleteKeyword,
    DoKeyword,
    DynamicKeyword,
    ElseKeyword,
    ExtendsKeyword,
    FalseKeyword,
    FinalKeyword,
    FinallyKeyword,
    ForKeyword,
    FunctionKeyword,
    GetKeyword,
    IfKeyword,
    ImplementsKeyword,
    ImportKeyword,
    InKeyword,
    InstanceOfKeyword,
    InterfaceKeyword,
    IntrinsicKeyword,
    NewKeyword,
    NullKeyword,
    OverrideKeyword,
    PrivateKeyword,
    ProtectedKeyword,
    PublicKeyword,
    ReturnKeyword,
    SetKeyword,
    StaticKeyword,
    SuperKeyword,
    SwitchKeyword,
    ThisKeyword,
    TellTargetKeyword,
    ThrowKeyword,
    TrueKeyword,
    TryKeyword,
    TypeOfKeyword,
    UndefinedKeyword,
    VarKeyword,
    VoidKeyword,
    WhileKeyword,
    WithKeyword
}

public enum Avm1SyntaxTriviaKind : byte
{
    Whitespace,
    EndOfLine,
    SingleLineComment,
    MultiLineComment
}

public enum Avm1LanguageDialect : byte
{
    ActionScript2,
    ActionScript1Compatibility,
    FlashLite
}

[Flags]
public enum Avm1SyntaxFeatures : byte
{
    None = 0,
    PreprocessorDirectives = 1 << 0
}

public sealed record Avm1ParseOptions
{
    public static Avm1ParseOptions Default { get; } = new();

    public Avm1LanguageDialect Dialect { get; init; } =
        Avm1LanguageDialect.ActionScript2;

    public Avm1SyntaxFeatures Features { get; init; } =
        Avm1SyntaxFeatures.PreprocessorDirectives;
}
