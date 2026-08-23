using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1StackDepthAnalysis
{
    private const int FlowStateCount = (int)Avm1FlowState.Merged;

    private readonly int[]? _stateEntryDepths;
    private readonly int[]? _stateExitDepths;
    private readonly bool[]? _hasKnownStateEntry;
    private readonly Avm1FlowGraph? _flowGraph;

    private Avm1StackDepthAnalysis(
        int[] entryDepths,
        int[] exitDepths,
        bool[] hasKnownEntry,
        Avm1Diagnostic[] diagnostics,
        int[]? stateEntryDepths = null,
        int[]? stateExitDepths = null,
        bool[]? hasKnownStateEntry = null,
        Avm1FlowGraph? flowGraph = null)
    {
        EntryDepths = entryDepths;
        ExitDepths = exitDepths;
        HasKnownEntry = hasKnownEntry;
        Diagnostics = diagnostics;
        _stateEntryDepths = stateEntryDepths;
        _stateExitDepths = stateExitDepths;
        _hasKnownStateEntry = hasKnownStateEntry;
        _flowGraph = flowGraph;
    }

    public IReadOnlyList<int> EntryDepths { get; }

    public IReadOnlyList<int> ExitDepths { get; }

    public IReadOnlyList<bool> HasKnownEntry { get; }

    public IReadOnlyList<Avm1Diagnostic> Diagnostics { get; }

    public static Avm1StackDepthAnalysis Build(Avm1InstructionTable instructions, Avm1ControlFlowGraph cfg)
    {
        return Build(instructions, cfg, Avm1FlowGraph.Build(instructions, cfg));
    }

    internal static Avm1StackDepthAnalysis Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1FlowGraph flowGraph)
    {
        return cfg.CompletionEdges.Count == 0
            ? BuildBlockFlow(instructions, cfg)
            : new FlowContextBuilder(instructions, cfg, flowGraph).Build();
    }

    public bool HasKnownEntryForState(BlockIndex block, Avm1FlowState state)
    {
        if (!block.IsValid || state is Avm1FlowState.Merged)
            return false;

        if (_flowGraph is not null && _hasKnownStateEntry is not null)
        {
            return _flowGraph.Points.Any(point =>
                point.Block == block &&
                _flowGraph.GetContext(point.Context).State == state &&
                _hasKnownStateEntry[point.Index.Value]);
        }

        if (_hasKnownStateEntry is null)
            return state is Avm1FlowState.Normal &&
                block.Value < HasKnownEntry.Count &&
                HasKnownEntry[block.Value];

        var point = GetPoint(block, state);
        return point < _hasKnownStateEntry.Length && _hasKnownStateEntry[point];
    }

    public int GetEntryDepth(BlockIndex block, Avm1FlowState state)
    {
        if (!HasKnownEntryForState(block, state))
            return -1;

        if (_flowGraph is not null && _stateEntryDepths is not null)
        {
            return _flowGraph.Points
                .Where(point =>
                    point.Block == block &&
                    _flowGraph.GetContext(point.Context).State == state &&
                    _hasKnownStateEntry![point.Index.Value])
                .Min(point => _stateEntryDepths[point.Index.Value]);
        }

        return _stateEntryDepths is null
            ? EntryDepths[block.Value]
            : _stateEntryDepths[GetPoint(block, state)];
    }

    public int GetExitDepth(BlockIndex block, Avm1FlowState state)
    {
        if (!HasKnownEntryForState(block, state))
            return -1;

        if (_flowGraph is not null && _stateExitDepths is not null)
        {
            return _flowGraph.Points
                .Where(point =>
                    point.Block == block &&
                    _flowGraph.GetContext(point.Context).State == state &&
                    _hasKnownStateEntry![point.Index.Value])
                .Min(point => _stateExitDepths[point.Index.Value]);
        }

        return _stateExitDepths is null
            ? ExitDepths[block.Value]
            : _stateExitDepths[GetPoint(block, state)];
    }

    public bool HasKnownFlowEntry(FlowPointIndex point) =>
        _flowGraph is not null &&
        _hasKnownStateEntry is not null &&
        point.IsValid &&
        point.Value < _hasKnownStateEntry.Length &&
        _hasKnownStateEntry[point.Value];

    public int GetFlowEntryDepth(FlowPointIndex point) =>
        HasKnownFlowEntry(point) && _stateEntryDepths is not null
            ? _stateEntryDepths[point.Value]
            : -1;

    public int GetFlowExitDepth(FlowPointIndex point) =>
        HasKnownFlowEntry(point) && _stateExitDepths is not null
            ? _stateExitDepths[point.Value]
            : -1;

    private static Avm1StackDepthAnalysis BuildBlockFlow(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg)
    {
        var entryDepths = Enumerable.Repeat(-1, cfg.Count).ToArray();
        var exitDepths = Enumerable.Repeat(-1, cfg.Count).ToArray();
        var hasKnownEntry = new bool[cfg.Count];
        var diagnostics = new List<Avm1Diagnostic>();
        var dynamicEffectDiagnostics = new HashSet<ActionOpcode>();

        if (cfg.Count == 0)
            return new Avm1StackDepthAnalysis(entryDepths, exitDepths, hasKnownEntry, []);

        var worklist = new Queue<BlockIndex>();
        SetEntryDepth(
            instructions,
            cfg,
            new BlockIndex(0),
            0,
            ActionIndex.Invalid,
            entryDepths,
            hasKnownEntry,
            diagnostics,
            worklist);

        while (worklist.Count > 0)
        {
            var blockIndex = worklist.Dequeue();
            var block = cfg[blockIndex];
            var depth = entryDepths[blockIndex.Value];
            var exceptionalDepth = depth;
            var stack = CreateUnknownStack(depth);

            for (var actionIndex = block.StartAction.Value; actionIndex < block.EndAction.Value; actionIndex++)
            {
                var action = new ActionIndex(actionIndex);
                var currentAction = instructions[action].Action;
                var effect = GetStackEffect(currentAction, stack);
                var depthBeforeAction = depth;
                if (!effect.IsKnown)
                {
                    if (dynamicEffectDiagnostics.Add(currentAction.Opcode))
                    {
                        diagnostics.Add(new Avm1Diagnostic(
                            Avm1DiagnosticSeverity.Info,
                            action,
                            $"Stack effect is dynamic for {currentAction.Opcode}; " +
                            "stack-depth analysis continues conservatively."));
                    }
                    if (actionIndex == block.EndAction.Value - 1)
                        exceptionalDepth = depthBeforeAction;
                    continue;
                }

                var underflow = depth < effect.PopCount;
                if (underflow)
                {
                    if (!IsDynamicEnumerationCleanupOperand(instructions, cfg, action) &&
                        !IsOptionalMergePop(instructions, cfg, blockIndex) &&
                        !IsTerminalEpiloguePop(instructions, cfg, blockIndex))
                    {
                        diagnostics.Add(new Avm1Diagnostic(
                            Avm1DiagnosticSeverity.Warning,
                            action,
                            "Stack underflow in stack-depth analysis."));
                    }
                }

                ApplyStackEffect(currentAction, effect, stack, underflow);
                depth = stack.Count;
                if (actionIndex == block.EndAction.Value - 1)
                    exceptionalDepth = Math.Max(0, depthBeforeAction - effect.PopCount);
            }

            exitDepths[blockIndex.Value] = depth;
            SetEntryDepth(
                instructions,
                cfg,
                block.FirstSuccessor,
                GetNormalExitDepth(instructions, cfg, block, block.FirstSuccessor, depth),
                block.EndAction,
                entryDepths,
                hasKnownEntry,
                diagnostics,
                worklist);
            SetEntryDepth(
                instructions,
                cfg,
                block.SecondSuccessor,
                GetNormalExitDepth(instructions, cfg, block, block.SecondSuccessor, depth),
                block.EndAction,
                entryDepths,
                hasKnownEntry,
                diagnostics,
                worklist);
            if (block.ExceptionSuccessor.IsValid)
            {
                var unwindSource = block.ExceptionStackSource;
                var handlerDepth = unwindSource.IsValid && hasKnownEntry[unwindSource.Value]
                    ? entryDepths[unwindSource.Value]
                    : exceptionalDepth;
                SetEntryDepth(
                    instructions,
                    cfg,
                    block.ExceptionSuccessor,
                    handlerDepth,
                    block.EndAction,
                    entryDepths,
                    hasKnownEntry,
                    diagnostics,
                    worklist);
            }

            foreach (var completion in cfg.GetCompletionEdges(blockIndex))
            {
                var completionDepth = completion.StackSource.IsValid &&
                    hasKnownEntry[completion.StackSource.Value]
                        ? entryDepths[completion.StackSource.Value]
                        : depth;
                SetEntryDepth(
                    instructions,
                    cfg,
                    completion.Target,
                    completionDepth,
                    block.EndAction,
                    entryDepths,
                    hasKnownEntry,
                    diagnostics,
                    worklist);
            }
        }

        return new Avm1StackDepthAnalysis(entryDepths, exitDepths, hasKnownEntry, diagnostics.ToArray());
    }

    private static int GetPoint(BlockIndex block, Avm1FlowState state) =>
        block.Value * FlowStateCount + (int)state;

    private sealed class FlowContextBuilder
    {
        private readonly Avm1InstructionTable _instructions;
        private readonly Avm1ControlFlowGraph _cfg;
        private readonly Avm1FlowGraph _flowGraph;
        private readonly int[] _entryDepths;
        private readonly int[] _exitDepths;
        private readonly bool[] _hasEntry;
        private readonly bool[] _queued;
        private readonly Queue<FlowPointIndex> _worklist = [];
        private readonly List<Avm1Diagnostic> _diagnostics = [];
        private readonly HashSet<(int Action, string Message)> _diagnosticKeys = [];
        private readonly HashSet<ActionOpcode> _dynamicEffectDiagnostics = [];

        public FlowContextBuilder(
            Avm1InstructionTable instructions,
            Avm1ControlFlowGraph cfg,
            Avm1FlowGraph flowGraph)
        {
            _instructions = instructions;
            _cfg = cfg;
            _flowGraph = flowGraph;
            _entryDepths = Enumerable.Repeat(-1, flowGraph.Count).ToArray();
            _exitDepths = Enumerable.Repeat(-1, flowGraph.Count).ToArray();
            _hasEntry = new bool[flowGraph.Count];
            _queued = new bool[flowGraph.Count];
        }

        public Avm1StackDepthAnalysis Build()
        {
            if (_flowGraph.TryGetPoint(
                new BlockIndex(0),
                _flowGraph.NormalContext,
                out var entry))
            {
                SetEntryDepth(entry, 0, ActionIndex.Invalid);
            }

            while (_worklist.TryDequeue(out var point))
            {
                _queued[point.Value] = false;
                ProcessPoint(point);
            }

            var entryDepths = Enumerable.Repeat(-1, _cfg.Count).ToArray();
            var exitDepths = Enumerable.Repeat(-1, _cfg.Count).ToArray();
            var hasKnownEntry = new bool[_cfg.Count];
            foreach (var group in _flowGraph.Points
                .Where(point => _hasEntry[point.Index.Value])
                .GroupBy(point => point.Block.Value))
            {
                hasKnownEntry[group.Key] = true;
                entryDepths[group.Key] = group.Min(point => _entryDepths[point.Index.Value]);
                exitDepths[group.Key] = group.Min(point => _exitDepths[point.Index.Value]);
            }

            return new Avm1StackDepthAnalysis(
                entryDepths,
                exitDepths,
                hasKnownEntry,
                _diagnostics.ToArray(),
                _entryDepths,
                _exitDepths,
                _hasEntry,
                _flowGraph);
        }

        private void ProcessPoint(FlowPointIndex pointIndex)
        {
            var point = _flowGraph[pointIndex];
            var block = _cfg[point.Block];
            var depth = _entryDepths[pointIndex.Value];
            var exceptionalDepth = depth;
            var stack = CreateUnknownStack(depth);

            for (var actionIndex = block.StartAction.Value; actionIndex < block.EndAction.Value; actionIndex++)
            {
                var action = new ActionIndex(actionIndex);
                var currentAction = _instructions[action].Action;
                var effect = GetStackEffect(currentAction, stack);
                var depthBeforeAction = depth;
                if (!effect.IsKnown)
                {
                    if (_dynamicEffectDiagnostics.Add(currentAction.Opcode))
                    {
                        AddDiagnostic(
                            Avm1DiagnosticSeverity.Info,
                            action,
                            $"Stack effect is dynamic for {currentAction.Opcode}; " +
                            "stack-depth analysis continues conservatively.");
                    }
                    if (actionIndex == block.EndAction.Value - 1)
                        exceptionalDepth = depthBeforeAction;
                    continue;
                }

                var underflow = depth < effect.PopCount;
                if (underflow)
                {
                    if (!IsDynamicEnumerationCleanupOperand(_instructions, _cfg, action) &&
                        !IsOptionalMergePop(_instructions, _cfg, point.Block) &&
                        !IsTerminalEpiloguePop(_instructions, _cfg, point.Block))
                    {
                        AddDiagnostic(
                            Avm1DiagnosticSeverity.Warning,
                            action,
                            "Stack underflow in stack-depth analysis.");
                    }
                }

                ApplyStackEffect(currentAction, effect, stack, underflow);
                depth = stack.Count;
                if (actionIndex == block.EndAction.Value - 1)
                    exceptionalDepth = Math.Max(0, depthBeforeAction - effect.PopCount);
            }

            _exitDepths[pointIndex.Value] = depth;
            foreach (var transition in _flowGraph.GetTransitions(pointIndex))
            {
                var nextDepth = transition.Kind switch
                {
                    Avm1FlowTransitionKind.Exception => GetUnwindDepth(
                        transition.StackSource,
                        transition.TargetContext,
                        exceptionalDepth),
                    Avm1FlowTransitionKind.Completion => GetUnwindDepth(
                        transition.StackSource,
                        transition.TargetContext,
                        depth),
                    _ => GetNormalExitDepth(
                        _instructions,
                        _cfg,
                        block,
                        _flowGraph[transition.Target].Block,
                        depth)
                };
                SetEntryDepth(transition.Target, nextDepth, block.EndAction);
            }
        }

        private int GetUnwindDepth(
            BlockIndex stackSource,
            FlowContextIndex context,
            int fallback)
        {
            if (!stackSource.IsValid)
                return fallback;

            var candidate = context;
            while (candidate.IsValid)
            {
                if (_flowGraph.TryGetPoint(stackSource, candidate, out var sourcePoint) &&
                    _hasEntry[sourcePoint.Value])
                {
                    return _entryDepths[sourcePoint.Value];
                }

                candidate = _flowGraph.GetContext(candidate).Parent;
            }

            return fallback;
        }

        private void SetEntryDepth(
            FlowPointIndex point,
            int depth,
            ActionIndex sourceAction)
        {
            if (!point.IsValid || point.Value >= _flowGraph.Count)
                return;

            if (!_hasEntry[point.Value])
            {
                _hasEntry[point.Value] = true;
                _entryDepths[point.Value] = depth;
                Enqueue(point);
                return;
            }

            if (_entryDepths[point.Value] == depth)
                return;

            var flowPoint = _flowGraph[point];
            if (IsOptionalMergePop(_instructions, _cfg, flowPoint.Block))
            {
                var mergedDepth = Math.Min(_entryDepths[point.Value], depth);
                if (_entryDepths[point.Value] != mergedDepth)
                {
                    _entryDepths[point.Value] = mergedDepth;
                    Enqueue(point);
                }
                return;
            }

            var context = _flowGraph.GetContext(flowPoint.Context);
            AddDiagnostic(
                Avm1DiagnosticSeverity.Warning,
                sourceAction,
                $"Inconsistent stack depth at block {flowPoint.Block.Value} in {context.State} context {flowPoint.Context.Value}. Existing depth is {_entryDepths[point.Value]}, incoming depth is {depth}.");
        }

        private void Enqueue(FlowPointIndex point)
        {
            if (_queued[point.Value])
                return;

            _queued[point.Value] = true;
            _worklist.Enqueue(point);
        }

        private void AddDiagnostic(
            Avm1DiagnosticSeverity severity,
            ActionIndex action,
            string message)
        {
            if (!_diagnosticKeys.Add((action.Value, message)))
                return;

            _diagnostics.Add(new Avm1Diagnostic(severity, action, message));
        }
    }

    private static void SetEntryDepth(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        BlockIndex block,
        int depth,
        ActionIndex sourceAction,
        int[] entryDepths,
        bool[] hasKnownEntry,
        List<Avm1Diagnostic> diagnostics,
        Queue<BlockIndex> worklist)
    {
        if (!block.IsValid || block.Value >= entryDepths.Length)
            return;

        if (!hasKnownEntry[block.Value])
        {
            hasKnownEntry[block.Value] = true;
            entryDepths[block.Value] = depth;
            worklist.Enqueue(block);
            return;
        }

        if (entryDepths[block.Value] == depth)
            return;

        if (IsOptionalMergePop(instructions, cfg, block))
        {
            var mergedDepth = Math.Min(entryDepths[block.Value], depth);
            if (entryDepths[block.Value] != mergedDepth)
            {
                entryDepths[block.Value] = mergedDepth;
                worklist.Enqueue(block);
            }
            return;
        }

        diagnostics.Add(new Avm1Diagnostic(
            Avm1DiagnosticSeverity.Warning,
            sourceAction,
            $"Inconsistent stack depth at block {block.Value}. Existing depth is {entryDepths[block.Value]}, incoming depth is {depth}."));
    }

    internal static bool IsOptionalMergePop(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        BlockIndex block)
    {
        if (!block.IsValid ||
            block.Value >= cfg.Count ||
            instructions[cfg[block].StartAction].Action is not ActionPop)
        {
            return false;
        }

        var predecessorCount = 0;
        var hasConditionalPredecessor = false;
        foreach (var candidate in cfg.Blocks)
        {
            if (candidate.FirstSuccessor != block && candidate.SecondSuccessor != block)
                continue;

            predecessorCount++;
            hasConditionalPredecessor |= candidate.Terminator is
                Avm1BlockTerminatorKind.ConditionalBranch;
        }

        return predecessorCount >= 2 && hasConditionalPredecessor;
    }

    internal static bool IsTerminalEpiloguePop(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        BlockIndex block)
    {
        if (!block.IsValid || block.Value >= cfg.Count)
            return false;

        var candidate = cfg[block];
        if (candidate.EndAction.Value != candidate.StartAction.Value + 1 ||
            instructions[candidate.StartAction].Action is not ActionPop ||
            candidate.StartAction.Value == 0 ||
            instructions[new ActionIndex(candidate.StartAction.Value - 1)].Action is not (
                ActionReturn or ActionThrow))
        {
            return false;
        }

        foreach (var predecessor in cfg.Blocks)
        {
            if (predecessor.FirstSuccessor == block || predecessor.SecondSuccessor == block)
                return false;
        }

        return true;
    }

    private static int GetNormalExitDepth(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        BlockIndex target,
        int depth)
    {
        if (!TryGetEnumerationEndPredicate(
                instructions,
                cfg,
                block,
                out var trueMeansEnd) ||
            !target.IsValid)
        {
            return depth;
        }

        bool branchTaken;
        if (target == block.SecondSuccessor)
            branchTaken = true;
        else if (target == block.FirstSuccessor)
            branchTaken = false;
        else
            return depth;

        return branchTaken == trueMeansEnd ? depth : depth + 1;
    }

    private static bool TryGetEnumerationEndPredicate(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        out bool trueMeansEnd)
    {
        trueMeansEnd = true;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            block.EndAction.Value - block.StartAction.Value < 3 ||
            instructions[new ActionIndex(block.EndAction.Value - 1)].Action is not ActionIf)
        {
            return false;
        }

        var action = block.StartAction.Value;
        var hasEnumerationStore =
            instructions[new ActionIndex(action)].Action is ActionStoreRegister;
        if (hasEnumerationStore)
            action++;

        if (action + 2 >= block.EndAction.Value ||
            instructions[new ActionIndex(action)].Action is not ActionPush push ||
            push.PushValues.Count != 1 ||
            push.PushValues[0] is not PushValue.PushValueNull ||
            instructions[new ActionIndex(action + 1)].Action is not (
                ActionEquals or ActionEquals2 or ActionStrictEquals))
        {
            return false;
        }

        action += 2;
        while (action < block.EndAction.Value - 1 &&
               instructions[new ActionIndex(action)].Action is ActionNot)
        {
            trueMeansEnd = !trueMeansEnd;
            action++;
        }

        if (action != block.EndAction.Value - 1)
            return false;

        if (!hasEnumerationStore)
        {
            return instructions.GetBranchTargetAction(new ActionIndex(action)) ==
                block.StartAction;
        }

        foreach (var predecessor in cfg.Blocks)
        {
            if ((predecessor.FirstSuccessor != block.Index &&
                 predecessor.SecondSuccessor != block.Index) ||
                predecessor.EndAction.Value <= predecessor.StartAction.Value)
            {
                continue;
            }

            var predecessorAction = new ActionIndex(predecessor.EndAction.Value - 1);
            if (instructions[predecessorAction].Action is ActionEnumerate or ActionEnumerate2)
                return true;
        }

        return false;
    }

    private static bool IsDynamicEnumerationCleanupOperand(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        ActionIndex action)
    {
        if (instructions[action].Action is not (
                ActionEquals or ActionEquals2 or ActionStrictEquals) ||
            !cfg.TryGetBlockForAction(action, out var blockIndex))
        {
            return false;
        }

        var block = cfg[blockIndex];
        if (action.Value != block.StartAction.Value + 1 ||
            block.EndAction.Value - block.StartAction.Value < 3 ||
            instructions[block.StartAction].Action is not ActionPush push ||
            push.PushValues.Count != 1 ||
            push.PushValues[0] is not PushValue.PushValueNull)
        {
            return false;
        }

        var branchAction = new ActionIndex(block.EndAction.Value - 1);
        if (instructions[branchAction].Action is not ActionIf ||
            instructions.GetBranchTargetAction(branchAction) != block.StartAction)
        {
            return false;
        }

        for (var index = action.Value + 1; index < branchAction.Value; index++)
            if (instructions[new ActionIndex(index)].Action is not ActionNot)
                return false;

        return true;
    }

    private static Avm1StackEffect GetStackEffect(Action action)
    {
        return action switch
        {
            ActionConstantPool or ActionEnd => new Avm1StackEffect(0, 0, true),
            ActionPush push => new Avm1StackEffect(0, push.PushValues.Count, true),
            ActionPop => new Avm1StackEffect(1, 0, true),
            ActionPushDuplicate => new Avm1StackEffect(1, 2, true),
            ActionStackSwap => new Avm1StackEffect(2, 2, true),
            ActionStoreRegister => new Avm1StackEffect(0, 0, true),
            ActionGetVariable => new Avm1StackEffect(1, 1, true),
            ActionSetVariable => new Avm1StackEffect(2, 0, true),
            ActionGetMember => new Avm1StackEffect(2, 1, true),
            ActionSetMember => new Avm1StackEffect(3, 0, true),
            ActionGetProperty => new Avm1StackEffect(2, 1, true),
            ActionSetProperty or ActionCloneSprite =>
                new Avm1StackEffect(3, 0, true),
            ActionRemoveSprite => new Avm1StackEffect(1, 0, true),
            ActionStartDrag => new Avm1StackEffect(0, 0, false),
            ActionEndDrag => new Avm1StackEffect(0, 0, true),
            ActionSetTarget => new Avm1StackEffect(0, 0, true),
            ActionSetTarget2 => new Avm1StackEffect(1, 0, true),
            ActionDefineLocal => new Avm1StackEffect(2, 0, true),
            ActionDefineLocal2 => new Avm1StackEffect(1, 0, true),
            ActionWith => new Avm1StackEffect(1, 0, true),
            ActionTry or Avm1TryEndAction or Avm1CatchStartAction or Avm1CatchEndAction or
                Avm1FinallyStartAction or Avm1FinallyEndAction => new Avm1StackEffect(0, 0, true),
            ActionEnumerate or ActionEnumerate2 => new Avm1StackEffect(1, 1, true),
            ActionDefineFunction function => new Avm1StackEffect(
                0,
                string.IsNullOrEmpty(function.Name) ? 1 : 0,
                true),
            ActionDefineFunction2 function => new Avm1StackEffect(
                0,
                string.IsNullOrEmpty(function.Name) ? 1 : 0,
                true),
            ActionCastOp => new Avm1StackEffect(2, 1, true),
            ActionExtends => new Avm1StackEffect(2, 0, true),
            ActionImplementsOp => new Avm1StackEffect(0, 0, false),
            ActionGetTime => new Avm1StackEffect(0, 1, true),
            ActionGotoFrame2 => new Avm1StackEffect(1, 0, true),
            ActionCall => new Avm1StackEffect(1, 0, true),
            ActionWaitForFrame => new Avm1StackEffect(0, 0, true),
            ActionWaitForFrame2 => new Avm1StackEffect(1, 0, true),
            ActionGetURL2 => new Avm1StackEffect(2, 0, true),
            ActionReturn or ActionThrow => new Avm1StackEffect(1, 0, true),
            ActionIf => new Avm1StackEffect(1, 0, true),
            ActionJump => new Avm1StackEffect(0, 0, true),
            ActionNot => new Avm1StackEffect(1, 1, true),
            ActionAdd or ActionAdd2 or ActionStringAdd or ActionSubtract or ActionMultiply or ActionDivide or
                ActionModulo or ActionEquals or ActionEquals2 or ActionStringEquals or ActionLess or ActionLess2 or
                ActionStringLess or ActionGreater or ActionStringGreater or ActionAnd or ActionOr or ActionBitAnd or
                ActionBitOr or ActionBitXor or ActionBitLShift or ActionBitRShift or ActionBitURShift or ActionStrictEquals =>
                new Avm1StackEffect(2, 1, true),
            ActionIncrement or ActionDecrement or ActionToNumber or ActionToString or
                ActionToInteger or ActionRandomNumber or ActionStringLength or
                ActionMBStringLength or ActionCharToAscii or ActionAsciiToChar or
                ActionMBCharToAscii or ActionMBAsciiToChar or
                ActionTypeOf or ActionTargetPath =>
                new Avm1StackEffect(1, 1, true),
            ActionDelete => new Avm1StackEffect(2, 1, true),
            ActionDelete2 => new Avm1StackEffect(1, 1, true),
            ActionInstanceOf => new Avm1StackEffect(2, 1, true),
            ActionTrace => new Avm1StackEffect(1, 0, true),
            ActionCallFunction or ActionCallMethod or ActionNewObject or ActionNewMethod or ActionInitArray or ActionInitObject =>
                new Avm1StackEffect(0, 0, false),
            ActionStringExtract or ActionMBStringExtract =>
                new Avm1StackEffect(3, 1, true),
            _ => new Avm1StackEffect(0, 0, true)
        };
    }

    private static Avm1StackEffect GetStackEffect(
        Action action,
        IReadOnlyList<StackDepthValue> stack)
    {
        var effect = GetStackEffect(action);
        if (effect.IsKnown)
            return effect;

        if (action is ActionStartDrag)
        {
            return TryGetKnownTruthiness(stack, offsetFromTop: 2, out var constrained)
                ? new Avm1StackEffect(constrained ? 7 : 3, 0, true)
                : effect;
        }

        var shape = action switch
        {
            ActionCallFunction or ActionNewObject =>
                (CountOffset: 1, FixedPopCount: 2, ValuesPerCount: 1, PushCount: 1),
            ActionCallMethod or ActionNewMethod =>
                (CountOffset: 2, FixedPopCount: 3, ValuesPerCount: 1, PushCount: 1),
            ActionInitArray =>
                (CountOffset: 0, FixedPopCount: 1, ValuesPerCount: 1, PushCount: 1),
            ActionInitObject =>
                (CountOffset: 0, FixedPopCount: 1, ValuesPerCount: 2, PushCount: 1),
            ActionImplementsOp =>
                (CountOffset: 1, FixedPopCount: 2, ValuesPerCount: 1, PushCount: 0),
            _ => default
        };
        if (shape.FixedPopCount == 0 ||
            !TryGetKnownInteger(stack, shape.CountOffset, out var count) ||
            count < 0)
        {
            return effect;
        }

        var popCount = (long)shape.FixedPopCount + (long)count * shape.ValuesPerCount;
        return popCount <= int.MaxValue
            ? new Avm1StackEffect((int)popCount, shape.PushCount, true)
            : effect;
    }

    private static List<StackDepthValue> CreateUnknownStack(int depth)
    {
        var stack = new List<StackDepthValue>(Math.Max(depth, 4));
        for (var index = 0; index < depth; index++)
            stack.Add(StackDepthValue.Unknown);
        return stack;
    }

    private static void ApplyStackEffect(
        Action action,
        Avm1StackEffect effect,
        List<StackDepthValue> stack,
        bool underflow)
    {
        if (underflow)
        {
            stack.Clear();
            AddUnknownValues(stack, effect.PushCount);
            return;
        }

        switch (action)
        {
            case ActionPush push:
                foreach (var value in push.PushValues)
                    stack.Add(ToStackDepthValue(value));
                return;
            case ActionPushDuplicate:
                stack.Add(stack[^1]);
                return;
            case ActionStackSwap:
                (stack[^2], stack[^1]) = (stack[^1], stack[^2]);
                return;
            case ActionStoreRegister:
                return;
        }

        if (effect.PopCount > 0)
            stack.RemoveRange(stack.Count - effect.PopCount, effect.PopCount);
        AddUnknownValues(stack, effect.PushCount);
    }

    private static void AddUnknownValues(List<StackDepthValue> stack, int count)
    {
        for (var index = 0; index < count; index++)
            stack.Add(StackDepthValue.Unknown);
    }

    private static StackDepthValue ToStackDepthValue(PushValue value) => value switch
    {
        PushValue.PushValueInteger integer => StackDepthValue.Integer(integer.Value),
        PushValue.PushValueFloat number
            when float.IsFinite(number.Value) &&
                 number.Value >= int.MinValue &&
                 number.Value <= int.MaxValue &&
                 number.Value == MathF.Truncate(number.Value) =>
            StackDepthValue.Integer((int)number.Value),
        PushValue.PushValueDouble number
            when double.IsFinite(number.Value) &&
                 number.Value >= int.MinValue &&
                 number.Value <= int.MaxValue &&
                 number.Value == Math.Truncate(number.Value) =>
            StackDepthValue.Integer((int)number.Value),
        PushValue.PushValueBoolean boolean => StackDepthValue.Boolean(boolean.Value),
        _ => StackDepthValue.Unknown
    };

    private static bool TryGetKnownInteger(
        IReadOnlyList<StackDepthValue> stack,
        int offsetFromTop,
        out int value)
    {
        var index = stack.Count - offsetFromTop - 1;
        if ((uint)index >= (uint)stack.Count ||
            stack[index].Kind is not StackDepthValueKind.Integer)
        {
            value = 0;
            return false;
        }

        value = stack[index].Payload;
        return true;
    }

    private static bool TryGetKnownTruthiness(
        IReadOnlyList<StackDepthValue> stack,
        int offsetFromTop,
        out bool value)
    {
        var index = stack.Count - offsetFromTop - 1;
        if ((uint)index >= (uint)stack.Count)
        {
            value = false;
            return false;
        }

        switch (stack[index].Kind)
        {
            case StackDepthValueKind.Boolean:
                value = stack[index].Payload != 0;
                return true;
            case StackDepthValueKind.Integer:
                value = stack[index].Payload != 0;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private enum StackDepthValueKind : byte
    {
        Unknown,
        Integer,
        Boolean
    }

    private readonly record struct StackDepthValue(
        StackDepthValueKind Kind,
        int Payload)
    {
        public static StackDepthValue Unknown => new(StackDepthValueKind.Unknown, 0);

        public static StackDepthValue Integer(int value) =>
            new(StackDepthValueKind.Integer, value);

        public static StackDepthValue Boolean(bool value) =>
            new(StackDepthValueKind.Boolean, value ? 1 : 0);
    }
}

public readonly record struct Avm1StackEffect(int PopCount, int PushCount, bool IsKnown);
