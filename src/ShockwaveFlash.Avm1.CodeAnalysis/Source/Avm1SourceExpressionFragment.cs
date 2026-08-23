namespace ShockwaveFlash.Avm1.Source;

public sealed class Avm1SourceExpressionFragment
{
    internal Avm1SourceExpressionFragment(
        Avm1SourceArena arena,
        SourceExpressionIndex expression,
        Avm1SourceDiagnostic[] diagnostics)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!expression.IsValid)
            throw new ArgumentOutOfRangeException(nameof(expression));

        Arena = arena;
        Expression = expression;
        Diagnostics = diagnostics;
    }

    public Avm1SourceArena Arena { get; }

    public SourceExpressionIndex Expression { get; }

    public IReadOnlyList<Avm1SourceDiagnostic> Diagnostics { get; }

    public bool IsComplete => Diagnostics.All(diagnostic =>
        diagnostic.Severity is not Avm1SourceDiagnosticSeverity.Error);

    public void WriteAs2(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new Emit.Avm1SourceAs2Emitter(writer).Write(this);
    }

    public string GetAs2Text()
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        WriteAs2(writer);
        return writer.ToString();
    }

    internal Avm1SourceMethod CreateReturnMethod() =>
        Arena.CreateReturnMethod(Expression, Diagnostics.ToArray());
}
