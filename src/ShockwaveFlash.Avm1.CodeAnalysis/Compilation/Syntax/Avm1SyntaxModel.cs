using System.Globalization;
using System.Text;

namespace ShockwaveFlash.Avm1.Compilation.Syntax;

public readonly record struct Avm1TextSpan(int Start, int Length)
{
    public static readonly Avm1TextSpan Invalid = new(-1, 0);

    public static readonly Avm1TextSpan Empty = new(0, 0);

    public int End => checked(Start + Length);

    public bool IsValid => Start >= 0;

    public bool IsEmpty => Length == 0;

    public static Avm1TextSpan FromBounds(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        return new Avm1TextSpan(start, end - start);
    }

    public override string ToString() =>
        Start.ToString(CultureInfo.InvariantCulture) + ".." +
        End.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct Avm1SyntaxTokenIndex(int Value)
{
    public static readonly Avm1SyntaxTokenIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1SyntaxTriviaList(int Start, int Count)
{
    public static readonly Avm1SyntaxTriviaList Empty = new(0, 0);
}

public readonly record struct Avm1SyntaxTokenList(int Start, int Count)
{
    public static readonly Avm1SyntaxTokenList Empty = new(0, 0);
}

public readonly record struct Avm1SyntaxTrivia(
    Avm1SyntaxTriviaKind Kind,
    Avm1TextSpan Span);

public readonly record struct Avm1SyntaxToken(
    Avm1SyntaxKind Kind,
    Avm1TextSpan Span,
    Avm1SyntaxTriviaList LeadingTrivia)
{
    public ReadOnlySpan<char> GetText(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.AsSpan(Span.Start, Span.Length);
    }
}

public enum Avm1SyntaxDiagnosticSeverity : byte
{
    Info,
    Warning,
    Error
}

public readonly record struct Avm1SyntaxDiagnostic(
    string Code,
    Avm1SyntaxDiagnosticSeverity Severity,
    Avm1TextSpan Span,
    string Message);

public sealed class Avm1SyntaxTokenStream
{
    private readonly Avm1SyntaxToken[] _tokens;
    private readonly Avm1SyntaxTrivia[] _trivia;
    private readonly Avm1SyntaxDiagnostic[] _diagnostics;

    internal Avm1SyntaxTokenStream(
        string text,
        Avm1ParseOptions options,
        Avm1SyntaxToken[] tokens,
        Avm1SyntaxTrivia[] trivia,
        Avm1SyntaxDiagnostic[] diagnostics)
    {
        Text = text;
        Options = options;
        _tokens = tokens;
        _trivia = trivia;
        _diagnostics = diagnostics;
    }

    public string Text { get; }

    public Avm1ParseOptions Options { get; }

    public IReadOnlyList<Avm1SyntaxToken> Tokens => _tokens;

    public IReadOnlyList<Avm1SyntaxTrivia> Trivia => _trivia;

    public IReadOnlyList<Avm1SyntaxDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic =>
        diagnostic.Severity is Avm1SyntaxDiagnosticSeverity.Error);

    public ReadOnlySpan<char> GetText(Avm1TextSpan span) =>
        Text.AsSpan(span.Start, span.Length);

    public ReadOnlySpan<Avm1SyntaxTrivia> GetLeadingTrivia(
        Avm1SyntaxToken token) =>
        _trivia.AsSpan(token.LeadingTrivia.Start, token.LeadingTrivia.Count);

    public void WriteTokenizedText(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        foreach (var token in _tokens)
        {
            foreach (var trivia in GetLeadingTrivia(token))
                writer.Write(GetText(trivia.Span));
            writer.Write(GetText(token.Span));
        }
    }

    public string GetTokenizedText()
    {
        var builder = new StringBuilder(Text.Length);
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteTokenizedText(writer);
        return builder.ToString();
    }
}
