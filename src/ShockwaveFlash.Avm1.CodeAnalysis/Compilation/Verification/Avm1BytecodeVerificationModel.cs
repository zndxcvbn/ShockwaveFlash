using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Verification;

public sealed record Avm1BytecodeVerificationOptions(byte SwfVersion)
{
    public Avm1CompilationRegisterFile RegisterFile { get; init; }

    public byte RegisterCount { get; init; }

    public bool VerifyDataFlow { get; init; } = true;

    public bool RequireEndAction { get; init; }

    public bool RequireEmptyStackAtExit { get; init; } = true;

    public bool AllowRuntimeStackUnderflow { get; init; }

    public ushort? InitialConstantPoolCount { get; init; }

    internal Avm1CodeUnitContext? CodeUnitContext { get; init; }

    internal int[] ExternalBranchExitOffsets { get; init; } = [];
}

public readonly record struct Avm1BytecodeVerificationDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    int ActionIndex,
    int ByteOffset,
    string Message);

public sealed class Avm1BytecodeVerificationResult
{
    private readonly Avm1Action[] _actions;
    private readonly int[] _actionOffsets;
    private readonly Avm1BytecodeVerificationDiagnostic[] _diagnostics;

    internal Avm1BytecodeVerificationResult(
        Avm1Action[] actions,
        int[] actionOffsets,
        int byteLength,
        int maximumStackDepth,
        bool maximumStackDepthIsExact,
        Avm1BytecodeVerificationDiagnostic[] diagnostics)
    {
        _actions = actions;
        _actionOffsets = actionOffsets;
        ByteLength = byteLength;
        MaximumStackDepth = maximumStackDepth;
        MaximumStackDepthIsExact = maximumStackDepthIsExact;
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<Avm1Action> Actions => _actions;

    public IReadOnlyList<int> ActionOffsets => _actionOffsets;

    public int ByteLength { get; }

    public int MaximumStackDepth { get; }

    public bool MaximumStackDepthIsExact { get; }

    public IReadOnlyList<Avm1BytecodeVerificationDiagnostic> Diagnostics => _diagnostics;

    public bool Succeeded => _diagnostics.All(diagnostic =>
        diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);
}
