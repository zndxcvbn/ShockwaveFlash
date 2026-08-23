using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf5;

namespace ShockwaveFlash.Avm1.Compatibility;

public static class Avm1MethodCompatibilityAnalyzer
{
    public static Avm1MethodCompatibilityResult Compare(
        Avm1MethodCompatibilityInput reference,
        Avm1MethodCompatibilityInput candidate,
        Avm1MethodCompatibilityOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        options ??= new Avm1MethodCompatibilityOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Avm1CompatibilityDiagnostic>();
        var referenceVerification = Verify(
            reference,
            options,
            Avm1CompatibilitySide.Reference,
            diagnostics);
        var candidateVerification = Verify(
            candidate,
            options,
            Avm1CompatibilitySide.Candidate,
            diagnostics);
        var referenceProfile = CreateProfile(reference, referenceVerification);
        var candidateProfile = CreateProfile(candidate, candidateVerification);

        Avm1SourceMethod? referenceSource = null;
        Avm1SourceMethod? candidateSource = null;
        Avm1SourceEquivalenceResult? equivalence = null;
        if (options.CompareNormalizedSource &&
            referenceVerification.Succeeded &&
            candidateVerification.Succeeded)
        {
            referenceSource = ProjectSource(
                reference,
                Avm1CompatibilitySide.Reference,
                options.CoalesceNonInterferingRegisterVersions,
                diagnostics);
            candidateSource = ProjectSource(
                candidate,
                Avm1CompatibilitySide.Candidate,
                options.CoalesceNonInterferingRegisterVersions,
                diagnostics);
            if (referenceSource is { IsComplete: true } &&
                candidateSource is { IsComplete: true })
            {
                cancellationToken.ThrowIfCancellationRequested();
                equivalence = Avm1SourceEquivalence.Compare(
                    referenceSource,
                    candidateSource,
                    cancellationToken);
                AddEquivalenceDiagnostics(equivalence, diagnostics);
            }
        }

        return new Avm1MethodCompatibilityResult(
            referenceVerification,
            candidateVerification,
            referenceProfile,
            candidateProfile,
            referenceSource,
            candidateSource,
            equivalence,
            options.CompareNormalizedSource,
            diagnostics.ToArray());
    }

    private static Avm1BytecodeVerificationResult Verify(
        Avm1MethodCompatibilityInput input,
        Avm1MethodCompatibilityOptions options,
        Avm1CompatibilitySide side,
        List<Avm1CompatibilityDiagnostic> diagnostics)
    {
        var result = Avm1BytecodeVerifier.Verify(
            input.Bytecode,
            new Avm1BytecodeVerificationOptions(input.SwfVersion)
            {
                RegisterFile = input.RegisterCount.HasValue
                    ? Avm1CompilationRegisterFile.DefineFunction2
                    : Avm1CompilationRegisterFile.Legacy,
                RegisterCount = input.RegisterCount.GetValueOrDefault(),
                InitialConstantPoolCount = input.InitialConstantPool is { } pool
                    ? checked((ushort)pool.Count)
                    : null,
                VerifyDataFlow = options.VerifyDataFlow,
                RequireEndAction = false,
                RequireEmptyStackAtExit = options.RequireEmptyStackAtExit,
                AllowRuntimeStackUnderflow =
                    side is Avm1CompatibilitySide.Reference &&
                    options.AllowReferenceStackUnderflow
            });
        for (var i = 0; i < result.Diagnostics.Count; i++)
        {
            var diagnostic = result.Diagnostics[i];
            diagnostics.Add(new Avm1CompatibilityDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                side,
                $"{input.Name} at action {diagnostic.ActionIndex}, byte " +
                $"{diagnostic.ByteOffset}: {diagnostic.Message}"));
        }
        return result;
    }

    private static Avm1MethodBytecodeProfile CreateProfile(
        Avm1MethodCompatibilityInput input,
        Avm1BytecodeVerificationResult verification)
    {
        var counts = verification.Actions
            .GroupBy(action => action.Opcode)
            .OrderBy(group => (byte)group.Key)
            .Select(group => new Avm1OpcodeCount(group.Key, group.Count()))
            .ToArray();
        return new Avm1MethodBytecodeProfile(
            verification.ByteLength,
            verification.Actions.Count,
            verification.MaximumStackDepth,
            verification.MaximumStackDepthIsExact,
            input.RegisterCount,
            counts);
    }

    private static Avm1SourceMethod? ProjectSource(
        Avm1MethodCompatibilityInput input,
        Avm1CompatibilitySide side,
        bool coalesceNonInterferingRegisterVersions,
        List<Avm1CompatibilityDiagnostic> diagnostics)
    {
        try
        {
            IReadOnlyList<Action> actions = Action.DecodeCollection(
                input.Bytecode,
                input.SwfVersion);
            if (input.InitialConstantPool is { } initialConstantPool)
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
                input.SwfVersion,
                input.FunctionContext,
                options: new Avm1MethodDecompilationOptions
                {
                    SourceProjectionHints = input.SourceProjectionHints
                });
            var source = Avm1SourceProjector.ProjectMethod(
                decompilation,
                registerAliases: null,
                includedStatementNodes: null,
                coalesceNonInterferingRegisterVersions,
                input.SourceProjectionHints);
            for (var i = 0; i < source.Diagnostics.Count; i++)
            {
                var diagnostic = source.Diagnostics[i];
                diagnostics.Add(new Avm1CompatibilityDiagnostic(
                    diagnostic.Code,
                    MapSeverity(diagnostic.Severity),
                    side,
                    $"{input.Name}: {diagnostic.Message}"));
            }
            return source;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or
            StackOverflowException or
            OperationCanceledException))
        {
            diagnostics.Add(new Avm1CompatibilityDiagnostic(
                "AVM1CMP001",
                Avm1CompilationDiagnosticSeverity.Error,
                side,
                $"Cannot project {input.Name} to Source HIR: " +
                exception.Message));
            return null;
        }
    }

    private static void AddEquivalenceDiagnostics(
        Avm1SourceEquivalenceResult equivalence,
        List<Avm1CompatibilityDiagnostic> diagnostics)
    {
        var severity = equivalence.Status switch
        {
            Avm1SourceEquivalenceStatus.Different =>
                Avm1CompilationDiagnosticSeverity.Error,
            Avm1SourceEquivalenceStatus.Incomplete =>
                Avm1CompilationDiagnosticSeverity.Warning,
            _ => Avm1CompilationDiagnosticSeverity.Info
        };
        for (var i = 0; i < equivalence.Diagnostics.Count; i++)
        {
            var diagnostic = equivalence.Diagnostics[i];
            diagnostics.Add(new Avm1CompatibilityDiagnostic(
                diagnostic.Code,
                severity,
                Avm1CompatibilitySide.Comparison,
                $"{diagnostic.Path}: {diagnostic.Message}"));
        }
    }

    private static Avm1CompilationDiagnosticSeverity MapSeverity(
        Avm1SourceDiagnosticSeverity severity) => severity switch
        {
            Avm1SourceDiagnosticSeverity.Info =>
                Avm1CompilationDiagnosticSeverity.Info,
            Avm1SourceDiagnosticSeverity.Warning =>
                Avm1CompilationDiagnosticSeverity.Warning,
            _ => Avm1CompilationDiagnosticSeverity.Error
        };
}
