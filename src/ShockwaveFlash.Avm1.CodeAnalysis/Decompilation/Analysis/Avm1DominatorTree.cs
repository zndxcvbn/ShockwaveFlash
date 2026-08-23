using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1DominatorTree
{
    private readonly bool[,] _dominators;
    private readonly bool[] _reachable;

    private Avm1DominatorTree(
        BlockIndex[] immediateDominators,
        bool[,] dominators,
        bool[] reachable)
    {
        ImmediateDominators = immediateDominators;
        _dominators = dominators;
        _reachable = reachable;
    }

    public IReadOnlyList<BlockIndex> ImmediateDominators { get; }

    public static Avm1DominatorTree Build(Avm1ControlFlowGraph cfg) =>
        BuildCore(cfg, includeExceptionEdges: false);

    internal static Avm1DominatorTree BuildIncludingExceptionEdges(
        Avm1ControlFlowGraph cfg) =>
        BuildCore(cfg, includeExceptionEdges: true);

    private static Avm1DominatorTree BuildCore(
        Avm1ControlFlowGraph cfg,
        bool includeExceptionEdges)
    {
        var count = cfg.Count;
        var dominators = new bool[count, count];
        var reachable = FindReachableBlocks(cfg, includeExceptionEdges);
        if (count == 0)
            return new Avm1DominatorTree([], dominators, reachable);

        for (var block = 0; block < count; block++)
        {
            if (!reachable[block])
            {
                dominators[block, block] = true;
                continue;
            }

            for (var candidate = 0; candidate < count; candidate++)
                dominators[block, candidate] = block != 0 && reachable[candidate];
        }

        dominators[0, 0] = true;

        var predecessors = BuildPredecessors(cfg, includeExceptionEdges);
        var changed = true;
        var next = new bool[count];
        while (changed)
        {
            changed = false;

            for (var block = 1; block < count; block++)
            {
                if (!reachable[block])
                    continue;

                for (var candidate = 0; candidate < count; candidate++)
                    next[candidate] = reachable[candidate];

                foreach (var predecessor in predecessors[block])
                {
                    if (!reachable[predecessor.Value])
                        continue;

                    for (var candidate = 0; candidate < count; candidate++)
                        next[candidate] &= dominators[predecessor.Value, candidate];
                }

                next[block] = true;

                for (var candidate = 0; candidate < count; candidate++)
                {
                    if (dominators[block, candidate] == next[candidate])
                        continue;

                    dominators[block, candidate] = next[candidate];
                    changed = true;
                }
            }
        }

        var immediateDominators = new BlockIndex[count];
        immediateDominators[0] = BlockIndex.Invalid;
        for (var block = 1; block < count; block++)
        {
            immediateDominators[block] = reachable[block]
                ? FindImmediateDominator(block, dominators, reachable, count)
                : BlockIndex.Invalid;
        }

        return new Avm1DominatorTree(immediateDominators, dominators, reachable);
    }

    public bool Dominates(BlockIndex dominator, BlockIndex block)
    {
        if (!dominator.IsValid || !block.IsValid)
            return false;

        if (dominator.Value >= _dominators.GetLength(1) || block.Value >= _dominators.GetLength(0))
            return false;

        return _dominators[block.Value, dominator.Value];
    }

    public bool IsReachable(BlockIndex block) =>
        block.IsValid &&
        block.Value < _reachable.Length &&
        _reachable[block.Value];

    private static bool[] FindReachableBlocks(
        Avm1ControlFlowGraph cfg,
        bool includeExceptionEdges)
    {
        var reachable = new bool[cfg.Count];
        if (cfg.Count == 0)
            return reachable;

        var worklist = new Queue<BlockIndex>();
        reachable[0] = true;
        worklist.Enqueue(new BlockIndex(0));
        while (worklist.TryDequeue(out var block))
        {
            var node = cfg[block];
            Add(node.FirstSuccessor);
            Add(node.SecondSuccessor);
            if (includeExceptionEdges)
                Add(node.ExceptionSuccessor);
        }

        return reachable;

        void Add(BlockIndex successor)
        {
            if (!successor.IsValid ||
                successor.Value >= reachable.Length ||
                reachable[successor.Value])
            {
                return;
            }

            reachable[successor.Value] = true;
            worklist.Enqueue(successor);
        }
    }

    private static List<BlockIndex>[] BuildPredecessors(
        Avm1ControlFlowGraph cfg,
        bool includeExceptionEdges)
    {
        var predecessors = new List<BlockIndex>[cfg.Count];
        for (var i = 0; i < predecessors.Length; i++)
            predecessors[i] = [];

        foreach (var block in cfg.Blocks)
        {
            AddPredecessor(predecessors, block.FirstSuccessor, block.Index);
            AddPredecessor(predecessors, block.SecondSuccessor, block.Index);
            if (includeExceptionEdges)
                AddPredecessor(predecessors, block.ExceptionSuccessor, block.Index);
        }

        return predecessors;
    }

    private static void AddPredecessor(List<BlockIndex>[] predecessors, BlockIndex successor, BlockIndex predecessor)
    {
        if (!successor.IsValid || successor.Value >= predecessors.Length)
            return;

        predecessors[successor.Value].Add(predecessor);
    }

    private static BlockIndex FindImmediateDominator(
        int block,
        bool[,] dominators,
        bool[] reachable,
        int count)
    {
        for (var candidate = 0; candidate < count; candidate++)
        {
            if (!reachable[candidate] ||
                candidate == block ||
                !dominators[block, candidate])
                continue;

            var isClosest = true;
            for (var other = 0; other < count; other++)
            {
                if (!reachable[other] ||
                    other == block ||
                    other == candidate ||
                    !dominators[block, other])
                    continue;

                if (!dominators[candidate, other])
                {
                    isClosest = false;
                    break;
                }
            }

            if (isClosest)
                return new BlockIndex(candidate);
        }

        return BlockIndex.Invalid;
    }
}
