namespace ShockwaveFlash.Avm1.Decompilation;

public enum Avm1DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public readonly record struct Avm1Diagnostic(
    Avm1DiagnosticSeverity Severity,
    ActionIndex Action,
    string Message);
