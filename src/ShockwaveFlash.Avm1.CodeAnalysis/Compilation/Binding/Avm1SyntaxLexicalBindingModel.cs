using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Binding;

public readonly record struct Avm1SyntaxCodeUnitIndex(int Value)
{
    public static readonly Avm1SyntaxCodeUnitIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxScopeIndex(int Value)
{
    public static readonly Avm1SyntaxScopeIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxLocalSymbolIndex(int Value)
{
    public static readonly Avm1SyntaxLocalSymbolIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxLocalNameIndex(int Value)
{
    public static readonly Avm1SyntaxLocalNameIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxLocalDeclarationIndex(int Value)
{
    public static readonly Avm1SyntaxLocalDeclarationIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxControlTargetIndex(int Value)
{
    public static readonly Avm1SyntaxControlTargetIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxExpressionRootList(int Start, int Count)
{
    public static readonly Avm1SyntaxExpressionRootList Empty = new(0, 0);
}

public readonly record struct Avm1SyntaxLocalSymbolList(int Start, int Count)
{
    public static readonly Avm1SyntaxLocalSymbolList Empty = new(0, 0);
}

public readonly record struct Avm1SyntaxLocalDeclarationList(int Start, int Count)
{
    public static readonly Avm1SyntaxLocalDeclarationList Empty = new(0, 0);
}

public enum Avm1SyntaxCodeUnitKind : byte
{
    TypeInitializer,
    Member,
    FunctionDeclaration,
    FunctionExpression
}

[Flags]
public enum Avm1SyntaxCodeUnitFlags : byte
{
    None = 0,
    DeclaredInDynamicScope = 1 << 0,
    ContainsWith = 1 << 1,
    ContainsDirectEval = 1 << 2,
    HasDescendantDirectEval = 1 << 3
}

public enum Avm1SyntaxScopeKind : byte
{
    CodeUnit,
    FunctionName,
    Catch,
    With,
    Target
}

[Flags]
public enum Avm1SyntaxScopeFlags : byte
{
    None = 0,
    Dynamic = 1 << 0
}

[Flags]
public enum Avm1SyntaxLocalSymbolFlags : byte
{
    None = 0,
    Parameter = 1 << 0,
    Variable = 1 << 1,
    FunctionDeclaration = 1 << 2,
    Catch = 1 << 3,
    FunctionExpressionName = 1 << 4,
    Arguments = 1 << 5
}

public enum Avm1SyntaxLocalDeclarationKind : byte
{
    Parameter,
    Variable,
    FunctionDeclaration,
    Catch,
    FunctionExpressionName
}

public enum Avm1SyntaxIdentifierBindingKind : byte
{
    NotApplicable,
    Unresolved,
    Lexical,
    Dynamic
}

public readonly record struct Avm1SyntaxIdentifierBinding(
    Avm1SyntaxIdentifierBindingKind Kind,
    Avm1SyntaxLocalSymbolIndex Symbol)
{
    public static readonly Avm1SyntaxIdentifierBinding NotApplicable = new(
        Avm1SyntaxIdentifierBindingKind.NotApplicable,
        Avm1SyntaxLocalSymbolIndex.Invalid);

    public static readonly Avm1SyntaxIdentifierBinding Unresolved = new(
        Avm1SyntaxIdentifierBindingKind.Unresolved,
        Avm1SyntaxLocalSymbolIndex.Invalid);

    public static Avm1SyntaxIdentifierBinding Lexical(
        Avm1SyntaxLocalSymbolIndex symbol) =>
        new(Avm1SyntaxIdentifierBindingKind.Lexical, symbol);

    public static Avm1SyntaxIdentifierBinding Dynamic(
        Avm1SyntaxLocalSymbolIndex fallback) =>
        new(Avm1SyntaxIdentifierBindingKind.Dynamic, fallback);
}

public enum Avm1SyntaxControlTargetKind : byte
{
    Loop,
    Switch,
    Label
}

[Flags]
public enum Avm1SyntaxControlTargetFlags : byte
{
    None = 0,
    Breakable = 1 << 0,
    Continuable = 1 << 1
}

public enum Avm1SyntaxControlResolutionKind : byte
{
    NotApplicable,
    Bound,
    Unresolved,
    Invalid
}

public readonly record struct Avm1SyntaxControlTransferBinding(
    Avm1SyntaxControlResolutionKind Kind,
    Avm1SyntaxControlTargetIndex Target)
{
    public static readonly Avm1SyntaxControlTransferBinding NotApplicable = new(
        Avm1SyntaxControlResolutionKind.NotApplicable,
        Avm1SyntaxControlTargetIndex.Invalid);

    public static readonly Avm1SyntaxControlTransferBinding Unresolved = new(
        Avm1SyntaxControlResolutionKind.Unresolved,
        Avm1SyntaxControlTargetIndex.Invalid);

    public static readonly Avm1SyntaxControlTransferBinding Invalid = new(
        Avm1SyntaxControlResolutionKind.Invalid,
        Avm1SyntaxControlTargetIndex.Invalid);

    public static Avm1SyntaxControlTransferBinding Bound(
        Avm1SyntaxControlTargetIndex target) =>
        new(Avm1SyntaxControlResolutionKind.Bound, target);
}

public readonly record struct Avm1SyntaxCodeUnit(
    Avm1SyntaxCodeUnitIndex Index,
    Avm1SyntaxCodeUnitKind Kind,
    Avm1SyntaxCodeUnitIndex Parent,
    Avm1SyntaxScopeIndex DeclarationScope,
    Avm1SyntaxScopeIndex RootScope,
    Avm1SyntaxFileIndex File,
    Avm1SyntaxProgramSymbolIndex ContainingType,
    Avm1SyntaxProgramSymbolIndex ContainingMember,
    Avm1StatementIndex OwnerStatement,
    Avm1ExpressionIndex OwnerExpression,
    Avm1StatementIndex RootStatement,
    Avm1SyntaxExpressionRootList ExpressionRoots,
    Avm1SyntaxCodeUnitFlags Flags);

public readonly record struct Avm1SyntaxScope(
    Avm1SyntaxScopeIndex Index,
    Avm1SyntaxScopeKind Kind,
    Avm1SyntaxScopeIndex Parent,
    Avm1SyntaxCodeUnitIndex CodeUnit,
    Avm1StatementIndex OwnerStatement,
    Avm1ExpressionIndex OwnerExpression,
    Avm1SyntaxScopeFlags Flags,
    Avm1SyntaxLocalSymbolList Symbols);

public readonly record struct Avm1SyntaxLocalSymbol(
    Avm1SyntaxLocalSymbolIndex Index,
    Avm1SyntaxLocalNameIndex Name,
    Avm1SyntaxLocalSymbolFlags Flags,
    Avm1SyntaxScopeIndex Scope,
    Avm1SyntaxCodeUnitIndex CodeUnit,
    Avm1TextSpan FirstDeclarationSpan,
    Avm1SyntaxLocalDeclarationList Declarations);

public readonly record struct Avm1SyntaxLocalDeclaration(
    Avm1SyntaxLocalDeclarationIndex Index,
    Avm1SyntaxLocalSymbolIndex Symbol,
    Avm1SyntaxLocalDeclarationKind Kind,
    Avm1SyntaxFileIndex File,
    Avm1SyntaxCodeUnitIndex CodeUnit,
    Avm1SyntaxTokenIndex NameToken,
    Avm1StatementIndex OwnerStatement,
    Avm1ExpressionIndex OwnerExpression,
    int Ordinal,
    Avm1TextSpan Span);

public readonly record struct Avm1SyntaxControlTarget(
    Avm1SyntaxControlTargetIndex Index,
    Avm1SyntaxControlTargetKind Kind,
    Avm1SyntaxControlTargetFlags Flags,
    Avm1SyntaxFileIndex File,
    Avm1SyntaxCodeUnitIndex CodeUnit,
    Avm1StatementIndex OwnerStatement,
    Avm1StatementIndex TargetStatement,
    Avm1SyntaxLocalNameIndex LabelName);
