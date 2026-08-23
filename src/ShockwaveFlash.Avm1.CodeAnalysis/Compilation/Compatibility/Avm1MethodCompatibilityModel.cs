using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compatibility;

public sealed record Avm1MethodCompatibilityInput
{
    private IReadOnlyList<string>? _initialConstantPool;

    public Avm1MethodCompatibilityInput(
        string name,
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (swfVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(swfVersion));

        Name = name;
        Bytecode = bytecode;
        SwfVersion = swfVersion;
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> Bytecode { get; }

    public byte SwfVersion { get; }

    public FunctionContext? FunctionContext { get; init; }

    public byte? RegisterCount { get; init; }

    internal Avm1SourceProjectionHints? SourceProjectionHints { get; init; }

    public IReadOnlyList<string>? InitialConstantPool
    {
        get => _initialConstantPool;
        init
        {
            if (value is { Count: > ushort.MaxValue })
                throw new ArgumentOutOfRangeException(nameof(value));

            _initialConstantPool = value?.ToArray();
        }
    }
}

public sealed record Avm1MethodCompatibilityOptions
{
    public bool VerifyDataFlow { get; init; } = true;

    public bool RequireEmptyStackAtExit { get; init; } = true;

    public bool AllowReferenceStackUnderflow { get; init; }

    public bool CompareNormalizedSource { get; init; } = true;

    public bool CoalesceNonInterferingRegisterVersions { get; init; }
}

public enum Avm1CompatibilitySide : byte
{
    Reference,
    Candidate,
    Comparison
}

public readonly record struct Avm1CompatibilityDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1CompatibilitySide Side,
    string Message);

public readonly record struct Avm1OpcodeCount(
    ActionOpcode Opcode,
    int Count);

public sealed class Avm1MethodBytecodeProfile
{
    private readonly Avm1OpcodeCount[] _opcodeCounts;

    internal Avm1MethodBytecodeProfile(
        int byteLength,
        int actionCount,
        int maximumStackDepth,
        bool maximumStackDepthIsExact,
        byte? registerCount,
        Avm1OpcodeCount[] opcodeCounts)
    {
        ByteLength = byteLength;
        ActionCount = actionCount;
        MaximumStackDepth = maximumStackDepth;
        MaximumStackDepthIsExact = maximumStackDepthIsExact;
        RegisterCount = registerCount;
        _opcodeCounts = opcodeCounts;
    }

    public int ByteLength { get; }

    public int ActionCount { get; }

    public int MaximumStackDepth { get; }

    public bool MaximumStackDepthIsExact { get; }

    public byte? RegisterCount { get; }

    public IReadOnlyList<Avm1OpcodeCount> OpcodeCounts => _opcodeCounts;
}

public sealed class Avm1MethodCompatibilityResult
{
    private readonly Avm1CompatibilityDiagnostic[] _diagnostics;

    internal Avm1MethodCompatibilityResult(
        Avm1BytecodeVerificationResult referenceVerification,
        Avm1BytecodeVerificationResult candidateVerification,
        Avm1MethodBytecodeProfile referenceProfile,
        Avm1MethodBytecodeProfile candidateProfile,
        Avm1SourceMethod? referenceSource,
        Avm1SourceMethod? candidateSource,
        Avm1SourceEquivalenceResult? sourceEquivalence,
        bool sourceComparisonRequested,
        Avm1CompatibilityDiagnostic[] diagnostics)
    {
        ReferenceVerification = referenceVerification;
        CandidateVerification = candidateVerification;
        ReferenceProfile = referenceProfile;
        CandidateProfile = candidateProfile;
        ReferenceSource = referenceSource;
        CandidateSource = candidateSource;
        SourceEquivalence = sourceEquivalence;
        SourceComparisonRequested = sourceComparisonRequested;
        _diagnostics = diagnostics;
    }

    public Avm1BytecodeVerificationResult ReferenceVerification { get; }

    public Avm1BytecodeVerificationResult CandidateVerification { get; }

    public Avm1MethodBytecodeProfile ReferenceProfile { get; }

    public Avm1MethodBytecodeProfile CandidateProfile { get; }

    public Avm1SourceMethod? ReferenceSource { get; }

    public Avm1SourceMethod? CandidateSource { get; }

    public Avm1SourceEquivalenceResult? SourceEquivalence { get; }

    public bool SourceComparisonRequested { get; }

    public IReadOnlyList<Avm1CompatibilityDiagnostic> Diagnostics =>
        _diagnostics;

    public bool BytecodeIsValid =>
        ReferenceVerification.Succeeded && CandidateVerification.Succeeded;

    public bool IsCompatible =>
        BytecodeIsValid &&
        (!SourceComparisonRequested ||
            SourceEquivalence is { IsEquivalent: true });
}
