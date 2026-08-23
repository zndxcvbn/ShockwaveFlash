using ShockwaveFlash.Avm1.Compilation.Binding;
using ShockwaveFlash.Avm1.Compilation.Preprocessing;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public sealed record Avm1CompilationSource
{
    public Avm1CompilationSource(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        Path = path;
        Text = text;
    }

    public string Path { get; }

    public string Text { get; }
}

public sealed record Avm1CompilerServiceOptions(byte SwfVersion)
{
    public Avm1ParseOptions ParseOptions { get; init; } =
        Avm1ParseOptions.Default;

    public Avm1PreprocessorOptions PreprocessorOptions { get; init; } =
        Avm1PreprocessorOptions.Default;

    public IAvm1IncludeResolver? IncludeResolver { get; init; }

    public IAvm1ReferenceProvider? ReferenceProvider { get; init; } =
        Avm1ReferenceCatalogs.Default;

    public Avm1SourceTypeCheckingMode TypeCheckingMode { get; init; } =
        Avm1SourceTypeCheckingMode.Conservative;

    public Avm1ClassAbiProfile AbiProfile { get; init; }

    public Avm1OptimizationLevel OptimizationLevel { get; init; } =
        Avm1OptimizationLevel.Basic;

    public bool EmitPropertyFlags { get; init; } = true;

    public bool EmitRecoveredLinkage { get; init; } = true;

    public int MaxDegreeOfParallelism { get; init; } = 1;

    internal Avm1ClassCompilationOptions CreateClassOptions() =>
        new(SwfVersion)
        {
            AbiProfile = AbiProfile,
            OptimizationLevel = OptimizationLevel,
            EmitPropertyFlags = EmitPropertyFlags,
            EmitRecoveredLinkage = EmitRecoveredLinkage,
            MaxDegreeOfParallelism = MaxDegreeOfParallelism
        };
}

public sealed record Avm1CompilerServiceCacheOptions
{
    public int MaximumSyntaxTrees { get; init; } = 1024;

    public int MaximumFrontEnds { get; init; } = 16;

    public int MaximumCompilations { get; init; } = 16;
}

public enum Avm1CompilerServiceStage : byte
{
    Preprocessing,
    Syntax,
    DeclarationBinding,
    LexicalBinding,
    SourceProjection,
    TypeChecking,
    Compilation
}

public readonly record struct Avm1CompilerServiceDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1CompilerServiceStage Stage,
    string Path,
    Avm1TextSpan Span,
    int ByteOffset,
    string Message);

public readonly record struct Avm1CompilerServiceCacheStatistics(
    int SyntaxTreeHits,
    int SyntaxTreeMisses,
    int FrontEndHits,
    int FrontEndMisses,
    int CompilationHits,
    int CompilationMisses);

public sealed class Avm1CompilerServiceResult
{
    private readonly Avm1PreprocessorResult[] _preprocessedSources;
    private readonly Avm1CompilerServiceDiagnostic[] _diagnostics;

    internal Avm1CompilerServiceResult(
        Avm1PreprocessorResult[] preprocessedSources,
        Avm1SyntaxProgram syntaxProgram,
        Avm1SyntaxLexicalBinding lexicalBinding,
        Avm1SyntaxSourceProjection sourceProjection,
        Avm1SourceTypeCheckResult typeCheck,
        Avm1ProgramArtifact? programArtifact,
        Avm1CompilerDebugMap debugMap,
        Avm1CompilerServiceDiagnostic[] diagnostics,
        Avm1CompilerServiceCacheStatistics cacheStatistics)
    {
        _preprocessedSources = preprocessedSources;
        SyntaxProgram = syntaxProgram;
        LexicalBinding = lexicalBinding;
        SourceProjection = sourceProjection;
        TypeCheck = typeCheck;
        ProgramArtifact = programArtifact;
        DebugMap = debugMap;
        _diagnostics = diagnostics;
        CacheStatistics = cacheStatistics;
    }

    public IReadOnlyList<Avm1PreprocessorResult> PreprocessedSources =>
        _preprocessedSources;

    public Avm1SyntaxProgram SyntaxProgram { get; }

    public Avm1SyntaxLexicalBinding LexicalBinding { get; }

    public Avm1SyntaxSourceProjection SourceProjection { get; }

    public Avm1SourceProgram SourceProgram => SourceProjection.SourceProgram;

    public Avm1SourceTypeCheckResult TypeCheck { get; }

    public Avm1ProgramArtifact? ProgramArtifact { get; }

    public Avm1CompilerDebugMap DebugMap { get; }

    public IReadOnlyList<Avm1CompilerServiceDiagnostic> Diagnostics =>
        _diagnostics;

    public Avm1CompilerServiceCacheStatistics CacheStatistics { get; }

    public bool Succeeded =>
        ProgramArtifact is { Succeeded: true } &&
        _diagnostics.All(diagnostic =>
            diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);
}
