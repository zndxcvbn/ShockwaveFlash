using System.Globalization;
using ShockwaveFlash.Avm1.Compilation.Verification;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Assembly;

public readonly record struct Avm1AssemblyInstructionIndex(int Value)
{
    public static readonly Avm1AssemblyInstructionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "instruction" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1LabelId(int Value)
{
    public static readonly Avm1LabelId Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "label" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1AssemblyInstructionLocation(
    Avm1AssemblyInstructionIndex Instruction,
    int ActionIndex,
    int ByteOffset,
    int ByteLength);

public enum Avm1SyntheticActionKind : byte
{
    None,
    TrampolineGuard,
    TrampolineJump
}

public readonly record struct Avm1AssemblyActionLocation(
    int ActionIndex,
    int ByteOffset,
    int ByteLength,
    Avm1AssemblyInstructionIndex Owner,
    Avm1SyntheticActionKind SyntheticKind);

public readonly record struct Avm1AssemblyDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1AssemblyInstructionIndex Instruction,
    int ByteOffset,
    string Message);

public enum Avm1NestedCodeUnitKind : byte
{
    DefineFunctionBody,
    DefineFunction2Body,
    WithBody,
    TryBody,
    CatchBody,
    FinallyBody
}

public readonly record struct Avm1NestedActionBody(
    Avm1AssemblyInstructionIndex Owner,
    Avm1NestedCodeUnitKind Kind,
    Avm1ActionBody Body)
{
    public int ByteOffset { get; internal init; } = -1;

    internal Avm1AssemblyOptions? AssemblyOptions { get; init; }
}

internal readonly record struct Avm1ExternalBranchRelocation(
    Avm1AssemblyInstructionIndex Instruction,
    int ActionIndex,
    int ByteOffset,
    int ByteLength,
    ActionOpcode Opcode,
    Avm1ActionAssembler TargetAssembler,
    Avm1LabelId Target);

public sealed record Avm1AssemblyOptions(byte SwfVersion)
{
    public bool VerifyDataFlow { get; init; } = true;

    public bool RequireEndAction { get; init; }

    public bool RequireEmptyStackAtExit { get; init; } = true;

    public ushort? InitialConstantPoolCount { get; init; }

    internal Avm1CodeUnitContext? CodeUnitContext { get; init; }

    internal Avm1ActionAssembler? ExternalBranchParent { get; init; }
}

public sealed class Avm1ActionBody
{
    private readonly Avm1Action[] _actions;
    private readonly int[] _actionOffsets;
    private readonly int[] _labelOffsets;
    private readonly int[] _labelActionIndices;
    private readonly Avm1AssemblyInstructionLocation[] _instructionLocations;
    private readonly Avm1AssemblyActionLocation[] _actionLocations;
    private readonly Avm1NestedActionBody[] _nestedBodies;
    private readonly Avm1ExternalBranchRelocation[] _externalBranchRelocations;
    private readonly Avm1AssemblyDiagnostic[] _diagnostics;

    internal Avm1ActionBody(
        ReadOnlyMemory<byte> bytes,
        Avm1Action[] actions,
        int[] actionOffsets,
        int[] labelOffsets,
        int[] labelActionIndices,
        Avm1AssemblyInstructionLocation[] instructionLocations,
        Avm1AssemblyActionLocation[] actionLocations,
        Avm1NestedActionBody[] nestedBodies,
        Avm1ExternalBranchRelocation[] externalBranchRelocations,
        int maximumStackDepth,
        bool maximumStackDepthIsExact,
        Avm1AssemblyDiagnostic[] diagnostics)
    {
        Bytes = bytes;
        _actions = actions;
        _actionOffsets = actionOffsets;
        _labelOffsets = labelOffsets;
        _labelActionIndices = labelActionIndices;
        _instructionLocations = instructionLocations;
        _actionLocations = actionLocations;
        _nestedBodies = nestedBodies;
        _externalBranchRelocations = externalBranchRelocations;
        MaximumStackDepth = maximumStackDepth;
        MaximumStackDepthIsExact = maximumStackDepthIsExact;
        _diagnostics = diagnostics;
    }

    internal static Avm1ActionBody FromBytecode(
        ReadOnlyMemory<byte> bytecode,
        Avm1AssemblyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bytes = bytecode.ToArray();
        var verification = Avm1BytecodeVerifier.Verify(
            bytes,
            new Avm1BytecodeVerificationOptions(options.SwfVersion)
            {
                VerifyDataFlow = options.VerifyDataFlow,
                RequireEndAction = options.RequireEndAction,
                RequireEmptyStackAtExit = options.RequireEmptyStackAtExit,
                InitialConstantPoolCount = options.InitialConstantPoolCount,
                CodeUnitContext = options.CodeUnitContext
            });
        var actions = verification.Actions.ToArray();
        var offsets = verification.ActionOffsets.ToArray();
        var locations = new Avm1AssemblyActionLocation[actions.Length];
        for (var i = 0; i < locations.Length; i++)
        {
            var end = i + 1 < offsets.Length
                ? offsets[i + 1]
                : bytes.Length;
            locations[i] = new Avm1AssemblyActionLocation(
                i,
                offsets[i],
                end - offsets[i],
                Avm1AssemblyInstructionIndex.Invalid,
                Avm1SyntheticActionKind.None);
        }
        var diagnostics = verification.Diagnostics.Select(diagnostic =>
            new Avm1AssemblyDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                Avm1AssemblyInstructionIndex.Invalid,
                diagnostic.ByteOffset,
                diagnostic.Message)).ToArray();
        return new Avm1ActionBody(
            bytes,
            actions,
            offsets,
            [],
            [],
            [],
            locations,
            [],
            [],
            verification.MaximumStackDepth,
            verification.MaximumStackDepthIsExact,
            diagnostics);
    }

    public ReadOnlyMemory<byte> Bytes { get; }

    public IReadOnlyList<Avm1Action> Actions => _actions;

    public IReadOnlyList<int> ActionOffsets => _actionOffsets;

    public IReadOnlyList<int> LabelOffsets => _labelOffsets;

    public IReadOnlyList<int> LabelActionIndices => _labelActionIndices;

    public IReadOnlyList<Avm1AssemblyInstructionLocation> InstructionLocations =>
        _instructionLocations;

    public IReadOnlyList<Avm1AssemblyActionLocation> ActionLocations =>
        _actionLocations;

    public IReadOnlyList<Avm1NestedActionBody> NestedBodies => _nestedBodies;

    internal IReadOnlyList<Avm1ExternalBranchRelocation>
        ExternalBranchRelocations => _externalBranchRelocations;

    public int MaximumStackDepth { get; }

    public bool MaximumStackDepthIsExact { get; }

    public IReadOnlyList<Avm1AssemblyDiagnostic> Diagnostics => _diagnostics;

    public bool Succeeded => _diagnostics.All(diagnostic =>
        diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);

    public int GetLabelOffset(Avm1LabelId label)
    {
        if (!label.IsValid || label.Value >= _labelOffsets.Length)
            throw new ArgumentOutOfRangeException(nameof(label));
        return _labelOffsets[label.Value];
    }

    public int GetLabelActionIndex(Avm1LabelId label)
    {
        if (!label.IsValid || label.Value >= _labelActionIndices.Length)
            throw new ArgumentOutOfRangeException(nameof(label));
        return _labelActionIndices[label.Value];
    }

    public Avm1AssemblyInstructionLocation GetLocation(
        Avm1AssemblyInstructionIndex instruction)
    {
        if (!instruction.IsValid || instruction.Value >= _instructionLocations.Length)
            throw new ArgumentOutOfRangeException(nameof(instruction));
        return _instructionLocations[instruction.Value];
    }
}
