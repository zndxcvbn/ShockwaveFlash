using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation;

public enum Avm1ClassAbiProfile : byte
{
    Canonical,
    AdobeFlashCs6Compatible,
    OriginPreserving
}

public enum Avm1OriginReuseKind : byte
{
    None,
    CompleteInitializer,
    MethodBodies
}

public sealed record Avm1ClassCompilationOptions(byte SwfVersion)
{
    public Avm1ClassAbiProfile AbiProfile { get; init; }

    public Avm1OptimizationLevel OptimizationLevel { get; init; } =
        Avm1OptimizationLevel.Basic;

    public bool EmitPropertyFlags { get; init; } = true;

    public bool EmitRecoveredLinkage { get; init; } = true;

    public int MaxDegreeOfParallelism { get; init; } = 1;
}

public readonly record struct Avm1CompiledClassMember(
    Avm1SourceMethodDeclaration Declaration,
    Avm1MethodArtifact Method,
    byte RegisterCount,
    FunctionFlags FunctionFlags,
    IReadOnlyList<FunctionParameter> Parameters,
    bool OriginBodyReused);

public sealed class Avm1ClassArtifact
{
    private readonly Avm1CompiledClassMember[] _methods;
    private readonly Avm1CompilerDiagnostic[] _diagnostics;

    internal Avm1ClassArtifact(
        Avm1SourceClass source,
        byte swfVersion,
        Avm1ActionBody? initializerBody,
        Avm1CompiledClassMember[] methods,
        Avm1CompilerDiagnostic[] diagnostics,
        Avm1ClassAbiProfile abiProfile,
        Avm1OriginReuseKind originReuseKind,
        Avm1CompiledClassSourceMap? sourceMap = null)
    {
        Source = source;
        SwfVersion = swfVersion;
        InitializerBody = initializerBody;
        _methods = methods;
        _diagnostics = diagnostics;
        AbiProfile = abiProfile;
        OriginReuseKind = originReuseKind;
        SourceMap = sourceMap ?? Avm1CompiledClassSourceMap.Empty(methods);
    }

    public Avm1SourceClass Source { get; }

    public byte SwfVersion { get; }

    public Avm1ActionBody? InitializerBody { get; }

    public ReadOnlyMemory<byte> Bytecode =>
        InitializerBody?.Bytes ?? ReadOnlyMemory<byte>.Empty;

    public IReadOnlyList<Avm1CompiledClassMember> Methods => _methods;

    public IReadOnlyList<Avm1CompilerDiagnostic> Diagnostics => _diagnostics;

    public Avm1ClassAbiProfile AbiProfile { get; }

    public Avm1OriginReuseKind OriginReuseKind { get; }

    public Avm1CompiledClassSourceMap SourceMap { get; }

    public int ReusedMethodCount => _methods.Count(method =>
        method.OriginBodyReused);

    public bool Succeeded =>
        InitializerBody is { Succeeded: true } &&
        _diagnostics.All(diagnostic =>
            diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);
}

public sealed class Avm1ProgramArtifact
{
    private readonly Avm1ClassArtifact[] _classes;
    private readonly Avm1CompilerDiagnostic[] _diagnostics;

    internal Avm1ProgramArtifact(
        Avm1SourceProgram source,
        Avm1ClassArtifact[] classes,
        Avm1CompilerDiagnostic[] diagnostics)
    {
        Source = source;
        _classes = classes;
        _diagnostics = diagnostics;
    }

    public Avm1SourceProgram Source { get; }

    public IReadOnlyList<Avm1ClassArtifact> Classes => _classes;

    public IReadOnlyList<Avm1CompilerDiagnostic> Diagnostics => _diagnostics;

    public bool Succeeded =>
        _classes.All(sourceClass => sourceClass.Succeeded) &&
        _diagnostics.All(diagnostic =>
            diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);
}
