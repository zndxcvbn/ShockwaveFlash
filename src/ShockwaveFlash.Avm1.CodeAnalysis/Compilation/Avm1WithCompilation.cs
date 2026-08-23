using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public sealed record Avm1WithPlan(Avm1MirWithSiteIndex Site);

public sealed class Avm1CompiledWithComponentArtifact
{
    private readonly Avm1CompiledInstructionMap[] _instructionMap;
    private readonly Avm1CompiledFunctionArtifact[] _nestedFunctions;
    private readonly Avm1CompiledWithArtifact[] _nestedWithRegions;
    private readonly Avm1CompiledTryArtifact[] _nestedTryRegions;

    internal Avm1CompiledWithComponentArtifact(
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

public sealed record Avm1CompiledWithArtifact(
    Avm1WithPlan Plan,
    Avm1CompiledWithComponentArtifact Body);

internal sealed record Avm1CompiledWithCodeUnit(
    Avm1WithPlan Plan,
    Avm1CodeUnit Body,
    Avm1CompiledWithArtifact Artifact);

internal sealed record Avm1CompiledWithComponentCodeUnit(
    Avm1CodeUnit CodeUnit,
    Avm1CompiledWithComponentArtifact Artifact);
