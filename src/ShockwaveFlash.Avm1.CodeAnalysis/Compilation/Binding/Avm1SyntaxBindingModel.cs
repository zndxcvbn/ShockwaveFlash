using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Binding;

public readonly record struct Avm1SyntaxFileIndex(int Value)
{
    public static readonly Avm1SyntaxFileIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxProgramSymbolIndex(int Value)
{
    public static readonly Avm1SyntaxProgramSymbolIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxNameIndex(int Value)
{
    public static readonly Avm1SyntaxNameIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxSymbolList(int Start, int Count)
{
    public static readonly Avm1SyntaxSymbolList Empty = new(0, 0);
}

public readonly record struct Avm1SyntaxImportList(int Start, int Count)
{
    public static readonly Avm1SyntaxImportList Empty = new(0, 0);
}

public readonly record struct Avm1SyntaxTypeUseList(int Start, int Count)
{
    public static readonly Avm1SyntaxTypeUseList Empty = new(0, 0);
}

public enum Avm1SyntaxProgramSymbolKind : byte
{
    Class,
    Interface,
    Field,
    Constructor,
    Method,
    Getter,
    Setter
}

public enum Avm1SyntaxImportKind : byte
{
    Explicit,
    Wildcard
}

public enum Avm1SyntaxTypeUseKind : byte
{
    BaseClass,
    ExtendedInterface,
    ImplementedInterface
}

public enum Avm1SyntaxSymbolResolutionKind : byte
{
    NotApplicable,
    Unresolved,
    Bound,
    Ambiguous
}

public readonly record struct Avm1SyntaxSymbolResolution(
    Avm1SyntaxSymbolResolutionKind Kind,
    Avm1SyntaxProgramSymbolIndex Symbol)
{
    public static readonly Avm1SyntaxSymbolResolution NotApplicable = new(
        Avm1SyntaxSymbolResolutionKind.NotApplicable,
        Avm1SyntaxProgramSymbolIndex.Invalid);

    public static readonly Avm1SyntaxSymbolResolution Unresolved = new(
        Avm1SyntaxSymbolResolutionKind.Unresolved,
        Avm1SyntaxProgramSymbolIndex.Invalid);

    public static readonly Avm1SyntaxSymbolResolution Ambiguous = new(
        Avm1SyntaxSymbolResolutionKind.Ambiguous,
        Avm1SyntaxProgramSymbolIndex.Invalid);

    public static Avm1SyntaxSymbolResolution Bound(
        Avm1SyntaxProgramSymbolIndex symbol) =>
        new(Avm1SyntaxSymbolResolutionKind.Bound, symbol);
}

public readonly record struct Avm1SyntaxDeclarationOrigin(
    Avm1SyntaxFileIndex File,
    int TypeOrdinal,
    int MemberOrdinal,
    int DeclaratorOrdinal)
{
    public static readonly Avm1SyntaxDeclarationOrigin Invalid = new(
        Avm1SyntaxFileIndex.Invalid,
        -1,
        -1,
        -1);
}

public readonly record struct Avm1SyntaxProgramSymbol(
    Avm1SyntaxProgramSymbolIndex Index,
    Avm1SyntaxProgramSymbolKind Kind,
    Avm1SyntaxNameIndex Name,
    Avm1SyntaxNameIndex QualifiedName,
    Avm1SyntaxProgramSymbolIndex ContainingType,
    Avm1SyntaxModifiers Modifiers,
    Avm1SyntaxDeclarationOrigin Origin,
    Avm1TextSpan Span,
    Avm1SyntaxSymbolList Members);

public readonly record struct Avm1SyntaxProgramFile(
    Avm1SyntaxFileIndex Index,
    Avm1SyntaxNameIndex Path,
    Avm1SyntaxImportList Imports,
    Avm1SyntaxSymbolList Types,
    Avm1SyntaxTypeUseList TypeUses);

public readonly record struct Avm1SyntaxImport(
    Avm1SyntaxFileIndex File,
    Avm1SyntaxImportKind Kind,
    Avm1SyntaxNameIndex Name,
    Avm1SyntaxNameIndex NamespaceName,
    Avm1SyntaxNameIndex SimpleName,
    Avm1TextSpan Span,
    Avm1SyntaxSymbolResolution Target);

public readonly record struct Avm1SyntaxTypeUse(
    Avm1SyntaxProgramSymbolIndex ContainingType,
    Avm1SyntaxTypeUseKind Kind,
    Avm1SyntaxNameIndex Name,
    Avm1TextSpan Span,
    Avm1SyntaxSymbolResolution Resolution);

public readonly record struct Avm1SyntaxBindingDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1SyntaxFileIndex File,
    Avm1TextSpan Span,
    string Message);

public sealed class Avm1SyntaxSourceFile
{
    public Avm1SyntaxSourceFile(string path, Avm1SyntaxTree syntaxTree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(syntaxTree);
        Path = path;
        SyntaxTree = syntaxTree;
    }

    public string Path { get; }

    public Avm1SyntaxTree SyntaxTree { get; }
}
