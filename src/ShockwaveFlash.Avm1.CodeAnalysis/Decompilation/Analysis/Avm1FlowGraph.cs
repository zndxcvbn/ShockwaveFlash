using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1FlowGraph
{
    private static readonly Avm1FlowContext[] NormalContexts =
    [
        new Avm1FlowContext(
            new FlowContextIndex(0),
            FlowContextIndex.Invalid,
            Depth: 0,
            Avm1FlowState.Normal,
            Avm1FlowContextFlags.None)
    ];

    private readonly Avm1FlowTransition[] _transitions;
    private readonly FlowPointIndex[] _pointByBlockAndContext;
    private readonly FlowContextIndex _normalContext;
    private readonly int _blockCount;
    private readonly bool _isBlockFlow;

    private Avm1FlowGraph(
        Avm1FlowContext[] contexts,
        Avm1FlowPoint[] points,
        Avm1FlowTransition[] transitions,
        FlowPointIndex[] pointByBlockAndContext,
        int blockCount,
        bool isBlockFlow = false)
    {
        Contexts = contexts;
        Points = points;
        _transitions = transitions;
        Transitions = transitions;
        _pointByBlockAndContext = pointByBlockAndContext;
        _blockCount = blockCount;
        _isBlockFlow = isBlockFlow;
        _normalContext = contexts.Length == 0
            ? FlowContextIndex.Invalid
            : contexts[0].Index;
    }

    public IReadOnlyList<Avm1FlowContext> Contexts { get; }

    public IReadOnlyList<Avm1FlowPoint> Points { get; }

    public IReadOnlyList<Avm1FlowTransition> Transitions { get; }

    public FlowContextIndex NormalContext => _normalContext;

    public int Count => Points.Count;

    public Avm1FlowPoint this[FlowPointIndex point] => Points[point.Value];

    public Avm1FlowContext GetContext(FlowContextIndex context) => Contexts[context.Value];

    public Avm1FlowContext GetFrameContext(FlowContextIndex context, int frame)
    {
        var current = GetContext(context);
        var targetDepth = frame + 1;
        if (frame < 0 || targetDepth > current.Depth)
            throw new ArgumentOutOfRangeException(nameof(frame));

        while (current.Depth > targetDepth)
            current = GetContext(current.Parent);
        return current;
    }

    public ReadOnlySpan<Avm1FlowTransition> GetTransitions(FlowPointIndex point)
    {
        var flowPoint = Points[point.Value];
        if (!flowPoint.TransitionStart.IsValid || flowPoint.TransitionCount == 0)
            return [];

        return _transitions.AsSpan(flowPoint.TransitionStart.Value, flowPoint.TransitionCount);
    }

    public bool TryGetPoint(
        BlockIndex block,
        FlowContextIndex context,
        out FlowPointIndex point)
    {
        if (!block.IsValid || !context.IsValid)
        {
            point = FlowPointIndex.Invalid;
            return false;
        }

        if (block.Value >= _blockCount || context.Value >= Contexts.Count)
        {
            point = FlowPointIndex.Invalid;
            return false;
        }


        if (_isBlockFlow)
        {
            point = new FlowPointIndex(block.Value);
            return context == _normalContext;
        }

        point = _pointByBlockAndContext[block.Value * Contexts.Count + context.Value];
        return point.IsValid;
    }

    public static Avm1FlowGraph Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg) => cfg.CompletionEdges.Count == 0
            ? BuildBlockFlow(cfg)
            : new Builder(instructions, cfg).Build();

    private static Avm1FlowGraph BuildBlockFlow(Avm1ControlFlowGraph cfg)
    {
        if (cfg.Count == 0)
        {
            return new Avm1FlowGraph(
                NormalContexts,
                [],
                [],
                [],
                blockCount: 0,
                isBlockFlow: true);
        }

        var points = new Avm1FlowPoint[cfg.Count];
        var transitionCount = 0;
        foreach (var block in cfg.Blocks)
        {
            transitionCount += block.FirstSuccessor.IsValid ? 1 : 0;
            transitionCount += block.SecondSuccessor.IsValid ? 1 : 0;
            transitionCount += block.ExceptionSuccessor.IsValid ? 1 : 0;
        }

        var transitions = new Avm1FlowTransition[transitionCount];
        var cursor = 0;
        for (var blockValue = 0; blockValue < cfg.Count; blockValue++)
        {
            var blockIndex = new BlockIndex(blockValue);
            var block = cfg[blockIndex];
            var start = cursor;
            AddBlockTransition(
                transitions,
                ref cursor,
                Avm1FlowTransitionKind.Normal,
                block.FirstSuccessor,
                BlockIndex.Invalid,
                Avm1CompletionKind.Return);
            AddBlockTransition(
                transitions,
                ref cursor,
                Avm1FlowTransitionKind.Normal,
                block.SecondSuccessor,
                BlockIndex.Invalid,
                Avm1CompletionKind.Return);
            AddBlockTransition(
                transitions,
                ref cursor,
                Avm1FlowTransitionKind.Exception,
                block.ExceptionSuccessor,
                block.ExceptionStackSource,
                Avm1CompletionKind.Throw);
            points[blockValue] = new Avm1FlowPoint(
                new FlowPointIndex(blockValue),
                blockIndex,
                new FlowContextIndex(0),
                cursor == start
                    ? FlowTransitionIndex.Invalid
                    : new FlowTransitionIndex(start),
                cursor - start);
        }

        return new Avm1FlowGraph(
            NormalContexts,
            points,
            transitions,
            [],
            cfg.Count,
            isBlockFlow: true);
    }

    private static void AddBlockTransition(
        Avm1FlowTransition[] transitions,
        ref int cursor,
        Avm1FlowTransitionKind kind,
        BlockIndex target,
        BlockIndex stackSource,
        Avm1CompletionKind completionKind)
    {
        if (!target.IsValid)
            return;

        transitions[cursor++] = new Avm1FlowTransition(
            kind,
            new FlowPointIndex(target.Value),
            new FlowContextIndex(0),
            stackSource,
            completionKind);
    }

    private sealed class Builder
    {
        private readonly Avm1InstructionTable _instructions;
        private readonly Avm1ControlFlowGraph _cfg;
        private readonly int _maximumContextDepth;
        private readonly bool[] _isCatchEntry;
        private readonly bool[] _isFinallyExit;
        private readonly List<Avm1FlowContext> _contexts = [];
        private readonly Dictionary<(Avm1FlowState State, int Parent, Avm1FlowContextFlags Flags), FlowContextIndex> _contextMap = [];
        private readonly List<Avm1FlowPoint> _points = [];
        private readonly List<Avm1FlowTransition> _transitions = [];
        private readonly Dictionary<(int Block, int Context), FlowPointIndex> _pointMap = [];
        private readonly Queue<FlowPointIndex> _worklist = [];
        private readonly FlowContextIndex _normalContext;
        private readonly FlowContextIndex _returnContext;
        private FlowContextIndex _widenedThrowContext = FlowContextIndex.Invalid;

        public Builder(
            Avm1InstructionTable instructions,
            Avm1ControlFlowGraph cfg)
        {
            _instructions = instructions;
            _cfg = cfg;
            _maximumContextDepth = Math.Max(
                2,
                instructions.TryRegions.Count(region => region.FinallyEnterAction.IsValid) + 1);
            _isCatchEntry = new bool[cfg.Count];
            _isFinallyExit = new bool[cfg.Count];

            _normalContext = AddContext(
                Avm1FlowState.Normal,
                FlowContextIndex.Invalid,
                depth: 0,
                Avm1FlowContextFlags.None);
            _returnContext = AddContext(
                Avm1FlowState.Return,
                _normalContext,
                depth: 1,
                Avm1FlowContextFlags.None);

            for (var block = 0; block < cfg.Count; block++)
            {
                var basicBlock = cfg[new BlockIndex(block)];
                for (var action = basicBlock.StartAction.Value; action < basicBlock.EndAction.Value; action++)
                {
                    var kind = instructions[new ActionIndex(action)].Kind;
                    _isCatchEntry[block] |= kind is Avm1InstructionKind.CatchEnter;
                    _isFinallyExit[block] |= kind is Avm1InstructionKind.FinallyExit;
                }
            }
        }

        public Avm1FlowGraph Build()
        {
            if (_cfg.Count == 0)
            {
                return new Avm1FlowGraph(
                    _contexts.ToArray(),
                    [],
                    [],
                    [],
                    blockCount: 0);
            }

            AddPoint(new BlockIndex(0), _normalContext);
            DrainWorklist();

            for (var block = 0; block < _cfg.Count; block++)
            {
                if (_pointMap.ContainsKey((block, _normalContext.Value)))
                    continue;

                AddPoint(new BlockIndex(block), _normalContext);
                DrainWorklist();
            }

            var contexts = _contexts.ToArray();
            var points = _points.ToArray();
            var pointLookup = Enumerable.Repeat(
                FlowPointIndex.Invalid,
                _cfg.Count * contexts.Length).ToArray();
            foreach (var point in points)
            {
                pointLookup[point.Block.Value * contexts.Length + point.Context.Value] = point.Index;
            }

            return new Avm1FlowGraph(
                contexts,
                points,
                _transitions.ToArray(),
                pointLookup,
                _cfg.Count);
        }

        private void DrainWorklist()
        {
            while (_worklist.TryDequeue(out var point))
                ProcessPoint(point);
        }

        private void ProcessPoint(FlowPointIndex pointIndex)
        {
            var point = _points[pointIndex.Value];
            var block = _cfg[point.Block];
            var context = _contexts[point.Context.Value];
            var start = _transitions.Count;

            if (!_isFinallyExit[point.Block.Value] || context.State is Avm1FlowState.Normal)
            {
                AddTransition(
                    Avm1FlowTransitionKind.Normal,
                    block.FirstSuccessor,
                    point.Context,
                    BlockIndex.Invalid,
                    Avm1CompletionKind.Return);
                AddTransition(
                    Avm1FlowTransitionKind.Normal,
                    block.SecondSuccessor,
                    point.Context,
                    BlockIndex.Invalid,
                    Avm1CompletionKind.Return);
            }

            if (block.ExceptionSuccessor.IsValid)
            {
                var targetContext = IsCatchEntry(block.ExceptionSuccessor)
                    ? point.Context
                    : PushThrow(point.Context);
                AddTransition(
                    Avm1FlowTransitionKind.Exception,
                    block.ExceptionSuccessor,
                    targetContext,
                    block.ExceptionStackSource,
                    Avm1CompletionKind.Throw);
            }

            foreach (var completion in _cfg.GetCompletionEdges(point.Block))
            {
                var completionState = ToFlowState(completion.Kind);
                FlowContextIndex targetContext;
                if (block.Terminator is Avm1BlockTerminatorKind.Return &&
                    completion.Kind is Avm1CompletionKind.Return)
                {
                    targetContext = _returnContext;
                }
                else
                {
                    if (context.State != completionState)
                        continue;

                    targetContext = completion.Kind is Avm1CompletionKind.Throw &&
                        IsCatchEntry(completion.Target)
                            ? Pop(point.Context)
                            : point.Context;
                }

                AddTransition(
                    Avm1FlowTransitionKind.Completion,
                    completion.Target,
                    targetContext,
                    completion.StackSource,
                    completion.Kind);
            }

            _points[pointIndex.Value] = point with
            {
                TransitionStart = _transitions.Count == start
                    ? FlowTransitionIndex.Invalid
                    : new FlowTransitionIndex(start),
                TransitionCount = _transitions.Count - start
            };
        }

        private void AddTransition(
            Avm1FlowTransitionKind kind,
            BlockIndex targetBlock,
            FlowContextIndex targetContext,
            BlockIndex stackSource,
            Avm1CompletionKind completionKind)
        {
            if (!targetBlock.IsValid)
            {
                if (kind is Avm1FlowTransitionKind.Completion)
                {
                    _transitions.Add(new Avm1FlowTransition(
                        kind,
                        FlowPointIndex.Invalid,
                        targetContext,
                        stackSource,
                        completionKind));
                }

                return;
            }

            var target = AddPoint(targetBlock, targetContext);
            _transitions.Add(new Avm1FlowTransition(
                kind,
                target,
                targetContext,
                stackSource,
                completionKind));
        }

        private FlowPointIndex AddPoint(BlockIndex block, FlowContextIndex context)
        {
            var key = (block.Value, context.Value);
            if (_pointMap.TryGetValue(key, out var existing))
                return existing;

            var point = new FlowPointIndex(_points.Count);
            _points.Add(new Avm1FlowPoint(
                point,
                block,
                context,
                FlowTransitionIndex.Invalid,
                0));
            _pointMap.Add(key, point);
            _worklist.Enqueue(point);
            return point;
        }

        private FlowContextIndex PushThrow(FlowContextIndex parent)
        {
            var parentContext = _contexts[parent.Value];
            if ((parentContext.Flags & Avm1FlowContextFlags.Widened) != 0)
                return parent;

            if (parentContext.Depth >= _maximumContextDepth)
            {
                if (_widenedThrowContext.IsValid)
                    return _widenedThrowContext;

                _widenedThrowContext = AddContext(
                    Avm1FlowState.Throw,
                    _normalContext,
                    depth: 1,
                    Avm1FlowContextFlags.Widened);
                return _widenedThrowContext;
            }

            var key = (Avm1FlowState.Throw, parent.Value, Avm1FlowContextFlags.None);
            if (_contextMap.TryGetValue(key, out var context))
                return context;

            return AddContext(
                Avm1FlowState.Throw,
                parent,
                parentContext.Depth + 1,
                Avm1FlowContextFlags.None);
        }

        private FlowContextIndex Pop(FlowContextIndex context)
        {
            var value = _contexts[context.Value];
            return value.Parent.IsValid ? value.Parent : _normalContext;
        }

        private FlowContextIndex AddContext(
            Avm1FlowState state,
            FlowContextIndex parent,
            int depth,
            Avm1FlowContextFlags flags)
        {
            var index = new FlowContextIndex(_contexts.Count);
            _contexts.Add(new Avm1FlowContext(index, parent, depth, state, flags));
            _contextMap.Add((state, parent.Value, flags), index);
            return index;
        }

        private bool IsCatchEntry(BlockIndex block) =>
            block.IsValid && block.Value < _isCatchEntry.Length && _isCatchEntry[block.Value];

        private static Avm1FlowState ToFlowState(Avm1CompletionKind kind) =>
            kind is Avm1CompletionKind.Return
                ? Avm1FlowState.Return
                : Avm1FlowState.Throw;
    }
}

[Flags]
public enum Avm1FlowContextFlags : byte
{
    None = 0,
    Widened = 1
}

public enum Avm1FlowTransitionKind : byte
{
    Normal,
    Exception,
    Completion
}

public readonly record struct Avm1FlowContext(
    FlowContextIndex Index,
    FlowContextIndex Parent,
    int Depth,
    Avm1FlowState State,
    Avm1FlowContextFlags Flags);

public readonly record struct Avm1FlowPoint(
    FlowPointIndex Index,
    BlockIndex Block,
    FlowContextIndex Context,
    FlowTransitionIndex TransitionStart,
    int TransitionCount);

public readonly record struct Avm1FlowTransition(
    Avm1FlowTransitionKind Kind,
    FlowPointIndex Target,
    FlowContextIndex TargetContext,
    BlockIndex StackSource,
    Avm1CompletionKind CompletionKind);
