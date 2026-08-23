namespace ShockwaveFlash.Avm1.Decompilation;

public readonly record struct Avm1MethodDecompilationOptions(
    string? SetterPropertyName = null)
{
    public Avm1TimelineLayout? TimelineLayout { get; init; }

    public int MaximumStructuredBlockCount { get; init; }

    internal Avm1SourceProjectionHints? SourceProjectionHints { get; init; }
}
