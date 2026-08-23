using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Compilation.Lowering;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation;

public sealed class Avm1Compiler
{
    private sealed record FunctionBodyCompilation(
        Avm1MirMethod Mir,
        Avm1StackLirMethod StackLir,
        Avm1CompiledNestedCodeUnits Nested);

    private sealed record SourceRegisterPlanningResult(
        Avm1SourceRegisterPlan Registers,
        Avm1CompilationOptions Options);

    public Avm1ClassArtifact CompileClass(
        Avm1SourceClass sourceClass,
        Avm1ClassCompilationOptions options,
        CancellationToken cancellationToken = default) =>
        Avm1ClassCompiler.Compile(
            this,
            sourceClass,
            options,
            cancellationToken);

    public Avm1ProgramArtifact CompileProgram(
        Avm1SourceProgram program,
        Avm1ClassCompilationOptions options,
        CancellationToken cancellationToken = default) =>
        Avm1ClassCompiler.CompileProgram(
            this,
            program,
            options,
            cancellationToken);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The compiler instance will own reference providers and query caches.")]
    public Avm1MethodArtifact CompileMethod(
        Avm1SourceMethod method,
        Avm1CompilationOptions options,
        CancellationToken cancellationToken = default) =>
        CompileMethodCore(
            method,
            options,
            Avm1FunctionPreloadPlan.Empty,
            cancellationToken);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The compiler instance will own reference providers and query caches.")]
    internal Avm1MethodArtifact CompileMethod(
        Avm1SourceMethod method,
        Avm1CompilationOptions options,
        Avm1FunctionPreloadPlan functionPreloads,
        CancellationToken cancellationToken) =>
        CompileMethodCore(
            method,
            options,
            functionPreloads,
            cancellationToken);

    private static Avm1MethodArtifact CompileMethodCore(
        Avm1SourceMethod method,
        Avm1CompilationOptions options,
        Avm1FunctionPreloadPlan functionPreloads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Avm1CompilerDiagnostic>();
        foreach (var sourceDiagnostic in method.Diagnostics)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                sourceDiagnostic.Severity is Avm1SourceDiagnosticSeverity.Error
                    ? "AVM1CMP001"
                    : "AVM1CMP000",
                sourceDiagnostic.Severity switch
                {
                    Avm1SourceDiagnosticSeverity.Info =>
                        Avm1CompilationDiagnosticSeverity.Info,
                    Avm1SourceDiagnosticSeverity.Warning =>
                        Avm1CompilationDiagnosticSeverity.Warning,
                    Avm1SourceDiagnosticSeverity.Error =>
                        Avm1CompilationDiagnosticSeverity.Error,
                    _ => throw new ArgumentOutOfRangeException(nameof(method))
                },
                sourceDiagnostic.Origin,
                -1,
                $"Source HIR diagnostic {sourceDiagnostic.Code}: " +
                sourceDiagnostic.Message));
        }
        AddOptionDiagnostics(options, diagnostics);

        Avm1ClosureAnalysis? closureAnalysis = null;
        if (!HasErrors(diagnostics))
        {
            closureAnalysis = Avm1ClosureAnalysis.Analyze(
                method,
                cancellationToken);
            AddClosureDiagnostics(closureAnalysis, diagnostics);
        }

        var effectiveOptions = options;
        var registerPlan = default(Avm1RegisterPlanningResult);
        if (!HasErrors(diagnostics))
        {
            TryCreateRegisterPlan(
                method,
                options,
                executesInDynamicScope: false,
                diagnostics,
                cancellationToken,
                out effectiveOptions,
                out registerPlan);
        }

        Avm1MirMethod mir;
        if (HasErrors(diagnostics))
        {
            mir = EmptyMir();
            return CreateArtifact(
                method,
                closureAnalysis,
                mir,
                null,
                null,
                [],
                [],
                [],
                [],
                diagnostics);
        }

        var loweringContext = Avm1MirLoweringContext.CreateRoot(
            method.Arena,
            effectiveOptions.ReservedTemporaryRegisters,
            registerPlan.RequiredCompletionRegisters,
            functionPreloads: functionPreloads,
            invokesEval: closureAnalysis![closureAnalysis.RootCodeUnit].Flags.HasFlag(
                Avm1ClosureCodeUnitFlags.InvokesEval),
            protectedRegionExitMode: effectiveOptions.ProtectedRegionExitMode,
            expressionEvaluationMode:
                effectiveOptions.ExpressionEvaluationMode);
        mir = Avm1SourceToMirLowerer.Lower(
            method,
            loweringContext,
            diagnostics,
            effectiveOptions.TimelineLayout,
            cancellationToken);
        if (HasErrors(diagnostics))
        {
            return CreateArtifact(
                method,
                closureAnalysis,
                mir,
                null,
                null,
                [],
                [],
                [],
                [],
                diagnostics);
        }
        mir = Avm1MirOptimizer.Optimize(
            mir,
            effectiveOptions,
            cancellationToken);

        var stackLir = Avm1MirToStackLirLowerer.Lower(
            mir,
            CreateStackLirOptions(effectiveOptions, loweringContext),
            diagnostics,
            cancellationToken);
        if (HasErrors(diagnostics))
        {
            return CreateArtifact(
                method,
                closureAnalysis,
                mir,
                stackLir,
                null,
                [],
                [],
                [],
                [],
                diagnostics);
        }

        var compiledNested = CompileNestedCodeUnits(
            method,
            closureAnalysis!,
            mir,
            stackLir,
            loweringContext,
            closureAnalysis!.RootCodeUnit,
            effectiveOptions,
            diagnostics,
            [],
            [],
            compileFunctions: true,
            cancellationToken);
        var nestedArtifacts = CollectFunctionArtifacts(
            mir,
            compiledNested.Functions);
        var nestedWithArtifacts = CollectWithArtifacts(
            mir,
            compiledNested.WithRegions);
        var nestedTryArtifacts = CollectTryArtifacts(
            mir,
            compiledNested.TryRegions);
        if (HasErrors(diagnostics))
        {
            return CreateArtifact(
                method,
                closureAnalysis,
                mir,
                stackLir,
                null,
                [],
                nestedArtifacts,
                nestedWithArtifacts,
                nestedTryArtifacts,
                diagnostics);
        }

        var emission = Avm1StackLirEmitter.Emit(
            mir,
            stackLir,
            effectiveOptions,
            compiledNested.Functions,
            compiledNested.WithRegions,
            compiledNested.TryRegions,
            diagnostics,
            cancellationToken);
        if (HasErrors(diagnostics))
        {
            return CreateArtifact(
                method,
                closureAnalysis,
                mir,
                stackLir,
                null,
                emission.InstructionMap,
                nestedArtifacts,
                nestedWithArtifacts,
                nestedTryArtifacts,
                diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var actionBody = emission.CodeUnit.Assemble(
            new Avm1AssemblyOptions(effectiveOptions.SwfVersion)
            {
                RequireEndAction = false,
                RequireEmptyStackAtExit = true,
                CodeUnitContext = CreateCodeUnitContext(effectiveOptions)
            });
        AddAssemblyDiagnostics(
            actionBody,
            emission.InstructionMap,
            diagnostics);

        var expectedMaximumStackDepth = GetMaximumStackDepth(
            stackLir,
            compiledNested);
        if (actionBody.Succeeded &&
            actionBody.MaximumStackDepthIsExact &&
            actionBody.MaximumStackDepth != expectedMaximumStackDepth)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP105",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                $"Stack LIR code units predicted maximum depth " +
                $"{expectedMaximumStackDepth}, " +
                $"but bytecode verification computed {actionBody.MaximumStackDepth}."));
        }

        return CreateArtifact(
            method,
            closureAnalysis,
            mir,
            stackLir,
            actionBody,
            emission.InstructionMap,
            nestedArtifacts,
            nestedWithArtifacts,
            nestedTryArtifacts,
            diagnostics);
    }

    private static Avm1CompiledNestedCodeUnits CompileNestedCodeUnits(
        Avm1SourceMethod currentSource,
        Avm1ClosureAnalysis closureAnalysis,
        Avm1MirMethod parentMir,
        Avm1StackLirMethod parentStackLir,
        Avm1MirLoweringContext loweringContext,
        SourceCodeUnitIndex parentCodeUnit,
        Avm1CompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeTryBodies,
        bool compileFunctions,
        CancellationToken cancellationToken)
    {
        var functions = compileFunctions
            ? CompileNestedFunctions(
                currentSource,
                closureAnalysis,
                parentMir,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                cancellationToken)
            : [];
        if (HasErrors(diagnostics))
            return new Avm1CompiledNestedCodeUnits(functions, [], []);

        var componentOptions = CreateComponentOptions(options, parentStackLir);
        var withRegions = CompileNestedWithRegions(
            currentSource,
            closureAnalysis,
            parentMir,
            loweringContext,
            parentCodeUnit,
            componentOptions,
            diagnostics,
            activeFunctions,
            activeTryBodies,
            compileFunctions,
            cancellationToken);
        if (HasErrors(diagnostics))
            return new Avm1CompiledNestedCodeUnits(functions, withRegions, []);

        var tryRegions = CompileNestedTryRegions(
            currentSource,
            closureAnalysis,
            parentMir,
            loweringContext,
            parentCodeUnit,
            componentOptions,
            diagnostics,
            activeFunctions,
            activeTryBodies,
            compileFunctions,
            cancellationToken);
        return new Avm1CompiledNestedCodeUnits(functions, withRegions, tryRegions);
    }

    private static Dictionary<SourceFunctionIndex, Avm1CompiledFunctionCodeUnit>
        CompileNestedFunctions(
            Avm1SourceMethod currentSource,
            Avm1ClosureAnalysis closureAnalysis,
            Avm1MirMethod parentMir,
            SourceCodeUnitIndex parentCodeUnit,
            Avm1CompilationOptions options,
            List<Avm1CompilerDiagnostic> diagnostics,
            HashSet<SourceFunctionIndex> activeFunctions,
            HashSet<SourceStatementIndex> activeTryBodies,
            CancellationToken cancellationToken)
    {
        var compiled = new Dictionary<
            SourceFunctionIndex,
            Avm1CompiledFunctionCodeUnit>();
        foreach (var site in parentMir.FunctionSites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compiled.TryGetValue(site.Function, out var existing))
            {
                if (existing.Plan.IsDeclaration != site.IsDeclaration ||
                    existing.Plan.Name != GetFunctionSiteName(parentMir, site))
                {
                    AddFunctionDiagnostic(
                        diagnostics,
                        site.Origin,
                        $"Function {site.Function} is emitted with inconsistent " +
                        "declaration metadata.");
                }
                continue;
            }

            var function = CompileNestedFunction(
                currentSource,
                closureAnalysis,
                parentMir,
                site,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                cancellationToken);
            if (function is not null)
                compiled.Add(site.Function, function);
            if (HasErrors(diagnostics))
                break;
        }
        return compiled;
    }

    private static Avm1CompiledFunctionCodeUnit? CompileNestedFunction(
        Avm1SourceMethod currentSource,
        Avm1ClosureAnalysis closureAnalysis,
        Avm1MirMethod parentMir,
        Avm1MirFunctionSite site,
        SourceCodeUnitIndex parentCodeUnit,
        Avm1CompilationOptions parentOptions,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeTryBodies,
        CancellationToken cancellationToken)
    {
        var arena = currentSource.Arena;
        if (parentOptions.SwfVersion < 7)
        {
            AddFunctionDiagnostic(
                diagnostics,
                site.Origin,
                "Canonical nested functions require ActionDefineFunction2 and " +
                "target SWF 7 or newer.");
            return null;
        }
        if (!site.Function.IsValid || site.Function.Value >= arena.Functions.Count)
        {
            AddFunctionDiagnostic(
                diagnostics,
                site.Origin,
                "MIR function site references an invalid Source function.");
            return null;
        }
        if (!activeFunctions.Add(site.Function))
        {
            AddFunctionDiagnostic(
                diagnostics,
                site.Origin,
                $"Source function {site.Function} recursively contains its own " +
                "definition node.");
            return null;
        }

        try
        {
            var function = arena[site.Function];
            var isDeclaration = function.Flags.HasFlag(
                Avm1SourceFunctionFlags.Declaration);
            if (isDeclaration != site.IsDeclaration)
            {
                AddFunctionDiagnostic(
                    diagnostics,
                    site.Origin,
                    $"Source function {site.Function} declaration flags do not " +
                    "match its MIR site.");
                return null;
            }

            var codeUnit = closureAnalysis.ScopeAnalysis.GetCodeUnit(site.Function);
            if (!codeUnit.IsValid ||
                closureAnalysis[codeUnit].Parent != parentCodeUnit)
            {
                AddFunctionDiagnostic(
                    diagnostics,
                    site.Origin,
                    $"Source function {site.Function} is not owned by parent " +
                    $"code unit {parentCodeUnit}.");
                return null;
            }

            if (!TryGetFunctionParameters(
                arena,
                function,
                diagnostics,
                out var sourceParameters,
                out var actionParameters))
            {
                return null;
            }

            var source = new Avm1SourceMethod(
                arena,
                function.Body,
                sourceParameters,
                []);
            var requiresSelfBinding =
                function.NameSymbol.IsValid &&
                !function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration);
            var codeUnitFlags = closureAnalysis[codeUnit].Flags;
            var functionPreloads = Avm1FunctionPreloadPlan.Create(
                codeUnitFlags,
                requiresSelfBinding);
            var functionOptions = CreateNestedFunctionOptions(
                parentOptions,
                functionPreloads.PreloadRegisterCount);
            var executesInDynamicScope = codeUnitFlags.HasFlag(
                Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope);
            if (!TryCreateRegisterPlan(
                    source,
                    functionOptions,
                    executesInDynamicScope,
                    diagnostics,
                    cancellationToken,
                    out functionOptions,
                    out var registerPlan))
            {
                return null;
            }

            if (!TryPlanSourceRegisters(
                    source,
                    closureAnalysis,
                    codeUnit,
                    functionPreloads,
                    sourceParameters,
                    functionOptions,
                    registerPlan,
                    executesInDynamicScope,
                    diagnostics,
                    activeFunctions,
                    activeTryBodies,
                    cancellationToken,
                    out var sourceRegisterPlanning))
            {
                return null;
            }

            var sourceRegisters = sourceRegisterPlanning.Registers;
            var finalOptions = sourceRegisterPlanning.Options;

            var body = CompileFunctionBody(
                source,
                closureAnalysis,
                codeUnit,
                functionPreloads,
                finalOptions,
                registerPlan,
                sourceRegisters,
                executesInDynamicScope,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                compileFunctions: true,
                cancellationToken: cancellationToken);
            if (body is null)
                return null;

            ApplyParameterRegisters(
                sourceParameters,
                actionParameters,
                sourceRegisters);
            var registerCount = checked((byte)Math.Max(
                Math.Max(
                    GetRequiredRegisterCount(body.StackLir, body.Nested),
                    GetRequiredRegisterCount(sourceRegisters.MaximumRegister)),
                GetRequiredRegisterCount(
                    functionPreloads.PreloadRegisterCount)));
            var plan = new Avm1DefineFunction2Plan(
                site.Function,
                codeUnit,
                site.IsDeclaration,
                GetFunctionSiteName(parentMir, site),
                registerCount,
                functionPreloads.Flags,
                actionParameters,
                sourceRegisters.Allocations.ToArray());
            var emission = Avm1StackLirEmitter.Emit(
                body.Mir,
                body.StackLir,
                finalOptions,
                body.Nested.Functions,
                body.Nested.WithRegions,
                body.Nested.TryRegions,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            var nestedArtifacts = CollectFunctionArtifacts(
                body.Mir,
                body.Nested.Functions);
            var nestedWithArtifacts = CollectWithArtifacts(
                body.Mir,
                body.Nested.WithRegions);
            var nestedTryArtifacts = CollectTryArtifacts(
                body.Mir,
                body.Nested.TryRegions);
            var artifact = new Avm1CompiledFunctionArtifact(
                source,
                plan,
                body.Mir,
                body.StackLir,
                emission.InstructionMap,
                nestedArtifacts,
                nestedWithArtifacts,
                nestedTryArtifacts);
            return new Avm1CompiledFunctionCodeUnit(
                plan,
                emission.CodeUnit,
                artifact);
        }
        finally
        {
            activeFunctions.Remove(site.Function);
        }
    }

    private static bool TryPlanSourceRegisters(
        Avm1SourceMethod source,
        Avm1ClosureAnalysis closureAnalysis,
        SourceCodeUnitIndex codeUnit,
        Avm1FunctionPreloadPlan functionPreloads,
        IReadOnlyList<SourceSymbolIndex> sourceParameters,
        Avm1CompilationOptions functionOptions,
        Avm1RegisterPlanningResult registerPlan,
        bool executesInDynamicScope,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeTryBodies,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out SourceRegisterPlanningResult? result)
    {
        var namedDiagnostics = new List<Avm1CompilerDiagnostic>();
        var namedMeasurement = CompileFunctionBody(
            source,
            closureAnalysis,
            codeUnit,
            functionPreloads,
            functionOptions,
            registerPlan,
            Avm1SourceRegisterPlan.Empty,
            executesInDynamicScope,
            namedDiagnostics,
            activeFunctions,
            activeTryBodies,
            compileFunctions: false,
            cancellationToken: cancellationToken);
        if (namedMeasurement is null)
        {
            diagnostics.AddRange(namedDiagnostics);
            result = null;
            return false;
        }

        var namedScratchRegisterHighWater = GetRequiredScratchRegisterHighWater(
            namedMeasurement.StackLir,
            namedMeasurement.Nested,
            functionOptions,
            registerPlan.RequiredCompletionRegisters);
        List<Avm1CompilerDiagnostic>? fallbackDiagnostics = null;
        for (var includeInvocationTargets = true;
            ;
            includeInvocationTargets = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = Avm1SourceRegisterCandidates.Create(
                closureAnalysis,
                codeUnit,
                sourceParameters,
                includeInvocationTargets,
                cancellationToken);
            IReadOnlyList<SourceSymbolIndex> preservedLifetimeSymbols =
                functionOptions.SourceRegisterAllocationMode is
                    Avm1SourceRegisterAllocationMode.PreserveSourceSymbols
                    ? candidates.Symbols
                    : sourceParameters;
            var interference = Avm1SourceRegisterLivenessAnalysis.Analyze(
                namedMeasurement.Mir,
                namedMeasurement.Nested,
                candidates.Symbols,
                preservedLifetimeSymbols,
                cancellationToken);
            var shapeRegisters = Avm1SourceRegisterPlan.Allocate(
                candidates,
                interference,
                namedScratchRegisterHighWater + 1,
                cancellationToken);
            var shapeDiagnostics = new List<Avm1CompilerDiagnostic>();
            var shapeMeasurement = CompileFunctionBody(
                source,
                closureAnalysis,
                codeUnit,
                functionPreloads,
                functionOptions,
                registerPlan,
                shapeRegisters,
                executesInDynamicScope,
                shapeDiagnostics,
                activeFunctions,
                activeTryBodies,
                compileFunctions: false,
                cancellationToken: cancellationToken);
            if (shapeMeasurement is not null)
            {
                var shapeScratchRegisterHighWater =
                    GetRequiredScratchRegisterHighWater(
                        shapeMeasurement.StackLir,
                        shapeMeasurement.Nested,
                        functionOptions,
                        registerPlan.RequiredCompletionRegisters);
                var finalRegisters = Avm1SourceRegisterPlan.Allocate(
                    candidates,
                    interference,
                    shapeScratchRegisterHighWater + 1,
                    cancellationToken);
                if (!includeInvocationTargets ||
                    candidates.HasSameInvocationSelection(
                        shapeRegisters,
                        finalRegisters))
                {
                    result = new SourceRegisterPlanningResult(
                        finalRegisters,
                        CreateMeasuredFunctionOptions(
                            functionOptions,
                            shapeScratchRegisterHighWater));
                    return true;
                }
            }

            fallbackDiagnostics = shapeDiagnostics;
            if (!includeInvocationTargets)
                break;
        }

        diagnostics.AddRange(fallbackDiagnostics ?? namedDiagnostics);
        result = null;
        return false;
    }

    private static FunctionBodyCompilation? CompileFunctionBody(
        Avm1SourceMethod source,
        Avm1ClosureAnalysis closureAnalysis,
        SourceCodeUnitIndex codeUnit,
        Avm1FunctionPreloadPlan functionPreloads,
        Avm1CompilationOptions options,
        Avm1RegisterPlanningResult registerPlan,
        Avm1SourceRegisterPlan sourceRegisters,
        bool executesInDynamicScope,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeTryBodies,
        bool compileFunctions,
        CancellationToken cancellationToken)
    {
        var context = Avm1MirLoweringContext.CreateRoot(
            source.Arena,
            options.ReservedTemporaryRegisters,
            registerPlan.RequiredCompletionRegisters,
            sourceRegisters,
            functionPreloads,
            GetFunctionSelfBinding(
                source.Arena,
                closureAnalysis,
                codeUnit),
            functionPreloads.ArgumentsRegister,
            executesInDynamicScope,
            closureAnalysis[codeUnit].Flags.HasFlag(
                Avm1ClosureCodeUnitFlags.InvokesEval),
            options.ProtectedRegionExitMode,
            options.ExpressionEvaluationMode);
        var mir = Avm1SourceToMirLowerer.Lower(
            source,
            context,
            diagnostics,
            options.TimelineLayout,
            cancellationToken);
        if (HasErrors(diagnostics))
            return null;
        mir = Avm1MirOptimizer.Optimize(mir, options, cancellationToken);

        var stackLir = Avm1MirToStackLirLowerer.Lower(
            mir,
            CreateStackLirOptions(options, context),
            diagnostics,
            cancellationToken);
        if (HasErrors(diagnostics))
            return null;

        var nested = CompileNestedCodeUnits(
            source,
            closureAnalysis,
            mir,
            stackLir,
            context,
            codeUnit,
            options,
            diagnostics,
            activeFunctions,
            activeTryBodies,
            compileFunctions,
            cancellationToken);
        return HasErrors(diagnostics)
            ? null
            : new FunctionBodyCompilation(mir, stackLir, nested);
    }

    private static Dictionary<Avm1MirWithSiteIndex, Avm1CompiledWithCodeUnit>
        CompileNestedWithRegions(
            Avm1SourceMethod currentSource,
            Avm1ClosureAnalysis closureAnalysis,
            Avm1MirMethod parentMir,
            Avm1MirLoweringContext loweringContext,
            SourceCodeUnitIndex parentCodeUnit,
            Avm1CompilationOptions options,
            List<Avm1CompilerDiagnostic> diagnostics,
            HashSet<SourceFunctionIndex> activeFunctions,
            HashSet<SourceStatementIndex> activeProtectedBodies,
            bool compileFunctions,
            CancellationToken cancellationToken)
    {
        var compiled = new Dictionary<
            Avm1MirWithSiteIndex,
            Avm1CompiledWithCodeUnit>();
        foreach (var site in parentMir.WithSites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = CompileNestedWithRegion(
                currentSource,
                closureAnalysis,
                parentMir,
                site,
                loweringContext,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeProtectedBodies,
                compileFunctions,
                cancellationToken);
            if (region is not null)
                compiled.Add(site.Index, region);
            if (HasErrors(diagnostics))
                break;
        }
        return compiled;
    }

    private static Avm1CompiledWithCodeUnit? CompileNestedWithRegion(
        Avm1SourceMethod currentSource,
        Avm1ClosureAnalysis closureAnalysis,
        Avm1MirMethod parentMir,
        Avm1MirWithSite site,
        Avm1MirLoweringContext loweringContext,
        SourceCodeUnitIndex parentCodeUnit,
        Avm1CompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeProtectedBodies,
        bool compileFunctions,
        CancellationToken cancellationToken)
    {
        if (options.SwfVersion < 5)
        {
            AddWithDiagnostic(
                diagnostics,
                site.Origin,
                "With requires target SWF 5 or newer.");
            return null;
        }
        if (!IsValidStatement(currentSource.Arena, site.Body))
        {
            AddWithDiagnostic(
                diagnostics,
                site.Origin,
                $"MIR {site.Index} references an invalid Source statement body.");
            return null;
        }
        if (!TryCreateWithComponentContext(
                parentMir,
                site,
                loweringContext,
                diagnostics,
                out var componentContext))
        {
            return null;
        }

        var body = CompileWithComponent(
            currentSource,
            closureAnalysis,
            site.Body,
            componentContext,
            parentCodeUnit,
            options,
            diagnostics,
            activeFunctions,
            activeProtectedBodies,
            compileFunctions,
            cancellationToken);
        if (body is null)
            return null;

        var plan = new Avm1WithPlan(site.Index);
        var artifact = new Avm1CompiledWithArtifact(plan, body.Artifact);
        return new Avm1CompiledWithCodeUnit(plan, body.CodeUnit, artifact);
    }

    private static bool TryCreateWithComponentContext(
        Avm1MirMethod parentMir,
        Avm1MirWithSite site,
        Avm1MirLoweringContext parentContext,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1MirLoweringContext context)
    {
        context = null!;
        var range = site.VisibleControlScopes;
        if (range.Start < 0 ||
            range.Count < 0 ||
            range.Start > parentMir.WithVisibleControlScopes.Count - range.Count)
        {
            AddWithDiagnostic(
                diagnostics,
                site.Origin,
                $"MIR {site.Index} has an invalid visible control-scope range.");
            return false;
        }
        if (range.Count != 0 &&
            site.CompletionStorage.Kind is not Avm1MirCompletionStorageKind.Register)
        {
            AddWithDiagnostic(
                diagnostics,
                site.Origin,
                $"MIR {site.Index} requires register-backed completion storage.");
            return false;
        }

        var scopes = new Avm1MirVisibleControlScope[range.Count];
        for (var i = 0; i < scopes.Length; i++)
            scopes[i] = parentMir.GetVisibleControlScope(site, i);
        context = new Avm1MirLoweringContext(
            parentContext.CompletionVariablePrefix,
            CreateNestedSpillPrefix(
                parentContext.TemporarySpillPrefix,
                "with",
                site.Index.Value),
            CreateCompletionSink(parentMir, site.CompletionStorage),
            scopes,
            parentContext.CompletionRegisters,
            parentContext.CompletionRegisterCount,
            parentContext.SourceRegisters,
            parentContext.FunctionPreloads,
            SourceSymbolIndex.Invalid,
            0,
            parentContext.CompletionRegisterDepth +
                (site.CompletionStorage.Kind is Avm1MirCompletionStorageKind.Register
                    ? 1
                    : 0),
            parentContext.DynamicScopeDepth + 1,
            parentContext.InvokesEval,
            parentContext.ProtectedRegionExitMode,
            parentContext.ExpressionEvaluationMode);
        return true;
    }

    private static Avm1CompiledWithComponentCodeUnit? CompileWithComponent(
        Avm1SourceMethod currentSource,
        Avm1ClosureAnalysis closureAnalysis,
        SourceStatementIndex body,
        Avm1MirLoweringContext loweringContext,
        SourceCodeUnitIndex parentCodeUnit,
        Avm1CompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeProtectedBodies,
        bool compileFunctions,
        CancellationToken cancellationToken)
    {
        if (!activeProtectedBodies.Add(body))
        {
            AddWithDiagnostic(
                diagnostics,
                SourceOriginIndex.Invalid,
                $"Source with component {body} recursively contains itself.");
            return null;
        }

        try
        {
            var source = new Avm1SourceMethod(
                currentSource.Arena,
                body,
                currentSource.Parameters.ToArray(),
                []);
            var mir = Avm1SourceToMirLowerer.Lower(
                source,
                loweringContext,
                diagnostics,
                options.TimelineLayout,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;
            mir = Avm1MirOptimizer.Optimize(mir, options, cancellationToken);

            var stackLir = Avm1MirToStackLirLowerer.Lower(
                mir,
                CreateStackLirOptions(options, loweringContext),
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            var nested = CompileNestedCodeUnits(
                source,
                closureAnalysis,
                mir,
                stackLir,
                loweringContext,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeProtectedBodies,
                compileFunctions,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            if (!compileFunctions)
            {
                var analysisArtifact = new Avm1CompiledWithComponentArtifact(
                    source,
                    mir,
                    stackLir,
                    [],
                    [],
                    CollectWithArtifacts(mir, nested.WithRegions),
                    CollectTryArtifacts(mir, nested.TryRegions));
                return new Avm1CompiledWithComponentCodeUnit(
                    new Avm1CodeUnit(),
                    analysisArtifact);
            }

            var emission = Avm1StackLirEmitter.Emit(
                mir,
                stackLir,
                options,
                nested.Functions,
                nested.WithRegions,
                nested.TryRegions,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            var artifact = new Avm1CompiledWithComponentArtifact(
                source,
                mir,
                stackLir,
                emission.InstructionMap,
                CollectFunctionArtifacts(mir, nested.Functions),
                CollectWithArtifacts(mir, nested.WithRegions),
                CollectTryArtifacts(mir, nested.TryRegions));
            return new Avm1CompiledWithComponentCodeUnit(
                emission.CodeUnit,
                artifact);
        }
        finally
        {
            activeProtectedBodies.Remove(body);
        }
    }

    private static Dictionary<Avm1MirTrySiteIndex, Avm1CompiledTryCodeUnit>
        CompileNestedTryRegions(
            Avm1SourceMethod currentSource,
            Avm1ClosureAnalysis closureAnalysis,
            Avm1MirMethod parentMir,
            Avm1MirLoweringContext loweringContext,
            SourceCodeUnitIndex parentCodeUnit,
            Avm1CompilationOptions options,
            List<Avm1CompilerDiagnostic> diagnostics,
            HashSet<SourceFunctionIndex> activeFunctions,
            HashSet<SourceStatementIndex> activeTryBodies,
            bool compileFunctions,
            CancellationToken cancellationToken)
    {
        var compiled = new Dictionary<
            Avm1MirTrySiteIndex,
            Avm1CompiledTryCodeUnit>();
        foreach (var site in parentMir.TrySites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = CompileNestedTryRegion(
                currentSource,
                closureAnalysis,
                parentMir,
                site,
                loweringContext,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                compileFunctions,
                cancellationToken);
            if (region is not null)
                compiled.Add(site.Index, region);
            if (HasErrors(diagnostics))
                break;
        }
        return compiled;
    }

    private static Avm1CompiledTryCodeUnit? CompileNestedTryRegion(
        Avm1SourceMethod currentSource,
        Avm1ClosureAnalysis closureAnalysis,
        Avm1MirMethod parentMir,
        Avm1MirTrySite site,
        Avm1MirLoweringContext loweringContext,
        SourceCodeUnitIndex parentCodeUnit,
        Avm1CompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeTryBodies,
        bool compileFunctions,
        CancellationToken cancellationToken)
    {
        if (options.SwfVersion < 7)
        {
            AddTryDiagnostic(
                diagnostics,
                site.Origin,
                "Try/catch/finally and throw require target SWF 7 or newer.");
            return null;
        }

        var arena = currentSource.Arena;
        if (!IsValidStatement(arena, site.TryBody) ||
            (site.CatchBody.IsValid && !IsValidStatement(arena, site.CatchBody)) ||
            (site.FinallyBody.IsValid && !IsValidStatement(arena, site.FinallyBody)))
        {
            AddTryDiagnostic(
                diagnostics,
                site.Origin,
                $"MIR {site.Index} references an invalid Source statement body.");
            return null;
        }

        var catchVariable = string.Empty;
        if (site.CatchBody.IsValid)
        {
            if (!site.CatchSymbol.IsValid ||
                site.CatchSymbol.Value >= arena.Symbols.Count)
            {
                AddTryDiagnostic(
                    diagnostics,
                    site.Origin,
                    $"MIR {site.Index} references an invalid catch symbol.");
                return null;
            }

            var catchSymbol = arena[site.CatchSymbol];
            if (catchSymbol.Kind is not Avm1SourceSymbolKind.Catch ||
                !catchSymbol.Name.IsValid ||
                catchSymbol.Name.Value >= arena.Strings.Count)
            {
                AddTryDiagnostic(
                    diagnostics,
                    site.Origin,
                    $"MIR {site.Index} catch symbol metadata is invalid.");
                return null;
            }
            catchVariable = arena[catchSymbol.Name];
        }
        else if (site.CatchSymbol.IsValid)
        {
            AddTryDiagnostic(
                diagnostics,
                site.Origin,
                $"MIR {site.Index} has a catch symbol without a catch body.");
            return null;
        }

        if (!TryCreateTryComponentContext(
            parentMir,
            site,
            loweringContext,
            diagnostics,
            out var componentContext))
        {
            return null;
        }

        var tryBody = CompileTryComponent(
            currentSource,
            closureAnalysis,
            site.TryBody,
            componentContext,
            parentCodeUnit,
            options,
            diagnostics,
            activeFunctions,
            activeTryBodies,
            compileFunctions,
            cancellationToken);
        if (tryBody is null)
            return null;

        Avm1CompiledTryComponentCodeUnit? catchBody = null;
        if (site.CatchBody.IsValid)
        {
            catchBody = CompileTryComponent(
                currentSource,
                closureAnalysis,
                site.CatchBody,
                componentContext,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                compileFunctions,
                cancellationToken);
            if (catchBody is null)
                return null;
        }

        Avm1CompiledTryComponentCodeUnit? finallyBody = null;
        if (site.FinallyBody.IsValid)
        {
            finallyBody = CompileTryComponent(
                currentSource,
                closureAnalysis,
                site.FinallyBody,
                componentContext,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                compileFunctions,
                cancellationToken);
            if (finallyBody is null)
                return null;
        }

        var flags = (site.CatchBody.IsValid
                ? TryFlags.CatchBlock
                : (TryFlags)0) |
            (site.FinallyBody.IsValid
                ? TryFlags.FinallyBlock
                : (TryFlags)0);
        byte catchRegister = 0;
        if (site.CatchBody.IsValid &&
            loweringContext.SourceRegisters.TryGetRegister(
                site.CatchSymbol,
                out catchRegister))
        {
            flags |= TryFlags.CatchInRegister;
            catchVariable = string.Empty;
        }
        var plan = new Avm1TryPlan(
            site.Index,
            flags,
            site.CatchSymbol,
            catchVariable,
            catchRegister);
        var artifact = new Avm1CompiledTryArtifact(
            plan,
            tryBody.Artifact,
            catchBody?.Artifact,
            finallyBody?.Artifact);
        return new Avm1CompiledTryCodeUnit(
            plan,
            tryBody.CodeUnit,
            catchBody?.CodeUnit,
            finallyBody?.CodeUnit,
            tryBody.ParentBranches,
            catchBody?.ParentBranches ?? [],
            finallyBody?.ParentBranches ?? [],
            artifact);
    }

    private static bool TryCreateTryComponentContext(
        Avm1MirMethod parentMir,
        Avm1MirTrySite site,
        Avm1MirLoweringContext parentContext,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1MirLoweringContext context)
    {
        context = null!;
        var range = site.VisibleControlScopes;
        if (range.Start < 0 ||
            range.Count < 0 ||
            range.Start > parentMir.VisibleControlScopes.Count - range.Count)
        {
            AddTryDiagnostic(
                diagnostics,
                site.Origin,
                $"MIR {site.Index} has an invalid visible control-scope range.");
            return false;
        }

        if (range.Count != 0)
        {
            var usesPhysicalParentBranches =
                site.UsesPhysicalParentBranches;
            if (usesPhysicalParentBranches &&
                parentContext.ProtectedRegionExitMode is not
                    Avm1ProtectedRegionExitMode.AdobePhysicalBranch)
            {
                AddTryDiagnostic(
                    diagnostics,
                    site.Origin,
                    $"MIR {site.Index} requests Adobe physical parent branches " +
                    "under a semantic completion lowering context.");
                return false;
            }
            if (!usesPhysicalParentBranches &&
                (site.CompletionStorage.Kind is Avm1MirCompletionStorageKind.None ||
                site.CompletionStorage.Kind is
                    Avm1MirCompletionStorageKind.ActivationName &&
                (!site.CompletionStorage.Name.IsValid ||
                    site.CompletionStorage.Name.Value >= parentMir.Strings.Count)))
            {
                AddTryDiagnostic(
                    diagnostics,
                    site.Origin,
                    $"MIR {site.Index} has no valid completion storage.");
                return false;
            }
        }

        var scopes = new Avm1MirVisibleControlScope[range.Count];
        for (var i = 0; i < scopes.Length; i++)
            scopes[i] = parentMir.GetVisibleControlScope(site, i);
        context = new Avm1MirLoweringContext(
            parentContext.CompletionVariablePrefix,
            CreateNestedSpillPrefix(
                parentContext.TemporarySpillPrefix,
                "try",
                site.Index.Value),
            CreateCompletionSink(parentMir, site.CompletionStorage),
            scopes,
            parentContext.CompletionRegisters,
            parentContext.CompletionRegisterCount,
            parentContext.SourceRegisters,
            parentContext.FunctionPreloads,
            SourceSymbolIndex.Invalid,
            0,
            parentContext.CompletionRegisterDepth +
                (site.CompletionStorage.Kind is Avm1MirCompletionStorageKind.Register
                    ? 1
                    : 0),
            parentContext.DynamicScopeDepth,
            parentContext.InvokesEval,
            parentContext.ProtectedRegionExitMode,
            parentContext.ExpressionEvaluationMode);
        return true;
    }

    private static Avm1MirCompletionSink CreateCompletionSink(
        Avm1MirMethod mir,
        Avm1MirCompletionStorage storage) =>
        storage.Kind switch
        {
            Avm1MirCompletionStorageKind.None => Avm1MirCompletionSink.None,
            Avm1MirCompletionStorageKind.ActivationName =>
                Avm1MirCompletionSink.Activation(mir[storage.Name]),
            Avm1MirCompletionStorageKind.Register =>
                Avm1MirCompletionSink.PhysicalRegister(storage.Register),
            _ => throw new ArgumentOutOfRangeException(nameof(storage))
        };

    private static Avm1CompiledTryComponentCodeUnit? CompileTryComponent(
        Avm1SourceMethod currentSource,
        Avm1ClosureAnalysis closureAnalysis,
        SourceStatementIndex body,
        Avm1MirLoweringContext loweringContext,
        SourceCodeUnitIndex parentCodeUnit,
        Avm1CompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        HashSet<SourceFunctionIndex> activeFunctions,
        HashSet<SourceStatementIndex> activeTryBodies,
        bool compileFunctions,
        CancellationToken cancellationToken)
    {
        if (!activeTryBodies.Add(body))
        {
            AddTryDiagnostic(
                diagnostics,
                SourceOriginIndex.Invalid,
                $"Source try component {body} recursively contains itself.");
            return null;
        }

        try
        {
            var source = new Avm1SourceMethod(
                currentSource.Arena,
                body,
                currentSource.Parameters.ToArray(),
                []);
            var mir = Avm1SourceToMirLowerer.Lower(
                source,
                loweringContext,
                diagnostics,
                options.TimelineLayout,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;
            mir = Avm1MirOptimizer.Optimize(mir, options, cancellationToken);

            var stackLir = Avm1MirToStackLirLowerer.Lower(
                mir,
                CreateStackLirOptions(options, loweringContext),
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            var nested = CompileNestedCodeUnits(
                source,
                closureAnalysis,
                mir,
                stackLir,
                loweringContext,
                parentCodeUnit,
                options,
                diagnostics,
                activeFunctions,
                activeTryBodies,
                compileFunctions,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            if (!compileFunctions)
            {
                var analysisArtifact = new Avm1CompiledTryComponentArtifact(
                    source,
                    mir,
                    stackLir,
                    [],
                    [],
                    CollectWithArtifacts(mir, nested.WithRegions),
                    CollectTryArtifacts(mir, nested.TryRegions));
                return new Avm1CompiledTryComponentCodeUnit(
                    new Avm1CodeUnit(),
                    [],
                    analysisArtifact);
            }

            var emission = Avm1StackLirEmitter.Emit(
                mir,
                stackLir,
                options,
                nested.Functions,
                nested.WithRegions,
                nested.TryRegions,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return null;

            var artifact = new Avm1CompiledTryComponentArtifact(
                source,
                mir,
                stackLir,
                emission.InstructionMap,
                CollectFunctionArtifacts(mir, nested.Functions),
                CollectWithArtifacts(mir, nested.WithRegions),
                CollectTryArtifacts(mir, nested.TryRegions));
            return new Avm1CompiledTryComponentCodeUnit(
                emission.CodeUnit,
                emission.ParentBranches,
                artifact);
        }
        finally
        {
            activeTryBodies.Remove(body);
        }
    }

    private static bool IsValidStatement(
        Avm1SourceArena arena,
        SourceStatementIndex statement) =>
        statement.IsValid && statement.Value < arena.Statements.Count;

    private static bool TryGetFunctionParameters(
        Avm1SourceArena arena,
        Avm1SourceFunction function,
        List<Avm1CompilerDiagnostic> diagnostics,
        out SourceSymbolIndex[] sourceParameters,
        out FunctionParameter[] actionParameters)
    {
        sourceParameters = new SourceSymbolIndex[function.Parameters.Count];
        actionParameters = new FunctionParameter[function.Parameters.Count];
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var parameter = arena.GetParameter(function, i);
            if (!parameter.IsValid || parameter.Value >= arena.Symbols.Count)
            {
                AddFunctionDiagnostic(
                    diagnostics,
                    function.Origin,
                    $"Function {function.Index} contains invalid parameter {i}.");
                return false;
            }

            var symbol = arena[parameter];
            if (symbol.Kind is not Avm1SourceSymbolKind.Parameter ||
                !symbol.Name.IsValid ||
                symbol.Name.Value >= arena.Strings.Count)
            {
                AddFunctionDiagnostic(
                    diagnostics,
                    function.Origin,
                    $"Function {function.Index} parameter {parameter} has invalid " +
                    "symbol metadata.");
                return false;
            }

            sourceParameters[i] = parameter;
            actionParameters[i] = new FunctionParameter(0, arena[symbol.Name]);
        }
        return true;
    }

    private static Avm1CompilationOptions CreateNestedFunctionOptions(
        Avm1CompilationOptions parentOptions,
        int preloadRegisterCount) =>
        new(parentOptions.SwfVersion)
        {
            TimelineLayout = parentOptions.TimelineLayout,
            OptimizationLevel = parentOptions.OptimizationLevel,
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            SourceRegisterAllocationMode =
                parentOptions.SourceRegisterAllocationMode,
            RegisterCount = byte.MaxValue,
            ProtectedRegionExitMode = parentOptions.ProtectedRegionExitMode,
            ExpressionEvaluationMode = parentOptions.ExpressionEvaluationMode,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(
                checked((byte)(preloadRegisterCount + 1)),
                checked((byte)(byte.MaxValue - preloadRegisterCount - 1)))
        };

    private static Avm1CompilationOptions CreateMeasuredFunctionOptions(
        Avm1CompilationOptions options,
        byte scratchRegisterHighWater)
    {
        var available = options.ReservedTemporaryRegisters;
        return options with
        {
            ReservedTemporaryRegisters =
                available.IsEmpty ||
                scratchRegisterHighWater < available.First
                ? default
                : new Avm1TemporaryRegisterRange(
                    available.First,
                    checked((byte)(
                        scratchRegisterHighWater - available.First + 1)))
        };
    }

    private static SourceSymbolIndex GetFunctionSelfBinding(
        Avm1SourceArena arena,
        Avm1ClosureAnalysis closureAnalysis,
        SourceCodeUnitIndex codeUnit)
    {
        if (!codeUnit.IsValid ||
            codeUnit.Value >= closureAnalysis.ScopeAnalysis.CodeUnits.Count)
        {
            return SourceSymbolIndex.Invalid;
        }

        var functionIndex = closureAnalysis.ScopeAnalysis[codeUnit].Function;
        if (!functionIndex.IsValid ||
            functionIndex.Value >= arena.Functions.Count)
        {
            return SourceSymbolIndex.Invalid;
        }

        var function = arena[functionIndex];
        return function.NameSymbol.IsValid &&
            !function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration)
                ? function.NameSymbol
                : SourceSymbolIndex.Invalid;
    }

    private static void ApplyParameterRegisters(
        SourceSymbolIndex[] sourceParameters,
        FunctionParameter[] actionParameters,
        Avm1SourceRegisterPlan sourceRegisters)
    {
        for (var i = 0; i < sourceParameters.Length; i++)
        {
            if (!sourceRegisters.TryGetRegister(
                    sourceParameters[i],
                    out var register))
            {
                continue;
            }

            actionParameters[i] = actionParameters[i] with
            {
                Register = register
            };
        }
    }

    private static bool TryCreateRegisterPlan(
        Avm1SourceMethod source,
        Avm1CompilationOptions options,
        bool executesInDynamicScope,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        out Avm1CompilationOptions effectiveOptions,
        out Avm1RegisterPlanningResult plan)
    {
        plan = Avm1RegisterPlanningAnalysis.Analyze(
            source,
            executesInDynamicScope,
            cancellationToken);
        var compilerRegisters = options.ReservedTemporaryRegisters;
        if (compilerRegisters.IsEmpty)
            compilerRegisters = GetAutomaticCompilerRegisterRange(options);
        effectiveOptions = options with
        {
            ReservedTemporaryRegisters = compilerRegisters
        };

        if (plan.RequiredCompletionRegisters <= compilerRegisters.Count)
            return true;

        var capacity = compilerRegisters.IsEmpty
            ? "no compiler-owned registers are available"
            : $"compiler-owned range {compilerRegisters.First}.." +
                $"{compilerRegisters.Last} provides {compilerRegisters.Count}";
        diagnostics.Add(new Avm1CompilerDiagnostic(
            "AVM1CMP124",
            Avm1CompilationDiagnosticSeverity.Error,
            plan.LimitingOrigin,
            -1,
            $"Code unit requires {plan.RequiredCompletionRegisters} concurrent " +
            $"completion register(s), but {capacity}."));
        return false;
    }

    private static Avm1TemporaryRegisterRange GetAutomaticCompilerRegisterRange(
        Avm1CompilationOptions options) =>
        options.RegisterFile switch
        {
            Avm1CompilationRegisterFile.Legacy =>
                new Avm1TemporaryRegisterRange(0, 4),
            Avm1CompilationRegisterFile.DefineFunction2
                when options.RegisterCount > 1 =>
                new Avm1TemporaryRegisterRange(
                    1,
                    checked((byte)(options.RegisterCount - 1))),
            _ => default
        };

    private static Avm1CompilationOptions CreateComponentOptions(
        Avm1CompilationOptions parentOptions,
        Avm1StackLirMethod parentStackLir)
    {
        var range = parentOptions.ReservedTemporaryRegisters;
        if (range.IsEmpty || parentStackLir.TemporaryRegisters.Count == 0)
            return parentOptions;

        var first = parentStackLir.TemporaryRegisters.Max() + 1;
        if (first > range.Last)
        {
            return parentOptions with
            {
                ReservedTemporaryRegisters = default
            };
        }

        return parentOptions with
        {
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(
                checked((byte)first),
                checked((byte)(range.Last - first + 1)))
        };
    }

    private static Avm1StackLirLoweringOptions CreateStackLirOptions(
        Avm1CompilationOptions options,
        Avm1MirLoweringContext context)
    {
        var range = options.ReservedTemporaryRegisters;
        if (!range.IsEmpty)
        {
            var firstTemporaryRegister = Math.Max(
                range.First,
                context.CompletionRegisters.First +
                    context.CompletionRegisterCount);
            range = firstTemporaryRegister > range.Last
                ? default
                : new Avm1TemporaryRegisterRange(
                    checked((byte)firstTemporaryRegister),
                    checked((byte)(range.Last - firstTemporaryRegister + 1)));
        }

        return new Avm1StackLirLoweringOptions(
            range,
            context.CanUseActivationTemporarySpills
                ? context.TemporarySpillPrefix
                : null,
            options.OptimizationLevel is Avm1OptimizationLevel.Basic);
    }

    private static string CreateNestedSpillPrefix(
        string parentPrefix,
        string component,
        int siteIndex) =>
        string.Concat(
            parentPrefix,
            "_",
            component,
            siteIndex.ToString(CultureInfo.InvariantCulture));

    private static byte GetRequiredRegisterCount(
        Avm1StackLirMethod stackLir,
        Avm1CompiledNestedCodeUnits nested)
    {
        var maximum = GetMaximumRegister(stackLir, includeOperands: true);
        foreach (var region in nested.WithRegions.Values)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(region.Artifact, includeOperands: true));
        }
        foreach (var region in nested.TryRegions.Values)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(region.Artifact, includeOperands: true));
        }
        return maximum < 0
            ? (byte)0
            : checked((byte)(maximum + 1));
    }

    private static int GetRequiredRegisterCount(int maximumRegister) =>
        maximumRegister == 0 ? 0 : maximumRegister + 1;

    private static byte GetRequiredScratchRegisterHighWater(
        Avm1StackLirMethod stackLir,
        Avm1CompiledNestedCodeUnits nested,
        Avm1CompilationOptions options,
        int requiredCompletionRegisters)
    {
        var completionHighWater =
            requiredCompletionRegisters == 0 ||
            options.ReservedTemporaryRegisters.IsEmpty
                ? 0
                : options.ReservedTemporaryRegisters.First +
                    requiredCompletionRegisters - 1;
        var maximum = Math.Max(
            Math.Max(
                completionHighWater,
                options.ReservedTemporaryRegisters.IsEmpty
                    ? 0
                    : options.ReservedTemporaryRegisters.First - 1),
            GetMaximumRegister(stackLir, includeOperands: false));
        foreach (var region in nested.WithRegions.Values)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(region.Artifact, includeOperands: false));
        }
        foreach (var region in nested.TryRegions.Values)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(region.Artifact, includeOperands: false));
        }
        return checked((byte)maximum);
    }

    private static int GetMaximumRegister(
        Avm1StackLirMethod stackLir,
        bool includeOperands)
    {
        var maximum = stackLir.TemporaryRegisters.Count == 0
            ? -1
            : stackLir.TemporaryRegisters.Max();
        if (!includeOperands)
            return maximum;

        foreach (var instruction in stackLir.Instructions)
        {
            if (instruction.Kind is Avm1StackLirInstructionKind.PushRegister or
                Avm1StackLirInstructionKind.StoreRegister)
            {
                maximum = Math.Max(maximum, instruction.Register);
            }
        }
        return maximum;
    }

    private static int GetMaximumRegister(
        Avm1CompiledWithArtifact withRegion,
        bool includeOperands) =>
        GetMaximumRegister(withRegion.Body, includeOperands);

    private static int GetMaximumRegister(
        Avm1CompiledWithComponentArtifact component,
        bool includeOperands)
    {
        var maximum = GetMaximumRegister(component.StackLir, includeOperands);
        foreach (var nestedWith in component.NestedWithRegions)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(nestedWith, includeOperands));
        }
        foreach (var nestedTry in component.NestedTryRegions)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(nestedTry, includeOperands));
        }
        return maximum;
    }

    private static int GetMaximumRegister(
        Avm1CompiledTryArtifact tryRegion,
        bool includeOperands)
    {
        var maximum = GetMaximumRegister(
            tryRegion.TryBody,
            includeOperands);
        if (tryRegion.CatchBody is not null)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(tryRegion.CatchBody, includeOperands));
        }
        if (tryRegion.FinallyBody is not null)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(tryRegion.FinallyBody, includeOperands));
        }
        return maximum;
    }

    private static int GetMaximumRegister(
        Avm1CompiledTryComponentArtifact component,
        bool includeOperands)
    {
        var maximum = GetMaximumRegister(component.StackLir, includeOperands);
        foreach (var nestedWith in component.NestedWithRegions)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(nestedWith, includeOperands));
        }
        foreach (var nestedTry in component.NestedTryRegions)
        {
            maximum = Math.Max(
                maximum,
                GetMaximumRegister(nestedTry, includeOperands));
        }
        return maximum;
    }

    private static string GetFunctionSiteName(
        Avm1MirMethod mir,
        Avm1MirFunctionSite site) =>
        site.Name.IsValid && site.Name.Value < mir.Strings.Count
            ? mir[site.Name]
            : string.Empty;

    private static Avm1CompiledFunctionArtifact[] CollectFunctionArtifacts(
        Avm1MirMethod mir,
        Dictionary<SourceFunctionIndex, Avm1CompiledFunctionCodeUnit>
            compiledFunctions)
    {
        var result = new List<Avm1CompiledFunctionArtifact>();
        var added = new HashSet<SourceFunctionIndex>();
        foreach (var site in mir.FunctionSites)
        {
            if (added.Add(site.Function) &&
                compiledFunctions.TryGetValue(site.Function, out var compiled))
            {
                result.Add(compiled.Artifact);
            }
        }
        return result.ToArray();
    }

    private static Avm1CompiledTryArtifact[] CollectTryArtifacts(
        Avm1MirMethod mir,
        Dictionary<Avm1MirTrySiteIndex, Avm1CompiledTryCodeUnit>
            compiledTryRegions)
    {
        var result = new List<Avm1CompiledTryArtifact>(mir.TrySites.Count);
        foreach (var site in mir.TrySites)
        {
            if (compiledTryRegions.TryGetValue(site.Index, out var compiled))
                result.Add(compiled.Artifact);
        }
        return result.ToArray();
    }

    private static Avm1CompiledWithArtifact[] CollectWithArtifacts(
        Avm1MirMethod mir,
        Dictionary<Avm1MirWithSiteIndex, Avm1CompiledWithCodeUnit>
            compiledWithRegions)
    {
        var result = new List<Avm1CompiledWithArtifact>(mir.WithSites.Count);
        foreach (var site in mir.WithSites)
        {
            if (compiledWithRegions.TryGetValue(site.Index, out var compiled))
                result.Add(compiled.Artifact);
        }
        return result.ToArray();
    }

    private static int GetMaximumStackDepth(
        Avm1StackLirMethod root,
        Avm1CompiledNestedCodeUnits nested)
    {
        var maximum = root.MaximumStackDepth;
        foreach (var function in nested.Functions.Values)
            maximum = Math.Max(maximum, GetMaximumStackDepth(function.Artifact));
        foreach (var withRegion in nested.WithRegions.Values)
            maximum = Math.Max(maximum, GetMaximumStackDepth(withRegion.Artifact));
        foreach (var tryRegion in nested.TryRegions.Values)
            maximum = Math.Max(maximum, GetMaximumStackDepth(tryRegion.Artifact));
        return maximum;
    }

    private static int GetMaximumStackDepth(Avm1CompiledFunctionArtifact function)
    {
        var maximum = function.StackLir.MaximumStackDepth;
        foreach (var child in function.NestedFunctions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(child));
        foreach (var withRegion in function.NestedWithRegions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(withRegion));
        foreach (var tryRegion in function.NestedTryRegions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(tryRegion));
        return maximum;
    }

    private static int GetMaximumStackDepth(Avm1CompiledTryArtifact tryRegion)
    {
        var maximum = GetMaximumStackDepth(tryRegion.TryBody);
        if (tryRegion.CatchBody is not null)
            maximum = Math.Max(maximum, GetMaximumStackDepth(tryRegion.CatchBody));
        if (tryRegion.FinallyBody is not null)
            maximum = Math.Max(maximum, GetMaximumStackDepth(tryRegion.FinallyBody));
        return maximum;
    }

    private static int GetMaximumStackDepth(Avm1CompiledWithArtifact withRegion) =>
        GetMaximumStackDepth(withRegion.Body);

    private static int GetMaximumStackDepth(
        Avm1CompiledWithComponentArtifact component)
    {
        var maximum = component.StackLir.MaximumStackDepth;
        foreach (var function in component.NestedFunctions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(function));
        foreach (var withRegion in component.NestedWithRegions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(withRegion));
        foreach (var tryRegion in component.NestedTryRegions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(tryRegion));
        return maximum;
    }

    private static int GetMaximumStackDepth(
        Avm1CompiledTryComponentArtifact component)
    {
        var maximum = component.StackLir.MaximumStackDepth;
        foreach (var function in component.NestedFunctions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(function));
        foreach (var withRegion in component.NestedWithRegions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(withRegion));
        foreach (var tryRegion in component.NestedTryRegions)
            maximum = Math.Max(maximum, GetMaximumStackDepth(tryRegion));
        return maximum;
    }

    private static void AddFunctionDiagnostic(
        List<Avm1CompilerDiagnostic> diagnostics,
        SourceOriginIndex origin,
        string message) =>
        diagnostics.Add(new Avm1CompilerDiagnostic(
            "AVM1CMP120",
            Avm1CompilationDiagnosticSeverity.Error,
            origin,
            -1,
            message));

    private static void AddTryDiagnostic(
        List<Avm1CompilerDiagnostic> diagnostics,
        SourceOriginIndex origin,
        string message) =>
        diagnostics.Add(new Avm1CompilerDiagnostic(
            "AVM1CMP121",
            Avm1CompilationDiagnosticSeverity.Error,
            origin,
            -1,
            message));

    private static void AddWithDiagnostic(
        List<Avm1CompilerDiagnostic> diagnostics,
        SourceOriginIndex origin,
        string message) =>
        diagnostics.Add(new Avm1CompilerDiagnostic(
            "AVM1CMP123",
            Avm1CompilationDiagnosticSeverity.Error,
            origin,
            -1,
            message));

    private static void AddAssemblyDiagnostics(
        Avm1ActionBody body,
        Avm1CompiledInstructionMap[] instructionMap,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        foreach (var diagnostic in body.Diagnostics)
        {
            var origin = SourceOriginIndex.Invalid;
            if (diagnostic.Instruction.IsValid &&
                diagnostic.Instruction.Value < instructionMap.Length)
            {
                var candidate = instructionMap[diagnostic.Instruction.Value];
                if (candidate.AssemblyInstruction == diagnostic.Instruction)
                    origin = candidate.Origin;
            }

            diagnostics.Add(new Avm1CompilerDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                origin,
                diagnostic.ByteOffset,
                diagnostic.Message));
        }
    }

    private static Avm1MethodArtifact CreateArtifact(
        Avm1SourceMethod source,
        Avm1ClosureAnalysis? closureAnalysis,
        Avm1MirMethod mir,
        Avm1StackLirMethod? stackLir,
        Avm1ActionBody? actionBody,
        Avm1CompiledInstructionMap[] instructionMap,
        Avm1CompiledFunctionArtifact[] nestedFunctions,
        Avm1CompiledWithArtifact[] nestedWithRegions,
        Avm1CompiledTryArtifact[] nestedTryRegions,
        List<Avm1CompilerDiagnostic> diagnostics) =>
        new(
            source,
            closureAnalysis,
            mir,
            stackLir,
            actionBody,
            instructionMap,
            nestedFunctions,
            nestedWithRegions,
            nestedTryRegions,
            diagnostics.ToArray());

    private static void AddClosureDiagnostics(
        Avm1ClosureAnalysis analysis,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        foreach (var diagnostic in analysis.Diagnostics)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Origin,
                -1,
                diagnostic.Message));
        }
    }

    private static void AddOptionDiagnostics(
        Avm1CompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(options.OptimizationLevel))
        {
            AddOptionDiagnostic(
                diagnostics,
                $"Optimization level {options.OptimizationLevel} is not valid.");
            return;
        }

        if (!Enum.IsDefined(options.RegisterFile))
        {
            AddOptionDiagnostic(
                diagnostics,
                $"Register file {options.RegisterFile} is not valid.");
            return;
        }

        if (!Enum.IsDefined(options.SourceRegisterAllocationMode))
        {
            AddOptionDiagnostic(
                diagnostics,
                "Source-register allocation mode " +
                    $"{options.SourceRegisterAllocationMode} is not valid.");
            return;
        }

        if (!Enum.IsDefined(options.ProtectedRegionExitMode))
        {
            AddOptionDiagnostic(
                diagnostics,
                $"Protected-region exit mode {options.ProtectedRegionExitMode} " +
                "is not valid.");
            return;
        }

        if (!Enum.IsDefined(options.ExpressionEvaluationMode))
        {
            AddOptionDiagnostic(
                diagnostics,
                $"Expression evaluation mode {options.ExpressionEvaluationMode} " +
                "is not valid.");
            return;
        }

        if (options.RegisterFile is Avm1CompilationRegisterFile.Legacy &&
            options.RegisterCount != 0)
        {
            AddOptionDiagnostic(
                diagnostics,
                "RegisterCount must be zero for the fixed legacy register file.");
        }
        if (options.RegisterFile is Avm1CompilationRegisterFile.DefineFunction2 &&
            options.SwfVersion < 7)
        {
            AddOptionDiagnostic(
                diagnostics,
                "DefineFunction2 register context requires target SWF 7 or newer.");
        }

        var temporaryRange = options.ReservedTemporaryRegisters;
        if (temporaryRange.IsEmpty)
            return;

        var context = CreateCodeUnitContext(options);
        if (temporaryRange.Last > byte.MaxValue)
        {
            AddOptionDiagnostic(
                diagnostics,
                $"Reserved temporary register range {temporaryRange.First}.." +
                $"{temporaryRange.Last} exceeds the byte register index limit.");
            return;
        }

        for (var register = (int)temporaryRange.First;
            register <= temporaryRange.Last;
            register++)
        {
            if (context.IsRegisterValid((byte)register))
                continue;

            AddOptionDiagnostic(
                diagnostics,
                $"Reserved temporary register range {temporaryRange.First}.." +
                $"{temporaryRange.Last} is outside the {context.ValidRegisterRange} " +
                $"range for {options.RegisterFile}.");
            return;
        }
    }

    private static void AddOptionDiagnostic(
        List<Avm1CompilerDiagnostic> diagnostics,
        string message) =>
        diagnostics.Add(new Avm1CompilerDiagnostic(
            "AVM1CMP010",
            Avm1CompilationDiagnosticSeverity.Error,
            SourceOriginIndex.Invalid,
            -1,
            message));

    private static Avm1CodeUnitContext CreateCodeUnitContext(
        Avm1CompilationOptions options)
    {
        var constantPool = Avm1ConstantPoolContext.Missing;
        return options.RegisterFile switch
        {
            Avm1CompilationRegisterFile.Legacy =>
                Avm1CodeUnitContext.Legacy(constantPool),
            Avm1CompilationRegisterFile.DefineFunction2 =>
                Avm1CodeUnitContext.DefineFunction2(
                    options.RegisterCount,
                    constantPool),
            _ => Avm1CodeUnitContext.Legacy(constantPool)
        };
    }

    private static Avm1MirMethod EmptyMir() =>
        new(
            instructions: [],
            blocks: [],
            blockLayout: [],
            entryBlock: Avm1MirBlockIndex.Invalid,
            lValues: [],
            callSites: [],
            callArguments: [],
            aggregateSites: [],
            aggregateValues: [],
            urlSites: [],
            functionSites: [],
            trySites: [],
            visibleControlScopes: [],
            withSites: [],
            withVisibleControlScopes: [],
            completionSites: [],
            phiSites: [],
            phiIncomings: [],
            controlTargets: [],
            temporaryCount: 0,
            constants: [],
            strings: [],
            valueCount: 0);

    private static bool HasErrors(List<Avm1CompilerDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);
}
