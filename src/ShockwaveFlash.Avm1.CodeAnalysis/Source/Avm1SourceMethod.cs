namespace ShockwaveFlash.Avm1.Source;

public sealed class Avm1SourceMethod
{
    internal Avm1SourceMethod(
        Avm1SourceArena arena,
        SourceStatementIndex body,
        SourceSymbolIndex[] parameters,
        Avm1SourceDiagnostic[] diagnostics)
    {
        Arena = arena;
        Body = body;
        Parameters = parameters;
        Diagnostics = diagnostics;
    }

    public Avm1SourceArena Arena { get; }

    public SourceStatementIndex Body { get; }

    public IReadOnlyList<SourceSymbolIndex> Parameters { get; }

    public IReadOnlyList<Avm1SourceDiagnostic> Diagnostics { get; }

    public bool IsComplete => Diagnostics.All(diagnostic =>
        diagnostic.Severity is not Avm1SourceDiagnosticSeverity.Error);

    public void WriteAs2(TextWriter writer, int initialIndent = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new Emit.Avm1SourceAs2Emitter(writer, initialIndent).Write(this);
    }

    public string GetAs2Text()
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        WriteAs2(writer);
        return writer.ToString();
    }
}

public enum Avm1SourceDiagnosticSeverity : byte
{
    Info,
    Warning,
    Error
}

public readonly record struct Avm1SourceDiagnostic(
    string Code,
    Avm1SourceDiagnosticSeverity Severity,
    SourceOriginIndex Origin,
    string Message);
