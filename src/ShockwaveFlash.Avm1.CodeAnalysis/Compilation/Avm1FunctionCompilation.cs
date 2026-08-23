using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation;

public readonly record struct Avm1SourceRegisterAllocation(
    SourceSymbolIndex Symbol,
    byte Register);

public sealed class Avm1DefineFunction2Plan
{
    private readonly FunctionParameter[] _parameters;
    private readonly Avm1SourceRegisterAllocation[] _registerAllocations;

    internal Avm1DefineFunction2Plan(
        SourceFunctionIndex function,
        SourceCodeUnitIndex codeUnit,
        bool isDeclaration,
        string name,
        byte registerCount,
        FunctionFlags flags,
        FunctionParameter[] parameters,
        Avm1SourceRegisterAllocation[] registerAllocations)
    {
        Function = function;
        CodeUnit = codeUnit;
        IsDeclaration = isDeclaration;
        Name = name;
        RegisterCount = registerCount;
        Flags = flags;
        _parameters = parameters;
        _registerAllocations = registerAllocations;
    }

    public SourceFunctionIndex Function { get; }

    public SourceCodeUnitIndex CodeUnit { get; }

    public bool IsDeclaration { get; }

    public string Name { get; }

    public byte RegisterCount { get; }

    public FunctionFlags Flags { get; }

    public IReadOnlyList<FunctionParameter> Parameters => _parameters;

    public IReadOnlyList<Avm1SourceRegisterAllocation> RegisterAllocations =>
        _registerAllocations;
}

public sealed class Avm1CompiledFunctionArtifact
{
    private readonly Avm1CompiledInstructionMap[] _instructionMap;
    private readonly Avm1CompiledFunctionArtifact[] _nestedFunctions;
    private readonly Avm1CompiledWithArtifact[] _nestedWithRegions;
    private readonly Avm1CompiledTryArtifact[] _nestedTryRegions;

    internal Avm1CompiledFunctionArtifact(
        Avm1SourceMethod source,
        Avm1DefineFunction2Plan plan,
        Avm1MirMethod mir,
        Avm1StackLirMethod stackLir,
        Avm1CompiledInstructionMap[] instructionMap,
        Avm1CompiledFunctionArtifact[] nestedFunctions,
        Avm1CompiledWithArtifact[] nestedWithRegions,
        Avm1CompiledTryArtifact[] nestedTryRegions)
    {
        Source = source;
        Plan = plan;
        Mir = mir;
        StackLir = stackLir;
        _instructionMap = instructionMap;
        _nestedFunctions = nestedFunctions;
        _nestedWithRegions = nestedWithRegions;
        _nestedTryRegions = nestedTryRegions;
    }

    public Avm1SourceMethod Source { get; }

    public Avm1DefineFunction2Plan Plan { get; }

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

internal sealed record Avm1CompiledFunctionCodeUnit(
    Avm1DefineFunction2Plan Plan,
    Avm1CodeUnit CodeUnit,
    Avm1CompiledFunctionArtifact Artifact);
