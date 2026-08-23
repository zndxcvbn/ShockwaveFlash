using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Binding;

public readonly record struct Avm1SyntaxSourceDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1SyntaxFileIndex File,
    Avm1TextSpan Span,
    string Message);

public sealed class Avm1SyntaxSourceProjection
{
    private readonly Avm1SyntaxSourceDiagnostic[] _diagnostics;
    private readonly Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SourceClass>
        _types;
    private readonly Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SourceClassMember>
        _members;

    internal Avm1SyntaxSourceProjection(
        Avm1SyntaxProgram syntaxProgram,
        Avm1SyntaxLexicalBinding lexicalBinding,
        Avm1SourceProgram sourceProgram,
        Avm1SyntaxSourceDiagnostic[] diagnostics,
        Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SourceClass> types,
        Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SourceClassMember> members)
    {
        SyntaxProgram = syntaxProgram;
        LexicalBinding = lexicalBinding;
        SourceProgram = sourceProgram;
        _diagnostics = diagnostics;
        _types = types;
        _members = members;
    }

    public Avm1SyntaxProgram SyntaxProgram { get; }

    public Avm1SyntaxLexicalBinding LexicalBinding { get; }

    public Avm1SourceProgram SourceProgram { get; }

    public IReadOnlyList<Avm1SyntaxSourceDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors =>
        SyntaxProgram.HasErrors ||
        LexicalBinding.HasErrors ||
        _diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    public bool TryGetType(
        Avm1SyntaxProgramSymbolIndex syntaxSymbol,
        out Avm1SourceClass sourceType) =>
        _types.TryGetValue(syntaxSymbol, out sourceType!);

    public bool TryGetMember(
        Avm1SyntaxProgramSymbolIndex syntaxSymbol,
        out Avm1SourceClassMember sourceMember) =>
        _members.TryGetValue(syntaxSymbol, out sourceMember!);
}
