using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Analysis;

namespace ShockwaveFlash.Avm1.Decompilation.Ssa;

public sealed class Avm1RegisterSsa
{
    private Avm1RegisterSsa(
        Avm1RegisterAccess[] accesses,
        Avm1RegisterPhiNode[] phiNodes,
        int[] versionCountByRegister)
    {
        Accesses = accesses;
        PhiNodes = phiNodes;
        VersionCountByRegister = versionCountByRegister;
    }

    public IReadOnlyList<Avm1RegisterAccess> Accesses { get; }

    public IReadOnlyList<Avm1RegisterPhiNode> PhiNodes { get; }

    public IReadOnlyList<int> VersionCountByRegister { get; }

    public static Avm1RegisterSsa Build(Avm1TacIr tac)
    {
        var accesses = new List<Avm1RegisterAccess>();
        Span<int> currentVersions = stackalloc int[256];
        Span<int> nextVersions = stackalloc int[256];

        foreach (var instruction in tac.Instructions)
        {
            if (instruction.Op is Avm1TacOp.LoadRegister && instruction.IntOperand >= 0)
            {
                var register = instruction.IntOperand;
                accesses.Add(new Avm1RegisterAccess(
                    instruction.Index,
                    instruction.Action,
                    Avm1RegisterAccessKind.Read,
                    register,
                    currentVersions[register],
                    instruction.Result,
                    ValueIndex.Invalid));
            }
            else if (IsRegisterWrite(instruction))
            {
                var register = instruction.IntOperand;
                var version = ++nextVersions[register];
                currentVersions[register] = version;
                accesses.Add(new Avm1RegisterAccess(
                    instruction.Index,
                    instruction.Action,
                    Avm1RegisterAccessKind.Write,
                    register,
                    version,
                    instruction.Result,
                    instruction.Operand0));
            }
        }

        var counts = new int[256];
        for (var i = 0; i < counts.Length; i++)
            counts[i] = nextVersions[i];

        return new Avm1RegisterSsa(accesses.ToArray(), [], counts);
    }

    internal static Avm1RegisterSsa Build(
        Avm1TacIr tac,
        Avm1ControlFlowGraph cfg,
        Avm1FlowGraph flowGraph,
        Avm1CatchPayloadAnalysis catchPayloads)
    {
        if (cfg.Count == 0)
            return Build(tac);

        return cfg.CompletionEdges.Count == 0
            ? BuildBlockSsa(tac, cfg, catchPayloads)
            : new ContextBuilder(tac, cfg, flowGraph, catchPayloads).Build();
    }

    private static List<Avm1TacInstruction>[] BuildBlockInstructions(Avm1TacIr tac, Avm1ControlFlowGraph cfg)
    {
        var blockInstructions = new List<Avm1TacInstruction>[cfg.Count];
        for (var i = 0; i < blockInstructions.Length; i++)
            blockInstructions[i] = [];

        foreach (var instruction in tac.Instructions)
        {
            if (!instruction.Action.IsValid)
                continue;

            if (!cfg.TryGetBlockForAction(instruction.Action, out var block))
                continue;

            blockInstructions[block.Value].Add(instruction);
        }

        return blockInstructions;
    }

    private static Avm1RegisterSsa BuildBlockSsa(
        Avm1TacIr tac,
        Avm1ControlFlowGraph cfg,
        Avm1CatchPayloadAnalysis catchPayloads)
    {
        var blockInstructions = BuildBlockInstructions(tac, cfg);
        var predecessors = BuildBlockPredecessors(cfg);
        var entryVersions = CreateVersionMatrix(cfg.Count);
        var exitVersions = CreateVersionMatrix(cfg.Count);
        var hasKnownEntry = new bool[cfg.Count];
        var storeVersions = new Dictionary<IrIndex, int>();
        var phiBuilders = new Dictionary<(int Block, int Register), PhiBuilder>();
        var nextVersions = new int[256];

        hasKnownEntry[0] = true;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var block = 0; block < cfg.Count; block++)
            {
                if (!TryBuildBlockEntryVersions(
                    block,
                    predecessors,
                    exitVersions,
                    hasKnownEntry,
                    phiBuilders,
                    nextVersions,
                    out var nextEntry))
                {
                    continue;
                }

                if (!hasKnownEntry[block] || !VersionsEqual(entryVersions[block], nextEntry))
                {
                    Array.Copy(nextEntry, entryVersions[block], nextEntry.Length);
                    hasKnownEntry[block] = true;
                    changed = true;
                }

                var nextExit = TransferBlock(
                    blockInstructions[block],
                    entryVersions[block],
                    storeVersions,
                    nextVersions);
                if (!VersionsEqual(exitVersions[block], nextExit))
                {
                    Array.Copy(nextExit, exitVersions[block], nextExit.Length);
                    changed = true;
                }
            }
        }

        var accesses = BuildAccesses(
            blockInstructions,
            entryVersions,
            storeVersions,
            catchPayloads);
        var phiNodes = phiBuilders.Values
            .OrderBy(phi => phi.Block.Value)
            .ThenBy(phi => phi.Register)
            .Select(phi => phi.ToNode())
            .ToArray();
        return new Avm1RegisterSsa(accesses, phiNodes, nextVersions);
    }

    private static List<BlockIndex>[] BuildBlockPredecessors(Avm1ControlFlowGraph cfg)
    {
        var predecessors = new List<BlockIndex>[cfg.Count];
        for (var i = 0; i < predecessors.Length; i++)
            predecessors[i] = [];

        foreach (var block in cfg.Blocks)
        {
            AddBlockPredecessor(predecessors, block.FirstSuccessor, block.Index);
            AddBlockPredecessor(predecessors, block.SecondSuccessor, block.Index);
            AddBlockPredecessor(predecessors, block.ExceptionSuccessor, block.Index);
        }

        return predecessors;
    }

    private static void AddBlockPredecessor(
        List<BlockIndex>[] predecessors,
        BlockIndex successor,
        BlockIndex predecessor)
    {
        if (!successor.IsValid || successor.Value >= predecessors.Length)
            return;

        predecessors[successor.Value].Add(predecessor);
    }

    private static bool TryBuildBlockEntryVersions(
        int block,
        List<BlockIndex>[] predecessors,
        int[][] exitVersions,
        bool[] hasKnownEntry,
        Dictionary<(int Block, int Register), PhiBuilder> phiBuilders,
        int[] nextVersions,
        out int[] entryVersions)
    {
        entryVersions = new int[256];
        if (block == 0)
        {
            if (predecessors[block].Count == 0)
                return true;

            return TryMergeBlockPredecessors(
                block,
                predecessors[block],
                exitVersions,
                hasKnownEntry,
                includeInitialEntry: true,
                phiBuilders,
                nextVersions,
                entryVersions);
        }

        if (predecessors[block].Count == 0)
            return false;

        return TryMergeBlockPredecessors(
            block,
            predecessors[block],
            exitVersions,
            hasKnownEntry,
            includeInitialEntry: false,
            phiBuilders,
            nextVersions,
            entryVersions);
    }

    private static bool TryMergeBlockPredecessors(
        int block,
        List<BlockIndex> predecessors,
        int[][] exitVersions,
        bool[] hasKnownEntry,
        bool includeInitialEntry,
        Dictionary<(int Block, int Register), PhiBuilder> phiBuilders,
        int[] nextVersions,
        int[] entryVersions)
    {
        var hasAnyIncoming = includeInitialEntry;
        var incomingPredecessors = new List<BlockIndex>(
            predecessors.Count + (includeInitialEntry ? 1 : 0));
        if (includeInitialEntry)
            incomingPredecessors.Add(BlockIndex.Invalid);

        foreach (var predecessor in predecessors)
        {
            if (!hasKnownEntry[predecessor.Value])
                continue;

            hasAnyIncoming = true;
            incomingPredecessors.Add(predecessor);
        }

        if (!hasAnyIncoming)
            return false;

        var incomingStates = new Avm1FlowState[incomingPredecessors.Count];
        var incomingVersions = new List<int>(incomingPredecessors.Count);
        for (var register = 0; register < entryVersions.Length; register++)
        {
            incomingVersions.Clear();
            foreach (var predecessor in incomingPredecessors)
            {
                incomingVersions.Add(predecessor.IsValid
                    ? exitVersions[predecessor.Value][register]
                    : 0);
            }

            entryVersions[register] = ResolveBlockEntryVersion(
                new BlockIndex(block),
                register,
                incomingPredecessors,
                incomingStates,
                incomingVersions,
                phiBuilders,
                nextVersions);
        }

        return true;
    }

    private static int ResolveBlockEntryVersion(
        BlockIndex block,
        int register,
        IReadOnlyList<BlockIndex> predecessors,
        IReadOnlyList<Avm1FlowState> predecessorStates,
        List<int> incomingVersions,
        Dictionary<(int Block, int Register), PhiBuilder> phiBuilders,
        int[] nextVersions)
    {
        if (incomingVersions.Count == 0)
            return 0;

        var first = incomingVersions[0];
        var needsPhi = incomingVersions.Any(version => version != first);
        var key = (block.Value, register);
        phiBuilders.TryGetValue(key, out var phi);
        if (!needsPhi && phi is null)
            return first;

        if (phi is null)
        {
            phi = new PhiBuilder(
                block,
                register,
                ++nextVersions[register],
                Avm1FlowState.Normal);
            phiBuilders.Add(key, phi);
        }

        phi.SetIncoming(predecessors, predecessorStates, incomingVersions);
        return phi.Version;
    }

    private static int[][] CreateVersionMatrix(int blockCount)
    {
        var versions = new int[blockCount][];
        for (var i = 0; i < versions.Length; i++)
            versions[i] = new int[256];

        return versions;
    }

    private sealed class ContextBuilder
    {
        private readonly Avm1ControlFlowGraph _cfg;
        private readonly Avm1FlowGraph _flowGraph;
        private readonly Avm1CatchPayloadAnalysis _catchPayloads;
        private readonly List<Avm1TacInstruction>[] _blockInstructions;
        private readonly Dictionary<FlowPredecessor, int[]>[] _incoming;
        private readonly int[][] _entryVersions;
        private readonly int[][] _exitVersions;
        private readonly bool[] _hasEntry;
        private readonly bool[] _processed;
        private readonly bool[] _queued;
        private readonly Queue<FlowPointIndex> _worklist = [];
        private readonly Dictionary<IrIndex, int> _storeVersions = [];
        private readonly Dictionary<(int Block, int Context, int Register), ContextPhiBuilder> _phiBuilders = [];
        private readonly int[] _nextVersions = new int[256];

        public ContextBuilder(
            Avm1TacIr tac,
            Avm1ControlFlowGraph cfg,
            Avm1FlowGraph flowGraph,
            Avm1CatchPayloadAnalysis catchPayloads)
        {
            _cfg = cfg;
            _flowGraph = flowGraph;
            _catchPayloads = catchPayloads;
            _blockInstructions = BuildBlockInstructions(tac, cfg);
            _incoming = new Dictionary<FlowPredecessor, int[]>[flowGraph.Count];
            _entryVersions = CreateVersionMatrix(flowGraph.Count);
            _exitVersions = CreateVersionMatrix(flowGraph.Count);
            _hasEntry = new bool[flowGraph.Count];
            _processed = new bool[flowGraph.Count];
            _queued = new bool[flowGraph.Count];

            for (var point = 0; point < flowGraph.Count; point++)
                _incoming[point] = [];
        }

        public Avm1RegisterSsa Build()
        {
            if (_flowGraph.TryGetPoint(
                new BlockIndex(0),
                _flowGraph.NormalContext,
                out var entry))
            {
                SetIncoming(
                    entry,
                    new FlowPredecessor(FlowPointIndex.Invalid),
                    new int[256]);
                DrainWorklist();
            }

            EnsureStoreVersions();
            var aggregateEntryVersions = BuildAggregateEntryVersions();
            var accesses = BuildAccesses(
                _blockInstructions,
                aggregateEntryVersions,
                _storeVersions,
                _catchPayloads);
            var phiNodes = _phiBuilders.Values
                .OrderBy(phi => phi.Block.Value)
                .ThenBy(phi => phi.Register)
                .ThenBy(phi => phi.State)
                .ThenBy(phi => phi.Context.Value)
                .ThenBy(phi => phi.Version)
                .Select(phi => phi.ToNode())
                .ToArray();
            return new Avm1RegisterSsa(accesses, phiNodes, _nextVersions);
        }

        private void DrainWorklist()
        {
            while (_worklist.TryDequeue(out var point))
            {
                _queued[point.Value] = false;
                ProcessPoint(point);
            }
        }

        private void EnsureStoreVersions()
        {
            foreach (var instructions in _blockInstructions)
            {
                foreach (var instruction in instructions)
                {
                    if (!IsRegisterWrite(instruction) ||
                        _storeVersions.ContainsKey(instruction.Index))
                    {
                        continue;
                    }

                    _storeVersions.Add(
                        instruction.Index,
                        ++_nextVersions[instruction.IntOperand]);
                }
            }
        }

        private void ProcessPoint(FlowPointIndex pointIndex)
        {
            var point = _flowGraph[pointIndex];
            var nextExit = TransferBlock(
                _blockInstructions[point.Block.Value],
                _entryVersions[pointIndex.Value],
                _storeVersions,
                _nextVersions);
            if (_processed[pointIndex.Value] &&
                VersionsEqual(_exitVersions[pointIndex.Value], nextExit))
            {
                return;
            }

            Array.Copy(nextExit, _exitVersions[pointIndex.Value], nextExit.Length);
            _processed[pointIndex.Value] = true;
            foreach (var transition in _flowGraph.GetTransitions(pointIndex))
            {
                SetIncoming(
                    transition.Target,
                    new FlowPredecessor(pointIndex),
                    nextExit);
            }
        }

        private void SetIncoming(
            FlowPointIndex point,
            FlowPredecessor predecessor,
            int[] versions)
        {
            if (!point.IsValid || point.Value >= _flowGraph.Count)
                return;

            var incoming = _incoming[point.Value];
            if (incoming.TryGetValue(predecessor, out var previous) &&
                VersionsEqual(previous, versions))
            {
                return;
            }

            incoming[predecessor] = versions.ToArray();
            var merged = MergeIncoming(point, incoming);
            if (_hasEntry[point.Value] &&
                VersionsEqual(_entryVersions[point.Value], merged))
            {
                return;
            }

            Array.Copy(merged, _entryVersions[point.Value], merged.Length);
            _hasEntry[point.Value] = true;
            Enqueue(point);
        }

        private int[] MergeIncoming(
            FlowPointIndex pointIndex,
            IReadOnlyDictionary<FlowPredecessor, int[]> incoming)
        {
            var point = _flowGraph[pointIndex];
            var context = _flowGraph.GetContext(point.Context);
            var ordered = incoming.OrderBy(pair => pair.Key.Point.Value).ToArray();
            var predecessors = new BlockIndex[ordered.Length];
            var predecessorStates = new Avm1FlowState[ordered.Length];
            var predecessorContexts = new FlowContextIndex[ordered.Length];
            for (var i = 0; i < ordered.Length; i++)
            {
                var predecessor = ordered[i].Key.Point;
                if (!predecessor.IsValid)
                {
                    predecessors[i] = BlockIndex.Invalid;
                    predecessorStates[i] = Avm1FlowState.Normal;
                    predecessorContexts[i] = _flowGraph.NormalContext;
                    continue;
                }

                var predecessorPoint = _flowGraph[predecessor];
                predecessors[i] = predecessorPoint.Block;
                predecessorContexts[i] = predecessorPoint.Context;
                predecessorStates[i] = _flowGraph.GetContext(predecessorPoint.Context).State;
            }

            var merged = new int[256];
            var versions = new int[ordered.Length];
            for (var register = 0; register < merged.Length; register++)
            {
                for (var i = 0; i < ordered.Length; i++)
                    versions[i] = ordered[i].Value[register];
                merged[register] = ResolveEntryVersion(
                    point.Block,
                    point.Context,
                    context.State,
                    register,
                    predecessors,
                    predecessorStates,
                    predecessorContexts,
                    versions);
            }

            return merged;
        }

        private int[][] BuildAggregateEntryVersions()
        {
            var aggregate = CreateVersionMatrix(_cfg.Count);
            var pointsByBlock = _flowGraph.Points
                .Where(point => _hasEntry[point.Index.Value])
                .GroupBy(point => point.Block.Value)
                .ToDictionary(group => group.Key, group => group.ToArray());
            for (var block = 0; block < _cfg.Count; block++)
            {
                if (!pointsByBlock.TryGetValue(block, out var points) || points.Length == 0)
                    continue;
                if (points.Length == 1)
                {
                    Array.Copy(
                        _entryVersions[points[0].Index.Value],
                        aggregate[block],
                        aggregate[block].Length);
                    continue;
                }

                var predecessors = Enumerable.Repeat(new BlockIndex(block), points.Length).ToArray();
                var predecessorStates = points
                    .Select(point => _flowGraph.GetContext(point.Context).State)
                    .ToArray();
                var predecessorContexts = points.Select(point => point.Context).ToArray();
                var versions = new int[points.Length];
                for (var register = 0; register < aggregate[block].Length; register++)
                {
                    for (var i = 0; i < points.Length; i++)
                    {
                        versions[i] = _entryVersions[points[i].Index.Value][register];
                    }
                    aggregate[block][register] = ResolveEntryVersion(
                        new BlockIndex(block),
                        FlowContextIndex.Invalid,
                        Avm1FlowState.Merged,
                        register,
                        predecessors,
                        predecessorStates,
                        predecessorContexts,
                        versions);
                }
            }

            return aggregate;
        }

        private int ResolveEntryVersion(
            BlockIndex block,
            FlowContextIndex context,
            Avm1FlowState state,
            int register,
            BlockIndex[] predecessors,
            Avm1FlowState[] predecessorStates,
            FlowContextIndex[] predecessorContexts,
            int[] incomingVersions)
        {
            if (incomingVersions.Length == 0)
                return 0;

            var first = incomingVersions[0];
            var needsPhi = incomingVersions.Any(version => version != first);
            var key = (block.Value, context.Value, register);
            _phiBuilders.TryGetValue(key, out var phi);
            if (!needsPhi && phi is null)
                return first;

            if (phi is null)
            {
                phi = new ContextPhiBuilder(
                    block,
                    register,
                    ++_nextVersions[register],
                    state,
                    context);
                _phiBuilders.Add(key, phi);
            }

            phi.SetIncoming(
                predecessors,
                predecessorStates,
                predecessorContexts,
                incomingVersions);
            return phi.Version;
        }

        private void Enqueue(FlowPointIndex point)
        {
            if (_queued[point.Value])
                return;

            _queued[point.Value] = true;
            _worklist.Enqueue(point);
        }

        private readonly record struct FlowPredecessor(FlowPointIndex Point);

        private sealed class ContextPhiBuilder(
            BlockIndex block,
            int register,
            int version,
            Avm1FlowState state,
            FlowContextIndex context)
        {
            private BlockIndex[] _predecessors = [];
            private Avm1FlowState[] _predecessorStates = [];
            private FlowContextIndex[] _predecessorContexts = [];
            private int[] _incomingVersions = [];

            public BlockIndex Block { get; } = block;

            public int Register { get; } = register;

            public int Version { get; } = version;

            public Avm1FlowState State { get; } = state;

            public FlowContextIndex Context { get; } = context;

            public void SetIncoming(
                BlockIndex[] predecessors,
                Avm1FlowState[] predecessorStates,
                FlowContextIndex[] predecessorContexts,
                int[] incomingVersions)
            {
                _predecessors = predecessors;
                _predecessorStates = predecessorStates;
                _predecessorContexts = predecessorContexts;
                _incomingVersions = incomingVersions.ToArray();
            }

            public Avm1RegisterPhiNode ToNode() => new(
                Block,
                Register,
                Version,
                _predecessors,
                _incomingVersions,
                State,
                _predecessorStates,
                Context,
                _predecessorContexts);
        }
    }

    private static int[] TransferBlock(
        List<Avm1TacInstruction> instructions,
        int[] entryVersions,
        Dictionary<IrIndex, int> storeVersions,
        int[] nextVersions)
    {
        var currentVersions = new int[256];
        Array.Copy(entryVersions, currentVersions, entryVersions.Length);

        foreach (var instruction in instructions)
        {
            if (!IsRegisterWrite(instruction))
                continue;

            var register = instruction.IntOperand;
            if (!storeVersions.TryGetValue(instruction.Index, out var version))
            {
                version = ++nextVersions[register];
                storeVersions.Add(instruction.Index, version);
            }

            currentVersions[register] = version;
        }

        return currentVersions;
    }

    private static Avm1RegisterAccess[] BuildAccesses(
        List<Avm1TacInstruction>[] blockInstructions,
        int[][] entryVersions,
        Dictionary<IrIndex, int> storeVersions,
        Avm1CatchPayloadAnalysis catchPayloads)
    {
        var accesses = new List<Avm1RegisterAccess>();

        for (var block = 0; block < blockInstructions.Length; block++)
        {
            var currentVersions = new int[256];
            Array.Copy(entryVersions[block], currentVersions, currentVersions.Length);

            foreach (var instruction in blockInstructions[block])
            {
                if (instruction.Op is Avm1TacOp.LoadRegister && instruction.IntOperand >= 0)
                {
                    var register = instruction.IntOperand;
                    accesses.Add(new Avm1RegisterAccess(
                        instruction.Index,
                        instruction.Action,
                        Avm1RegisterAccessKind.Read,
                        register,
                        currentVersions[register],
                        instruction.Result,
                        ValueIndex.Invalid));
                }
                else if (IsRegisterWrite(instruction))
                {
                    var register = instruction.IntOperand;
                    var version = storeVersions.TryGetValue(instruction.Index, out var storedVersion)
                        ? storedVersion
                        : currentVersions[register];
                    currentVersions[register] = version;
                    var source = instruction.Operand0;
                    if (instruction.Op is Avm1TacOp.CatchEnter &&
                        catchPayloads.TryGetExactSource(instruction.Action, out var catchSource))
                    {
                        source = catchSource;
                    }
                    accesses.Add(new Avm1RegisterAccess(
                        instruction.Index,
                        instruction.Action,
                        Avm1RegisterAccessKind.Write,
                        register,
                        version,
                        instruction.Result,
                        source));
                }
            }
        }

        return accesses
            .OrderBy(a => a.Instruction.Value)
            .ToArray();
    }

    private static bool VersionsEqual(int[] left, int[] right)
    {
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i])
                return false;

        return true;
    }

    private static bool IsRegisterWrite(Avm1TacInstruction instruction) =>
        instruction.IntOperand >= 0 &&
        instruction.Op is Avm1TacOp.StoreRegister or Avm1TacOp.CatchEnter;

    private sealed class PhiBuilder
    {
        private BlockIndex[] _predecessors = [];
        private Avm1FlowState[] _predecessorStates = [];
        private int[] _incomingVersions = [];

        public PhiBuilder(
            BlockIndex block,
            int register,
            int version,
            Avm1FlowState state)
        {
            Block = block;
            Register = register;
            Version = version;
            State = state;
        }

        public BlockIndex Block { get; }

        public int Register { get; }

        public int Version { get; }

        public Avm1FlowState State { get; }

        public void SetIncoming(
            IReadOnlyList<BlockIndex> predecessors,
            IReadOnlyList<Avm1FlowState> predecessorStates,
            IReadOnlyList<int> incomingVersions)
        {
            _predecessors = predecessors.ToArray();
            _predecessorStates = predecessorStates.ToArray();
            _incomingVersions = incomingVersions.ToArray();
        }

        public Avm1RegisterPhiNode ToNode() => new(
            Block,
            Register,
            Version,
            _predecessors,
            _incomingVersions,
            State,
            _predecessorStates,
            FlowContextIndex.Invalid,
            Enumerable.Repeat(FlowContextIndex.Invalid, _predecessors.Length).ToArray());
    }
}

public enum Avm1RegisterAccessKind
{
    Read,
    Write
}

public readonly record struct Avm1RegisterAccess(
    IrIndex Instruction,
    ActionIndex Action,
    Avm1RegisterAccessKind Kind,
    int Register,
    int Version,
    ValueIndex Value,
    ValueIndex Source);

public readonly record struct Avm1RegisterPhiNode(
    BlockIndex Block,
    int Register,
    int Version,
    IReadOnlyList<BlockIndex> Predecessors,
    IReadOnlyList<int> IncomingVersions,
    Avm1FlowState State,
    IReadOnlyList<Avm1FlowState> IncomingStates,
    FlowContextIndex Context,
    IReadOnlyList<FlowContextIndex> IncomingContexts);
