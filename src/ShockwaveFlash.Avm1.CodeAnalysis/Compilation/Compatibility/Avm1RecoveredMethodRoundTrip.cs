using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compatibility;

public enum Avm1RecoveredMethodRoundTripStatus : byte
{
    Equivalent,
    ReferenceInvalid,
    SourceProjectionFailed,
    SourceIncomplete,
    CompilationFailed,
    CandidateInvalid,
    ComparisonIncomplete,
    Different
}

public enum Avm1RecoveredMethodRoundTripStage : byte
{
    ReferenceVerification,
    SourceProjection,
    Compilation,
    CandidateVerification,
    Comparison
}

public readonly record struct Avm1RecoveredMethodRoundTripDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1RecoveredMethodRoundTripStage Stage,
    string Message);

public sealed record Avm1RecoveredMethodRoundTripOptions
{
    public Avm1OptimizationLevel OptimizationLevel { get; init; } =
        Avm1OptimizationLevel.Basic;

    public Avm1SourceRegisterAllocationMode SourceRegisterAllocationMode
    {
        get;
        init;
    } = Avm1SourceRegisterAllocationMode.PreserveSourceSymbols;

    public Avm1ProtectedRegionExitMode ProtectedRegionExitMode { get; init; }

    public Avm1ExpressionEvaluationMode ExpressionEvaluationMode { get; init; } =
        Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible;

    public Avm1MethodCompatibilityOptions Compatibility { get; init; } = new()
    {
        AllowReferenceStackUnderflow = true
    };
}

public sealed class Avm1RecoveredMethodRoundTripResult
{
    private readonly Avm1RecoveredMethodRoundTripDiagnostic[] _diagnostics;

    internal Avm1RecoveredMethodRoundTripResult(
        Avm1RecoveredMethodRoundTripStatus status,
        Avm1BytecodeVerificationResult? referenceVerification,
        Avm1SourceMethod? source,
        Avm1MethodArtifact? compilation,
        Avm1MethodCompatibilityResult? compatibility,
        Avm1MethodCompatibilityInput? candidate,
        Avm1RecoveredMethodRoundTripDiagnostic[] diagnostics)
    {
        Status = status;
        ReferenceVerification = referenceVerification;
        Source = source;
        Compilation = compilation;
        Compatibility = compatibility;
        Candidate = candidate;
        _diagnostics = diagnostics;
    }

    public Avm1RecoveredMethodRoundTripStatus Status { get; }

    public Avm1BytecodeVerificationResult? ReferenceVerification { get; }

    public Avm1SourceMethod? Source { get; }

    public Avm1MethodArtifact? Compilation { get; }

    public Avm1MethodCompatibilityResult? Compatibility { get; }

    public Avm1MethodCompatibilityInput? Candidate { get; }

    public IReadOnlyList<Avm1RecoveredMethodRoundTripDiagnostic> Diagnostics =>
        _diagnostics;

    public bool IsEquivalent =>
        Status is Avm1RecoveredMethodRoundTripStatus.Equivalent;
}

public static class Avm1RecoveredMethodRoundTripAnalyzer
{
    public static Avm1RecoveredMethodRoundTripResult Analyze(
        Avm1MethodCompatibilityInput reference,
        Avm1RecoveredMethodRoundTripOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        options ??= new Avm1RecoveredMethodRoundTripOptions();
        ArgumentNullException.ThrowIfNull(options.Compatibility);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Avm1RecoveredMethodRoundTripDiagnostic>();
        var referenceVerification = VerifyReference(reference, options.Compatibility);
        AddVerificationDiagnostics(
            referenceVerification,
            Avm1RecoveredMethodRoundTripStage.ReferenceVerification,
            diagnostics);
        if (!referenceVerification.Succeeded)
        {
            return CreateResult(
                Avm1RecoveredMethodRoundTripStatus.ReferenceInvalid,
                referenceVerification,
                diagnostics: diagnostics);
        }

        var source = ProjectReference(
            reference,
            options.Compatibility.CoalesceNonInterferingRegisterVersions,
            diagnostics);
        if (source is null)
        {
            return CreateResult(
                Avm1RecoveredMethodRoundTripStatus.SourceProjectionFailed,
                referenceVerification,
                diagnostics: diagnostics);
        }

        AddSourceDiagnostics(source, diagnostics);
        if (!source.IsComplete)
        {
            return CreateResult(
                Avm1RecoveredMethodRoundTripStatus.SourceIncomplete,
                referenceVerification,
                source,
                diagnostics: diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var flags = reference.FunctionContext?.Flags ?? 0;
        var usesDefineFunction2 = reference.RegisterCount.HasValue;
        var preloadPlan = usesDefineFunction2
            ? Avm1FunctionPreloadPlan.FromFlags(flags)
            : Avm1FunctionPreloadPlan.Empty;
        var compilationOptions = CreateCompilationOptions(
            reference.SwfVersion,
            usesDefineFunction2,
            preloadPlan.PreloadRegisterCount,
            options);
        var compilation = new Avm1Compiler().CompileMethod(
            source,
            compilationOptions,
            preloadPlan,
            cancellationToken);
        AddCompilerDiagnostics(compilation, diagnostics);
        if (!compilation.Succeeded)
        {
            return CreateResult(
                Avm1RecoveredMethodRoundTripStatus.CompilationFailed,
                referenceVerification,
                source,
                compilation,
                diagnostics: diagnostics);
        }

        var candidate = CreateCandidateInput(
            reference,
            source,
            compilation,
            usesDefineFunction2,
            flags);
        var compatibility = Avm1MethodCompatibilityAnalyzer.Compare(
            reference,
            candidate,
            options.Compatibility,
            cancellationToken);
        AddCompatibilityDiagnostics(compatibility, diagnostics);

        var status = GetStatus(compatibility);
        return CreateResult(
            status,
            referenceVerification,
            source,
            compilation,
            compatibility,
            candidate,
            diagnostics);
    }

    private static Avm1BytecodeVerificationResult VerifyReference(
        Avm1MethodCompatibilityInput reference,
        Avm1MethodCompatibilityOptions options) =>
        Avm1BytecodeVerifier.Verify(
            reference.Bytecode,
            new Avm1BytecodeVerificationOptions(reference.SwfVersion)
            {
                RegisterFile = reference.RegisterCount.HasValue
                    ? Avm1CompilationRegisterFile.DefineFunction2
                    : Avm1CompilationRegisterFile.Legacy,
                RegisterCount = reference.RegisterCount.GetValueOrDefault(),
                InitialConstantPoolCount = reference.InitialConstantPool is { } pool
                    ? checked((ushort)pool.Count)
                    : null,
                VerifyDataFlow = options.VerifyDataFlow,
                RequireEndAction = false,
                RequireEmptyStackAtExit = options.RequireEmptyStackAtExit,
                AllowRuntimeStackUnderflow =
                    options.AllowReferenceStackUnderflow
            });

    private static Avm1SourceMethod? ProjectReference(
        Avm1MethodCompatibilityInput reference,
        bool coalesceNonInterferingRegisterVersions,
        List<Avm1RecoveredMethodRoundTripDiagnostic> diagnostics)
    {
        try
        {
            IReadOnlyList<Action> actions = Action.DecodeCollection(
                reference.Bytecode,
                reference.SwfVersion,
                strict: true);
            if (reference.InitialConstantPool is { } initialConstantPool)
            {
                var contextualActions = new List<Action>(actions.Count + 1)
                {
                    new ActionConstantPool(initialConstantPool)
                };
                contextualActions.AddRange(actions);
                actions = contextualActions;
            }

            var decompilation = Avm1Decompiler.DecompileMethod(
                actions,
                reference.SwfVersion,
                reference.FunctionContext);
            return Avm1SourceProjector.ProjectMethod(
                decompilation,
                registerAliases: null,
                includedStatementNodes: null,
                coalesceNonInterferingRegisterVersions);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or
            StackOverflowException or
            OperationCanceledException))
        {
            diagnostics.Add(new Avm1RecoveredMethodRoundTripDiagnostic(
                "AVM1RT001",
                Avm1CompilationDiagnosticSeverity.Error,
                Avm1RecoveredMethodRoundTripStage.SourceProjection,
                $"Cannot project {reference.Name} to Source HIR: " +
                    exception.Message));
            return null;
        }
    }

    private static Avm1CompilationOptions CreateCompilationOptions(
        byte swfVersion,
        bool usesDefineFunction2,
        byte preloadRegisterCount,
        Avm1RecoveredMethodRoundTripOptions options)
    {
        var firstTemporary = checked((byte)(preloadRegisterCount + 1));
        return new Avm1CompilationOptions(swfVersion)
        {
            OptimizationLevel = options.OptimizationLevel,
            SourceRegisterAllocationMode =
                options.SourceRegisterAllocationMode,
            RegisterFile = usesDefineFunction2
                ? Avm1CompilationRegisterFile.DefineFunction2
                : Avm1CompilationRegisterFile.Legacy,
            RegisterCount = usesDefineFunction2 ? byte.MaxValue : (byte)0,
            ReservedTemporaryRegisters = usesDefineFunction2
                ? new Avm1TemporaryRegisterRange(
                    firstTemporary,
                    checked((byte)(byte.MaxValue - firstTemporary)))
                : default,
            ProtectedRegionExitMode = options.ProtectedRegionExitMode,
            ExpressionEvaluationMode = options.ExpressionEvaluationMode
        };
    }

    private static Avm1MethodCompatibilityInput CreateCandidateInput(
        Avm1MethodCompatibilityInput reference,
        Avm1SourceMethod source,
        Avm1MethodArtifact compilation,
        bool usesDefineFunction2,
        FunctionFlags flags)
    {
        var parameters = source.Parameters.Select(parameter =>
        {
            var symbol = source.Arena[parameter];
            return new FunctionParameter(0, source.Arena[symbol.Name]);
        }).ToArray();
        var context = reference.FunctionContext is null && parameters.Length == 0
            ? null
            : new FunctionContext(flags, parameters);
        return new Avm1MethodCompatibilityInput(
            reference.Name + ".candidate",
            compilation.Bytecode,
            reference.SwfVersion)
        {
            FunctionContext = context,
            RegisterCount = usesDefineFunction2 ? byte.MaxValue : null,
            SourceProjectionHints = CreateSourceProjectionHints(compilation)
        };
    }

    private static Avm1SourceProjectionHints CreateSourceProjectionHints(
        Avm1MethodArtifact compilation) =>
        new(
            sourceRegisters: [],
            identityPreservingRegisters: [],
            compilation.NestedFunctions.Select(CreateSourceProjectionHints));

    private static Avm1SourceProjectionHints CreateSourceProjectionHints(
        Avm1CompiledFunctionArtifact function) =>
        new(
            function.Plan.RegisterAllocations.Select(allocation =>
                (int)allocation.Register),
            function.Plan.RegisterAllocations
                .Where(allocation =>
                    allocation.Symbol.IsValid &&
                    allocation.Symbol.Value < function.Source.Arena.Symbols.Count &&
                    function.Source.Arena[allocation.Symbol].Kind is
                        Avm1SourceSymbolKind.Local &&
                    !function.Source.Arena[allocation.Symbol].Flags.HasFlag(
                        Avm1SourceSymbolFlags.CompilerGenerated))
                .Select(allocation => (int)allocation.Register),
            function.NestedFunctions.Select(CreateSourceProjectionHints));

    private static Avm1RecoveredMethodRoundTripStatus GetStatus(
        Avm1MethodCompatibilityResult compatibility)
    {
        if (!compatibility.CandidateVerification.Succeeded)
            return Avm1RecoveredMethodRoundTripStatus.CandidateInvalid;
        return compatibility.SourceEquivalence?.Status switch
        {
            Avm1SourceEquivalenceStatus.Equivalent =>
                Avm1RecoveredMethodRoundTripStatus.Equivalent,
            Avm1SourceEquivalenceStatus.Different =>
                Avm1RecoveredMethodRoundTripStatus.Different,
            _ => Avm1RecoveredMethodRoundTripStatus.ComparisonIncomplete
        };
    }

    private static void AddVerificationDiagnostics(
        Avm1BytecodeVerificationResult verification,
        Avm1RecoveredMethodRoundTripStage stage,
        List<Avm1RecoveredMethodRoundTripDiagnostic> diagnostics)
    {
        foreach (var diagnostic in verification.Diagnostics)
        {
            diagnostics.Add(new Avm1RecoveredMethodRoundTripDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                stage,
                $"Action {diagnostic.ActionIndex}, byte {diagnostic.ByteOffset}: " +
                    diagnostic.Message));
        }
    }

    private static void AddSourceDiagnostics(
        Avm1SourceMethod source,
        List<Avm1RecoveredMethodRoundTripDiagnostic> diagnostics)
    {
        foreach (var diagnostic in source.Diagnostics)
        {
            diagnostics.Add(new Avm1RecoveredMethodRoundTripDiagnostic(
                diagnostic.Code,
                diagnostic.Severity switch
                {
                    Avm1SourceDiagnosticSeverity.Info =>
                        Avm1CompilationDiagnosticSeverity.Info,
                    Avm1SourceDiagnosticSeverity.Warning =>
                        Avm1CompilationDiagnosticSeverity.Warning,
                    _ => Avm1CompilationDiagnosticSeverity.Error
                },
                Avm1RecoveredMethodRoundTripStage.SourceProjection,
                diagnostic.Message));
        }
    }

    private static void AddCompilerDiagnostics(
        Avm1MethodArtifact compilation,
        List<Avm1RecoveredMethodRoundTripDiagnostic> diagnostics)
    {
        foreach (var diagnostic in compilation.Diagnostics)
        {
            diagnostics.Add(new Avm1RecoveredMethodRoundTripDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                Avm1RecoveredMethodRoundTripStage.Compilation,
                diagnostic.Message));
        }
    }

    private static void AddCompatibilityDiagnostics(
        Avm1MethodCompatibilityResult compatibility,
        List<Avm1RecoveredMethodRoundTripDiagnostic> diagnostics)
    {
        foreach (var diagnostic in compatibility.Diagnostics)
        {
            diagnostics.Add(new Avm1RecoveredMethodRoundTripDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Side switch
                {
                    Avm1CompatibilitySide.Candidate =>
                        Avm1RecoveredMethodRoundTripStage.CandidateVerification,
                    Avm1CompatibilitySide.Comparison =>
                        Avm1RecoveredMethodRoundTripStage.Comparison,
                    _ => Avm1RecoveredMethodRoundTripStage.ReferenceVerification
                },
                diagnostic.Message));
        }
    }

    private static Avm1RecoveredMethodRoundTripResult CreateResult(
        Avm1RecoveredMethodRoundTripStatus status,
        Avm1BytecodeVerificationResult? referenceVerification = null,
        Avm1SourceMethod? source = null,
        Avm1MethodArtifact? compilation = null,
        Avm1MethodCompatibilityResult? compatibility = null,
        Avm1MethodCompatibilityInput? candidate = null,
        List<Avm1RecoveredMethodRoundTripDiagnostic>? diagnostics = null) =>
        new(
            status,
            referenceVerification,
            source,
            compilation,
            compatibility,
            candidate,
            diagnostics?.ToArray() ?? []);
}
