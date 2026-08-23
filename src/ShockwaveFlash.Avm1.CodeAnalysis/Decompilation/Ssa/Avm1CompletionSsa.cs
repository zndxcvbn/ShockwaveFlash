using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Ssa;

public sealed class Avm1CompletionSsa
{
    private readonly Avm1CompletionPhiInput[] _phiInputs;

    private Avm1CompletionSsa(
        Avm1CompletionValue[] values,
        Avm1CompletionPhiNode[] phiNodes,
        Avm1CompletionPhiInput[] phiInputs,
        Avm1CompletionFlow[] flows)
    {
        Values = values;
        PhiNodes = phiNodes;
        _phiInputs = phiInputs;
        PhiInputs = phiInputs;
        Flows = flows;
    }

    public IReadOnlyList<Avm1CompletionValue> Values { get; }

    public IReadOnlyList<Avm1CompletionPhiNode> PhiNodes { get; }

    public IReadOnlyList<Avm1CompletionPhiInput> PhiInputs { get; }

    public IReadOnlyList<Avm1CompletionFlow> Flows { get; }

    public ReadOnlySpan<Avm1CompletionPhiInput> GetPhiInputs(Avm1CompletionPhiNode phi)
    {
        if (!phi.InputStart.IsValid || phi.InputCount == 0)
            return [];

        return _phiInputs.AsSpan(
            phi.InputStart.Value,
            phi.InputCount);
    }

    internal static Avm1CompletionSsa Build(
        Avm1ControlFlowGraph cfg,
        Avm1TacIr tac,
        Avm1FlowGraph flowGraph) => cfg.CompletionEdges.Count == 0
            ? BuildWithoutCompletionEdges(cfg, tac, flowGraph)
            : new ContextBuilder(cfg, tac, flowGraph).Build();

    private static Avm1CompletionSsa BuildWithoutCompletionEdges(
        Avm1ControlFlowGraph cfg,
        Avm1TacIr tac,
        Avm1FlowGraph flowGraph)
    {
        if (cfg.Count == 0)
            return new Avm1CompletionSsa([], [], [], []);

        var values = new List<Avm1CompletionValue>();
        var flows = new List<Avm1CompletionFlow>();
        var throwPayloads = Enumerable.Repeat(CompletionValueIndex.Invalid, cfg.Count).ToArray();

        foreach (var instruction in tac.Instructions)
        {
            if (instruction.Op is not (Avm1TacOp.Return or Avm1TacOp.Throw) ||
                !cfg.TryGetBlockForAction(instruction.Action, out var block))
            {
                continue;
            }

            var kind = instruction.Op is Avm1TacOp.Return
                ? Avm1CompletionKind.Return
                : Avm1CompletionKind.Throw;
            var payload = new CompletionValueIndex(values.Count);
            values.Add(new Avm1CompletionValue(
                payload,
                instruction.Operand0.IsValid
                    ? Avm1CompletionValueKind.Source
                    : Avm1CompletionValueKind.Unknown,
                kind,
                instruction.Operand0,
                instruction.Action));

            if (kind is Avm1CompletionKind.Return)
            {
                flows.Add(new Avm1CompletionFlow(
                    block,
                    BlockIndex.Invalid,
                    Avm1AbruptFlowEdgeKind.Terminal,
                    kind,
                    Avm1FlowState.Normal,
                    Avm1FlowState.Return,
                    payload,
                    flowGraph.NormalContext,
                    FlowContextIndex.Invalid));
            }
            else
            {
                throwPayloads[block.Value] = payload;
            }
        }

        for (var blockValue = 0; blockValue < cfg.Count; blockValue++)
        {
            var blockIndex = new BlockIndex(blockValue);
            var block = cfg[blockIndex];
            var throwPayload = throwPayloads[blockValue];
            if (block.ExceptionSuccessor.IsValid)
            {
                if (!throwPayload.IsValid)
                {
                    var action = block.EndAction.Value > block.StartAction.Value
                        ? new ActionIndex(block.EndAction.Value - 1)
                        : block.StartAction;
                    throwPayload = new CompletionValueIndex(values.Count);
                    values.Add(new Avm1CompletionValue(
                        throwPayload,
                        Avm1CompletionValueKind.Unknown,
                        Avm1CompletionKind.Throw,
                        ValueIndex.Invalid,
                        action));
                }

                flows.Add(new Avm1CompletionFlow(
                    blockIndex,
                    block.ExceptionSuccessor,
                    Avm1AbruptFlowEdgeKind.Exception,
                    Avm1CompletionKind.Throw,
                    Avm1FlowState.Normal,
                    Avm1FlowState.Normal,
                    throwPayload,
                    flowGraph.NormalContext,
                    flowGraph.NormalContext));
            }
            else if (block.Terminator is Avm1BlockTerminatorKind.Throw && throwPayload.IsValid)
            {
                flows.Add(new Avm1CompletionFlow(
                    blockIndex,
                    BlockIndex.Invalid,
                    Avm1AbruptFlowEdgeKind.Terminal,
                    Avm1CompletionKind.Throw,
                    Avm1FlowState.Normal,
                    Avm1FlowState.Throw,
                    throwPayload,
                    flowGraph.NormalContext,
                    FlowContextIndex.Invalid));
            }
        }

        return new Avm1CompletionSsa(
            values.ToArray(),
            [],
            [],
            flows.ToArray());
    }

    private sealed class ContextBuilder
    {
        private readonly Avm1ControlFlowGraph _cfg;
        private readonly Avm1FlowGraph _flowGraph;
        private readonly Dictionary<FlowPredecessor, CompletionValueIndex[]>[] _incoming;
        private readonly CompletionValueIndex[][] _entryPayloads;
        private readonly bool[] _hasEntry;
        private readonly bool[] _queued;
        private readonly ValueIndex[] _returnPayloads;
        private readonly ValueIndex[] _throwPayloads;
        private readonly ActionIndex[] _returnActions;
        private readonly ActionIndex[] _throwActions;
        private readonly Queue<FlowPointIndex> _worklist = [];
        private readonly List<Avm1CompletionValue> _values = [];
        private readonly Dictionary<(Avm1CompletionKind Kind, int Action), CompletionValueIndex> _sources = [];
        private readonly Dictionary<(int Block, int Context, int Frame), ContextPhiBuilder> _phis = [];
        private readonly Dictionary<(int Block, int Context, int Frame, int Predecessor), CompletionValueIndex> _mergeUnknowns = [];
        private readonly HashSet<Avm1CompletionFlow> _flowSet = [];

        public ContextBuilder(
            Avm1ControlFlowGraph cfg,
            Avm1TacIr tac,
            Avm1FlowGraph flowGraph)
        {
            _cfg = cfg;
            _flowGraph = flowGraph;
            _incoming = new Dictionary<FlowPredecessor, CompletionValueIndex[]>[flowGraph.Count];
            _entryPayloads = new CompletionValueIndex[flowGraph.Count][];
            _hasEntry = new bool[flowGraph.Count];
            _queued = new bool[flowGraph.Count];
            _returnPayloads = Enumerable.Repeat(ValueIndex.Invalid, cfg.Count).ToArray();
            _throwPayloads = Enumerable.Repeat(ValueIndex.Invalid, cfg.Count).ToArray();
            _returnActions = Enumerable.Repeat(ActionIndex.Invalid, cfg.Count).ToArray();
            _throwActions = Enumerable.Repeat(ActionIndex.Invalid, cfg.Count).ToArray();

            for (var point = 0; point < flowGraph.Count; point++)
            {
                _incoming[point] = [];
                _entryPayloads[point] = [];
            }

            foreach (var instruction in tac.Instructions)
            {
                if (!cfg.TryGetBlockForAction(instruction.Action, out var block))
                    continue;

                if (instruction.Op is Avm1TacOp.Return)
                {
                    _returnPayloads[block.Value] = instruction.Operand0;
                    _returnActions[block.Value] = instruction.Action;
                }
                else if (instruction.Op is Avm1TacOp.Throw)
                {
                    _throwPayloads[block.Value] = instruction.Operand0;
                    _throwActions[block.Value] = instruction.Action;
                }
            }
        }

        public Avm1CompletionSsa Build()
        {
            if (_cfg.Count == 0)
                return new Avm1CompletionSsa([], [], [], []);

            if (_flowGraph.TryGetPoint(
                new BlockIndex(0),
                _flowGraph.NormalContext,
                out var entry))
            {
                SetIncoming(
                    entry,
                    new FlowPredecessor(FlowPointIndex.Invalid),
                    []);
                DrainWorklist();
            }

            return CreateResult();
        }

        private void DrainWorklist()
        {
            while (_worklist.TryDequeue(out var point))
            {
                _queued[point.Value] = false;
                ProcessPoint(point);
            }
        }

        private void ProcessPoint(FlowPointIndex pointIndex)
        {
            var point = _flowGraph[pointIndex];
            var block = _cfg[point.Block];
            var sourceContext = _flowGraph.GetContext(point.Context);
            var payloads = _entryPayloads[pointIndex.Value];
            var routedReturn = false;
            var routedThrow = false;

            foreach (var transition in _flowGraph.GetTransitions(pointIndex))
            {
                var targetBlock = transition.Target.IsValid
                    ? _flowGraph[transition.Target].Block
                    : BlockIndex.Invalid;
                var targetContext = _flowGraph.GetContext(transition.TargetContext);
                switch (transition.Kind)
                {
                    case Avm1FlowTransitionKind.Normal:
                        SetIncoming(
                            transition.Target,
                            new FlowPredecessor(pointIndex),
                            payloads);
                        break;

                    case Avm1FlowTransitionKind.Exception:
                        {
                            var throwPayload = GetAbruptPayload(
                                point.Block,
                                Avm1CompletionKind.Throw);
                            AddFlow(new Avm1CompletionFlow(
                                point.Block,
                                targetBlock,
                                Avm1AbruptFlowEdgeKind.Exception,
                                Avm1CompletionKind.Throw,
                                sourceContext.State,
                                targetContext.State,
                                throwPayload,
                                point.Context,
                                transition.TargetContext));
                            SetIncoming(
                                transition.Target,
                                new FlowPredecessor(pointIndex),
                                BuildExceptionPayloads(
                                    point.Context,
                                    transition.TargetContext,
                                    payloads,
                                    throwPayload));
                            routedThrow |= block.Terminator is Avm1BlockTerminatorKind.Throw;
                            break;
                        }

                    case Avm1FlowTransitionKind.Completion:
                        {
                            var completionPayload = block.Terminator is Avm1BlockTerminatorKind.Return &&
                                transition.CompletionKind is Avm1CompletionKind.Return
                                    ? GetAbruptPayload(point.Block, Avm1CompletionKind.Return)
                                    : GetActivePayload(
                                        point.Block,
                                        transition.CompletionKind,
                                        payloads);
                            AddFlow(new Avm1CompletionFlow(
                                point.Block,
                                targetBlock,
                                Avm1AbruptFlowEdgeKind.Completion,
                                transition.CompletionKind,
                                sourceContext.State,
                                targetContext.State,
                                completionPayload,
                                point.Context,
                                transition.TargetContext));
                            SetIncoming(
                                transition.Target,
                                new FlowPredecessor(pointIndex),
                                BuildCompletionPayloads(
                                    point.Context,
                                    transition.TargetContext,
                                    payloads,
                                    completionPayload,
                                    block.Terminator is Avm1BlockTerminatorKind.Return &&
                                        transition.CompletionKind is Avm1CompletionKind.Return));
                            routedReturn |= transition.CompletionKind is Avm1CompletionKind.Return;
                            routedThrow |= transition.CompletionKind is Avm1CompletionKind.Throw;
                            break;
                        }
                }
            }

            if (block.Terminator is Avm1BlockTerminatorKind.Return && !routedReturn)
            {
                AddTerminalFlow(point, sourceContext, Avm1CompletionKind.Return);
            }
            else if (block.Terminator is Avm1BlockTerminatorKind.Throw && !routedThrow)
            {
                AddTerminalFlow(point, sourceContext, Avm1CompletionKind.Throw);
            }
        }

        private CompletionValueIndex[] BuildExceptionPayloads(
            FlowContextIndex sourceContext,
            FlowContextIndex targetContext,
            CompletionValueIndex[] payloads,
            CompletionValueIndex throwPayload)
        {
            if (sourceContext == targetContext)
                return payloads;

            var target = _flowGraph.GetContext(targetContext);
            if ((target.Flags & Avm1FlowContextFlags.Widened) != 0)
                return [throwPayload];

            var result = new CompletionValueIndex[target.Depth];
            Array.Copy(payloads, result, Math.Min(payloads.Length, result.Length));
            if (result.Length > 0)
                result[^1] = throwPayload;
            return result;
        }

        private CompletionValueIndex[] BuildCompletionPayloads(
            FlowContextIndex sourceContext,
            FlowContextIndex targetContext,
            CompletionValueIndex[] payloads,
            CompletionValueIndex completionPayload,
            bool overridesContext)
        {
            var target = _flowGraph.GetContext(targetContext);
            if (overridesContext)
                return target.Depth == 0 ? [] : [completionPayload];

            if (sourceContext == targetContext)
                return payloads;

            var result = new CompletionValueIndex[target.Depth];
            Array.Copy(payloads, result, Math.Min(payloads.Length, result.Length));
            return result;
        }

        private CompletionValueIndex GetActivePayload(
            BlockIndex block,
            Avm1CompletionKind kind,
            CompletionValueIndex[] payloads) => payloads.Length == 0 || !payloads[^1].IsValid
            ? GetUnknownPayload(block, kind)
            : payloads[^1];

        private void AddTerminalFlow(
            Avm1FlowPoint point,
            Avm1FlowContext sourceContext,
            Avm1CompletionKind kind)
        {
            AddFlow(new Avm1CompletionFlow(
                point.Block,
                BlockIndex.Invalid,
                Avm1AbruptFlowEdgeKind.Terminal,
                kind,
                sourceContext.State,
                ToFlowState(kind),
                GetAbruptPayload(point.Block, kind),
                point.Context,
                FlowContextIndex.Invalid));
        }

        private CompletionValueIndex GetAbruptPayload(
            BlockIndex block,
            Avm1CompletionKind kind)
        {
            var sourceValue = kind is Avm1CompletionKind.Return
                ? _returnPayloads[block.Value]
                : _throwPayloads[block.Value];
            var action = kind is Avm1CompletionKind.Return
                ? _returnActions[block.Value]
                : _throwActions[block.Value];
            return sourceValue.IsValid && action.IsValid
                ? GetSourcePayload(kind, action, sourceValue)
                : GetUnknownPayload(block, kind);
        }

        private CompletionValueIndex GetUnknownPayload(
            BlockIndex block,
            Avm1CompletionKind kind)
        {
            var basicBlock = _cfg[block];
            var action = basicBlock.EndAction.Value > basicBlock.StartAction.Value
                ? new ActionIndex(basicBlock.EndAction.Value - 1)
                : basicBlock.StartAction;
            return GetSourcePayload(kind, action, ValueIndex.Invalid);
        }

        private CompletionValueIndex GetSourcePayload(
            Avm1CompletionKind kind,
            ActionIndex action,
            ValueIndex sourceValue)
        {
            var key = (kind, action.Value);
            if (_sources.TryGetValue(key, out var result))
                return result;

            result = new CompletionValueIndex(_values.Count);
            _values.Add(new Avm1CompletionValue(
                result,
                sourceValue.IsValid
                    ? Avm1CompletionValueKind.Source
                    : Avm1CompletionValueKind.Unknown,
                kind,
                sourceValue,
                action));
            _sources.Add(key, result);
            return result;
        }

        private void SetIncoming(
            FlowPointIndex point,
            FlowPredecessor predecessor,
            CompletionValueIndex[] payloads)
        {
            if (!point.IsValid || point.Value >= _flowGraph.Count)
                return;

            var incoming = _incoming[point.Value];
            if (incoming.TryGetValue(predecessor, out var previous) &&
                ValuesEqual(previous, payloads))
            {
                return;
            }

            incoming[predecessor] = payloads.ToArray();
            var merged = MergeIncoming(point, incoming);
            if (_hasEntry[point.Value] && ValuesEqual(_entryPayloads[point.Value], merged))
                return;

            _entryPayloads[point.Value] = merged;
            _hasEntry[point.Value] = true;
            Enqueue(point);
        }

        private CompletionValueIndex[] MergeIncoming(
            FlowPointIndex pointIndex,
            IReadOnlyDictionary<FlowPredecessor, CompletionValueIndex[]> incoming)
        {
            var point = _flowGraph[pointIndex];
            var context = _flowGraph.GetContext(point.Context);
            if (context.Depth == 0)
                return [];

            var ordered = incoming.OrderBy(pair => pair.Key.Point.Value).ToArray();
            var result = new CompletionValueIndex[context.Depth];
            for (var frame = 0; frame < result.Length; frame++)
            {
                var incomingValues = ordered
                    .Select(pair => frame < pair.Value.Length
                        ? pair.Value[frame]
                        : GetMergeUnknown(point, frame, pair.Key.Point))
                    .ToArray();
                var first = incomingValues[0];
                var needsPhi = incomingValues.Any(value => value != first);
                var key = (point.Block.Value, point.Context.Value, frame);
                _phis.TryGetValue(key, out var phi);
                if (!needsPhi && phi is null)
                {
                    result[frame] = first;
                    continue;
                }

                if (phi is null)
                {
                    var frameContext = _flowGraph.GetFrameContext(point.Context, frame);
                    var phiResult = new CompletionValueIndex(_values.Count);
                    _values.Add(new Avm1CompletionValue(
                        phiResult,
                        Avm1CompletionValueKind.Phi,
                        ToCompletionKind(frameContext.State),
                        ValueIndex.Invalid,
                        ActionIndex.Invalid));
                    phi = new ContextPhiBuilder(
                        phiResult,
                        point.Block,
                        point.Context,
                        frame,
                        frameContext.State);
                    _phis.Add(key, phi);
                }

                phi.SetIncoming(
                    ordered.Select(pair => pair.Key.Point).ToArray(),
                    incomingValues);
                result[frame] = phi.Result;
            }

            return result;
        }

        private CompletionValueIndex GetMergeUnknown(
            Avm1FlowPoint point,
            int frame,
            FlowPointIndex predecessor)
        {
            var key = (point.Block.Value, point.Context.Value, frame, predecessor.Value);
            if (_mergeUnknowns.TryGetValue(key, out var result))
                return result;

            var frameContext = _flowGraph.GetFrameContext(point.Context, frame);
            result = GetUnknownPayload(point.Block, ToCompletionKind(frameContext.State));
            _mergeUnknowns.Add(key, result);
            return result;
        }

        private Avm1CompletionSsa CreateResult()
        {
            var inputs = new List<Avm1CompletionPhiInput>();
            var phiNodes = new List<Avm1CompletionPhiNode>(_phis.Count);
            foreach (var phi in _phis.Values
                .OrderBy(phi => phi.Block.Value)
                .ThenBy(phi => phi.Context.Value)
                .ThenBy(phi => phi.Frame))
            {
                var start = phi.IncomingValues.Length == 0
                    ? CompletionInputIndex.Invalid
                    : new CompletionInputIndex(inputs.Count);
                for (var i = 0; i < phi.IncomingValues.Length; i++)
                {
                    var predecessor = phi.Predecessors[i];
                    var predecessorPoint = predecessor.IsValid
                        ? _flowGraph[predecessor]
                        : default;
                    var predecessorContext = predecessor.IsValid
                        ? _flowGraph.GetContext(predecessorPoint.Context)
                        : _flowGraph.GetContext(_flowGraph.NormalContext);
                    inputs.Add(new Avm1CompletionPhiInput(
                        predecessor.IsValid ? predecessorPoint.Block : BlockIndex.Invalid,
                        predecessorContext.State,
                        phi.IncomingValues[i],
                        predecessor.IsValid
                            ? predecessorPoint.Context
                            : _flowGraph.NormalContext));
                }

                phiNodes.Add(new Avm1CompletionPhiNode(
                    phi.Result,
                    phi.Block,
                    phi.State,
                    start,
                    phi.IncomingValues.Length,
                    phi.Frame,
                    phi.Context));
            }

            var flows = _flowSet
                .OrderBy(flow => flow.Source.Value)
                .ThenBy(flow => flow.SourceContext.Value)
                .ThenBy(flow => flow.EdgeKind)
                .ThenBy(flow => flow.Kind)
                .ThenBy(flow => flow.Target.Value)
                .ThenBy(flow => flow.Payload.Value)
                .ToArray();
            return new Avm1CompletionSsa(
                _values.ToArray(),
                phiNodes.ToArray(),
                inputs.ToArray(),
                flows);
        }

        private void AddFlow(Avm1CompletionFlow flow) => _flowSet.Add(flow);

        private void Enqueue(FlowPointIndex point)
        {
            if (_queued[point.Value])
                return;

            _queued[point.Value] = true;
            _worklist.Enqueue(point);
        }

        private static bool ValuesEqual(
            CompletionValueIndex[] left,
            CompletionValueIndex[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private static Avm1FlowState ToFlowState(Avm1CompletionKind kind) =>
            kind is Avm1CompletionKind.Return
                ? Avm1FlowState.Return
                : Avm1FlowState.Throw;

        private static Avm1CompletionKind ToCompletionKind(Avm1FlowState state) =>
            state is Avm1FlowState.Return
                ? Avm1CompletionKind.Return
                : Avm1CompletionKind.Throw;

        private readonly record struct FlowPredecessor(FlowPointIndex Point);

        private sealed class ContextPhiBuilder(
            CompletionValueIndex result,
            BlockIndex block,
            FlowContextIndex context,
            int frame,
            Avm1FlowState state)
        {
            public CompletionValueIndex Result { get; } = result;

            public BlockIndex Block { get; } = block;

            public FlowContextIndex Context { get; } = context;

            public int Frame { get; } = frame;

            public Avm1FlowState State { get; } = state;

            public FlowPointIndex[] Predecessors { get; private set; } = [];

            public CompletionValueIndex[] IncomingValues { get; private set; } = [];

            public void SetIncoming(
                FlowPointIndex[] predecessors,
                CompletionValueIndex[] incomingValues)
            {
                Predecessors = predecessors;
                IncomingValues = incomingValues;
            }
        }
    }

}

public enum Avm1CompletionValueKind : byte
{
    Source,
    Unknown,
    Phi
}

public enum Avm1AbruptFlowEdgeKind : byte
{
    Exception,
    Completion,
    Terminal
}

public readonly record struct Avm1CompletionValue(
    CompletionValueIndex Index,
    Avm1CompletionValueKind Kind,
    Avm1CompletionKind CompletionKind,
    ValueIndex SourceValue,
    ActionIndex OriginAction);

public readonly record struct Avm1CompletionPhiNode(
    CompletionValueIndex Result,
    BlockIndex Block,
    Avm1FlowState State,
    CompletionInputIndex InputStart,
    int InputCount,
    int Frame,
    FlowContextIndex Context);

public readonly record struct Avm1CompletionPhiInput(
    BlockIndex Predecessor,
    Avm1FlowState PredecessorState,
    CompletionValueIndex Value,
    FlowContextIndex PredecessorContext);

public readonly record struct Avm1CompletionFlow(
    BlockIndex Source,
    BlockIndex Target,
    Avm1AbruptFlowEdgeKind EdgeKind,
    Avm1CompletionKind Kind,
    Avm1FlowState SourceState,
    Avm1FlowState TargetState,
    CompletionValueIndex Payload,
    FlowContextIndex SourceContext,
    FlowContextIndex TargetContext);
