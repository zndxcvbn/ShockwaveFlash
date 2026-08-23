using System.Globalization;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation.Lowering;

internal readonly record struct Avm1MirCompletionSink(
    Avm1MirCompletionStorageKind Kind,
    string? ActivationName,
    byte Register)
{
    public static readonly Avm1MirCompletionSink None = new(
        Avm1MirCompletionStorageKind.None,
        null,
        0);

    public static Avm1MirCompletionSink Activation(string name) =>
        new(Avm1MirCompletionStorageKind.ActivationName, name, 0);

    public static Avm1MirCompletionSink PhysicalRegister(byte register) =>
        new(Avm1MirCompletionStorageKind.Register, null, register);
}

internal sealed record Avm1MirLoweringContext(
    string CompletionVariablePrefix,
    string TemporarySpillPrefix,
    Avm1MirCompletionSink CompletionSink,
    Avm1MirVisibleControlScope[] ExternalControlScopes,
    Avm1TemporaryRegisterRange CompletionRegisters,
    int CompletionRegisterCount,
    Avm1SourceRegisterPlan SourceRegisters,
    Avm1FunctionPreloadPlan FunctionPreloads,
    SourceSymbolIndex SelfBindingSymbol,
    byte CurrentFunctionArgumentsRegister,
    int CompletionRegisterDepth,
    int DynamicScopeDepth,
    bool InvokesEval,
    Avm1ProtectedRegionExitMode ProtectedRegionExitMode,
    Avm1ExpressionEvaluationMode ExpressionEvaluationMode)
{
    public static Avm1MirLoweringContext CreateRoot(
        Avm1SourceArena arena,
        Avm1TemporaryRegisterRange completionRegisters = default,
        int completionRegisterCount = 0,
        Avm1SourceRegisterPlan? sourceRegisters = null,
        Avm1FunctionPreloadPlan functionPreloads = default,
        SourceSymbolIndex? selfBindingSymbol = null,
        byte currentFunctionArgumentsRegister = 0,
        bool executesInDynamicScope = false,
        bool invokesEval = false,
        Avm1ProtectedRegionExitMode protectedRegionExitMode =
            Avm1ProtectedRegionExitMode.SemanticCompletion,
        Avm1ExpressionEvaluationMode expressionEvaluationMode =
            Avm1ExpressionEvaluationMode.Semantic)
    {
        return new Avm1MirLoweringContext(
            CreateUniquePrefix(arena, "__avm1_completion"),
            CreateUniquePrefix(arena, "__avm1_spill"),
            Avm1MirCompletionSink.None,
            [],
            completionRegisters,
            completionRegisterCount,
            sourceRegisters ?? Avm1SourceRegisterPlan.Empty,
            functionPreloads,
            selfBindingSymbol ?? SourceSymbolIndex.Invalid,
            currentFunctionArgumentsRegister,
            CompletionRegisterDepth: 0,
            DynamicScopeDepth: executesInDynamicScope ? 1 : 0,
            invokesEval,
            protectedRegionExitMode,
            expressionEvaluationMode);
    }

    public bool IsInDynamicScope => DynamicScopeDepth != 0;

    public bool CanUseActivationTemporarySpills =>
        !IsInDynamicScope && !InvokesEval;

    public bool TryGetCompletionRegister(out byte register)
    {
        var candidate = CompletionRegisters.First + CompletionRegisterDepth;
        if (CompletionRegisters.IsEmpty ||
            CompletionRegisterDepth >= CompletionRegisterCount ||
            candidate > CompletionRegisters.Last)
        {
            register = 0;
            return false;
        }

        register = checked((byte)candidate);
        return true;
    }

    private static string CreateUniquePrefix(
        Avm1SourceArena arena,
        string basePrefix)
    {
        var prefix = basePrefix;
        var suffix = 0;
        while (arena.Strings.Any(value =>
            value.Equals(prefix, StringComparison.Ordinal) ||
            value.StartsWith(prefix + "_", StringComparison.Ordinal)))
        {
            prefix = basePrefix + "_" + (++suffix).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return prefix;
    }
}

internal static class Avm1SourceToMirLowerer
{
    public static Avm1MirMethod Lower(
        Avm1SourceMethod method,
        List<Avm1CompilerDiagnostic> diagnostics,
        Avm1TimelineLayout? timelineLayout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Lower(
            method,
            Avm1MirLoweringContext.CreateRoot(method.Arena),
            diagnostics,
            timelineLayout,
            cancellationToken);
    }

    public static Avm1MirMethod Lower(
        Avm1SourceMethod method,
        Avm1MirLoweringContext context,
        List<Avm1CompilerDiagnostic> diagnostics,
        Avm1TimelineLayout? timelineLayout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var lowerer = new Lowerer(
            method,
            context,
            diagnostics,
            timelineLayout,
            cancellationToken);
        lowerer.LowerFunctionSelfBinding();
        lowerer.LowerStatement(method.Body);
        lowerer.CompletePendingBlocks();
        return lowerer.ToMethod();
    }

    private sealed class Lowerer
    {
        private readonly Avm1SourceArena _arena;
        private readonly Avm1MirLoweringContext _context;
        private readonly List<Avm1CompilerDiagnostic> _diagnostics;
        private readonly CancellationToken _cancellationToken;
        private readonly Avm1TimelineLayout? _timelineLayout;
        private readonly MirBuilder _builder = new();
        private readonly Avm1MirCompletionStorage _completionSink;
        private readonly List<ControlScope> _controlScopes = [];
        private readonly List<TargetScope> _targetScopes = [];
        private readonly List<PendingBlock> _pendingBlocks = [];
        private Avm1MirBlockIndex _externalCompletionExit =
            Avm1MirBlockIndex.Invalid;

        public Lowerer(
            Avm1SourceMethod method,
            Avm1MirLoweringContext context,
            List<Avm1CompilerDiagnostic> diagnostics,
            Avm1TimelineLayout? timelineLayout,
            CancellationToken cancellationToken)
        {
            _arena = method.Arena;
            _context = context;
            _diagnostics = diagnostics;
            _cancellationToken = cancellationToken;
            _timelineLayout = timelineLayout;
            _completionSink = context.CompletionSink.Kind switch
            {
                Avm1MirCompletionStorageKind.None =>
                    Avm1MirCompletionStorage.None,
                Avm1MirCompletionStorageKind.ActivationName
                    when context.CompletionSink.ActivationName is not null =>
                    Avm1MirCompletionStorage.Activation(
                        _builder.InternCompletionString(
                            context.CompletionSink.ActivationName)),
                Avm1MirCompletionStorageKind.Register =>
                    Avm1MirCompletionStorage.PhysicalRegister(
                        context.CompletionSink.Register),
                _ => throw new ArgumentException(
                    "Completion sink metadata is invalid.",
                    nameof(context))
            };
        }

        public Avm1MirMethod ToMethod() => _builder.ToMethod();

        public void LowerFunctionSelfBinding()
        {
            if (!_context.SelfBindingSymbol.IsValid)
                return;
            if (_context.CurrentFunctionArgumentsRegister == 0)
            {
                AddMalformed(
                    SourceOriginIndex.Invalid,
                    "Named function expression has no preloaded arguments register.");
                return;
            }
            if (!TryGetSymbolName(
                    _context.SelfBindingSymbol,
                    SourceOriginIndex.Invalid,
                    out var name))
            {
                return;
            }

            var argumentsStorage = _builder.AddRegisterLValue(
                SourceSymbolIndex.Invalid,
                _context.CurrentFunctionArgumentsRegister,
                SourceOriginIndex.Invalid);
            var currentFunction = _builder.AddValueInstruction(
                Avm1MirInstructionKind.LoadCurrentFunction,
                lValue: argumentsStorage,
                effects: Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow);
            var selfStorage = AddBoundSymbolLValue(
                _context.SelfBindingSymbol,
                name,
                SourceOriginIndex.Invalid);
            _builder.AddInstruction(
                Avm1MirInstructionKind.DeclareLocal,
                currentFunction,
                lValue: selfStorage,
                name: name,
                nameKind: Avm1MirNameKind.BoundSymbol,
                effects: GetWriteEffects(_builder.GetLValue(selfStorage)));
        }

        public void CompletePendingBlocks()
        {
            if (_pendingBlocks.Count == 0 && !_externalCompletionExit.IsValid)
                return;

            var finalBlock = _externalCompletionExit;
            if (_builder.CanAppend)
            {
                if (!finalBlock.IsValid)
                    finalBlock = _builder.CreateBlock();
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: finalBlock);
            }

            foreach (var pending in _pendingBlocks)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _builder.StartBlock(pending.Block, isReachable: true);
                if (pending.Kind is PendingBlockKind.EnumerationCleanup)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.EndEnumeration,
                        effects: Avm1MirEffect.ControlFlow,
                        target: pending.Target,
                        origin: pending.Origin);
                    continue;
                }

                var value = pending.Temporary.IsValid
                    ? _builder.AddValueInstruction(
                        Avm1MirInstructionKind.LoadTemporary,
                        temporary: pending.Temporary,
                        origin: pending.Origin)
                    : Avm1MirValueIndex.Invalid;
                _builder.AddInstruction(
                    pending.Kind is PendingBlockKind.Return
                        ? Avm1MirInstructionKind.Return
                        : Avm1MirInstructionKind.Throw,
                    operand: value,
                    effects: Avm1MirEffect.Completion |
                        (pending.Kind is PendingBlockKind.Throw
                            ? Avm1MirEffect.MayThrow
                            : Avm1MirEffect.None),
                    origin: pending.Origin);
                if (pending.Temporary.IsValid)
                    _builder.ReleaseTemporary(pending.Temporary);
            }

            if (finalBlock.IsValid)
                _builder.StartBlock(finalBlock, isReachable: true);
        }

        public void LowerStatement(SourceStatementIndex index)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_builder.CanAppend)
                return;
            if (!TryGetStatement(index, out var statement))
                return;

            switch (statement.Kind)
            {
                case Avm1SourceStatementKind.Block:
                    for (var i = 0; i < statement.Children.Count; i++)
                        LowerStatement(_arena.GetChild(statement, i));
                    return;

                case Avm1SourceStatementKind.Expression:
                    {
                        var value = LowerExpression(
                            statement.Expression,
                            resultRequired: false);
                        if (value.IsValid)
                        {
                            _builder.AddInstruction(
                                Avm1MirInstructionKind.Discard,
                                value,
                                origin: statement.Origin);
                        }
                        return;
                    }

                case Avm1SourceStatementKind.VariableDeclaration:
                    {
                        if (!TryGetSymbolName(
                            statement.Symbol,
                            statement.Origin,
                            out var name))
                        {
                            return;
                        }

                        var initializer = statement.Expression.IsValid
                            ? LowerExpression(statement.Expression)
                            : Avm1MirValueIndex.Invalid;
                        if (statement.Expression.IsValid && !initializer.IsValid)
                            return;

                        var lValue = AddBoundSymbolLValue(
                            statement.Symbol,
                            name,
                            statement.Origin);
                        var storage = _builder.GetLValue(lValue);
                        if (storage.Kind is Avm1MirLValueKind.Register)
                        {
                            if (!initializer.IsValid)
                                initializer = AddUndefinedConstant(statement.Origin);
                            _builder.AddInstruction(
                                Avm1MirInstructionKind.DeclareLocal,
                                initializer,
                                lValue: lValue,
                                effects: Avm1MirEffect.None,
                                origin: statement.Origin);
                        }
                        else
                        {
                            _builder.AddInstruction(
                                Avm1MirInstructionKind.DeclareLocal,
                                initializer,
                                lValue: lValue,
                                name: name,
                                nameKind: Avm1MirNameKind.BoundSymbol,
                                effects: Avm1MirEffect.WritesActivation,
                                origin: statement.Origin);
                        }
                        return;
                    }

                case Avm1SourceStatementKind.FunctionDeclaration:
                    LowerFunctionDeclaration(statement);
                    return;

                case Avm1SourceStatementKind.Return:
                    LowerReturn(statement);
                    return;

                case Avm1SourceStatementKind.Throw:
                    LowerThrow(statement);
                    return;

                case Avm1SourceStatementKind.Try:
                    LowerTry(statement);
                    return;

                case Avm1SourceStatementKind.With:
                    LowerWith(statement);
                    return;

                case Avm1SourceStatementKind.TellTarget:
                    LowerTellTarget(statement);
                    return;

                case Avm1SourceStatementKind.If:
                    LowerIf(statement);
                    return;

                case Avm1SourceStatementKind.IfFrameLoaded:
                    LowerIfFrameLoaded(statement);
                    return;

                case Avm1SourceStatementKind.While:
                    LowerWhile(statement);
                    return;

                case Avm1SourceStatementKind.DoWhile:
                    LowerDoWhile(statement);
                    return;

                case Avm1SourceStatementKind.For:
                    LowerFor(statement);
                    return;

                case Avm1SourceStatementKind.ForIn:
                    LowerForIn(statement);
                    return;

                case Avm1SourceStatementKind.Switch:
                    LowerSwitch(statement);
                    return;

                case Avm1SourceStatementKind.Break:
                case Avm1SourceStatementKind.Continue:
                    LowerControlJump(statement);
                    return;

                default:
                    AddDiagnostic(
                        "AVM1CMP100",
                        statement.Origin,
                        $"Source statement {statement.Kind} is not supported by " +
                        "the current method backend.");
                    return;
            }
        }

        private void LowerFunctionDeclaration(Avm1SourceStatement statement)
        {
            if (!statement.Expression.IsValid ||
                !TryGetExpression(statement.Expression, out var expression) ||
                expression.Kind is not Avm1SourceExpressionKind.FunctionLiteral ||
                !TryGetFunction(expression, out var function))
            {
                AddMalformed(
                    statement.Origin,
                    "Function declaration has no valid function literal.");
                return;
            }
            if (!function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration))
            {
                AddMalformed(
                    statement.Origin,
                    "Function declaration references a non-declaration function.");
                return;
            }
            if (!statement.Symbol.IsValid ||
                function.NameSymbol != statement.Symbol ||
                !TryGetSymbolName(
                    statement.Symbol,
                    statement.Origin,
                    out var name))
            {
                AddMalformed(
                    statement.Origin,
                    "Function declaration symbol does not match its function name.");
                return;
            }

            var site = _builder.AddFunctionSite(
                function.Index,
                name,
                isDeclaration: true,
                statement.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.DefineFunction,
                functionSite: site,
                effects: Avm1MirEffect.WritesActivation |
                    Avm1MirEffect.Allocates,
                origin: statement.Origin);
        }

        private void LowerReturn(Avm1SourceStatement statement)
        {
            var value = statement.Expression.IsValid
                ? LowerExpression(statement.Expression)
                : Avm1MirValueIndex.Invalid;
            if (statement.Expression.IsValid && !value.IsValid)
                return;

            if (_targetScopes.Count != 0)
                EmitTargetRestore(0, statement.Origin);

            var enumerationCount = CountActiveEnumerationScopes();
            if (enumerationCount == 0)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Return,
                    value,
                    effects: Avm1MirEffect.Completion,
                    origin: statement.Origin);
                return;
            }

            var returnTemporary = Avm1MirTemporaryIndex.Invalid;
            if (value.IsValid)
            {
                returnTemporary = _builder.AcquireTemporary();
                _builder.AddInstruction(
                    Avm1MirInstructionKind.StoreTemporary,
                    operand: value,
                    temporary: returnTemporary,
                    origin: statement.Origin);
            }

            var returnBlock = _builder.CreateBlock();
            _pendingBlocks.Add(PendingBlock.Return(
                returnBlock,
                returnTemporary,
                statement.Origin));
            var cleanupBlock = CreateEnumerationCleanupChain(
                enumerationCount,
                returnBlock,
                statement.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                effects: Avm1MirEffect.ControlFlow | Avm1MirEffect.Completion,
                target: cleanupBlock,
                origin: statement.Origin);
        }

        private void LowerThrow(Avm1SourceStatement statement)
        {
            if (!statement.Expression.IsValid)
            {
                AddMalformed(statement.Origin, "Throw statement has no expression.");
                return;
            }

            var value = LowerExpression(statement.Expression);
            if (!value.IsValid)
                return;

            if (_targetScopes.Count != 0)
                EmitTargetRestore(0, statement.Origin);

            var enumerationCount = CountActiveEnumerationScopes();
            if (enumerationCount == 0)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Throw,
                    operand: value,
                    effects: Avm1MirEffect.Completion | Avm1MirEffect.MayThrow,
                    origin: statement.Origin);
                return;
            }

            var throwTemporary = _builder.AcquireTemporary();
            _builder.AddInstruction(
                Avm1MirInstructionKind.StoreTemporary,
                operand: value,
                temporary: throwTemporary,
                origin: statement.Origin);

            var throwBlock = _builder.CreateBlock();
            _pendingBlocks.Add(PendingBlock.Throw(
                throwBlock,
                throwTemporary,
                statement.Origin));
            var cleanupBlock = CreateEnumerationCleanupChain(
                enumerationCount,
                throwBlock,
                statement.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                effects: Avm1MirEffect.ControlFlow | Avm1MirEffect.Completion,
                target: cleanupBlock,
                origin: statement.Origin);
        }

        private void LowerTry(Avm1SourceStatement statement)
        {
            if (!TryGetTryComponents(
                statement,
                out var tryBody,
                out var catchBody,
                out var finallyBody,
                out var catchSymbol))
            {
                return;
            }

            var visibleControlScopes = GetVisibleControlScopes();
            var usePhysicalParentBranches =
                _context.ProtectedRegionExitMode is
                    Avm1ProtectedRegionExitMode.AdobePhysicalBranch &&
                !_context.IsInDynamicScope &&
                visibleControlScopes.Length != 0 &&
                !visibleControlScopes.Any(scope => scope.IsEnumeration) &&
                !ContainsWith(tryBody) &&
                (!catchBody.IsValid || !ContainsWith(catchBody)) &&
                (!finallyBody.IsValid || !ContainsWith(finallyBody));
            var completionStorage = Avm1MirCompletionStorage.None;
            if (!usePhysicalParentBranches &&
                !TryCreateCompletionStorage(
                    statement,
                    visibleControlScopes,
                    requiresRegister: _context.IsInDynamicScope,
                    out completionStorage))
            {
                return;
            }
            InitializeCompletionStorage(completionStorage, statement.Origin);

            if (usePhysicalParentBranches)
            {
                foreach (var scope in _controlScopes)
                {
                    scope.HasBreakEdge = true;
                    if (scope.ContinueBlock.IsValid)
                        scope.HasContinueEdge = true;
                    _builder.MarkPhysicalControlTarget(scope.Target);
                }
            }

            var site = _builder.AddTrySite(
                statement.Index,
                tryBody,
                catchBody,
                finallyBody,
                catchSymbol,
                completionStorage,
                visibleControlScopes,
                usePhysicalParentBranches,
                statement.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Try,
                trySite: site,
                effects: Avm1MirEffect.ReadsActivation |
                    Avm1MirEffect.WritesActivation |
                    Avm1MirEffect.ReadsDynamicScope |
                    Avm1MirEffect.WritesDynamicScope |
                    Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.WritesHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow |
                    Avm1MirEffect.Completion |
                    Avm1MirEffect.ControlFlow,
                origin: statement.Origin);

            if (completionStorage.Kind is not Avm1MirCompletionStorageKind.None)
                LowerCompletionDispatch(completionStorage, statement.Origin);
        }

        private bool ContainsWith(SourceStatementIndex statementIndex)
        {
            if (!statementIndex.IsValid ||
                statementIndex.Value >= _arena.Statements.Count)
            {
                return false;
            }

            var statement = _arena[statementIndex];
            if (statement.Kind is Avm1SourceStatementKind.With)
                return true;
            for (var i = 0; i < statement.Initializers.Count; i++)
            {
                if (ContainsWith(_arena.GetInitializer(statement, i)))
                    return true;
            }
            for (var i = 0; i < statement.Children.Count; i++)
            {
                if (ContainsWith(_arena.GetChild(statement, i)))
                    return true;
            }
            return false;
        }

        private void LowerWith(Avm1SourceStatement statement)
        {
            if (!TryGetWithBody(statement, out var body))
                return;

            var visibleControlScopes = GetVisibleControlScopes();
            if (!TryCreateCompletionStorage(
                    statement,
                    visibleControlScopes,
                    requiresRegister: true,
                    out var completionStorage))
            {
                return;
            }
            InitializeCompletionStorage(completionStorage, statement.Origin);

            var scopeObject = LowerExpression(statement.Expression);
            if (!scopeObject.IsValid)
                return;

            var site = _builder.AddWithSite(
                statement.Index,
                body,
                completionStorage,
                visibleControlScopes,
                statement.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.With,
                operand: scopeObject,
                withSite: site,
                effects: Avm1MirEffect.ReadsActivation |
                    Avm1MirEffect.WritesActivation |
                    Avm1MirEffect.ReadsDynamicScope |
                    Avm1MirEffect.WritesDynamicScope |
                    Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.WritesHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow |
                    Avm1MirEffect.Completion |
                    Avm1MirEffect.ControlFlow,
                origin: statement.Origin);

            if (completionStorage.Kind is not Avm1MirCompletionStorageKind.None)
                LowerCompletionDispatch(completionStorage, statement.Origin);
        }

        private void LowerTellTarget(Avm1SourceStatement statement)
        {
            if (!statement.Expression.IsValid ||
                statement.SecondaryExpression.IsValid ||
                statement.Initializers.Count != 0 ||
                statement.Expressions.Count != 0 ||
                statement.Children.Count != 1)
            {
                AddMalformed(
                    statement.Origin,
                    "TellTarget statement has an invalid structural payload.");
                return;
            }

            var body = _arena.GetChild(statement, 0);
            if (!TryGetStatement(body, out _))
                return;

            TargetScope scope;
            if (TryGetStringLiteral(statement.Expression, out var literalTarget))
            {
                scope = TargetScope.Immediate(literalTarget);
            }
            else
            {
                var target = LowerExpression(statement.Expression);
                if (!target.IsValid)
                    return;

                var temporary = _builder.AcquireTemporary();
                _builder.AddInstruction(
                    Avm1MirInstructionKind.StoreTemporary,
                    operand: target,
                    temporary: temporary,
                    origin: statement.Origin);
                scope = TargetScope.Dynamic(temporary);
            }

            EmitTarget(scope, statement.Origin);
            var restoreDepth = _targetScopes.Count;
            _targetScopes.Add(scope);
            try
            {
                LowerStatement(body);
                if (_builder.CanAppend)
                    EmitTargetRestore(restoreDepth, statement.Origin);
            }
            finally
            {
                _targetScopes.RemoveAt(_targetScopes.Count - 1);
                if (scope.Temporary.IsValid)
                    _builder.ReleaseTemporary(scope.Temporary);
            }
        }

        private void EmitTargetRestore(
            int targetDepth,
            SourceOriginIndex origin)
        {
            if (targetDepth < 0 || targetDepth > _targetScopes.Count)
                throw new ArgumentOutOfRangeException(nameof(targetDepth));

            if (targetDepth == 0)
            {
                EmitTarget(TargetScope.Immediate(string.Empty), origin);
                return;
            }

            EmitTarget(_targetScopes[targetDepth - 1], origin);
        }

        private void EmitTarget(TargetScope scope, SourceOriginIndex origin)
        {
            const Avm1MirEffect effects =
                Avm1MirEffect.WritesDynamicScope |
                Avm1MirEffect.WritesRuntimeState;
            if (scope.ImmediateTarget is not null)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.SetTargetImmediate,
                    name: scope.ImmediateTarget,
                    effects: effects,
                    origin: origin);
                return;
            }

            var target = _builder.AddValueInstruction(
                Avm1MirInstructionKind.LoadTemporary,
                temporary: scope.Temporary,
                origin: origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.SetTarget,
                operand: target,
                effects: effects,
                origin: origin);
        }

        private Avm1MirVisibleControlScope[] GetVisibleControlScopes()
        {
            var result = new Avm1MirVisibleControlScope[
                _controlScopes.Count + _context.ExternalControlScopes.Length];
            var resultIndex = 0;
            for (var i = _controlScopes.Count - 1; i >= 0; i--)
            {
                var scope = _controlScopes[i];
                result[resultIndex++] = new Avm1MirVisibleControlScope(
                    scope.Statement,
                    scope.ContinueBlock.IsValid
                        ? Avm1MirControlTargetKind.Loop
                        : Avm1MirControlTargetKind.Switch,
                    scope.Label,
                    scope.IsEnumeration);
            }
            foreach (var scope in _context.ExternalControlScopes)
                result[resultIndex++] = scope;
            return result;
        }

        private bool TryCreateCompletionStorage(
            Avm1SourceStatement statement,
            Avm1MirVisibleControlScope[] visibleControlScopes,
            bool requiresRegister,
            out Avm1MirCompletionStorage storage)
        {
            storage = Avm1MirCompletionStorage.None;
            if (visibleControlScopes.Length == 0)
                return true;

            if (requiresRegister)
            {
                if (!_context.TryGetCompletionRegister(out var register))
                {
                    AddDiagnostic(
                        "AVM1CMP122",
                        statement.Origin,
                        $"{statement.Kind} requires a planned completion register " +
                        "to transport break/continue across its dynamic-scope " +
                        "code-unit boundary.");
                    return false;
                }

                storage = Avm1MirCompletionStorage.PhysicalRegister(register);
                return true;
            }

            var variable = _context.CompletionVariablePrefix + "_" +
                statement.Index.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            storage = Avm1MirCompletionStorage.Activation(
                _builder.InternCompletionString(variable));
            return true;
        }

        private void InitializeCompletionStorage(
            Avm1MirCompletionStorage storage,
            SourceOriginIndex origin)
        {
            if (storage.Kind is Avm1MirCompletionStorageKind.None)
                return;

            if (storage.Kind is Avm1MirCompletionStorageKind.Register)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.InitializePendingCompletion,
                    completionStorage: storage,
                    origin: origin);
                return;
            }

            var zero = AddIntegerConstant(0, origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.DeclareLocal,
                operand: zero,
                name: _builder.GetCompletionString(storage),
                nameKind: Avm1MirNameKind.BoundSymbol,
                effects: Avm1MirEffect.WritesActivation,
                origin: origin);
        }

        private void LowerCompletionDispatch(
            Avm1MirCompletionStorage storage,
            SourceOriginIndex origin)
        {
            for (var i = _controlScopes.Count - 1; i >= 0; i--)
            {
                var scope = _controlScopes[i];
                LowerLocalCompletionDispatch(storage, scope, isBreak: true, origin);
                if (scope.ContinueBlock.IsValid)
                {
                    LowerLocalCompletionDispatch(
                        storage,
                        scope,
                        isBreak: false,
                        origin);
                }
            }

            foreach (var scope in _context.ExternalControlScopes)
            {
                LowerExternalCompletionDispatch(
                    storage,
                    scope,
                    Avm1MirCompletionKind.Break,
                    origin);
                if (scope.Kind is Avm1MirControlTargetKind.Loop)
                {
                    LowerExternalCompletionDispatch(
                        storage,
                        scope,
                        Avm1MirCompletionKind.Continue,
                        origin);
                }
            }
        }

        private void LowerLocalCompletionDispatch(
            Avm1MirCompletionStorage storage,
            ControlScope scope,
            bool isBreak,
            SourceOriginIndex origin)
        {
            var continuation = _builder.CreateBlock();
            var condition = AddCompletionTokenComparison(
                storage,
                GetCompletionToken(
                    scope.Statement,
                    isBreak
                        ? Avm1MirCompletionKind.Break
                        : Avm1MirCompletionKind.Continue),
                origin);
            var target = GetLocalCompletionTarget(scope, isBreak, origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.BranchIfTrue,
                operand: condition,
                effects: Avm1MirEffect.ControlFlow,
                target: target,
                alternativeTarget: continuation,
                origin: origin);
            _builder.StartBlock(continuation, isReachable: true);
        }

        private void LowerExternalCompletionDispatch(
            Avm1MirCompletionStorage storage,
            Avm1MirVisibleControlScope scope,
            Avm1MirCompletionKind kind,
            SourceOriginIndex origin)
        {
            if (_completionSink.Kind is Avm1MirCompletionStorageKind.None)
            {
                AddMalformed(
                    origin,
                    "An inherited completion target has no enclosing try sink.");
                return;
            }

            var propagation = _builder.CreateBlock();
            var continuation = _builder.CreateBlock();
            var token = GetCompletionToken(scope.Statement, kind);
            var condition = AddCompletionTokenComparison(storage, token, origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.BranchIfTrue,
                operand: condition,
                effects: Avm1MirEffect.ControlFlow,
                target: propagation,
                alternativeTarget: continuation,
                origin: origin);

            _builder.StartBlock(propagation, isReachable: true);
            EmitPendingCompletion(
                _completionSink,
                scope.Statement,
                kind,
                origin);
            BranchToExternalCompletionExit(origin);

            _builder.StartBlock(continuation, isReachable: true);
        }

        private Avm1MirValueIndex AddCompletionTokenComparison(
            Avm1MirCompletionStorage storage,
            int token,
            SourceOriginIndex origin)
        {
            var current = _builder.AddValueInstruction(
                Avm1MirInstructionKind.LoadPendingCompletion,
                completionStorage: storage,
                effects: storage.Kind is Avm1MirCompletionStorageKind.ActivationName
                    ? Avm1MirEffect.ReadsActivation
                    : Avm1MirEffect.None,
                origin: origin);
            var expected = AddIntegerConstant(token, origin);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Binary,
                operand: current,
                secondaryOperand: expected,
                @operator: Avm1MirOperator.StrictEqual,
                origin: origin);
        }

        private bool TryGetWithBody(
            Avm1SourceStatement statement,
            out SourceStatementIndex body)
        {
            body = SourceStatementIndex.Invalid;
            if (!statement.Expression.IsValid ||
                statement.SecondaryExpression.IsValid ||
                statement.Symbol.IsValid ||
                statement.Name.IsValid ||
                statement.Label.IsValid ||
                statement.Opaque.IsValid ||
                statement.Flags is not Avm1SourceStatementFlags.None ||
                statement.Initializers.Count != 0 ||
                statement.Expressions.Count != 0 ||
                statement.Children.Count != 1)
            {
                AddMalformed(
                    statement.Origin,
                    "With statement has an invalid structural payload.");
                return false;
            }

            body = _arena.GetChild(statement, 0);
            return TryGetStatement(body, out _);
        }

        private Avm1MirValueIndex AddIntegerConstant(
            int value,
            SourceOriginIndex origin)
        {
            var constant = _builder.AddConstant(
                Avm1MirConstantKind.Integer,
                booleanValue: false,
                integerValue: value,
                numberValue: 0,
                stringValue: null);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Constant,
                constant: constant,
                origin: origin);
        }

        private Avm1MirValueIndex AddUndefinedConstant(SourceOriginIndex origin)
        {
            var constant = _builder.AddConstant(
                Avm1MirConstantKind.Undefined,
                booleanValue: false,
                integerValue: 0,
                numberValue: 0,
                stringValue: null);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Constant,
                constant: constant,
                origin: origin);
        }

        private Avm1MirValueIndex AddStringConstant(
            string value,
            SourceOriginIndex origin)
        {
            var constant = _builder.AddConstant(
                Avm1MirConstantKind.String,
                booleanValue: false,
                integerValue: 0,
                numberValue: 0,
                stringValue: value);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Constant,
                constant: constant,
                origin: origin);
        }

        private bool TryGetTryComponents(
            Avm1SourceStatement statement,
            out SourceStatementIndex tryBody,
            out SourceStatementIndex catchBody,
            out SourceStatementIndex finallyBody,
            out SourceSymbolIndex catchSymbol)
        {
            tryBody = SourceStatementIndex.Invalid;
            catchBody = SourceStatementIndex.Invalid;
            finallyBody = SourceStatementIndex.Invalid;
            catchSymbol = SourceSymbolIndex.Invalid;

            if (statement.Expression.IsValid ||
                statement.SecondaryExpression.IsValid ||
                statement.Symbol.IsValid ||
                statement.Name.IsValid ||
                statement.Label.IsValid ||
                statement.Opaque.IsValid ||
                statement.Flags is not Avm1SourceStatementFlags.None ||
                statement.Initializers.Count != 0 ||
                statement.Expressions.Count != 0 ||
                statement.Children.Count is < 2 or > 3)
            {
                AddMalformed(
                    statement.Origin,
                    "Try statement has an invalid structural payload.");
                return false;
            }

            tryBody = _arena.GetChild(statement, 0);
            if (!TryGetStatement(tryBody, out _))
                return false;

            var componentIndex = 1;
            if (!TryGetStatement(
                _arena.GetChild(statement, componentIndex),
                out var component))
            {
                return false;
            }

            if (component.Kind is Avm1SourceStatementKind.CatchClause)
            {
                if (!TryGetTryClauseBody(component, expectsSymbol: true, out catchBody))
                    return false;
                if (!TryGetSymbolName(component.Symbol, component.Origin, out _) ||
                    _arena[component.Symbol].Kind is not Avm1SourceSymbolKind.Catch)
                {
                    AddMalformed(
                        component.Origin,
                        "Catch clause must reference a named catch symbol.");
                    return false;
                }

                catchSymbol = component.Symbol;
                componentIndex++;
            }

            if (componentIndex < statement.Children.Count)
            {
                if (!TryGetStatement(
                    _arena.GetChild(statement, componentIndex),
                    out component) ||
                    component.Kind is not Avm1SourceStatementKind.FinallyClause ||
                    !TryGetTryClauseBody(component, expectsSymbol: false, out finallyBody))
                {
                    AddMalformed(
                        statement.Origin,
                        "Try statement has an invalid finally clause.");
                    return false;
                }
                componentIndex++;
            }

            if (componentIndex != statement.Children.Count ||
                (!catchBody.IsValid && !finallyBody.IsValid))
            {
                AddMalformed(
                    statement.Origin,
                    "Try statement requires a catch or finally clause in canonical order.");
                return false;
            }

            return true;
        }

        private bool TryGetTryClauseBody(
            Avm1SourceStatement clause,
            bool expectsSymbol,
            out SourceStatementIndex body)
        {
            body = SourceStatementIndex.Invalid;
            if (clause.Expression.IsValid ||
                clause.SecondaryExpression.IsValid ||
                clause.Name.IsValid ||
                clause.Label.IsValid ||
                clause.Opaque.IsValid ||
                clause.Flags is not Avm1SourceStatementFlags.None ||
                clause.Initializers.Count != 0 ||
                clause.Expressions.Count != 0 ||
                clause.Children.Count != 1 ||
                clause.Symbol.IsValid != expectsSymbol)
            {
                AddMalformed(clause.Origin, $"{clause.Kind} has an invalid payload.");
                return false;
            }

            body = _arena.GetChild(clause, 0);
            return TryGetStatement(body, out _);
        }

        private void LowerIf(Avm1SourceStatement statement)
        {
            if (!statement.Expression.IsValid)
            {
                AddMalformed(statement.Origin, "If statement has no condition expression.");
                return;
            }
            if (statement.Children.Count is < 1 or > 2)
            {
                AddMalformed(
                    statement.Origin,
                    "If statement must contain a then branch and at most one else branch.");
                return;
            }

            var thenBlock = _builder.CreateBlock();
            var elseBlock = statement.Children.Count == 2
                ? _builder.CreateBlock()
                : Avm1MirBlockIndex.Invalid;
            var mergeBlock = _builder.CreateBlock();
            if (!LowerCondition(
                    statement.Expression,
                    thenBlock,
                    elseBlock.IsValid ? elseBlock : mergeBlock,
                    statement.Origin))
            {
                return;
            }

            _builder.StartBlock(thenBlock, isReachable: true);
            LowerStatement(_arena.GetChild(statement, 0));
            var thenFallsThrough = _builder.CanAppend;
            if (thenFallsThrough)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: mergeBlock,
                    origin: statement.Origin);
            }

            var elseFallsThrough = statement.Children.Count == 1;
            if (elseBlock.IsValid)
            {
                _builder.StartBlock(elseBlock, isReachable: true);
                LowerStatement(_arena.GetChild(statement, 1));
                elseFallsThrough = _builder.CanAppend;
                if (elseFallsThrough)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.Branch,
                        effects: Avm1MirEffect.ControlFlow,
                        target: mergeBlock,
                        origin: statement.Origin);
                }
            }

            _builder.StartBlock(
                mergeBlock,
                isReachable: thenFallsThrough || elseFallsThrough);
        }

        private void LowerIfFrameLoaded(Avm1SourceStatement statement)
        {
            if (!statement.Expression.IsValid || statement.Children.Count != 1)
            {
                AddMalformed(
                    statement.Origin,
                    "ifFrameLoaded requires one body and one frame argument.");
                return;
            }

            var frame = statement.SecondaryExpression.IsValid
                ? statement.SecondaryExpression
                : statement.Expression;
            var immediateFrame = -1;
            if (statement.SecondaryExpression.IsValid)
            {
                if (!TryResolveSceneFrameTarget(
                        statement.Expression,
                        frame,
                        statement.Origin,
                        out immediateFrame))
                {
                    return;
                }
            }
            else if (TryGetImmediateTimelineTarget(
                         frame,
                         out var frameIndex,
                         out var label) &&
                     label is null)
            {
                immediateFrame = frameIndex;
            }

            var frameValue = immediateFrame >= 0
                ? Avm1MirValueIndex.Invalid
                : LowerExpression(frame);
            if (immediateFrame < 0 && !frameValue.IsValid)
                return;

            var bodyBlock = _builder.CreateBlock();
            var mergeBlock = _builder.CreateBlock();
            var frameConstant = immediateFrame >= 0
                ? _builder.AddConstant(
                    Avm1MirConstantKind.Integer,
                    booleanValue: false,
                    integerValue: immediateFrame,
                    numberValue: 0,
                    stringValue: null)
                : Avm1MirConstantIndex.Invalid;
            _builder.AddInstruction(
                immediateFrame >= 0
                    ? Avm1MirInstructionKind.WaitForFrameImmediate
                    : Avm1MirInstructionKind.WaitForFrame,
                operand: frameValue,
                constant: frameConstant,
                effects: Avm1MirEffect.ReadsRuntimeState |
                    Avm1MirEffect.ControlFlow,
                target: bodyBlock,
                alternativeTarget: mergeBlock,
                origin: statement.Origin);

            _builder.StartBlock(bodyBlock, isReachable: true);
            LowerStatement(_arena.GetChild(statement, 0));
            if (_builder.CanAppend)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: mergeBlock,
                    origin: statement.Origin);
            }

            _builder.StartBlock(mergeBlock, isReachable: true);
        }

        private bool TryResolveSceneFrameTarget(
            SourceExpressionIndex sceneExpression,
            SourceExpressionIndex frameExpression,
            SourceOriginIndex origin,
            out int frameIndex)
        {
            frameIndex = -1;
            if (_timelineLayout is null)
            {
                AddMalformed(
                    origin,
                    "ifFrameLoaded scene overload requires timeline scene-layout metadata.");
                return false;
            }
            if (!TryGetStringLiteral(sceneExpression, out var sceneName))
            {
                AddMalformed(
                    origin,
                    "ifFrameLoaded scene name must be a string literal.");
                return false;
            }
            if (!TryGetImmediateTimelineTarget(
                    frameExpression,
                    out var relativeFrame,
                    out var label))
            {
                AddMalformed(
                    origin,
                    "ifFrameLoaded scene frame must be a numeric literal or frame-label literal.");
                return false;
            }

            uint resolved;
            var found = label is null
                ? _timelineLayout.TryResolveFrame(
                    sceneName,
                    checked((uint)relativeFrame + 1),
                    out resolved)
                : _timelineLayout.TryResolveFrameLabel(
                    sceneName,
                    label,
                    out resolved);
            if (!found)
            {
                AddMalformed(
                    origin,
                    $"Timeline scene/frame target {sceneName} is missing, ambiguous, or outside the scene.");
                return false;
            }
            if (resolved > ushort.MaxValue)
            {
                AddMalformed(
                    origin,
                    $"Resolved timeline frame {resolved} exceeds the ActionWaitForFrame range.");
                return false;
            }

            frameIndex = checked((int)resolved);
            return true;
        }

        private void LowerWhile(Avm1SourceStatement statement)
        {
            if (!TryValidateLoop(statement, requiresCondition: true, out var labelName))
                return;

            var conditionBlock = _builder.CreateBlock();
            var bodyBlock = _builder.CreateBlock();
            var exitBlock = _builder.CreateBlock();
            var scope = CreateLoopScope(
                statement,
                exitBlock,
                conditionBlock,
                labelName);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                effects: Avm1MirEffect.ControlFlow,
                target: conditionBlock,
                origin: statement.Origin);

            _builder.StartBlock(conditionBlock, isReachable: true);
            var conditionIsValid = LowerCondition(
                statement.Expression,
                bodyBlock,
                exitBlock,
                statement.Origin);

            _builder.StartBlock(bodyBlock, isReachable: conditionIsValid);
            LowerControlScopeBody(statement, scope);
            if (_builder.CanAppend)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: conditionBlock,
                    origin: statement.Origin);
            }

            _builder.StartBlock(exitBlock, isReachable: conditionIsValid);
        }

        private void LowerDoWhile(Avm1SourceStatement statement)
        {
            if (!TryValidateLoop(statement, requiresCondition: true, out var labelName))
                return;

            var bodyBlock = _builder.CreateBlock();
            var conditionBlock = _builder.CreateBlock();
            var exitBlock = _builder.CreateBlock();
            var scope = CreateLoopScope(
                statement,
                exitBlock,
                conditionBlock,
                labelName);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                effects: Avm1MirEffect.ControlFlow,
                target: bodyBlock,
                origin: statement.Origin);

            _builder.StartBlock(bodyBlock, isReachable: true);
            LowerControlScopeBody(statement, scope);
            var bodyFallsThrough = _builder.CanAppend;
            if (bodyFallsThrough)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: conditionBlock,
                    origin: statement.Origin);
            }

            var conditionReachable = bodyFallsThrough || scope.HasContinueEdge;
            _builder.StartBlock(conditionBlock, isReachable: conditionReachable);
            var conditionIsValid = false;
            if (conditionReachable)
            {
                conditionIsValid = LowerCondition(
                    statement.Expression,
                    bodyBlock,
                    exitBlock,
                    statement.Origin);
            }

            _builder.StartBlock(
                exitBlock,
                isReachable: scope.HasBreakEdge ||
                    conditionReachable && conditionIsValid);
        }

        private void LowerFor(Avm1SourceStatement statement)
        {
            if (!TryValidateLoop(statement, requiresCondition: false, out var labelName) ||
                !TryCollectForClauses(
                    statement,
                    out var initializers,
                    out var updates))
            {
                return;
            }

            foreach (var initializer in initializers)
            {
                LowerStatement(initializer);
                if (!_builder.CanAppend)
                    return;
            }

            var conditionBlock = _builder.CreateBlock();
            var bodyBlock = _builder.CreateBlock();
            var updateBlock = updates.Length == 0
                ? Avm1MirBlockIndex.Invalid
                : _builder.CreateBlock();
            var exitBlock = _builder.CreateBlock();
            var continueBlock = updateBlock.IsValid ? updateBlock : conditionBlock;
            var scope = CreateLoopScope(
                statement,
                exitBlock,
                continueBlock,
                labelName);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                effects: Avm1MirEffect.ControlFlow,
                target: conditionBlock,
                origin: statement.Origin);

            _builder.StartBlock(conditionBlock, isReachable: true);
            if (statement.Expression.IsValid)
            {
                LowerCondition(
                    statement.Expression,
                    bodyBlock,
                    exitBlock,
                    statement.Origin);
            }
            else
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: bodyBlock,
                    origin: statement.Origin);
            }

            _builder.StartBlock(bodyBlock, isReachable: true);
            LowerControlScopeBody(statement, scope);
            var bodyFallsThrough = _builder.CanAppend;
            if (bodyFallsThrough)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: continueBlock,
                    origin: statement.Origin);
            }

            if (updateBlock.IsValid)
            {
                var updateReachable = bodyFallsThrough || scope.HasContinueEdge;
                _builder.StartBlock(updateBlock, isReachable: updateReachable);
                if (updateReachable)
                {
                    foreach (var update in updates)
                        LowerDiscardedExpression(update, statement.Origin);
                    if (_builder.CanAppend)
                    {
                        _builder.AddInstruction(
                            Avm1MirInstructionKind.Branch,
                            effects: Avm1MirEffect.ControlFlow,
                            target: conditionBlock,
                            origin: statement.Origin);
                    }
                }
            }

            _builder.StartBlock(
                exitBlock,
                isReachable: statement.Expression.IsValid || scope.HasBreakEdge);
        }

        private bool LowerCondition(
            SourceExpressionIndex index,
            Avm1MirBlockIndex whenTrue,
            Avm1MirBlockIndex whenFalse,
            SourceOriginIndex branchOrigin)
        {
            if (!TryGetExpression(index, out var expression))
                return false;

            if (_context.CanUseActivationTemporarySpills ||
                _context.ExpressionEvaluationMode is
                    Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible)
            {
                var materializedCondition = LowerExpression(index);
                if (!materializedCondition.IsValid)
                    return false;

                _builder.AddInstruction(
                    Avm1MirInstructionKind.BranchIfTrue,
                    operand: materializedCondition,
                    effects: Avm1MirEffect.ControlFlow,
                    target: whenTrue,
                    alternativeTarget: whenFalse,
                    origin: branchOrigin);
                return true;
            }

            if (expression.Kind is Avm1SourceExpressionKind.Unary &&
                expression.Operator is Avm1SourceOperator.LogicalNot &&
                expression.Children.Count == 1)
            {
                return LowerCondition(
                    _arena.GetChild(expression, 0),
                    whenFalse,
                    whenTrue,
                    branchOrigin);
            }

            if (expression.Kind is Avm1SourceExpressionKind.Binary &&
                expression.Operator is
                    Avm1SourceOperator.LogicalAnd or
                    Avm1SourceOperator.LogicalOr &&
                expression.Children.Count == 2)
            {
                var rightBlock = _builder.CreateBlock();
                var evaluateRightWhenTrue = expression.Operator is
                    Avm1SourceOperator.LogicalAnd;
                if (!LowerCondition(
                        _arena.GetChild(expression, 0),
                        evaluateRightWhenTrue ? rightBlock : whenTrue,
                        evaluateRightWhenTrue ? whenFalse : rightBlock,
                        expression.Origin))
                {
                    return false;
                }

                _builder.StartBlock(rightBlock, isReachable: true);
                return LowerCondition(
                    _arena.GetChild(expression, 1),
                    whenTrue,
                    whenFalse,
                    branchOrigin);
            }

            var condition = LowerExpression(index);
            if (!condition.IsValid)
                return false;

            _builder.AddInstruction(
                Avm1MirInstructionKind.BranchIfTrue,
                operand: condition,
                effects: Avm1MirEffect.ControlFlow,
                target: whenTrue,
                alternativeTarget: whenFalse,
                origin: branchOrigin);
            return true;
        }

        private void LowerForIn(Avm1SourceStatement statement)
        {
            if (!TryValidateForIn(statement, out var labelName, out var keyExpression))
                return;

            if (statement.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey))
            {
                if (!TryGetSymbolName(
                    keyExpression.Symbol,
                    statement.Origin,
                    out var keyName))
                {
                    return;
                }

                var lValue = AddBoundSymbolLValue(
                    keyExpression.Symbol,
                    keyName,
                    statement.Origin);
                var storage = _builder.GetLValue(lValue);
                if (storage.Kind is Avm1MirLValueKind.Register)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.DeclareLocal,
                        lValue: lValue,
                        origin: statement.Origin);
                }
                else
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.DeclareLocal,
                        name: keyName,
                        nameKind: Avm1MirNameKind.BoundSymbol,
                        effects: Avm1MirEffect.WritesActivation,
                        origin: statement.Origin);
                }
            }

            var collection = LowerExpression(statement.SecondaryExpression);
            if (!collection.IsValid)
                return;

            var keyTemporary = _builder.AcquireTemporary();
            try
            {
                var headerBlock = _builder.CreateBlock();
                var bodyBlock = _builder.CreateBlock();
                var exitBlock = _builder.CreateBlock();
                var scope = CreateLoopScope(
                    statement,
                    exitBlock,
                    headerBlock,
                    labelName,
                    isEnumeration: true);

                _builder.AddInstruction(
                    Avm1MirInstructionKind.BeginEnumeration,
                    operand: collection,
                    effects: Avm1MirEffect.ReadsHeap |
                        Avm1MirEffect.MayInvokeUserCode |
                        Avm1MirEffect.MayThrow,
                    origin: statement.Origin);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: headerBlock,
                    origin: statement.Origin);

                _builder.StartBlock(headerBlock, isReachable: true);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.EnumerateNext,
                    temporary: keyTemporary,
                    effects: Avm1MirEffect.ControlFlow,
                    target: bodyBlock,
                    alternativeTarget: exitBlock,
                    origin: statement.Origin);

                _builder.StartBlock(bodyBlock, isReachable: true);
                if (TryLowerLValue(
                    statement.Expression,
                    statement.Origin,
                    out var keyLValue))
                {
                    var key = _builder.AddValueInstruction(
                        Avm1MirInstructionKind.LoadTemporary,
                        temporary: keyTemporary,
                        origin: statement.Origin);
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.StoreLValue,
                        operand: key,
                        lValue: keyLValue,
                        effects: GetWriteEffects(_builder.GetLValue(keyLValue)),
                        origin: statement.Origin);
                    LowerControlScopeBody(statement, scope);
                }

                if (_builder.CanAppend)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.Branch,
                        effects: Avm1MirEffect.ControlFlow,
                        target: headerBlock,
                        origin: statement.Origin);
                }

                _builder.StartBlock(exitBlock, isReachable: true);
            }
            finally
            {
                _builder.ReleaseTemporary(keyTemporary);
            }
        }

        private void LowerSwitch(Avm1SourceStatement statement)
        {
            if (!TryValidateSwitch(
                    statement,
                    out var labels,
                    out var caseLabelIndices,
                    out var defaultLabelIndex,
                    out var labelName))
            {
                return;
            }

            var selector = LowerExpression(statement.Expression);
            if (!selector.IsValid)
                return;

            var dispatchBlocks = new Avm1MirBlockIndex[
                Math.Max(0, caseLabelIndices.Length - 1)];
            for (var i = 0; i < dispatchBlocks.Length; i++)
                dispatchBlocks[i] = _builder.CreateBlock();

            var bodyBlocks = new Avm1MirBlockIndex[labels.Length];
            for (var i = 0; i < bodyBlocks.Length; i++)
                bodyBlocks[i] = _builder.CreateBlock();

            var exitBlock = _builder.CreateBlock();
            var scope = CreateSwitchScope(statement, exitBlock, labelName);
            if (caseLabelIndices.Length == 0)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Discard,
                    selector,
                    origin: statement.Origin);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Branch,
                    effects: Avm1MirEffect.ControlFlow,
                    target: defaultLabelIndex >= 0
                        ? bodyBlocks[defaultLabelIndex]
                        : exitBlock,
                    origin: statement.Origin);
            }
            else
            {
                var selectorTemporary = _builder.AcquireTemporary();
                var failedCaseIndex = -1;
                try
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.StoreTemporary,
                        operand: selector,
                        temporary: selectorTemporary,
                        origin: statement.Origin);

                    for (var caseIndex = 0;
                        caseIndex < caseLabelIndices.Length;
                        caseIndex++)
                    {
                        if (caseIndex > 0)
                        {
                            _builder.StartBlock(
                                dispatchBlocks[caseIndex - 1],
                                isReachable: true);
                        }

                        var labelIndex = caseLabelIndices[caseIndex];
                        var @case = labels[labelIndex];
                        var caseValue = LowerExpression(@case.Expression);
                        if (!caseValue.IsValid)
                        {
                            failedCaseIndex = caseIndex;
                            break;
                        }

                        var loadedSelector = _builder.AddValueInstruction(
                            Avm1MirInstructionKind.LoadTemporary,
                            temporary: selectorTemporary,
                            origin: statement.Origin);
                        var matches = _builder.AddValueInstruction(
                            Avm1MirInstructionKind.Binary,
                            operand: caseValue,
                            secondaryOperand: loadedSelector,
                            @operator: Avm1MirOperator.StrictEqual,
                            effects: Avm1MirEffect.MayThrow,
                            origin: @case.Origin);
                        var missTarget = caseIndex + 1 < caseLabelIndices.Length
                            ? dispatchBlocks[caseIndex]
                            : defaultLabelIndex >= 0
                                ? bodyBlocks[defaultLabelIndex]
                                : exitBlock;
                        _builder.AddInstruction(
                            Avm1MirInstructionKind.BranchIfTrue,
                            operand: matches,
                            effects: Avm1MirEffect.ControlFlow,
                            target: bodyBlocks[labelIndex],
                            alternativeTarget: missTarget,
                            origin: @case.Origin);
                    }
                }
                finally
                {
                    _builder.ReleaseTemporary(selectorTemporary);
                }

                if (failedCaseIndex >= 0)
                {
                    for (var dispatchIndex = failedCaseIndex;
                        dispatchIndex < dispatchBlocks.Length;
                        dispatchIndex++)
                    {
                        _builder.StartBlock(
                            dispatchBlocks[dispatchIndex],
                            isReachable: false);
                    }
                    for (var bodyIndex = 0;
                        bodyIndex < bodyBlocks.Length;
                        bodyIndex++)
                    {
                        _builder.StartBlock(bodyBlocks[bodyIndex], isReachable: false);
                    }
                    _builder.StartBlock(exitBlock, isReachable: false);
                    return;
                }
            }

            var exitReachable = defaultLabelIndex < 0;
            _controlScopes.Add(scope);
            try
            {
                for (var labelIndex = 0; labelIndex < labels.Length; labelIndex++)
                {
                    _builder.StartBlock(bodyBlocks[labelIndex], isReachable: true);
                    var label = labels[labelIndex];
                    for (var childIndex = 0;
                        childIndex < label.Children.Count;
                        childIndex++)
                    {
                        LowerStatement(_arena.GetChild(label, childIndex));
                    }

                    if (!_builder.CanAppend)
                        continue;

                    var fallThroughTarget = labelIndex + 1 < labels.Length
                        ? bodyBlocks[labelIndex + 1]
                        : exitBlock;
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.Branch,
                        effects: Avm1MirEffect.ControlFlow,
                        target: fallThroughTarget,
                        origin: label.Origin);
                    exitReachable |= fallThroughTarget == exitBlock;
                }
            }
            finally
            {
                _controlScopes.RemoveAt(_controlScopes.Count - 1);
            }

            _builder.StartBlock(
                exitBlock,
                isReachable: exitReachable || scope.HasBreakEdge);
        }

        private void LowerControlJump(Avm1SourceStatement statement)
        {
            if (statement.Children.Count != 0 ||
                statement.Initializers.Count != 0 ||
                statement.Expressions.Count != 0 ||
                statement.Expression.IsValid ||
                statement.SecondaryExpression.IsValid)
            {
                AddMalformed(
                    statement.Origin,
                    $"{statement.Kind} statement has an invalid payload.");
                return;
            }

            var scope = ResolveControlScope(statement, out var resolutionFailed);
            if (resolutionFailed)
                return;

            if (scope is null)
            {
                if (TryResolveExternalControlScope(
                    statement,
                    out var externalScope,
                    out var externalResolutionFailed))
                {
                    if (_targetScopes.Count != 0)
                        EmitTargetRestore(0, statement.Origin);
                    var kind = statement.Kind is Avm1SourceStatementKind.Break
                        ? Avm1MirCompletionKind.Break
                        : Avm1MirCompletionKind.Continue;
                    if (_context.ProtectedRegionExitMode is
                            Avm1ProtectedRegionExitMode.AdobePhysicalBranch &&
                        _completionSink.Kind is
                            Avm1MirCompletionStorageKind.None)
                    {
                        var site = _builder.AddCompletionSite(
                            kind,
                            externalScope.Statement,
                            Avm1MirCompletionStorage.None,
                            GetCompletionToken(externalScope.Statement, kind),
                            statement.Origin);
                        _builder.AddInstruction(
                            Avm1MirInstructionKind.ExternalControlBranch,
                            completionSite: site,
                            effects: Avm1MirEffect.ControlFlow |
                                Avm1MirEffect.Completion,
                            origin: statement.Origin);
                        return;
                    }
                    if (_completionSink.Kind is
                        Avm1MirCompletionStorageKind.None)
                    {
                        AddMalformed(
                            statement.Origin,
                            "External completion has no enclosing protected-region " +
                            "sink.");
                        return;
                    }

                    EmitPendingCompletion(
                        _completionSink,
                        externalScope.Statement,
                        kind,
                        statement.Origin);
                    BranchToExternalCompletionExit(statement.Origin);
                    return;
                }
                if (externalResolutionFailed)
                    return;

                AddMalformed(
                    statement.Origin,
                    statement.Label.IsValid
                        ? $"{statement.Kind} label does not resolve to an active " +
                            "control scope."
                        : $"{statement.Kind} statement is outside a control scope.");
                return;
            }

            var isBreak = statement.Kind is Avm1SourceStatementKind.Break;
            if (_targetScopes.Count > scope.TargetDepth)
                EmitTargetRestore(scope.TargetDepth, statement.Origin);
            var semanticTarget = isBreak ? scope.BreakBlock : scope.ContinueBlock;
            var emittedTarget = GetLocalCompletionTarget(
                scope,
                isBreak,
                statement.Origin);
            _builder.AddInstruction(
                isBreak
                    ? Avm1MirInstructionKind.Break
                    : Avm1MirInstructionKind.Continue,
                controlTarget: scope.Target,
                effects: Avm1MirEffect.ControlFlow | Avm1MirEffect.Completion,
                target: emittedTarget,
                alternativeTarget: emittedTarget == semanticTarget
                    ? Avm1MirBlockIndex.Invalid
                    : semanticTarget,
                origin: statement.Origin);
        }

        private Avm1MirBlockIndex GetLocalCompletionTarget(
            ControlScope scope,
            bool isBreak,
            SourceOriginIndex origin)
        {
            if (isBreak)
                scope.HasBreakEdge = true;
            else
                scope.HasContinueEdge = true;

            var semanticTarget = isBreak ? scope.BreakBlock : scope.ContinueBlock;
            var enumerationCount = CountEnumerationScopesExitedBy(scope, isBreak);
            return enumerationCount == 0
                ? semanticTarget
                : CreateEnumerationCleanupChain(
                    enumerationCount,
                    semanticTarget,
                    origin);
        }

        private void EmitPendingCompletion(
            Avm1MirCompletionStorage storage,
            SourceStatementIndex target,
            Avm1MirCompletionKind kind,
            SourceOriginIndex origin)
        {
            var site = _builder.AddCompletionSite(
                kind,
                target,
                storage,
                GetCompletionToken(target, kind),
                origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.SetPendingCompletion,
                completionSite: site,
                effects: (storage.Kind is Avm1MirCompletionStorageKind.ActivationName
                        ? Avm1MirEffect.WritesActivation
                        : Avm1MirEffect.None) |
                    Avm1MirEffect.Completion,
                origin: origin);
        }

        private void BranchToExternalCompletionExit(SourceOriginIndex origin)
        {
            if (!_externalCompletionExit.IsValid)
                _externalCompletionExit = _builder.CreateBlock();
            var enumerationCount = CountActiveEnumerationScopes();
            var target = enumerationCount == 0
                ? _externalCompletionExit
                : CreateEnumerationCleanupChain(
                    enumerationCount,
                    _externalCompletionExit,
                    origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                effects: Avm1MirEffect.ControlFlow | Avm1MirEffect.Completion,
                target: target,
                origin: origin);
        }

        private static int GetCompletionToken(
            SourceStatementIndex target,
            Avm1MirCompletionKind kind) =>
            checked(((target.Value + 1) * 2) +
                (kind is Avm1MirCompletionKind.Continue ? 1 : 0));

        private void LowerControlScopeBody(
            Avm1SourceStatement statement,
            ControlScope scope)
        {
            _controlScopes.Add(scope);
            try
            {
                for (var i = 0; i < statement.Children.Count; i++)
                    LowerStatement(_arena.GetChild(statement, i));
            }
            finally
            {
                _controlScopes.RemoveAt(_controlScopes.Count - 1);
            }
        }

        private ControlScope CreateLoopScope(
            Avm1SourceStatement statement,
            Avm1MirBlockIndex breakBlock,
            Avm1MirBlockIndex continueBlock,
            string? labelName,
            bool isEnumeration = false)
        {
            var target = _builder.AddControlTarget(
                Avm1MirControlTargetKind.Loop,
                statement.Index,
                breakBlock,
                continueBlock,
                labelName,
                statement.Origin);
            return new ControlScope(
                target,
                statement.Index,
                statement.Label,
                labelName,
                breakBlock,
                continueBlock,
                isEnumeration,
                _targetScopes.Count);
        }

        private ControlScope CreateSwitchScope(
            Avm1SourceStatement statement,
            Avm1MirBlockIndex breakBlock,
            string? labelName)
        {
            var target = _builder.AddControlTarget(
                Avm1MirControlTargetKind.Switch,
                statement.Index,
                breakBlock,
                Avm1MirBlockIndex.Invalid,
                labelName,
                statement.Origin);
            return new ControlScope(
                target,
                statement.Index,
                statement.Label,
                labelName,
                breakBlock,
                Avm1MirBlockIndex.Invalid,
                isEnumeration: false,
                _targetScopes.Count);
        }

        private ControlScope? ResolveControlScope(
            Avm1SourceStatement statement,
            out bool resolutionFailed)
        {
            resolutionFailed = false;
            var isContinue = statement.Kind is Avm1SourceStatementKind.Continue;
            for (var i = _controlScopes.Count - 1; i >= 0; i--)
            {
                var scope = _controlScopes[i];
                if (statement.Label.IsValid)
                {
                    if (scope.Label != statement.Label)
                        continue;
                    if (isContinue && !scope.ContinueBlock.IsValid)
                    {
                        AddMalformed(
                            statement.Origin,
                            "Continue label does not refer to an active loop.");
                        resolutionFailed = true;
                        return null;
                    }

                    return scope;
                }

                if (!isContinue || scope.ContinueBlock.IsValid)
                    return scope;
            }

            return null;
        }

        private bool TryResolveExternalControlScope(
            Avm1SourceStatement statement,
            out Avm1MirVisibleControlScope result,
            out bool resolutionFailed)
        {
            resolutionFailed = false;
            var isContinue = statement.Kind is Avm1SourceStatementKind.Continue;
            foreach (var scope in _context.ExternalControlScopes)
            {
                if (statement.Label.IsValid)
                {
                    if (scope.Label != statement.Label)
                        continue;
                    if (isContinue && scope.Kind is not Avm1MirControlTargetKind.Loop)
                    {
                        AddMalformed(
                            statement.Origin,
                            "Continue label does not refer to an active loop.");
                        result = default;
                        resolutionFailed = true;
                        return false;
                    }

                    result = scope;
                    return true;
                }

                if (!isContinue || scope.Kind is Avm1MirControlTargetKind.Loop)
                {
                    result = scope;
                    return true;
                }
            }

            result = default;
            return false;
        }

        private int CountActiveEnumerationScopes()
        {
            var count = 0;
            for (var i = _controlScopes.Count - 1; i >= 0; i--)
            {
                if (_controlScopes[i].IsEnumeration)
                    count++;
            }
            return count;
        }

        private int CountEnumerationScopesExitedBy(
            ControlScope target,
            bool isBreak)
        {
            var targetIndex = _controlScopes.LastIndexOf(target);
            if (targetIndex < 0)
                throw new InvalidOperationException("Control scope is not active.");

            var count = 0;
            var lastExitedIndex = isBreak ? targetIndex : targetIndex + 1;
            for (var i = _controlScopes.Count - 1; i >= lastExitedIndex; i--)
            {
                if (_controlScopes[i].IsEnumeration)
                    count++;
            }
            return count;
        }

        private Avm1MirBlockIndex CreateEnumerationCleanupChain(
            int enumerationCount,
            Avm1MirBlockIndex continuation,
            SourceOriginIndex origin)
        {
            var target = continuation;
            for (var i = 0; i < enumerationCount; i++)
            {
                var cleanup = _builder.CreateBlock();
                _pendingBlocks.Add(PendingBlock.EnumerationCleanup(
                    cleanup,
                    target,
                    origin));
                target = cleanup;
            }
            return target;
        }

        private bool TryValidateLoop(
            Avm1SourceStatement statement,
            bool requiresCondition,
            out string? labelName)
        {
            labelName = null;
            if (requiresCondition && !statement.Expression.IsValid)
            {
                AddMalformed(statement.Origin, $"{statement.Kind} loop has no condition.");
                return false;
            }
            if (statement.SecondaryExpression.IsValid)
            {
                AddMalformed(
                    statement.Origin,
                    $"{statement.Kind} loop has an unexpected secondary expression.");
                return false;
            }
            if (statement.Kind is not Avm1SourceStatementKind.For &&
                (statement.Initializers.Count != 0 || statement.Expressions.Count != 0))
            {
                AddMalformed(
                    statement.Origin,
                    $"{statement.Kind} loop has unexpected for-clause ranges.");
                return false;
            }
            return TryValidateControlLabel(statement, "loop", out labelName);
        }

        private bool TryValidateForIn(
            Avm1SourceStatement statement,
            out string? labelName,
            out Avm1SourceExpression keyExpression)
        {
            labelName = null;
            keyExpression = default;
            if (!statement.Expression.IsValid ||
                !statement.SecondaryExpression.IsValid)
            {
                AddMalformed(
                    statement.Origin,
                    "For-in statement requires a key target and collection expression.");
                return false;
            }
            if (statement.Symbol.IsValid ||
                statement.Name.IsValid ||
                statement.Opaque.IsValid ||
                statement.Initializers.Count != 0 ||
                statement.Expressions.Count != 0 ||
                statement.Flags is not (
                    Avm1SourceStatementFlags.None or
                    Avm1SourceStatementFlags.ForInDeclaresKey))
            {
                AddMalformed(statement.Origin, "For-in statement has an invalid payload.");
                return false;
            }
            if (!TryGetExpression(statement.Expression, out keyExpression))
                return false;
            if (statement.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey) &&
                keyExpression.Kind is not Avm1SourceExpressionKind.SymbolReference)
            {
                AddMalformed(
                    statement.Origin,
                    "A declared for-in key must be a bound symbol reference.");
                return false;
            }

            return TryValidateControlLabel(statement, "for-in loop", out labelName);
        }

        private bool TryValidateSwitch(
            Avm1SourceStatement statement,
            out Avm1SourceStatement[] labels,
            out int[] caseLabelIndices,
            out int defaultLabelIndex,
            out string? labelName)
        {
            labels = [];
            caseLabelIndices = [];
            defaultLabelIndex = -1;
            labelName = null;
            if (!statement.Expression.IsValid)
            {
                AddMalformed(statement.Origin, "Switch statement has no discriminator.");
                return false;
            }
            if (statement.SecondaryExpression.IsValid ||
                statement.Symbol.IsValid ||
                statement.Name.IsValid ||
                statement.Opaque.IsValid ||
                statement.Flags is not Avm1SourceStatementFlags.None ||
                statement.Initializers.Count != 0 ||
                statement.Expressions.Count != 0)
            {
                AddMalformed(statement.Origin, "Switch statement has an invalid payload.");
                return false;
            }
            if (!TryValidateControlLabel(statement, "switch", out labelName))
                return false;

            labels = new Avm1SourceStatement[statement.Children.Count];
            var cases = new List<int>(labels.Length);
            for (var labelIndex = 0; labelIndex < labels.Length; labelIndex++)
            {
                var child = _arena.GetChild(statement, labelIndex);
                if (!TryGetStatement(child, out var label))
                    return false;
                if (label.Kind is not (
                    Avm1SourceStatementKind.SwitchCase or
                    Avm1SourceStatementKind.SwitchDefault))
                {
                    AddMalformed(
                        label.Origin,
                        $"Switch child {label.Kind} is not a case or default label.");
                    return false;
                }
                if (label.SecondaryExpression.IsValid ||
                    label.Symbol.IsValid ||
                    label.Name.IsValid ||
                    label.Label.IsValid ||
                    label.Opaque.IsValid ||
                    label.Flags is not Avm1SourceStatementFlags.None ||
                    label.Initializers.Count != 0 ||
                    label.Expressions.Count != 0)
                {
                    AddMalformed(label.Origin, $"Switch {label.Kind} has an invalid payload.");
                    return false;
                }

                if (label.Kind is Avm1SourceStatementKind.SwitchCase)
                {
                    if (!label.Expression.IsValid)
                    {
                        AddMalformed(label.Origin, "Switch case has no value expression.");
                        return false;
                    }
                    cases.Add(labelIndex);
                }
                else
                {
                    if (label.Expression.IsValid)
                    {
                        AddMalformed(label.Origin, "Switch default has a value expression.");
                        return false;
                    }
                    if (defaultLabelIndex >= 0)
                    {
                        AddMalformed(statement.Origin, "Switch contains more than one default label.");
                        return false;
                    }
                    defaultLabelIndex = labelIndex;
                }

                labels[labelIndex] = label;
            }

            caseLabelIndices = cases.ToArray();
            return true;
        }

        private bool TryValidateControlLabel(
            Avm1SourceStatement statement,
            string role,
            out string? labelName)
        {
            labelName = null;
            if (!statement.Label.IsValid)
                return true;
            if (statement.Label.Value >= _arena.Labels.Count)
            {
                AddMalformed(statement.Origin, $"{role} label handle is invalid.");
                return false;
            }

            var label = _arena[statement.Label];
            if (label.Index != statement.Label ||
                !TryGetString(label.Name, statement.Origin, $"{role} label", out var name))
            {
                return false;
            }
            if (_controlScopes.Any(scope =>
                string.Equals(scope.LabelName, name, StringComparison.Ordinal)))
            {
                AddMalformed(statement.Origin, $"{role} label is already active.");
                return false;
            }

            labelName = name;
            return true;
        }

        private bool TryCollectForClauses(
            Avm1SourceStatement statement,
            out SourceStatementIndex[] initializers,
            out SourceExpressionIndex[] updates)
        {
            initializers = new SourceStatementIndex[statement.Initializers.Count];
            for (var i = 0; i < initializers.Length; i++)
            {
                var initializer = _arena.GetInitializer(statement, i);
                if (!TryGetStatement(initializer, out var source))
                {
                    updates = [];
                    return false;
                }
                if (source.Kind is not (
                        Avm1SourceStatementKind.Expression or
                        Avm1SourceStatementKind.VariableDeclaration))
                {
                    AddMalformed(
                        statement.Origin,
                        "For initializer must be an expression or variable declaration.");
                    updates = [];
                    return false;
                }
                initializers[i] = initializer;
            }

            updates = new SourceExpressionIndex[statement.Expressions.Count];
            for (var i = 0; i < updates.Length; i++)
            {
                var update = _arena.GetExpression(statement, i);
                if (!TryGetExpression(update, out _))
                    return false;
                updates[i] = update;
            }
            return true;
        }

        private void LowerDiscardedExpression(
            SourceExpressionIndex expression,
            SourceOriginIndex origin)
        {
            var value = LowerExpression(expression, resultRequired: false);
            if (value.IsValid)
            {
                _builder.AddInstruction(
                    Avm1MirInstructionKind.Discard,
                    value,
                    origin: origin);
            }
        }

        private Avm1MirValueIndex LowerExpression(
            SourceExpressionIndex index,
            bool resultRequired = true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetExpression(index, out var expression))
                return Avm1MirValueIndex.Invalid;

            switch (expression.Kind)
            {
                case Avm1SourceExpressionKind.Literal:
                    return LowerLiteral(expression);

                case Avm1SourceExpressionKind.SymbolReference:
                    {
                        if (!TryGetSymbolName(
                            expression.Symbol,
                            expression.Origin,
                            out var name))
                        {
                            return Avm1MirValueIndex.Invalid;
                        }

                        var lValue = AddBoundSymbolLValue(
                            expression.Symbol,
                            name,
                            expression.Origin);
                        return _builder.AddValueInstruction(
                            Avm1MirInstructionKind.LoadLValue,
                            lValue: lValue,
                            effects: GetReadEffects(_builder.GetLValue(lValue)),
                            origin: expression.Origin);
                    }

                case Avm1SourceExpressionKind.DynamicName:
                case Avm1SourceExpressionKind.QualifiedName:
                    {
                        if (!TryGetString(
                            expression.Name,
                            expression.Origin,
                            "name expression",
                            out var name))
                        {
                            return Avm1MirValueIndex.Invalid;
                        }

                        var nameKind = expression.Kind is
                            Avm1SourceExpressionKind.DynamicName
                                ? Avm1MirNameKind.DynamicName
                                : Avm1MirNameKind.QualifiedName;
                        var lValue = _builder.AddNameLValue(
                            name,
                            nameKind,
                            expression.Origin);
                        return _builder.AddValueInstruction(
                            Avm1MirInstructionKind.LoadLValue,
                            lValue: lValue,
                            effects: GetReadEffects(nameKind),
                            origin: expression.Origin);
                    }

                case Avm1SourceExpressionKind.ComputedDynamicName:
                    return LowerComputedDynamicName(expression);

                case Avm1SourceExpressionKind.MemberAccess:
                    {
                        if (!TryLowerLValue(index, expression.Origin, out var lValue))
                            return Avm1MirValueIndex.Invalid;

                        return _builder.AddValueInstruction(
                            Avm1MirInstructionKind.LoadLValue,
                            lValue: lValue,
                            effects: Avm1MirEffect.ReadsHeap |
                                Avm1MirEffect.MayInvokeUserCode |
                                Avm1MirEffect.MayThrow,
                            origin: expression.Origin);
                    }

                case Avm1SourceExpressionKind.Delete:
                    return LowerDelete(expression);

                case Avm1SourceExpressionKind.Assignment:
                    return LowerAssignment(expression, resultRequired);

                case Avm1SourceExpressionKind.IntrinsicCall:
                    return LowerIntrinsic(expression, resultRequired);

                case Avm1SourceExpressionKind.Call:
                    return Avm1SourceEvalAnalysis.IsDirectEvalCall(
                        _arena,
                        expression)
                            ? LowerDirectEval(expression)
                            : LowerInvocation(
                                expression,
                                Avm1MirInvocationKind.Call);

                case Avm1SourceExpressionKind.New:
                    return LowerInvocation(
                        expression,
                        Avm1MirInvocationKind.Construct);

                case Avm1SourceExpressionKind.ArrayLiteral:
                    return LowerAggregate(expression, Avm1MirAggregateKind.Array);

                case Avm1SourceExpressionKind.ObjectLiteral:
                    return LowerAggregate(expression, Avm1MirAggregateKind.Object);

                case Avm1SourceExpressionKind.FunctionLiteral:
                    return LowerFunctionLiteral(expression);

                case Avm1SourceExpressionKind.Unary:
                    return LowerUnary(expression, resultRequired);

                case Avm1SourceExpressionKind.Binary:
                    return expression.Operator is
                        Avm1SourceOperator.LogicalAnd or
                        Avm1SourceOperator.LogicalOr
                            ? LowerLogical(expression)
                            : LowerBinary(expression);

                case Avm1SourceExpressionKind.Sequence:
                    return LowerSequence(expression, resultRequired);

                case Avm1SourceExpressionKind.Conditional:
                    return LowerConditional(expression);

                default:
                    AddDiagnostic(
                        "AVM1CMP101",
                        expression.Origin,
                        $"Source expression {expression.Kind} is not supported by " +
                        "the current method backend.");
                    return Avm1MirValueIndex.Invalid;
            }
        }

        private Avm1MirValueIndex LowerIntrinsic(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (!TryGetString(
                    expression.Name,
                    expression.Origin,
                    "intrinsic name",
                    out var name))
            {
                return Avm1MirValueIndex.Invalid;
            }

            return name switch
            {
                "Number" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.ToNumber,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "String" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.ToString,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "int" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.ToInteger,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "random" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.RandomNumber,
                    Avm1MirEffect.ReadsRuntimeState |
                    Avm1MirEffect.WritesRuntimeState |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow),
                "length" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.StringLength,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "mblength" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.MbStringLength,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "ord" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.CharToAscii,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "chr" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.AsciiToChar,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "mbord" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.MbCharToAscii,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "mbchr" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.MbAsciiToChar,
                    Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow),
                "substring" => LowerStringExtractIntrinsic(
                    expression,
                    Avm1MirOperator.StringExtract),
                "mbsubstring" => LowerStringExtractIntrinsic(
                    expression,
                    Avm1MirOperator.MbStringExtract),
                "getProperty" => LowerFixedHostIntrinsic(
                    expression,
                    Avm1MirOperator.GetProperty,
                    requiredArity: 2,
                    producesResult: true,
                    resultRequired),
                "setProperty" => LowerFixedHostIntrinsic(
                    expression,
                    Avm1MirOperator.SetProperty,
                    requiredArity: 3,
                    producesResult: false,
                    resultRequired),
                "duplicateMovieClip" => LowerFixedHostIntrinsic(
                    expression,
                    Avm1MirOperator.CloneSprite,
                    requiredArity: 3,
                    producesResult: false,
                    resultRequired),
                "removeMovieClip" => LowerFixedHostIntrinsic(
                    expression,
                    Avm1MirOperator.RemoveSprite,
                    requiredArity: 1,
                    producesResult: false,
                    resultRequired),
                "startDrag" => LowerStartDragIntrinsic(
                    expression,
                    resultRequired),
                "stopDrag" => LowerFixedHostIntrinsic(
                    expression,
                    Avm1MirOperator.EndDrag,
                    requiredArity: 0,
                    producesResult: false,
                    resultRequired),
                "targetPath" => LowerUnaryIntrinsic(
                    expression,
                    Avm1MirOperator.TargetPath,
                    Avm1MirEffect.ReadsHeap | Avm1MirEffect.MayThrow),
                "getTimer" => LowerGetTime(expression),
                "trace" => LowerTrace(expression, resultRequired),
                "nextFrame" => LowerTimelineIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelineNextFrame,
                    resultRequired),
                "prevFrame" => LowerTimelineIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelinePreviousFrame,
                    resultRequired),
                "play" => LowerTimelineIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelinePlay,
                    resultRequired),
                "stop" => LowerTimelineIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelineStop,
                    resultRequired),
                "stopAllSounds" => LowerTimelineIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelineStopSounds,
                    resultRequired),
                "toggleHighQuality" => LowerTimelineIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelineToggleQuality,
                    resultRequired),
                "call" => LowerTimelineCallIntrinsic(expression, resultRequired),
                "gotoAndPlay" => LowerTimelineGotoIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelineGotoAndPlay,
                    resultRequired),
                "gotoAndStop" => LowerTimelineGotoIntrinsic(
                    expression,
                    Avm1MirInstructionKind.TimelineGotoAndStop,
                    resultRequired),
                "getURL" => LowerGetUrl(expression, resultRequired),
                "fscommand" => LowerFsCommand(expression, resultRequired),
                "loadMovie" => LowerUrlLoad(
                    expression,
                    resultRequired,
                    "loadMovie",
                    numericLevel: false,
                    GetUrlFlags.LoadTarget),
                "loadMovieNum" => LowerUrlLoad(
                    expression,
                    resultRequired,
                    "loadMovieNum",
                    numericLevel: true,
                    GetUrlFlags.MethodNone),
                "loadVariables" => LowerUrlLoad(
                    expression,
                    resultRequired,
                    "loadVariables",
                    numericLevel: false,
                    GetUrlFlags.LoadTarget | GetUrlFlags.LoadVariables),
                "loadVariablesNum" => LowerUrlLoad(
                    expression,
                    resultRequired,
                    "loadVariablesNum",
                    numericLevel: true,
                    GetUrlFlags.LoadVariables),
                _ => UnsupportedIntrinsic(expression, name)
            };
        }

        private Avm1MirValueIndex LowerTimelineIntrinsic(
            Avm1SourceExpression expression,
            Avm1MirInstructionKind kind,
            bool resultRequired)
        {
            if (expression.Children.Count != 0)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {_arena[expression.Name]} requires no arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            _builder.AddInstruction(
                kind,
                effects: Avm1MirEffect.WritesRuntimeState,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerStringExtractIntrinsic(
            Avm1SourceExpression expression,
            Avm1MirOperator @operator)
        {
            if (expression.Children.Count != 3)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {_arena[expression.Name]} requires three arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            var arguments = new Avm1MirValueIndex[3];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = LowerExpression(_arena.GetChild(expression, i));
                if (!arguments[i].IsValid)
                    return Avm1MirValueIndex.Invalid;
            }

            var callSite = _builder.AddCallSite(
                Avm1MirInvocationKind.Call,
                Avm1MirInvocationTargetKind.Name,
                Avm1MirValueIndex.Invalid,
                Avm1MirValueIndex.Invalid,
                _arena[expression.Name],
                Avm1MirNameKind.None,
                SourceSymbolIndex.Invalid,
                arguments,
                Avm1MirEvaluationOrder.Forward,
                Avm1MirInvocationTargetOrder.BeforeArguments,
                expression.Origin);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Intrinsic,
                callSite: callSite,
                @operator: @operator,
                effects: Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerFixedHostIntrinsic(
            Avm1SourceExpression expression,
            Avm1MirOperator @operator,
            int requiredArity,
            bool producesResult,
            bool resultRequired)
        {
            if (expression.Children.Count != requiredArity)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {_arena[expression.Name]} requires " +
                    $"{requiredArity} argument(s).");
                return Avm1MirValueIndex.Invalid;
            }

            var arguments = new Avm1MirValueIndex[requiredArity];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = LowerExpression(_arena.GetChild(expression, i));
                if (!arguments[i].IsValid)
                    return Avm1MirValueIndex.Invalid;
            }

            var callSite = _builder.AddCallSite(
                Avm1MirInvocationKind.Call,
                Avm1MirInvocationTargetKind.Name,
                Avm1MirValueIndex.Invalid,
                Avm1MirValueIndex.Invalid,
                _arena[expression.Name],
                Avm1MirNameKind.None,
                SourceSymbolIndex.Invalid,
                arguments,
                Avm1MirEvaluationOrder.Forward,
                Avm1MirInvocationTargetOrder.BeforeArguments,
                expression.Origin);
            var effects = @operator is Avm1MirOperator.GetProperty
                ? Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.ReadsRuntimeState |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow
                : Avm1MirEffect.WritesHeap |
                    Avm1MirEffect.WritesRuntimeState |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow;
            if (producesResult)
            {
                return _builder.AddValueInstruction(
                    Avm1MirInstructionKind.Intrinsic,
                    callSite: callSite,
                    @operator: @operator,
                    effects: effects,
                    origin: expression.Origin);
            }

            _builder.AddInstruction(
                Avm1MirInstructionKind.Intrinsic,
                callSite: callSite,
                @operator: @operator,
                effects: effects,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerStartDragIntrinsic(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count is not (2 or 6))
            {
                AddMalformed(
                    expression.Origin,
                    "Intrinsic startDrag requires two or six arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            var arguments = new Avm1MirValueIndex[expression.Children.Count];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = LowerExpression(_arena.GetChild(expression, i));
                if (!arguments[i].IsValid)
                    return Avm1MirValueIndex.Invalid;
            }

            var callSite = _builder.AddCallSite(
                Avm1MirInvocationKind.Call,
                Avm1MirInvocationTargetKind.Name,
                Avm1MirValueIndex.Invalid,
                Avm1MirValueIndex.Invalid,
                _arena[expression.Name],
                Avm1MirNameKind.None,
                SourceSymbolIndex.Invalid,
                arguments,
                Avm1MirEvaluationOrder.Forward,
                Avm1MirInvocationTargetOrder.BeforeArguments,
                expression.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Intrinsic,
                callSite: callSite,
                @operator: Avm1MirOperator.StartDrag,
                effects:
                    Avm1MirEffect.WritesHeap |
                    Avm1MirEffect.WritesRuntimeState |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerTimelineGotoIntrinsic(
            Avm1SourceExpression expression,
            Avm1MirInstructionKind kind,
            bool resultRequired)
        {
            if (expression.Children.Count == 2)
                return LowerSceneTimelineGotoIntrinsic(expression, kind, resultRequired);

            if (expression.Children.Count != 1)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {_arena[expression.Name]} requires one or two arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            var targetExpression = _arena.GetChild(expression, 0);
            if (TryGetImmediateTimelineTarget(
                    targetExpression,
                    out var frameIndex,
                    out var label))
            {
                var immediateKind = kind is
                    Avm1MirInstructionKind.TimelineGotoAndPlay
                        ? Avm1MirInstructionKind.TimelineGotoImmediateAndPlay
                        : Avm1MirInstructionKind.TimelineGotoImmediateAndStop;
                if (label is not null)
                {
                    _builder.AddInstruction(
                        immediateKind,
                        name: label,
                        effects: Avm1MirEffect.WritesRuntimeState,
                        origin: expression.Origin);
                }
                else
                {
                    var frameConstant = _builder.AddConstant(
                        Avm1MirConstantKind.Integer,
                        booleanValue: false,
                        integerValue: frameIndex,
                        numberValue: 0,
                        stringValue: null);
                    _builder.AddInstruction(
                        immediateKind,
                        constant: frameConstant,
                        effects: Avm1MirEffect.WritesRuntimeState,
                        origin: expression.Origin);
                }

                return resultRequired
                    ? AddUndefinedConstant(expression.Origin)
                    : Avm1MirValueIndex.Invalid;
            }

            var frame = LowerExpression(targetExpression);
            if (!frame.IsValid)
                return Avm1MirValueIndex.Invalid;

            _builder.AddInstruction(
                kind,
                operand: frame,
                effects: Avm1MirEffect.WritesRuntimeState,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerTimelineCallIntrinsic(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count != 1)
            {
                AddMalformed(
                    expression.Origin,
                    "Intrinsic call requires one frame argument.");
                return Avm1MirValueIndex.Invalid;
            }

            var frame = LowerExpression(_arena.GetChild(expression, 0));
            if (!frame.IsValid)
                return Avm1MirValueIndex.Invalid;

            _builder.AddInstruction(
                Avm1MirInstructionKind.TimelineCall,
                operand: frame,
                effects:
                    Avm1MirEffect.ReadsRuntimeState |
                    Avm1MirEffect.WritesRuntimeState |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerSceneTimelineGotoIntrinsic(
            Avm1SourceExpression expression,
            Avm1MirInstructionKind kind,
            bool resultRequired)
        {
            var name = _arena[expression.Name];
            if (_timelineLayout is null)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {name} scene overload requires timeline scene-layout metadata.");
                return Avm1MirValueIndex.Invalid;
            }

            if (!TryGetStringLiteral(
                    _arena.GetChild(expression, 0),
                    out var sceneName))
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {name} scene name must be a string literal.");
                return Avm1MirValueIndex.Invalid;
            }

            if (!_timelineLayout.TryGetScene(sceneName, out var scene))
            {
                AddMalformed(
                    expression.Origin,
                    $"Timeline scene {sceneName} is missing or ambiguous.");
                return Avm1MirValueIndex.Invalid;
            }
            if (scene.FrameOffset > ushort.MaxValue)
            {
                AddMalformed(
                    expression.Origin,
                    $"Timeline scene {sceneName} offset {scene.FrameOffset} exceeds the ActionGotoFrame2 scene-bias range.");
                return Avm1MirValueIndex.Invalid;
            }

            var frame = LowerExpression(_arena.GetChild(expression, 1));
            if (!frame.IsValid)
                return Avm1MirValueIndex.Invalid;

            var sceneBias = _builder.AddConstant(
                Avm1MirConstantKind.Integer,
                booleanValue: false,
                integerValue: checked((int)scene.FrameOffset),
                numberValue: 0,
                stringValue: null);
            _builder.AddInstruction(
                kind,
                operand: frame,
                constant: sceneBias,
                effects: Avm1MirEffect.WritesRuntimeState,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private bool TryGetImmediateTimelineTarget(
            SourceExpressionIndex index,
            out int frameIndex,
            out string? label)
        {
            frameIndex = -1;
            label = null;
            if (!index.IsValid || index.Value >= _arena.Expressions.Count)
                return false;

            var expression = _arena[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid ||
                expression.Literal.Value >= _arena.Literals.Count)
            {
                return false;
            }

            var literal = _arena[expression.Literal];
            if (literal.Kind is Avm1SourceLiteralKind.String &&
                literal.StringValue.IsValid &&
                literal.StringValue.Value < _arena.Strings.Count)
            {
                label = _arena[literal.StringValue];
                return true;
            }

            double sourceFrame = literal.Kind switch
            {
                Avm1SourceLiteralKind.Integer => literal.IntegerValue,
                Avm1SourceLiteralKind.Number => literal.NumberValue,
                _ => double.NaN
            };
            if (!double.IsFinite(sourceFrame) ||
                sourceFrame != Math.Truncate(sourceFrame) ||
                sourceFrame < 1 ||
                sourceFrame > ushort.MaxValue + 1d)
            {
                return false;
            }

            frameIndex = checked((int)sourceFrame - 1);
            return true;
        }

        private Avm1MirValueIndex LowerGetUrl(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count is < 1 or > 3)
            {
                AddMalformed(
                    expression.Origin,
                    "Intrinsic getURL requires one to three arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            var methodFlags = GetUrlFlags.MethodNone;
            if (expression.Children.Count == 3)
            {
                if (!TryGetStringLiteral(
                    _arena.GetChild(expression, 2),
                    out var method))
                {
                    AddMalformed(
                        expression.Origin,
                        "Intrinsic getURL method must be a string literal GET or POST.");
                    return Avm1MirValueIndex.Invalid;
                }

                methodFlags = method.ToUpperInvariant() switch
                {
                    "GET" => GetUrlFlags.MethodGet,
                    "POST" => GetUrlFlags.MethodPost,
                    _ => GetUrlFlags.ReservedMask
                };
                if (methodFlags is GetUrlFlags.ReservedMask)
                {
                    AddMalformed(
                        expression.Origin,
                        "Intrinsic getURL method must be GET or POST.");
                    return Avm1MirValueIndex.Invalid;
                }
            }

            var urlExpression = _arena.GetChild(expression, 0);
            var hasTarget = expression.Children.Count >= 2;
            var targetExpression = hasTarget
                ? _arena.GetChild(expression, 1)
                : SourceExpressionIndex.Invalid;
            var literalTarget = string.Empty;
            var hasLiteralTarget = !hasTarget ||
                TryGetStringLiteral(targetExpression, out literalTarget);
            if (methodFlags is GetUrlFlags.MethodNone &&
                TryGetStringLiteral(urlExpression, out var literalUrl) &&
                hasLiteralTarget)
            {
                var site = _builder.AddUrlSite(
                    literalUrl,
                    literalTarget);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.GetUrlImmediate,
                    urlSite: site,
                    effects: GetUrlEffects,
                    origin: expression.Origin);
            }
            else
            {
                var url = LowerExpression(urlExpression);
                if (!url.IsValid)
                    return Avm1MirValueIndex.Invalid;
                var target = hasTarget
                    ? LowerExpression(targetExpression)
                    : AddStringConstant(string.Empty, expression.Origin);
                if (!target.IsValid)
                    return Avm1MirValueIndex.Invalid;
                var site = _builder.AddStackUrlSite(methodFlags);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.GetUrl2,
                    operand: url,
                    secondaryOperand: target,
                    urlSite: site,
                    effects: GetUrlEffects,
                    origin: expression.Origin);
            }

            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerFsCommand(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count is < 1 or > 2)
            {
                AddMalformed(
                    expression.Origin,
                    "Intrinsic fscommand requires one or two arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            const string prefix = "FSCommand:";
            var commandExpression = _arena.GetChild(expression, 0);
            var hasParameters = expression.Children.Count == 2;
            var parameterExpression = hasParameters
                ? _arena.GetChild(expression, 1)
                : SourceExpressionIndex.Invalid;
            var literalParameters = string.Empty;
            var hasLiteralParameters = !hasParameters ||
                TryGetStringLiteral(parameterExpression, out literalParameters);
            if (TryGetStringLiteral(commandExpression, out var literalCommand) &&
                hasLiteralParameters)
            {
                var site = _builder.AddUrlSite(
                    prefix + literalCommand,
                    literalParameters);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.GetUrlImmediate,
                    urlSite: site,
                    effects: GetUrlEffects,
                    origin: expression.Origin);
            }
            else
            {
                var prefixValue = AddStringConstant(prefix, expression.Origin);
                var commandValue = LowerExpression(commandExpression);
                if (!commandValue.IsValid)
                    return Avm1MirValueIndex.Invalid;
                var url = _builder.AddValueInstruction(
                    Avm1MirInstructionKind.Binary,
                    prefixValue,
                    commandValue,
                    @operator: Avm1MirOperator.Add,
                    effects: Avm1MirEffect.MayInvokeUserCode |
                        Avm1MirEffect.MayThrow,
                    origin: expression.Origin);
                var target = hasParameters
                    ? LowerExpression(parameterExpression)
                    : AddStringConstant(string.Empty, expression.Origin);
                if (!target.IsValid)
                    return Avm1MirValueIndex.Invalid;
                var site = _builder.AddStackUrlSite(GetUrlFlags.MethodNone);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.GetUrl2,
                    operand: url,
                    secondaryOperand: target,
                    urlSite: site,
                    effects: GetUrlEffects,
                    origin: expression.Origin);
            }

            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerUrlLoad(
            Avm1SourceExpression expression,
            bool resultRequired,
            string intrinsicName,
            bool numericLevel,
            GetUrlFlags baseFlags)
        {
            if (expression.Children.Count is < 2 or > 3)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {intrinsicName} requires two or three arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            var flags = baseFlags;
            if (expression.Children.Count == 3)
            {
                if (!TryGetStringLiteral(
                        _arena.GetChild(expression, 2),
                        out var method))
                {
                    AddMalformed(
                        expression.Origin,
                        $"Intrinsic {intrinsicName} method must be a string literal GET or POST.");
                    return Avm1MirValueIndex.Invalid;
                }

                var methodFlags = method.ToUpperInvariant() switch
                {
                    "GET" => GetUrlFlags.MethodGet,
                    "POST" => GetUrlFlags.MethodPost,
                    _ => GetUrlFlags.ReservedMask
                };
                if (methodFlags is GetUrlFlags.ReservedMask)
                {
                    AddMalformed(
                        expression.Origin,
                        $"Intrinsic {intrinsicName} method must be GET or POST.");
                    return Avm1MirValueIndex.Invalid;
                }
                flags |= methodFlags;
            }

            var urlExpression = _arena.GetChild(expression, 0);
            var targetExpression = _arena.GetChild(expression, 1);
            if (numericLevel &&
                (flags & GetUrlFlags.LoadVariables) == 0 &&
                (flags & GetUrlFlags.MethodMask) is GetUrlFlags.MethodNone &&
                TryGetStringLiteral(urlExpression, out var literalUrl) &&
                TryGetLevelLiteral(targetExpression, out var literalLevel))
            {
                var immediateSite = _builder.AddUrlSite(
                    literalUrl,
                    "_level" + literalLevel);
                _builder.AddInstruction(
                    Avm1MirInstructionKind.GetUrlImmediate,
                    urlSite: immediateSite,
                    effects: GetUrlEffects,
                    origin: expression.Origin);
                return resultRequired
                    ? AddUndefinedConstant(expression.Origin)
                    : Avm1MirValueIndex.Invalid;
            }

            var url = LowerExpression(urlExpression);
            if (!url.IsValid)
                return Avm1MirValueIndex.Invalid;

            Avm1MirValueIndex target;
            if (!numericLevel)
            {
                target = LowerExpression(targetExpression);
            }
            else if (TryGetLevelLiteral(targetExpression, out literalLevel))
            {
                target = AddStringConstant(
                    "_level" + literalLevel,
                    expression.Origin);
            }
            else
            {
                var prefix = AddStringConstant("_level", expression.Origin);
                var level = LowerExpression(targetExpression);
                if (!level.IsValid)
                    return Avm1MirValueIndex.Invalid;
                target = _builder.AddValueInstruction(
                    Avm1MirInstructionKind.Binary,
                    operand: prefix,
                    secondaryOperand: level,
                    @operator: Avm1MirOperator.StringAdd,
                    effects: Avm1MirEffect.MayInvokeUserCode |
                        Avm1MirEffect.MayThrow,
                    origin: expression.Origin);
            }

            if (!target.IsValid)
                return Avm1MirValueIndex.Invalid;
            var site = _builder.AddStackUrlSite(flags);
            _builder.AddInstruction(
                Avm1MirInstructionKind.GetUrl2,
                operand: url,
                secondaryOperand: target,
                urlSite: site,
                effects: GetUrlEffects,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private bool TryGetLevelLiteral(
            SourceExpressionIndex index,
            out string value)
        {
            value = string.Empty;
            if (!index.IsValid || index.Value >= _arena.Expressions.Count)
                return false;

            var expression = _arena[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid ||
                expression.Literal.Value >= _arena.Literals.Count)
            {
                return false;
            }

            var literal = _arena[expression.Literal];
            value = literal.Kind switch
            {
                Avm1SourceLiteralKind.Integer =>
                    literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
                Avm1SourceLiteralKind.Number =>
                    Avm1Machine.FormatNumber(literal.NumberValue),
                _ => string.Empty
            };
            return literal.Kind is
                Avm1SourceLiteralKind.Integer or Avm1SourceLiteralKind.Number;
        }

        private bool TryGetStringLiteral(
            SourceExpressionIndex index,
            out string value)
        {
            value = string.Empty;
            if (!index.IsValid || index.Value >= _arena.Expressions.Count)
                return false;

            var expression = _arena[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid ||
                expression.Literal.Value >= _arena.Literals.Count)
            {
                return false;
            }

            var literal = _arena[expression.Literal];
            if (literal.Kind is not Avm1SourceLiteralKind.String ||
                !literal.StringValue.IsValid ||
                literal.StringValue.Value >= _arena.Strings.Count)
            {
                return false;
            }

            value = _arena[literal.StringValue];
            return true;
        }

        private const Avm1MirEffect GetUrlEffects =
            Avm1MirEffect.WritesRuntimeState |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow;

        private Avm1MirValueIndex LowerUnaryIntrinsic(
            Avm1SourceExpression expression,
            Avm1MirOperator @operator,
            Avm1MirEffect effects)
        {
            if (expression.Children.Count != 1)
            {
                AddMalformed(
                    expression.Origin,
                    $"Intrinsic {_arena[expression.Name]} requires exactly one argument.");
                return Avm1MirValueIndex.Invalid;
            }

            var operand = LowerExpression(_arena.GetChild(expression, 0));
            if (!operand.IsValid)
                return Avm1MirValueIndex.Invalid;
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Unary,
                operand,
                @operator: @operator,
                effects: effects,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerGetTime(Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 0)
            {
                AddMalformed(
                    expression.Origin,
                    "Intrinsic getTimer requires no arguments.");
                return Avm1MirValueIndex.Invalid;
            }

            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.GetTime,
                effects: Avm1MirEffect.ReadsRuntimeState,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerTrace(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count != 1)
            {
                AddMalformed(
                    expression.Origin,
                    "Intrinsic trace requires exactly one argument.");
                return Avm1MirValueIndex.Invalid;
            }

            var value = LowerExpression(_arena.GetChild(expression, 0));
            if (!value.IsValid)
                return Avm1MirValueIndex.Invalid;
            _builder.AddInstruction(
                Avm1MirInstructionKind.Trace,
                value,
                effects: Avm1MirEffect.WritesRuntimeState |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow,
                origin: expression.Origin);
            return resultRequired
                ? AddUndefinedConstant(expression.Origin)
                : Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex UnsupportedIntrinsic(
            Avm1SourceExpression expression,
            string name)
        {
            AddDiagnostic(
                "AVM1CMP101",
                expression.Origin,
                $"Source intrinsic {name} is not supported by the current method backend.");
            return Avm1MirValueIndex.Invalid;
        }

        private Avm1MirValueIndex LowerFunctionLiteral(
            Avm1SourceExpression expression)
        {
            if (!TryGetFunction(expression, out var function))
            {
                AddMalformed(
                    expression.Origin,
                    "Function literal has no valid function definition.");
                return Avm1MirValueIndex.Invalid;
            }
            if (function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration))
            {
                AddMalformed(
                    expression.Origin,
                    "Function declaration cannot be used as a value expression.");
                return Avm1MirValueIndex.Invalid;
            }
            var site = _builder.AddFunctionSite(
                function.Index,
                string.Empty,
                isDeclaration: false,
                expression.Origin);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.DefineFunction,
                functionSite: site,
                effects: Avm1MirEffect.Allocates,
                origin: expression.Origin);
        }

        private bool TryGetFunction(
            Avm1SourceExpression expression,
            out Avm1SourceFunction function)
        {
            if (expression.Function.IsValid &&
                expression.Function.Value < _arena.Functions.Count)
            {
                function = _arena[expression.Function];
                if (function.Body.IsValid &&
                    function.Body.Value < _arena.Statements.Count)
                {
                    return true;
                }
            }

            function = default;
            return false;
        }

        private Avm1MirValueIndex LowerLiteral(Avm1SourceExpression expression)
        {
            if (!expression.Literal.IsValid ||
                expression.Literal.Value >= _arena.Literals.Count)
            {
                AddMalformed(expression.Origin, "Literal expression has no valid literal.");
                return Avm1MirValueIndex.Invalid;
            }

            var literal = _arena[expression.Literal];
            string? stringValue = null;
            if (literal.Kind is Avm1SourceLiteralKind.String &&
                !TryGetString(
                    literal.StringValue,
                    expression.Origin,
                    "string literal",
                    out stringValue))
            {
                return Avm1MirValueIndex.Invalid;
            }

            var kind = literal.Kind switch
            {
                Avm1SourceLiteralKind.Undefined => Avm1MirConstantKind.Undefined,
                Avm1SourceLiteralKind.Null => Avm1MirConstantKind.Null,
                Avm1SourceLiteralKind.Boolean => Avm1MirConstantKind.Boolean,
                Avm1SourceLiteralKind.Integer => Avm1MirConstantKind.Integer,
                Avm1SourceLiteralKind.Number => Avm1MirConstantKind.Number,
                Avm1SourceLiteralKind.String => Avm1MirConstantKind.String,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(expression),
                    literal.Kind,
                    "Unknown Source HIR literal kind.")
            };
            var constant = _builder.AddConstant(
                kind,
                literal.BooleanValue,
                literal.IntegerValue,
                literal.NumberValue,
                stringValue);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Constant,
                constant: constant,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerDelete(Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 1)
            {
                AddMalformed(
                    expression.Origin,
                    "Delete expression must have exactly one target child.");
                return Avm1MirValueIndex.Invalid;
            }

            if (!TryLowerDeleteLValue(
                    _arena.GetChild(expression, 0),
                    expression.Origin,
                    out var lValue))
            {
                return Avm1MirValueIndex.Invalid;
            }

            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.DeleteLValue,
                lValue: lValue,
                effects: GetWriteEffects(_builder.GetLValue(lValue)),
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerAssignment(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count != 2)
            {
                AddMalformed(expression.Origin, "Assignment expression must have two children.");
                return Avm1MirValueIndex.Invalid;
            }

            var isSimpleAssignment = expression.Operator is Avm1SourceOperator.Assign;
            var isCompoundAssignment = TryMapCompoundAssignmentOperator(
                expression.Operator,
                out var binaryOperator);
            if (!isSimpleAssignment && !isCompoundAssignment)
            {
                AddDiagnostic(
                    "AVM1CMP101",
                    expression.Origin,
                    $"Assignment operator {expression.Operator} requires the " +
                    "general lvalue backend.");
                return Avm1MirValueIndex.Invalid;
            }

            var target = _arena.GetChild(expression, 0);
            if (!TryLowerLValue(
                target,
                expression.Origin,
                out var lValue,
                captureMemberAddress: isCompoundAssignment))
            {
                return Avm1MirValueIndex.Invalid;
            }

            var lValueData = _builder.GetLValue(lValue);
            try
            {
                var value = Avm1MirValueIndex.Invalid;
                if (isSimpleAssignment)
                {
                    value = LowerExpression(_arena.GetChild(expression, 1));
                }
                else
                {
                    var readLValue = lValue;
                    if (_context.ExpressionEvaluationMode is
                            Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible &&
                        RequiresAdobeMemberAddressReevaluation(target) &&
                        !TryLowerLValue(
                            target,
                            expression.Origin,
                            out readLValue))
                    {
                        return Avm1MirValueIndex.Invalid;
                    }

                    var current = _builder.AddValueInstruction(
                        Avm1MirInstructionKind.LoadLValue,
                        lValue: readLValue,
                        effects: GetReadEffects(_builder.GetLValue(readLValue)),
                        origin: expression.Origin);
                    var right = LowerExpression(_arena.GetChild(expression, 1));
                    if (right.IsValid)
                    {
                        value = _builder.AddValueInstruction(
                            Avm1MirInstructionKind.Binary,
                            operand: current,
                            secondaryOperand: right,
                            @operator: binaryOperator,
                            effects: Avm1MirEffect.MayInvokeUserCode |
                                Avm1MirEffect.MayThrow,
                            origin: expression.Origin);
                    }
                }

                if (!value.IsValid)
                    return Avm1MirValueIndex.Invalid;

                var effects = GetWriteEffects(lValueData);
                if (resultRequired)
                {
                    return _builder.AddValueInstruction(
                        Avm1MirInstructionKind.StoreLValue,
                        operand: value,
                        lValue: lValue,
                        effects: effects,
                        origin: expression.Origin);
                }

                _builder.AddInstruction(
                    Avm1MirInstructionKind.StoreLValue,
                    operand: value,
                    lValue: lValue,
                    effects: effects,
                    origin: expression.Origin);
                return Avm1MirValueIndex.Invalid;
            }
            finally
            {
                ReleaseCapturedMemberLValue(lValueData);
            }
        }

        private bool RequiresAdobeMemberAddressReevaluation(
            SourceExpressionIndex target)
        {
            var member = _arena[target];
            if (member.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                member.Children.Count != 2)
            {
                return false;
            }

            if (_context.IsInDynamicScope)
                return true;

            return !IsStableMemberAddressComponent(
                    _arena.GetChild(member, 0)) ||
                !IsStableMemberAddressComponent(
                    _arena.GetChild(member, 1));
        }

        private bool IsStableMemberAddressComponent(SourceExpressionIndex index)
        {
            var expression = _arena[index];
            return expression.Kind is
                Avm1SourceExpressionKind.Literal or
                Avm1SourceExpressionKind.SymbolReference or
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.QualifiedName;
        }

        private Avm1MirValueIndex LowerInvocation(
            Avm1SourceExpression expression,
            Avm1MirInvocationKind kind)
        {
            if (expression.Children.Count == 0)
            {
                AddMalformed(
                    expression.Origin,
                    $"{kind} expression must have a target child.");
                return Avm1MirValueIndex.Invalid;
            }

            var arguments = new Avm1MirValueIndex[expression.Children.Count - 1];
            var adobeEvaluation = _context.ExpressionEvaluationMode is
                Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible;
            Avm1MirInvocationTargetKind targetKind;
            Avm1MirValueIndex target;
            Avm1MirValueIndex memberName;
            string? name;
            Avm1MirNameKind nameKind;
            SourceSymbolIndex targetSymbol;
            if (adobeEvaluation)
            {
                for (var i = arguments.Length - 1; i >= 0; i--)
                {
                    arguments[i] = LowerExpression(
                        _arena.GetChild(expression, i + 1));
                    if (!arguments[i].IsValid)
                        return Avm1MirValueIndex.Invalid;
                }
                if (!TryLowerInvocationTarget(
                        _arena.GetChild(expression, 0),
                        expression.Origin,
                        out targetKind,
                        out target,
                        out memberName,
                        out name,
                        out nameKind,
                        out targetSymbol))
                {
                    return Avm1MirValueIndex.Invalid;
                }
            }
            else
            {
                if (!TryLowerInvocationTarget(
                        _arena.GetChild(expression, 0),
                        expression.Origin,
                        out targetKind,
                        out target,
                        out memberName,
                        out name,
                        out nameKind,
                        out targetSymbol))
                {
                    return Avm1MirValueIndex.Invalid;
                }
                for (var i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = LowerExpression(
                        _arena.GetChild(expression, i + 1));
                    if (!arguments[i].IsValid)
                        return Avm1MirValueIndex.Invalid;
                }
            }

            var callSite = _builder.AddCallSite(
                kind,
                targetKind,
                target,
                memberName,
                name,
                nameKind,
                targetSymbol,
                arguments,
                adobeEvaluation
                        ? Avm1MirEvaluationOrder.Reverse
                        : Avm1MirEvaluationOrder.Forward,
                adobeEvaluation
                    ? Avm1MirInvocationTargetOrder.AfterArguments
                    : Avm1MirInvocationTargetOrder.BeforeArguments,
                expression.Origin);
            var effects = Avm1MirEffect.ReadsDynamicScope |
                Avm1MirEffect.WritesDynamicScope |
                Avm1MirEffect.ReadsHeap |
                Avm1MirEffect.WritesHeap |
                Avm1MirEffect.MayInvokeUserCode |
                Avm1MirEffect.MayThrow;
            if (kind is Avm1MirInvocationKind.Construct)
                effects |= Avm1MirEffect.Allocates;

            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Invoke,
                callSite: callSite,
                effects: effects,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerDirectEval(
            Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 2)
            {
                AddMalformed(
                    expression.Origin,
                    "Direct eval call must have exactly one argument.");
                return Avm1MirValueIndex.Invalid;
            }

            return LowerEvaluatedName(
                _arena.GetChild(expression, 1),
                expression.Origin);
        }

        private Avm1MirValueIndex LowerComputedDynamicName(
            Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 1)
            {
                AddMalformed(
                    expression.Origin,
                    "Computed dynamic name must have exactly one child.");
                return Avm1MirValueIndex.Invalid;
            }

            return LowerEvaluatedName(
                _arena.GetChild(expression, 0),
                expression.Origin);
        }

        private Avm1MirValueIndex LowerEvaluatedName(
            SourceExpressionIndex name,
            SourceOriginIndex origin)
        {
            var value = LowerExpression(name);
            if (!value.IsValid)
                return Avm1MirValueIndex.Invalid;

            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.EvaluateName,
                operand: value,
                effects: Avm1MirEffect.ReadsActivation |
                    Avm1MirEffect.ReadsDynamicScope |
                    Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow,
                origin: origin);
        }

        private Avm1MirValueIndex LowerAggregate(
            Avm1SourceExpression expression,
            Avm1MirAggregateKind kind)
        {
            if (kind is Avm1MirAggregateKind.Object &&
                expression.Children.Count % 2 != 0)
            {
                AddMalformed(
                    expression.Origin,
                    "Object literal must contain alternating key and value children.");
                return Avm1MirValueIndex.Invalid;
            }

            var values = new Avm1MirValueIndex[expression.Children.Count];
            if (kind is Avm1MirAggregateKind.Array &&
                _context.ExpressionEvaluationMode is
                    Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible)
            {
                for (var i = values.Length - 1; i >= 0; i--)
                {
                    values[i] = LowerExpression(_arena.GetChild(expression, i));
                    if (!values[i].IsValid)
                        return Avm1MirValueIndex.Invalid;
                }
            }
            else
            {
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = LowerExpression(_arena.GetChild(expression, i));
                    if (!values[i].IsValid)
                        return Avm1MirValueIndex.Invalid;
                }
            }

            var aggregateSite = _builder.AddAggregateSite(
                kind,
                values,
                kind is Avm1MirAggregateKind.Array &&
                    _context.ExpressionEvaluationMode is
                        Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
                            ? Avm1MirEvaluationOrder.Reverse
                            : Avm1MirEvaluationOrder.Forward,
                expression.Origin);
            var effects = Avm1MirEffect.Allocates;
            if (kind is Avm1MirAggregateKind.Object &&
                RequiresObjectKeyConversionEffects(expression))
            {
                effects |= Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow;
            }
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Aggregate,
                aggregateSite: aggregateSite,
                effects: effects,
                origin: expression.Origin);
        }

        private bool RequiresObjectKeyConversionEffects(
            Avm1SourceExpression expression)
        {
            for (var i = 0; i < expression.Children.Count; i += 2)
            {
                var key = _arena[_arena.GetChild(expression, i)];
                if (key.Kind is not Avm1SourceExpressionKind.Literal ||
                    !key.Literal.IsValid ||
                    key.Literal.Value >= _arena.Literals.Count)
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryLowerInvocationTarget(
            SourceExpressionIndex index,
            SourceOriginIndex origin,
            out Avm1MirInvocationTargetKind targetKind,
            out Avm1MirValueIndex target,
            out Avm1MirValueIndex memberName,
            out string? name,
            out Avm1MirNameKind nameKind,
            out SourceSymbolIndex symbol)
        {
            targetKind = Avm1MirInvocationTargetKind.Value;
            target = Avm1MirValueIndex.Invalid;
            memberName = Avm1MirValueIndex.Invalid;
            name = null;
            nameKind = Avm1MirNameKind.None;
            symbol = SourceSymbolIndex.Invalid;
            if (!TryGetExpression(index, out var expression))
                return false;

            switch (expression.Kind)
            {
                case Avm1SourceExpressionKind.SymbolReference:
                    symbol = expression.Symbol;
                    if (!TryGetSymbolName(expression.Symbol, origin, out name))
                        return false;
                    if (_context.SourceRegisters.TryGetRegister(
                            expression.Symbol,
                            out _))
                    {
                        target = LowerExpression(index);
                        return target.IsValid;
                    }
                    targetKind = Avm1MirInvocationTargetKind.Name;
                    nameKind = Avm1MirNameKind.BoundSymbol;
                    return true;

                case Avm1SourceExpressionKind.DynamicName:
                case Avm1SourceExpressionKind.QualifiedName:
                    if (!TryGetString(
                        expression.Name,
                        origin,
                        "invocation target",
                        out name))
                    {
                        return false;
                    }
                    targetKind = Avm1MirInvocationTargetKind.Name;
                    nameKind = expression.Kind is Avm1SourceExpressionKind.DynamicName
                        ? Avm1MirNameKind.DynamicName
                        : Avm1MirNameKind.QualifiedName;
                    return true;

                case Avm1SourceExpressionKind.MemberAccess:
                    if (expression.Children.Count != 2)
                    {
                        AddMalformed(
                            expression.Origin,
                            "Invocation member target must have receiver and key children.");
                        return false;
                    }
                    target = LowerExpression(_arena.GetChild(expression, 0));
                    memberName = LowerExpression(_arena.GetChild(expression, 1));
                    if (!target.IsValid || !memberName.IsValid)
                        return false;
                    targetKind = Avm1MirInvocationTargetKind.Member;
                    return true;

                default:
                    target = LowerExpression(index);
                    return target.IsValid;
            }
        }

        private Avm1MirValueIndex LowerUnary(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count != 1)
            {
                AddMalformed(expression.Origin, "Unary expression must have one child.");
                return Avm1MirValueIndex.Invalid;
            }

            if (expression.Operator is
                Avm1SourceOperator.PrefixIncrement or
                Avm1SourceOperator.PrefixDecrement or
                Avm1SourceOperator.PostfixIncrement or
                Avm1SourceOperator.PostfixDecrement)
            {
                return LowerStep(expression, resultRequired);
            }

            if (expression.Operator is Avm1SourceOperator.Void)
            {
                var value = LowerExpression(_arena.GetChild(expression, 0));
                if (value.IsValid)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.Discard,
                        value,
                        origin: expression.Origin);
                }
                return resultRequired
                    ? AddConstantValue(
                        Avm1MirConstantKind.Undefined,
                        expression.Origin)
                    : Avm1MirValueIndex.Invalid;
            }

            if (expression.Operator is Avm1SourceOperator.UnaryMinus)
            {
                var constant = AddConstantValue(
                    Avm1MirConstantKind.Integer,
                    expression.Origin,
                    integerValue: 0);
                var operandValue = LowerExpression(_arena.GetChild(expression, 0));
                if (!operandValue.IsValid)
                    return Avm1MirValueIndex.Invalid;
                return _builder.AddValueInstruction(
                    Avm1MirInstructionKind.Binary,
                    operand: constant,
                    secondaryOperand: operandValue,
                    @operator: Avm1MirOperator.Subtract,
                    origin: expression.Origin);
            }

            if (expression.Operator is Avm1SourceOperator.BitwiseNot)
            {
                var operandValue = LowerExpression(_arena.GetChild(expression, 0));
                if (!operandValue.IsValid)
                    return Avm1MirValueIndex.Invalid;
                var constant = AddConstantValue(
                    Avm1MirConstantKind.Integer,
                    expression.Origin,
                    integerValue: -1);
                return _builder.AddValueInstruction(
                    Avm1MirInstructionKind.Binary,
                    operand: operandValue,
                    secondaryOperand: constant,
                    @operator: Avm1MirOperator.BitXor,
                    origin: expression.Origin);
            }

            var @operator = expression.Operator switch
            {
                Avm1SourceOperator.LogicalNot => Avm1MirOperator.LogicalNot,
                Avm1SourceOperator.TypeOf => Avm1MirOperator.TypeOf,
                Avm1SourceOperator.UnaryPlus => Avm1MirOperator.ToNumber,
                _ => Avm1MirOperator.None
            };
            if (@operator is Avm1MirOperator.None)
            {
                AddDiagnostic(
                    "AVM1CMP101",
                    expression.Origin,
                    $"Unary operator {expression.Operator} is not supported by " +
                    "the current method backend.");
                return Avm1MirValueIndex.Invalid;
            }

            var operand = LowerExpression(_arena.GetChild(expression, 0));
            if (!operand.IsValid)
                return Avm1MirValueIndex.Invalid;

            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Unary,
                operand: operand,
                @operator: @operator,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerSequence(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            if (expression.Children.Count == 0)
            {
                AddMalformed(
                    expression.Origin,
                    "Sequence expression must have at least one child.");
                return Avm1MirValueIndex.Invalid;
            }

            for (var i = 0; i + 1 < expression.Children.Count; i++)
            {
                var value = LowerExpression(_arena.GetChild(expression, i));
                if (value.IsValid)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.Discard,
                        value,
                        origin: expression.Origin);
                }
            }

            return LowerExpression(
                _arena.GetChild(expression, expression.Children.Count - 1),
                resultRequired);
        }

        private Avm1MirValueIndex AddConstantValue(
            Avm1MirConstantKind kind,
            SourceOriginIndex origin,
            int integerValue = 0)
        {
            var constant = _builder.AddConstant(
                kind,
                booleanValue: false,
                integerValue,
                numberValue: 0,
                stringValue: null);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Constant,
                constant: constant,
                origin: origin);
        }

        private Avm1MirValueIndex LowerStep(
            Avm1SourceExpression expression,
            bool resultRequired)
        {
            var postfix = expression.Operator is
                Avm1SourceOperator.PostfixIncrement or
                Avm1SourceOperator.PostfixDecrement;
            if (!TryLowerLValue(
                _arena.GetChild(expression, 0),
                expression.Origin,
                out var lValue,
                captureMemberAddress: true))
            {
                return Avm1MirValueIndex.Invalid;
            }

            var lValueData = _builder.GetLValue(lValue);
            var oldValueTemporary = Avm1MirTemporaryIndex.Invalid;
            try
            {
                var current = _builder.AddValueInstruction(
                    Avm1MirInstructionKind.LoadLValue,
                    lValue: lValue,
                    effects: GetReadEffects(lValueData),
                    origin: expression.Origin);
                if (postfix && resultRequired)
                {
                    current = _builder.AddValueInstruction(
                        Avm1MirInstructionKind.Unary,
                        operand: current,
                        @operator: Avm1MirOperator.ToNumber,
                        effects: Avm1MirEffect.MayInvokeUserCode |
                            Avm1MirEffect.MayThrow,
                        origin: expression.Origin);
                    oldValueTemporary = _builder.AcquireTemporary();
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.StoreTemporary,
                        operand: current,
                        temporary: oldValueTemporary,
                        origin: expression.Origin);
                    current = _builder.AddValueInstruction(
                        Avm1MirInstructionKind.LoadTemporary,
                        temporary: oldValueTemporary,
                        origin: expression.Origin);
                }

                var updated = _builder.AddValueInstruction(
                    Avm1MirInstructionKind.Unary,
                    operand: current,
                    @operator: expression.Operator is
                        Avm1SourceOperator.PrefixIncrement or
                        Avm1SourceOperator.PostfixIncrement
                            ? Avm1MirOperator.Increment
                            : Avm1MirOperator.Decrement,
                    effects: Avm1MirEffect.MayInvokeUserCode |
                        Avm1MirEffect.MayThrow,
                    origin: expression.Origin);
                var writeEffects = GetWriteEffects(lValueData);
                if (postfix && resultRequired)
                {
                    _builder.AddInstruction(
                        Avm1MirInstructionKind.StoreLValue,
                        operand: updated,
                        lValue: lValue,
                        effects: writeEffects,
                        origin: expression.Origin);
                    return _builder.AddValueInstruction(
                        Avm1MirInstructionKind.LoadTemporary,
                        temporary: oldValueTemporary,
                        origin: expression.Origin);
                }
                if (resultRequired)
                {
                    return _builder.AddValueInstruction(
                        Avm1MirInstructionKind.StoreLValue,
                        operand: updated,
                        lValue: lValue,
                        effects: writeEffects,
                        origin: expression.Origin);
                }

                _builder.AddInstruction(
                    Avm1MirInstructionKind.StoreLValue,
                    operand: updated,
                    lValue: lValue,
                    effects: writeEffects,
                    origin: expression.Origin);
                return Avm1MirValueIndex.Invalid;
            }
            finally
            {
                if (oldValueTemporary.IsValid)
                    _builder.ReleaseTemporary(oldValueTemporary);
                ReleaseCapturedMemberLValue(lValueData);
            }
        }

        private Avm1MirValueIndex LowerBinary(Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 2)
            {
                AddMalformed(expression.Origin, "Binary expression must have two children.");
                return Avm1MirValueIndex.Invalid;
            }

            if (!TryMapBinaryOperator(expression.Operator, out var @operator))
            {
                AddDiagnostic(
                    "AVM1CMP101",
                    expression.Origin,
                    $"Binary operator {expression.Operator} requires structured " +
                    "control flow or is not supported by the current backend.");
                return Avm1MirValueIndex.Invalid;
            }

            var left = LowerExpression(_arena.GetChild(expression, 0));
            var right = LowerExpression(_arena.GetChild(expression, 1));
            if (!left.IsValid || !right.IsValid)
                return Avm1MirValueIndex.Invalid;

            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Binary,
                operand: left,
                secondaryOperand: right,
                @operator: @operator,
                effects: Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerLogical(Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 2)
            {
                AddMalformed(
                    expression.Origin,
                    "Short-circuit expression must have two children.");
                return Avm1MirValueIndex.Invalid;
            }

            var left = LowerExpression(_arena.GetChild(expression, 0));
            if (!left.IsValid)
                return Avm1MirValueIndex.Invalid;

            var leftBlock = _builder.CurrentBlock;
            var rightBlock = _builder.CreateBlock();
            var mergeBlock = _builder.CreateBlock();
            var phiSite = _builder.CreatePhiSite(mergeBlock, expression.Origin);
            _builder.AddPhiIncoming(phiSite, leftBlock, left);
            var stagedLeft = _builder.AddValueInstruction(
                Avm1MirInstructionKind.StorePhi,
                operand: left,
                phiSite: phiSite,
                origin: expression.Origin);

            var evaluateRightWhenTrue = expression.Operator is
                Avm1SourceOperator.LogicalAnd;
            _builder.AddInstruction(
                Avm1MirInstructionKind.BranchIfTrue,
                operand: stagedLeft,
                phiSite: phiSite,
                effects: Avm1MirEffect.ControlFlow,
                target: evaluateRightWhenTrue ? rightBlock : mergeBlock,
                alternativeTarget: evaluateRightWhenTrue ? mergeBlock : rightBlock,
                origin: expression.Origin);

            _builder.StartBlock(rightBlock, isReachable: true);
            var right = LowerExpression(_arena.GetChild(expression, 1));
            if (!right.IsValid)
            {
                _builder.StartBlock(mergeBlock, isReachable: true);
                return Avm1MirValueIndex.Invalid;
            }

            var rightPredecessor = _builder.CurrentBlock;
            _builder.AddPhiIncoming(phiSite, rightPredecessor, right);
            _builder.AddInstruction(
                Avm1MirInstructionKind.StorePhi,
                operand: right,
                phiSite: phiSite,
                origin: expression.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                phiSite: phiSite,
                effects: Avm1MirEffect.ControlFlow,
                target: mergeBlock,
                origin: expression.Origin);

            _builder.StartBlock(mergeBlock, isReachable: true);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Phi,
                phiSite: phiSite,
                origin: expression.Origin);
        }

        private Avm1MirValueIndex LowerConditional(
            Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 3)
            {
                AddMalformed(
                    expression.Origin,
                    "Conditional expression must have condition, true, and false children.");
                return Avm1MirValueIndex.Invalid;
            }

            var condition = LowerExpression(_arena.GetChild(expression, 0));
            if (!condition.IsValid)
                return Avm1MirValueIndex.Invalid;

            var whenTrueBlock = _builder.CreateBlock();
            var whenFalseBlock = _builder.CreateBlock();
            var mergeBlock = _builder.CreateBlock();
            var phiSite = _builder.CreatePhiSite(mergeBlock, expression.Origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.BranchIfTrue,
                operand: condition,
                phiSite: phiSite,
                effects: Avm1MirEffect.ControlFlow,
                target: whenTrueBlock,
                alternativeTarget: whenFalseBlock,
                origin: expression.Origin);

            _builder.StartBlock(whenTrueBlock, isReachable: true);
            var whenTrue = LowerExpression(_arena.GetChild(expression, 1));
            if (!whenTrue.IsValid)
            {
                _builder.StartBlock(whenFalseBlock, isReachable: true);
                _builder.StartBlock(mergeBlock, isReachable: true);
                return Avm1MirValueIndex.Invalid;
            }
            AddPhiIncomingAndBranch(
                phiSite,
                mergeBlock,
                whenTrue,
                expression.Origin);

            _builder.StartBlock(whenFalseBlock, isReachable: true);
            var whenFalse = LowerExpression(_arena.GetChild(expression, 2));
            if (!whenFalse.IsValid)
            {
                _builder.StartBlock(mergeBlock, isReachable: true);
                return Avm1MirValueIndex.Invalid;
            }
            AddPhiIncomingAndBranch(
                phiSite,
                mergeBlock,
                whenFalse,
                expression.Origin);

            _builder.StartBlock(mergeBlock, isReachable: true);
            return _builder.AddValueInstruction(
                Avm1MirInstructionKind.Phi,
                phiSite: phiSite,
                origin: expression.Origin);
        }

        private void AddPhiIncomingAndBranch(
            Avm1MirPhiSiteIndex phiSite,
            Avm1MirBlockIndex mergeBlock,
            Avm1MirValueIndex value,
            SourceOriginIndex origin)
        {
            _builder.AddPhiIncoming(phiSite, _builder.CurrentBlock, value);
            _builder.AddInstruction(
                Avm1MirInstructionKind.StorePhi,
                operand: value,
                phiSite: phiSite,
                origin: origin);
            _builder.AddInstruction(
                Avm1MirInstructionKind.Branch,
                phiSite: phiSite,
                effects: Avm1MirEffect.ControlFlow,
                target: mergeBlock,
                origin: origin);
        }

        private bool TryLowerLValue(
            SourceExpressionIndex index,
            SourceOriginIndex origin,
            out Avm1MirLValueIndex lValue,
            bool captureMemberAddress = false)
        {
            lValue = Avm1MirLValueIndex.Invalid;
            if (!TryGetExpression(index, out var target))
                return false;

            switch (target.Kind)
            {
                case Avm1SourceExpressionKind.SymbolReference:
                    if (!TryGetSymbolName(target.Symbol, origin, out var symbolName))
                        return false;
                    lValue = AddBoundSymbolLValue(
                        target.Symbol,
                        symbolName,
                        target.Origin);
                    return true;

                case Avm1SourceExpressionKind.DynamicName:
                    if (!TryGetString(
                        target.Name,
                        origin,
                        "assignment target",
                        out var dynamicName))
                    {
                        return false;
                    }
                    lValue = _builder.AddNameLValue(
                        dynamicName,
                        Avm1MirNameKind.DynamicName,
                        target.Origin);
                    return true;

                case Avm1SourceExpressionKind.QualifiedName:
                    if (!TryGetString(
                        target.Name,
                        origin,
                        "assignment target",
                        out var qualifiedName))
                    {
                        return false;
                    }
                    lValue = _builder.AddNameLValue(
                        qualifiedName,
                        Avm1MirNameKind.QualifiedName,
                        target.Origin);
                    return true;

                case Avm1SourceExpressionKind.MemberAccess:
                    if (target.Children.Count != 2)
                    {
                        AddMalformed(
                            target.Origin,
                            "Member access must have receiver and key children.");
                        return false;
                    }

                    var receiver = LowerExpression(_arena.GetChild(target, 0));
                    var key = LowerExpression(_arena.GetChild(target, 1));
                    if (!receiver.IsValid || !key.IsValid)
                        return false;

                    if (captureMemberAddress)
                    {
                        var receiverTemporary = _builder.AcquireTemporary();
                        var keyTemporary = _builder.AcquireTemporary();
                        _builder.AddInstruction(
                            Avm1MirInstructionKind.StoreTemporary,
                            operand: key,
                            temporary: keyTemporary,
                            origin: target.Origin);
                        _builder.AddInstruction(
                            Avm1MirInstructionKind.StoreTemporary,
                            operand: receiver,
                            temporary: receiverTemporary,
                            origin: target.Origin);
                        lValue = _builder.AddCapturedMemberLValue(
                            receiverTemporary,
                            keyTemporary,
                            target.Origin);
                    }
                    else
                    {
                        lValue = _builder.AddMemberLValue(
                            receiver,
                            key,
                            target.Origin);
                    }
                    return true;

                default:
                    AddDiagnostic(
                        "AVM1CMP102",
                        origin,
                        $"Assignment target {target.Kind} is not a supported lvalue.");
                    return false;
            }
        }

        private bool TryLowerDeleteLValue(
            SourceExpressionIndex index,
            SourceOriginIndex origin,
            out Avm1MirLValueIndex lValue)
        {
            lValue = Avm1MirLValueIndex.Invalid;
            if (!TryGetExpression(index, out var target))
                return false;

            switch (target.Kind)
            {
                case Avm1SourceExpressionKind.SymbolReference:
                    if (!TryGetSymbolName(target.Symbol, origin, out var symbolName))
                        return false;
                    lValue = _builder.AddNameLValue(
                        symbolName,
                        Avm1MirNameKind.BoundSymbol,
                        target.Origin,
                        target.Symbol);
                    return true;

                case Avm1SourceExpressionKind.DynamicName:
                case Avm1SourceExpressionKind.QualifiedName:
                    if (!TryGetString(
                        target.Name,
                        origin,
                        "delete target",
                        out var name))
                    {
                        return false;
                    }
                    lValue = _builder.AddNameLValue(
                        name,
                        target.Kind is Avm1SourceExpressionKind.DynamicName
                            ? Avm1MirNameKind.DynamicName
                            : Avm1MirNameKind.QualifiedName,
                        target.Origin);
                    return true;

                case Avm1SourceExpressionKind.ComputedDynamicName:
                    if (target.Children.Count != 1)
                    {
                        AddMalformed(
                            target.Origin,
                            "Computed dynamic name must have exactly one child.");
                        return false;
                    }
                    var computedName = LowerExpression(_arena.GetChild(target, 0));
                    if (!computedName.IsValid)
                        return false;
                    lValue = _builder.AddComputedNameLValue(
                        computedName,
                        target.Origin);
                    return true;

                case Avm1SourceExpressionKind.MemberAccess:
                    return TryLowerLValue(index, origin, out lValue);

                default:
                    AddDiagnostic(
                        "AVM1CMP102",
                        origin,
                        $"Delete target {target.Kind} is not a supported lvalue.");
                    return false;
            }
        }

        private void ReleaseCapturedMemberLValue(Avm1MirLValue lValue)
        {
            if (lValue.Kind is not Avm1MirLValueKind.CapturedMember)
                return;

            _builder.ReleaseTemporary(lValue.KeyTemporary);
            _builder.ReleaseTemporary(lValue.ReceiverTemporary);
        }

        private bool TryGetStatement(
            SourceStatementIndex index,
            out Avm1SourceStatement statement)
        {
            if (!index.IsValid || index.Value >= _arena.Statements.Count)
            {
                statement = default;
                AddMalformed(SourceOriginIndex.Invalid, "Statement handle is invalid.");
                return false;
            }

            statement = _arena[index];
            if (statement.Children.Start < 0 ||
                statement.Children.Count < 0 ||
                statement.Children.Start + statement.Children.Count >
                    _arena.StatementChildren.Count)
            {
                AddMalformed(statement.Origin, "Statement child range is invalid.");
                return false;
            }
            if (statement.Initializers.Start < 0 ||
                statement.Initializers.Count < 0 ||
                statement.Initializers.Start + statement.Initializers.Count >
                    _arena.StatementChildren.Count)
            {
                AddMalformed(statement.Origin, "Statement initializer range is invalid.");
                return false;
            }
            if (statement.Expressions.Start < 0 ||
                statement.Expressions.Count < 0 ||
                statement.Expressions.Start + statement.Expressions.Count >
                    _arena.ExpressionChildren.Count)
            {
                AddMalformed(statement.Origin, "Statement expression range is invalid.");
                return false;
            }
            return true;
        }

        private bool TryGetExpression(
            SourceExpressionIndex index,
            out Avm1SourceExpression expression)
        {
            if (!index.IsValid || index.Value >= _arena.Expressions.Count)
            {
                expression = default;
                AddMalformed(SourceOriginIndex.Invalid, "Expression handle is invalid.");
                return false;
            }

            expression = _arena[index];
            if (expression.Children.Start < 0 ||
                expression.Children.Count < 0 ||
                expression.Children.Start + expression.Children.Count >
                    _arena.ExpressionChildren.Count)
            {
                AddMalformed(expression.Origin, "Expression child range is invalid.");
                return false;
            }
            return true;
        }

        private bool TryGetSymbolName(
            SourceSymbolIndex index,
            SourceOriginIndex origin,
            out string name)
        {
            if (!index.IsValid || index.Value >= _arena.Symbols.Count)
            {
                name = string.Empty;
                AddMalformed(origin, "Symbol reference is invalid.");
                return false;
            }

            return TryGetString(_arena[index].Name, origin, "symbol", out name);
        }

        private bool TryGetString(
            SourceStringIndex index,
            SourceOriginIndex origin,
            string role,
            out string value)
        {
            if (!index.IsValid || index.Value >= _arena.Strings.Count)
            {
                value = string.Empty;
                AddMalformed(origin, $"The {role} has no valid string.");
                return false;
            }

            value = _arena[index];
            return true;
        }

        private void AddMalformed(SourceOriginIndex origin, string message) =>
            AddDiagnostic("AVM1CMP102", origin, message);

        private void AddDiagnostic(
            string code,
            SourceOriginIndex origin,
            string message) =>
            _diagnostics.Add(new Avm1CompilerDiagnostic(
                code,
                Avm1CompilationDiagnosticSeverity.Error,
                origin,
                -1,
                message));

        private Avm1MirEffect GetReadEffects(Avm1MirNameKind nameKind) =>
            nameKind is Avm1MirNameKind.BoundSymbol && !_context.IsInDynamicScope
                ? Avm1MirEffect.ReadsActivation
                : Avm1MirEffect.ReadsDynamicScope |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow;

        private Avm1MirEffect GetReadEffects(Avm1MirLValue lValue) =>
            lValue.Kind switch
            {
                Avm1MirLValueKind.Register => Avm1MirEffect.None,
                Avm1MirLValueKind.Name => GetReadEffects(lValue.NameKind),
                Avm1MirLValueKind.ComputedName =>
                    Avm1MirEffect.ReadsDynamicScope |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow,
                _ => Avm1MirEffect.ReadsHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow
            };

        private Avm1MirEffect GetWriteEffects(Avm1MirNameKind nameKind) =>
            nameKind is Avm1MirNameKind.BoundSymbol && !_context.IsInDynamicScope
                ? Avm1MirEffect.WritesActivation
                : Avm1MirEffect.WritesDynamicScope |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow;

        private Avm1MirEffect GetWriteEffects(Avm1MirLValue lValue) =>
            lValue.Kind switch
            {
                Avm1MirLValueKind.Register => Avm1MirEffect.None,
                Avm1MirLValueKind.Name => GetWriteEffects(lValue.NameKind),
                Avm1MirLValueKind.ComputedName =>
                    Avm1MirEffect.WritesDynamicScope |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow,
                _ => Avm1MirEffect.WritesHeap |
                    Avm1MirEffect.MayInvokeUserCode |
                    Avm1MirEffect.MayThrow
            };

        private Avm1MirLValueIndex AddBoundSymbolLValue(
            SourceSymbolIndex symbol,
            string name,
            SourceOriginIndex origin)
        {
            if (symbol.IsValid &&
                symbol.Value < _arena.Symbols.Count &&
                _context.FunctionPreloads.TryGetRegister(
                    _arena[symbol].Kind,
                    out var preloadRegister))
            {
                return _builder.AddRegisterLValue(
                    symbol,
                    preloadRegister,
                    origin);
            }

            return _context.SourceRegisters.TryGetRegister(
                symbol,
                out var register)
                ? _builder.AddRegisterLValue(symbol, register, origin)
                : _builder.AddNameLValue(
                    name,
                    Avm1MirNameKind.BoundSymbol,
                    origin,
                    symbol);
        }

        private static bool TryMapBinaryOperator(
            Avm1SourceOperator source,
            out Avm1MirOperator result)
        {
            result = source switch
            {
                Avm1SourceOperator.Add => Avm1MirOperator.Add,
                Avm1SourceOperator.Subtract => Avm1MirOperator.Subtract,
                Avm1SourceOperator.Multiply => Avm1MirOperator.Multiply,
                Avm1SourceOperator.Divide => Avm1MirOperator.Divide,
                Avm1SourceOperator.Modulo => Avm1MirOperator.Modulo,
                Avm1SourceOperator.Equal => Avm1MirOperator.Equal,
                Avm1SourceOperator.NotEqual => Avm1MirOperator.NotEqual,
                Avm1SourceOperator.StrictEqual => Avm1MirOperator.StrictEqual,
                Avm1SourceOperator.StrictNotEqual => Avm1MirOperator.StrictNotEqual,
                Avm1SourceOperator.Less => Avm1MirOperator.Less,
                Avm1SourceOperator.LessOrEqual => Avm1MirOperator.LessOrEqual,
                Avm1SourceOperator.Greater => Avm1MirOperator.Greater,
                Avm1SourceOperator.GreaterOrEqual => Avm1MirOperator.GreaterOrEqual,
                Avm1SourceOperator.BitAnd => Avm1MirOperator.BitAnd,
                Avm1SourceOperator.BitOr => Avm1MirOperator.BitOr,
                Avm1SourceOperator.BitXor => Avm1MirOperator.BitXor,
                Avm1SourceOperator.ShiftLeft => Avm1MirOperator.ShiftLeft,
                Avm1SourceOperator.ShiftRight => Avm1MirOperator.ShiftRight,
                Avm1SourceOperator.ShiftRightUnsigned =>
                    Avm1MirOperator.ShiftRightUnsigned,
                Avm1SourceOperator.InstanceOf => Avm1MirOperator.InstanceOf,
                _ => Avm1MirOperator.None
            };
            return result is not Avm1MirOperator.None;
        }

        private static bool TryMapCompoundAssignmentOperator(
            Avm1SourceOperator source,
            out Avm1MirOperator result)
        {
            result = source switch
            {
                Avm1SourceOperator.AddAssign => Avm1MirOperator.Add,
                Avm1SourceOperator.SubtractAssign => Avm1MirOperator.Subtract,
                Avm1SourceOperator.MultiplyAssign => Avm1MirOperator.Multiply,
                Avm1SourceOperator.DivideAssign => Avm1MirOperator.Divide,
                Avm1SourceOperator.ModuloAssign => Avm1MirOperator.Modulo,
                Avm1SourceOperator.BitAndAssign => Avm1MirOperator.BitAnd,
                Avm1SourceOperator.BitOrAssign => Avm1MirOperator.BitOr,
                Avm1SourceOperator.BitXorAssign => Avm1MirOperator.BitXor,
                Avm1SourceOperator.ShiftLeftAssign => Avm1MirOperator.ShiftLeft,
                Avm1SourceOperator.ShiftRightAssign => Avm1MirOperator.ShiftRight,
                Avm1SourceOperator.ShiftRightUnsignedAssign =>
                    Avm1MirOperator.ShiftRightUnsigned,
                _ => Avm1MirOperator.None
            };
            return result is not Avm1MirOperator.None;
        }

        private enum PendingBlockKind : byte
        {
            EnumerationCleanup,
            Return,
            Throw
        }

        private readonly record struct PendingBlock(
            PendingBlockKind Kind,
            Avm1MirBlockIndex Block,
            Avm1MirBlockIndex Target,
            Avm1MirTemporaryIndex Temporary,
            SourceOriginIndex Origin)
        {
            public static PendingBlock EnumerationCleanup(
                Avm1MirBlockIndex block,
                Avm1MirBlockIndex target,
                SourceOriginIndex origin) =>
                new(
                    PendingBlockKind.EnumerationCleanup,
                    block,
                    target,
                    Avm1MirTemporaryIndex.Invalid,
                    origin);

            public static PendingBlock Return(
                Avm1MirBlockIndex block,
                Avm1MirTemporaryIndex temporary,
                SourceOriginIndex origin) =>
                new(
                    PendingBlockKind.Return,
                    block,
                    Avm1MirBlockIndex.Invalid,
                    temporary,
                    origin);

            public static PendingBlock Throw(
                Avm1MirBlockIndex block,
                Avm1MirTemporaryIndex temporary,
                SourceOriginIndex origin) =>
                new(
                    PendingBlockKind.Throw,
                    block,
                    Avm1MirBlockIndex.Invalid,
                    temporary,
                    origin);
        }

        private readonly record struct TargetScope(
            string? ImmediateTarget,
            Avm1MirTemporaryIndex Temporary)
        {
            public static TargetScope Immediate(string target) =>
                new(target, Avm1MirTemporaryIndex.Invalid);

            public static TargetScope Dynamic(Avm1MirTemporaryIndex temporary) =>
                new(null, temporary);
        }

        private sealed class ControlScope(
            Avm1MirControlTargetIndex target,
            SourceStatementIndex statement,
            SourceLabelIndex label,
            string? labelName,
            Avm1MirBlockIndex breakBlock,
            Avm1MirBlockIndex continueBlock,
            bool isEnumeration,
            int targetDepth)
        {
            public Avm1MirControlTargetIndex Target { get; } = target;

            public SourceStatementIndex Statement { get; } = statement;

            public SourceLabelIndex Label { get; } = label;

            public string? LabelName { get; } = labelName;

            public Avm1MirBlockIndex BreakBlock { get; } = breakBlock;

            public Avm1MirBlockIndex ContinueBlock { get; } = continueBlock;

            public bool IsEnumeration { get; } = isEnumeration;

            public int TargetDepth { get; } = targetDepth;

            public bool HasBreakEdge { get; set; }

            public bool HasContinueEdge { get; set; }
        }
    }

    private sealed class MirBuilder
    {
        private readonly List<Avm1MirInstruction> _instructions = [];
        private readonly List<MutableBlock> _blocks = [];
        private readonly List<Avm1MirBlockIndex> _blockLayout = [];
        private readonly List<Avm1MirLValue> _lValues = [];
        private readonly List<Avm1MirCallSite> _callSites = [];
        private readonly List<Avm1MirValueIndex> _callArguments = [];
        private readonly List<Avm1MirAggregateSite> _aggregateSites = [];
        private readonly List<Avm1MirValueIndex> _aggregateValues = [];
        private readonly List<Avm1MirUrlSite> _urlSites = [];
        private readonly List<Avm1MirFunctionSite> _functionSites = [];
        private readonly List<Avm1MirTrySite> _trySites = [];
        private readonly List<Avm1MirVisibleControlScope> _visibleControlScopes = [];
        private readonly List<Avm1MirWithSite> _withSites = [];
        private readonly List<Avm1MirVisibleControlScope>
            _withVisibleControlScopes = [];
        private readonly List<Avm1MirCompletionSite> _completionSites = [];
        private readonly List<MutablePhiSite> _phiSites = [];
        private readonly List<Avm1MirControlTarget> _controlTargets = [];
        private readonly List<bool> _temporariesInUse = [];
        private readonly Stack<Avm1MirTemporaryIndex> _freeTemporaries = [];
        private readonly List<Avm1MirConstant> _constants = [];
        private readonly List<string> _strings = [];
        private readonly Dictionary<string, Avm1MirStringIndex> _stringIndices =
            new(StringComparer.Ordinal);
        private Avm1MirBlockIndex _currentBlock = Avm1MirBlockIndex.Invalid;
        private int _valueCount;

        public MirBuilder()
        {
            EntryBlock = CreateBlock();
            StartBlock(EntryBlock, isReachable: true);
        }

        public Avm1MirBlockIndex EntryBlock { get; }

        public Avm1MirBlockIndex CurrentBlock => _currentBlock;

        public bool CanAppend =>
            _currentBlock.IsValid &&
            _blocks[_currentBlock.Value].IsReachable &&
            !_blocks[_currentBlock.Value].IsTerminated;

        public Avm1MirBlockIndex CreateBlock()
        {
            var index = new Avm1MirBlockIndex(_blocks.Count);
            _blocks.Add(new MutableBlock());
            return index;
        }

        public Avm1MirTemporaryIndex AcquireTemporary()
        {
            if (_freeTemporaries.TryPop(out var temporary))
            {
                if (_temporariesInUse[temporary.Value])
                {
                    throw new InvalidOperationException(
                        $"MIR temporary {temporary} is already active.");
                }
                _temporariesInUse[temporary.Value] = true;
                return temporary;
            }

            var index = new Avm1MirTemporaryIndex(_temporariesInUse.Count);
            _temporariesInUse.Add(true);
            return index;
        }

        public void ReleaseTemporary(Avm1MirTemporaryIndex temporary)
        {
            if (!temporary.IsValid ||
                temporary.Value >= _temporariesInUse.Count ||
                !_temporariesInUse[temporary.Value])
            {
                throw new ArgumentOutOfRangeException(nameof(temporary));
            }

            _temporariesInUse[temporary.Value] = false;
            _freeTemporaries.Push(temporary);
        }

        public void StartBlock(Avm1MirBlockIndex index, bool isReachable)
        {
            if (!index.IsValid || index.Value >= _blocks.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            FinishCurrentBlock();
            var block = _blocks[index.Value];
            if (block.IsStarted)
                throw new InvalidOperationException($"MIR block {index} was started twice.");

            block.IsStarted = true;
            block.IsReachable = isReachable;
            block.Start = _instructions.Count;
            _blockLayout.Add(index);
            _currentBlock = index;
        }

        public Avm1MirLValueIndex AddNameLValue(
            string name,
            Avm1MirNameKind nameKind,
            SourceOriginIndex origin,
            SourceSymbolIndex? symbol = null)
        {
            var index = new Avm1MirLValueIndex(_lValues.Count);
            _lValues.Add(new Avm1MirLValue(
                index,
                Avm1MirLValueKind.Name,
                Avm1MirValueIndex.Invalid,
                Avm1MirValueIndex.Invalid,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirTemporaryIndex.Invalid,
                InternString(name),
                nameKind,
                origin,
                symbol ?? SourceSymbolIndex.Invalid,
                Register: 0));
            return index;
        }

        public Avm1MirLValueIndex AddRegisterLValue(
            SourceSymbolIndex symbol,
            byte register,
            SourceOriginIndex origin)
        {
            var index = new Avm1MirLValueIndex(_lValues.Count);
            _lValues.Add(new Avm1MirLValue(
                index,
                Avm1MirLValueKind.Register,
                Avm1MirValueIndex.Invalid,
                Avm1MirValueIndex.Invalid,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirStringIndex.Invalid,
                Avm1MirNameKind.None,
                origin,
                symbol,
                register));
            return index;
        }

        public Avm1MirLValueIndex AddComputedNameLValue(
            Avm1MirValueIndex name,
            SourceOriginIndex origin)
        {
            var index = new Avm1MirLValueIndex(_lValues.Count);
            _lValues.Add(new Avm1MirLValue(
                index,
                Avm1MirLValueKind.ComputedName,
                Avm1MirValueIndex.Invalid,
                name,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirStringIndex.Invalid,
                Avm1MirNameKind.DynamicName,
                origin,
                SourceSymbolIndex.Invalid,
                Register: 0));
            return index;
        }

        public Avm1MirAggregateSiteIndex AddAggregateSite(
            Avm1MirAggregateKind kind,
            Avm1MirValueIndex[] values,
            Avm1MirEvaluationOrder valueEvaluationOrder,
            SourceOriginIndex origin)
        {
            var valueStart = _aggregateValues.Count;
            foreach (var value in values)
                _aggregateValues.Add(value);

            var index = new Avm1MirAggregateSiteIndex(_aggregateSites.Count);
            _aggregateSites.Add(new Avm1MirAggregateSite(
                index,
                kind,
                new Avm1MirValueRange(valueStart, values.Length),
                valueEvaluationOrder,
                origin));
            return index;
        }

        public Avm1MirPhiSiteIndex CreatePhiSite(
            Avm1MirBlockIndex mergeBlock,
            SourceOriginIndex origin)
        {
            if (!mergeBlock.IsValid || mergeBlock.Value >= _blocks.Count)
                throw new ArgumentOutOfRangeException(nameof(mergeBlock));

            var index = new Avm1MirPhiSiteIndex(_phiSites.Count);
            _phiSites.Add(new MutablePhiSite(mergeBlock, origin));
            return index;
        }

        public Avm1MirFunctionSiteIndex AddFunctionSite(
            SourceFunctionIndex function,
            string name,
            bool isDeclaration,
            SourceOriginIndex origin)
        {
            var index = new Avm1MirFunctionSiteIndex(_functionSites.Count);
            _functionSites.Add(new Avm1MirFunctionSite(
                index,
                function,
                InternString(name),
                isDeclaration,
                origin));
            return index;
        }

        public Avm1MirTrySiteIndex AddTrySite(
            SourceStatementIndex statement,
            SourceStatementIndex tryBody,
            SourceStatementIndex catchBody,
            SourceStatementIndex finallyBody,
            SourceSymbolIndex catchSymbol,
            Avm1MirCompletionStorage completionStorage,
            Avm1MirVisibleControlScope[] visibleControlScopes,
            bool usesPhysicalParentBranches,
            SourceOriginIndex origin)
        {
            var scopeStart = _visibleControlScopes.Count;
            _visibleControlScopes.AddRange(visibleControlScopes);
            var index = new Avm1MirTrySiteIndex(_trySites.Count);
            _trySites.Add(new Avm1MirTrySite(
                index,
                statement,
                tryBody,
                catchBody,
                finallyBody,
                catchSymbol,
                completionStorage,
                new Avm1MirVisibleControlScopeRange(
                    scopeStart,
                    visibleControlScopes.Length),
                usesPhysicalParentBranches,
                origin));
            return index;
        }

        public Avm1MirWithSiteIndex AddWithSite(
            SourceStatementIndex statement,
            SourceStatementIndex body,
            Avm1MirCompletionStorage completionStorage,
            Avm1MirVisibleControlScope[] visibleControlScopes,
            SourceOriginIndex origin)
        {
            var scopeStart = _withVisibleControlScopes.Count;
            _withVisibleControlScopes.AddRange(visibleControlScopes);
            var index = new Avm1MirWithSiteIndex(_withSites.Count);
            _withSites.Add(new Avm1MirWithSite(
                index,
                statement,
                body,
                completionStorage,
                new Avm1MirVisibleControlScopeRange(
                    scopeStart,
                    visibleControlScopes.Length),
                origin));
            return index;
        }

        public Avm1MirCompletionSiteIndex AddCompletionSite(
            Avm1MirCompletionKind kind,
            SourceStatementIndex targetStatement,
            Avm1MirCompletionStorage storage,
            int token,
            SourceOriginIndex origin)
        {
            var index = new Avm1MirCompletionSiteIndex(_completionSites.Count);
            _completionSites.Add(new Avm1MirCompletionSite(
                index,
                kind,
                targetStatement,
                storage,
                token,
                origin));
            return index;
        }

        public void AddPhiIncoming(
            Avm1MirPhiSiteIndex phiSite,
            Avm1MirBlockIndex predecessor,
            Avm1MirValueIndex value)
        {
            if (!phiSite.IsValid || phiSite.Value >= _phiSites.Count)
                throw new ArgumentOutOfRangeException(nameof(phiSite));
            if (!predecessor.IsValid || predecessor.Value >= _blocks.Count)
                throw new ArgumentOutOfRangeException(nameof(predecessor));
            if (!value.IsValid || value.Value >= _valueCount)
                throw new ArgumentOutOfRangeException(nameof(value));

            _phiSites[phiSite.Value].Incomings.Add(
                new Avm1MirPhiIncoming(predecessor, value));
        }

        public Avm1MirControlTargetIndex AddControlTarget(
            Avm1MirControlTargetKind kind,
            SourceStatementIndex statement,
            Avm1MirBlockIndex breakBlock,
            Avm1MirBlockIndex continueBlock,
            string? name,
            SourceOriginIndex origin)
        {
            if (!breakBlock.IsValid || breakBlock.Value >= _blocks.Count)
                throw new ArgumentOutOfRangeException(nameof(breakBlock));
            if (kind is Avm1MirControlTargetKind.Loop &&
                (!continueBlock.IsValid || continueBlock.Value >= _blocks.Count))
            {
                throw new ArgumentOutOfRangeException(nameof(continueBlock));
            }
            if (kind is Avm1MirControlTargetKind.Switch && continueBlock.IsValid)
            {
                throw new ArgumentException(
                    "A switch control target cannot have a continue block.",
                    nameof(continueBlock));
            }

            var index = new Avm1MirControlTargetIndex(_controlTargets.Count);
            _controlTargets.Add(new Avm1MirControlTarget(
                index,
                kind,
                statement,
                breakBlock,
                continueBlock,
                name is null ? Avm1MirStringIndex.Invalid : InternString(name),
                HasPhysicalReferences: false,
                origin));
            return index;
        }

        public Avm1MirUrlSiteIndex AddUrlSite(string url, string target)
        {
            var index = new Avm1MirUrlSiteIndex(_urlSites.Count);
            _urlSites.Add(new Avm1MirUrlSite(
                index,
                InternString(url),
                InternString(target),
                GetUrlFlags.MethodNone));
            return index;
        }

        public Avm1MirUrlSiteIndex AddStackUrlSite(GetUrlFlags flags)
        {
            if (!Swf4.ActionGetURL2.IsValidFlags(flags))
                throw new ArgumentOutOfRangeException(nameof(flags));

            var index = new Avm1MirUrlSiteIndex(_urlSites.Count);
            _urlSites.Add(new Avm1MirUrlSite(
                index,
                Avm1MirStringIndex.Invalid,
                Avm1MirStringIndex.Invalid,
                flags));
            return index;
        }

        public void MarkPhysicalControlTarget(Avm1MirControlTargetIndex target)
        {
            if (!target.IsValid || target.Value >= _controlTargets.Count)
                throw new ArgumentOutOfRangeException(nameof(target));
            _controlTargets[target.Value] = _controlTargets[target.Value] with
            {
                HasPhysicalReferences = true
            };
        }

        public Avm1MirLValueIndex AddMemberLValue(
            Avm1MirValueIndex receiver,
            Avm1MirValueIndex key,
            SourceOriginIndex origin)
        {
            var index = new Avm1MirLValueIndex(_lValues.Count);
            _lValues.Add(new Avm1MirLValue(
                index,
                Avm1MirLValueKind.Member,
                receiver,
                key,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirTemporaryIndex.Invalid,
                Avm1MirStringIndex.Invalid,
                Avm1MirNameKind.None,
                origin,
                SourceSymbolIndex.Invalid,
                Register: 0));
            return index;
        }

        public Avm1MirLValueIndex AddCapturedMemberLValue(
            Avm1MirTemporaryIndex receiverTemporary,
            Avm1MirTemporaryIndex keyTemporary,
            SourceOriginIndex origin)
        {
            var index = new Avm1MirLValueIndex(_lValues.Count);
            _lValues.Add(new Avm1MirLValue(
                index,
                Avm1MirLValueKind.CapturedMember,
                Avm1MirValueIndex.Invalid,
                Avm1MirValueIndex.Invalid,
                receiverTemporary,
                keyTemporary,
                Avm1MirStringIndex.Invalid,
                Avm1MirNameKind.None,
                origin,
                SourceSymbolIndex.Invalid,
                Register: 0));
            return index;
        }

        public Avm1MirLValue GetLValue(Avm1MirLValueIndex index) =>
            _lValues[index.Value];

        public Avm1MirCallSiteIndex AddCallSite(
            Avm1MirInvocationKind kind,
            Avm1MirInvocationTargetKind targetKind,
            Avm1MirValueIndex target,
            Avm1MirValueIndex memberName,
            string? name,
            Avm1MirNameKind nameKind,
            SourceSymbolIndex symbol,
            Avm1MirValueIndex[] arguments,
            Avm1MirEvaluationOrder argumentEvaluationOrder,
            Avm1MirInvocationTargetOrder targetEvaluationOrder,
            SourceOriginIndex origin)
        {
            var argumentStart = _callArguments.Count;
            foreach (var argument in arguments)
                _callArguments.Add(argument);

            var index = new Avm1MirCallSiteIndex(_callSites.Count);
            _callSites.Add(new Avm1MirCallSite(
                index,
                kind,
                targetKind,
                target,
                memberName,
                name is null ? Avm1MirStringIndex.Invalid : InternString(name),
                nameKind,
                symbol,
                new Avm1MirValueRange(argumentStart, arguments.Length),
                argumentEvaluationOrder,
                targetEvaluationOrder,
                origin));
            return index;
        }

        public Avm1MirConstantIndex AddConstant(
            Avm1MirConstantKind kind,
            bool booleanValue,
            int integerValue,
            double numberValue,
            string? stringValue)
        {
            var index = new Avm1MirConstantIndex(_constants.Count);
            _constants.Add(new Avm1MirConstant(
                index,
                kind,
                booleanValue,
                integerValue,
                numberValue,
                stringValue is null
                    ? Avm1MirStringIndex.Invalid
                    : InternString(stringValue)));
            return index;
        }

        public Avm1MirValueIndex AddValueInstruction(
            Avm1MirInstructionKind kind,
            Avm1MirValueIndex? operand = null,
            Avm1MirValueIndex? secondaryOperand = null,
            Avm1MirLValueIndex? lValue = null,
            Avm1MirCallSiteIndex? callSite = null,
            Avm1MirAggregateSiteIndex? aggregateSite = null,
            Avm1MirUrlSiteIndex? urlSite = null,
            Avm1MirFunctionSiteIndex? functionSite = null,
            Avm1MirTrySiteIndex? trySite = null,
            Avm1MirWithSiteIndex? withSite = null,
            Avm1MirCompletionSiteIndex? completionSite = null,
            Avm1MirCompletionStorage? completionStorage = null,
            Avm1MirPhiSiteIndex? phiSite = null,
            Avm1MirControlTargetIndex? controlTarget = null,
            Avm1MirTemporaryIndex? temporary = null,
            string? name = null,
            Avm1MirConstantIndex? constant = null,
            Avm1MirNameKind nameKind = Avm1MirNameKind.None,
            Avm1MirOperator @operator = Avm1MirOperator.None,
            Avm1MirEffect effects = Avm1MirEffect.None,
            Avm1MirBlockIndex? target = null,
            Avm1MirBlockIndex? alternativeTarget = null,
            SourceOriginIndex? origin = null)
        {
            var result = new Avm1MirValueIndex(_valueCount++);
            Add(
                kind,
                result,
                operand,
                secondaryOperand,
                lValue,
                callSite,
                aggregateSite,
                urlSite,
                functionSite,
                trySite,
                withSite,
                completionSite,
                completionStorage,
                phiSite,
                controlTarget,
                temporary,
                name,
                constant,
                nameKind,
                @operator,
                effects,
                target,
                alternativeTarget,
                origin);
            return result;
        }

        public void AddInstruction(
            Avm1MirInstructionKind kind,
            Avm1MirValueIndex? operand = null,
            Avm1MirValueIndex? secondaryOperand = null,
            Avm1MirLValueIndex? lValue = null,
            Avm1MirCallSiteIndex? callSite = null,
            Avm1MirAggregateSiteIndex? aggregateSite = null,
            Avm1MirUrlSiteIndex? urlSite = null,
            Avm1MirFunctionSiteIndex? functionSite = null,
            Avm1MirTrySiteIndex? trySite = null,
            Avm1MirWithSiteIndex? withSite = null,
            Avm1MirCompletionSiteIndex? completionSite = null,
            Avm1MirCompletionStorage? completionStorage = null,
            Avm1MirPhiSiteIndex? phiSite = null,
            Avm1MirControlTargetIndex? controlTarget = null,
            Avm1MirTemporaryIndex? temporary = null,
            string? name = null,
            Avm1MirConstantIndex? constant = null,
            Avm1MirNameKind nameKind = Avm1MirNameKind.None,
            Avm1MirOperator @operator = Avm1MirOperator.None,
            Avm1MirEffect effects = Avm1MirEffect.None,
            Avm1MirBlockIndex? target = null,
            Avm1MirBlockIndex? alternativeTarget = null,
            SourceOriginIndex? origin = null) =>
            Add(
                kind,
                Avm1MirValueIndex.Invalid,
                operand,
                secondaryOperand,
                lValue,
                callSite,
                aggregateSite,
                urlSite,
                functionSite,
                trySite,
                withSite,
                completionSite,
                completionStorage,
                phiSite,
                controlTarget,
                temporary,
                name,
                constant,
                nameKind,
                @operator,
                effects,
                target,
                alternativeTarget,
                origin);

        public Avm1MirMethod ToMethod()
        {
            FinishCurrentBlock();
            if (_temporariesInUse.Any(inUse => inUse))
                throw new InvalidOperationException("MIR temporary lifetime was not closed.");
            var blocks = new Avm1MirBasicBlock[_blocks.Count];
            for (var i = 0; i < blocks.Length; i++)
            {
                var block = _blocks[i];
                if (!block.IsStarted)
                {
                    throw new InvalidOperationException(
                        $"MIR block {new Avm1MirBlockIndex(i)} was never started.");
                }
                blocks[i] = new Avm1MirBasicBlock(
                    new Avm1MirBlockIndex(i),
                    new Avm1MirInstructionRange(block.Start, block.Count),
                    block.IsReachable);
            }

            return new Avm1MirMethod(
                _instructions.ToArray(),
                blocks,
                _blockLayout.ToArray(),
                EntryBlock,
                _lValues.ToArray(),
                _callSites.ToArray(),
                _callArguments.ToArray(),
                _aggregateSites.ToArray(),
                _aggregateValues.ToArray(),
                _urlSites.ToArray(),
                _functionSites.ToArray(),
                _trySites.ToArray(),
                _visibleControlScopes.ToArray(),
                _withSites.ToArray(),
                _withVisibleControlScopes.ToArray(),
                _completionSites.ToArray(),
                CreatePhiSites(out var phiIncomings),
                phiIncomings,
                _controlTargets.ToArray(),
                _temporariesInUse.Count,
                _constants.ToArray(),
                _strings.ToArray(),
                _valueCount);
        }

        private void Add(
            Avm1MirInstructionKind kind,
            Avm1MirValueIndex result,
            Avm1MirValueIndex? operand,
            Avm1MirValueIndex? secondaryOperand,
            Avm1MirLValueIndex? lValue,
            Avm1MirCallSiteIndex? callSite,
            Avm1MirAggregateSiteIndex? aggregateSite,
            Avm1MirUrlSiteIndex? urlSite,
            Avm1MirFunctionSiteIndex? functionSite,
            Avm1MirTrySiteIndex? trySite,
            Avm1MirWithSiteIndex? withSite,
            Avm1MirCompletionSiteIndex? completionSite,
            Avm1MirCompletionStorage? completionStorage,
            Avm1MirPhiSiteIndex? phiSite,
            Avm1MirControlTargetIndex? controlTarget,
            Avm1MirTemporaryIndex? temporary,
            string? name,
            Avm1MirConstantIndex? constant,
            Avm1MirNameKind nameKind,
            Avm1MirOperator @operator,
            Avm1MirEffect effects,
            Avm1MirBlockIndex? target,
            Avm1MirBlockIndex? alternativeTarget,
            SourceOriginIndex? origin)
        {
            if (!CanAppend)
                throw new InvalidOperationException("Cannot append to a terminated MIR block.");

            var index = new Avm1MirInstructionIndex(_instructions.Count);
            _instructions.Add(new Avm1MirInstruction(
                index,
                kind,
                result,
                operand ?? Avm1MirValueIndex.Invalid,
                secondaryOperand ?? Avm1MirValueIndex.Invalid,
                lValue ?? Avm1MirLValueIndex.Invalid,
                callSite ?? Avm1MirCallSiteIndex.Invalid,
                aggregateSite ?? Avm1MirAggregateSiteIndex.Invalid,
                urlSite ?? Avm1MirUrlSiteIndex.Invalid,
                functionSite ?? Avm1MirFunctionSiteIndex.Invalid,
                trySite ?? Avm1MirTrySiteIndex.Invalid,
                withSite ?? Avm1MirWithSiteIndex.Invalid,
                completionSite ?? Avm1MirCompletionSiteIndex.Invalid,
                completionStorage ?? Avm1MirCompletionStorage.None,
                phiSite ?? Avm1MirPhiSiteIndex.Invalid,
                controlTarget ?? Avm1MirControlTargetIndex.Invalid,
                temporary ?? Avm1MirTemporaryIndex.Invalid,
                name is null ? Avm1MirStringIndex.Invalid : InternString(name),
                constant ?? Avm1MirConstantIndex.Invalid,
                nameKind,
                @operator,
                effects,
                target ?? Avm1MirBlockIndex.Invalid,
                alternativeTarget ?? Avm1MirBlockIndex.Invalid,
                origin ?? SourceOriginIndex.Invalid));

            if (kind is Avm1MirInstructionKind.Return or
                Avm1MirInstructionKind.Throw or
                Avm1MirInstructionKind.Break or
                Avm1MirInstructionKind.Continue or
                Avm1MirInstructionKind.ExternalControlBranch or
                Avm1MirInstructionKind.Branch or
                Avm1MirInstructionKind.BranchIfTrue or
                Avm1MirInstructionKind.WaitForFrameImmediate or
                Avm1MirInstructionKind.WaitForFrame or
                Avm1MirInstructionKind.EnumerateNext or
                Avm1MirInstructionKind.EndEnumeration)
            {
                _blocks[_currentBlock.Value].IsTerminated = true;
            }
        }

        private void FinishCurrentBlock()
        {
            if (!_currentBlock.IsValid)
                return;

            var block = _blocks[_currentBlock.Value];
            block.Count = _instructions.Count - block.Start;
        }

        private Avm1MirPhiSite[] CreatePhiSites(
            out Avm1MirPhiIncoming[] incomings)
        {
            var sites = new Avm1MirPhiSite[_phiSites.Count];
            var flattened = new List<Avm1MirPhiIncoming>();
            for (var i = 0; i < _phiSites.Count; i++)
            {
                var source = _phiSites[i];
                var start = flattened.Count;
                flattened.AddRange(source.Incomings);
                sites[i] = new Avm1MirPhiSite(
                    new Avm1MirPhiSiteIndex(i),
                    source.MergeBlock,
                    new Avm1MirPhiIncomingRange(start, source.Incomings.Count),
                    source.Origin);
            }

            incomings = flattened.ToArray();
            return sites;
        }

        private Avm1MirStringIndex InternString(string value)
        {
            if (_stringIndices.TryGetValue(value, out var existing))
                return existing;

            var index = new Avm1MirStringIndex(_strings.Count);
            _strings.Add(value);
            _stringIndices.Add(value, index);
            return index;
        }

        public Avm1MirStringIndex InternCompletionString(string value) =>
            InternString(value);

        public string GetCompletionString(Avm1MirCompletionStorage storage)
        {
            if (storage.Kind is not Avm1MirCompletionStorageKind.ActivationName ||
                !storage.Name.IsValid ||
                storage.Name.Value >= _strings.Count)
            {
                throw new ArgumentException(
                    "Completion storage is not a valid activation name.",
                    nameof(storage));
            }

            return _strings[storage.Name.Value];
        }

        private sealed class MutableBlock
        {
            public int Start { get; set; }

            public int Count { get; set; }

            public bool IsStarted { get; set; }

            public bool IsReachable { get; set; }

            public bool IsTerminated { get; set; }
        }

        private sealed class MutablePhiSite(
            Avm1MirBlockIndex mergeBlock,
            SourceOriginIndex origin)
        {
            public Avm1MirBlockIndex MergeBlock { get; } = mergeBlock;

            public SourceOriginIndex Origin { get; } = origin;

            public List<Avm1MirPhiIncoming> Incomings { get; } = [];
        }
    }
}
