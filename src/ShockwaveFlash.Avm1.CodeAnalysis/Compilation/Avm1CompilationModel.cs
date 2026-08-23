using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public enum Avm1CompilationRegisterFile : byte
{
    Legacy,
    DefineFunction2
}

public enum Avm1SourceRegisterAllocationMode : byte
{
    CoalesceNonInterfering,
    PreserveSourceSymbols
}

public enum Avm1OptimizationLevel : byte
{
    None,
    Basic
}

public enum Avm1ProtectedRegionExitMode : byte
{
    SemanticCompletion,
    AdobePhysicalBranch
}

public enum Avm1ExpressionEvaluationMode : byte
{
    Semantic,
    AdobeFlashCs6Compatible
}

public readonly record struct Avm1TemporaryRegisterRange(byte First, byte Count)
{
    public bool IsEmpty => Count == 0;

    public int Last => IsEmpty ? -1 : First + Count - 1;
}

public sealed record Avm1CompilationOptions(byte SwfVersion)
{
    public Avm1TimelineLayout? TimelineLayout { get; init; }

    public Avm1OptimizationLevel OptimizationLevel { get; init; }

    public Avm1CompilationRegisterFile RegisterFile { get; init; }

    public Avm1SourceRegisterAllocationMode SourceRegisterAllocationMode
    {
        get;
        init;
    }

    public byte RegisterCount { get; init; }

    public Avm1TemporaryRegisterRange ReservedTemporaryRegisters { get; init; }

    public Avm1ProtectedRegionExitMode ProtectedRegionExitMode { get; init; }

    public Avm1ExpressionEvaluationMode ExpressionEvaluationMode { get; init; }
}

public readonly record struct Avm1CompilerDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    SourceOriginIndex Origin,
    int ByteOffset,
    string Message);

public readonly record struct Avm1CompiledInstructionMap(
    Avm1AssemblyInstructionIndex AssemblyInstruction,
    Avm1StackLirInstructionIndex StackInstruction,
    Avm1MirInstructionIndex MirInstruction,
    SourceOriginIndex Origin);

public sealed class Avm1MethodArtifact
{
    private readonly Avm1CompilerDiagnostic[] _diagnostics;
    private readonly Avm1CompiledInstructionMap[] _instructionMap;
    private readonly Avm1CompiledFunctionArtifact[] _nestedFunctions;
    private readonly Avm1CompiledWithArtifact[] _nestedWithRegions;
    private readonly Avm1CompiledTryArtifact[] _nestedTryRegions;

    internal Avm1MethodArtifact(
        Avm1SourceMethod source,
        Avm1ClosureAnalysis? closureAnalysis,
        Avm1MirMethod mir,
        Avm1StackLirMethod? stackLir,
        Avm1ActionBody? actionBody,
        Avm1CompiledInstructionMap[] instructionMap,
        Avm1CompiledFunctionArtifact[] nestedFunctions,
        Avm1CompiledWithArtifact[] nestedWithRegions,
        Avm1CompiledTryArtifact[] nestedTryRegions,
        Avm1CompilerDiagnostic[] diagnostics)
    {
        Source = source;
        ClosureAnalysis = closureAnalysis;
        Mir = mir;
        StackLir = stackLir;
        ActionBody = actionBody;
        _instructionMap = instructionMap;
        _nestedFunctions = nestedFunctions;
        _nestedWithRegions = nestedWithRegions;
        _nestedTryRegions = nestedTryRegions;
        _diagnostics = diagnostics;
        SourceMap = Avm1CompiledSourceMapBuilder.Build(
            source,
            mir,
            stackLir,
            actionBody,
            instructionMap,
            nestedFunctions,
            nestedWithRegions,
            nestedTryRegions);
    }

    public Avm1SourceMethod Source { get; }

    public Avm1ClosureAnalysis? ClosureAnalysis { get; }

    public Avm1MirMethod Mir { get; }

    public Avm1StackLirMethod? StackLir { get; }

    public Avm1ActionBody? ActionBody { get; }

    public ReadOnlyMemory<byte> Bytecode =>
        ActionBody?.Bytes ?? ReadOnlyMemory<byte>.Empty;

    public IReadOnlyList<Avm1CompiledInstructionMap> InstructionMap =>
        _instructionMap;

    public Avm1CompiledSourceMap SourceMap { get; }

    public IReadOnlyList<Avm1CompiledFunctionArtifact> NestedFunctions =>
        _nestedFunctions;

    public IReadOnlyList<Avm1CompiledWithArtifact> NestedWithRegions =>
        _nestedWithRegions;

    public IReadOnlyList<Avm1CompiledTryArtifact> NestedTryRegions =>
        _nestedTryRegions;

    public IReadOnlyList<Avm1CompilerDiagnostic> Diagnostics => _diagnostics;

    public bool Succeeded =>
        ActionBody is { Succeeded: true } &&
        _diagnostics.All(diagnostic =>
            diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);
}
