using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation;

public sealed record Avm1TryPlan(
    Avm1MirTrySiteIndex Site,
    TryFlags Flags,
    SourceSymbolIndex CatchSymbol,
    string CatchVariable,
    byte CatchRegister);

public sealed class Avm1CompiledTryComponentArtifact
{
    private readonly Avm1CompiledInstructionMap[] _instructionMap;
    private readonly Avm1CompiledFunctionArtifact[] _nestedFunctions;
    private readonly Avm1CompiledWithArtifact[] _nestedWithRegions;
    private readonly Avm1CompiledTryArtifact[] _nestedTryRegions;

    internal Avm1CompiledTryComponentArtifact(
        Avm1SourceMethod source,
        Avm1MirMethod mir,
        Avm1StackLirMethod stackLir,
        Avm1CompiledInstructionMap[] instructionMap,
        Avm1CompiledFunctionArtifact[] nestedFunctions,
        Avm1CompiledWithArtifact[] nestedWithRegions,
        Avm1CompiledTryArtifact[] nestedTryRegions)
    {
        Source = source;
        Mir = mir;
        StackLir = stackLir;
        _instructionMap = instructionMap;
        _nestedFunctions = nestedFunctions;
        _nestedWithRegions = nestedWithRegions;
        _nestedTryRegions = nestedTryRegions;
    }

    public Avm1SourceMethod Source { get; }

    public Avm1MirMethod Mir { get; }

    public Avm1StackLirMethod StackLir { get; }

    public IReadOnlyList<Avm1CompiledInstructionMap> InstructionMap =>
        _instructionMap;

    public IReadOnlyList<Avm1CompiledFunctionArtifact> NestedFunctions =>
        _nestedFunctions;

    public IReadOnlyList<Avm1CompiledWithArtifact> NestedWithRegions =>
        _nestedWithRegions;

    public IReadOnlyList<Avm1CompiledTryArtifact> NestedTryRegions =>
        _nestedTryRegions;
}

public sealed record Avm1CompiledTryArtifact(
    Avm1TryPlan Plan,
    Avm1CompiledTryComponentArtifact TryBody,
    Avm1CompiledTryComponentArtifact? CatchBody,
    Avm1CompiledTryComponentArtifact? FinallyBody);

internal sealed record Avm1CompiledTryCodeUnit(
    Avm1TryPlan Plan,
    Avm1CodeUnit TryBody,
    Avm1CodeUnit? CatchBody,
    Avm1CodeUnit? FinallyBody,
    Avm1PendingParentBranch[] TryBodyParentBranches,
    Avm1PendingParentBranch[] CatchBodyParentBranches,
    Avm1PendingParentBranch[] FinallyBodyParentBranches,
    Avm1CompiledTryArtifact Artifact);

internal sealed record Avm1CompiledTryComponentCodeUnit(
    Avm1CodeUnit CodeUnit,
    Avm1PendingParentBranch[] ParentBranches,
    Avm1CompiledTryComponentArtifact Artifact);

internal readonly record struct Avm1PendingParentBranch(
    Avm1AssemblyInstructionIndex AssemblyInstruction,
    Avm1MirCompletionKind Kind,
    SourceStatementIndex TargetStatement,
    SourceOriginIndex Origin);

internal sealed record Avm1CompiledNestedCodeUnits(
    Dictionary<SourceFunctionIndex, Avm1CompiledFunctionCodeUnit> Functions,
    Dictionary<Avm1MirWithSiteIndex, Avm1CompiledWithCodeUnit> WithRegions,
    Dictionary<Avm1MirTrySiteIndex, Avm1CompiledTryCodeUnit> TryRegions);
