using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1PostDominatorTree
{
    private readonly bool[,] _postDominators;

    private Avm1PostDominatorTree(BlockIndex virtualExit, BlockIndex[] immediatePostDominators, bool[,] postDominators)
    {
        VirtualExit = virtualExit;
        ImmediatePostDominators = immediatePostDominators;
        _postDominators = postDominators;
    }

    public BlockIndex VirtualExit { get; }

    public IReadOnlyList<BlockIndex> ImmediatePostDominators { get; }

    public static Avm1PostDominatorTree Build(Avm1ControlFlowGraph cfg)
    {
        var virtualExit = new BlockIndex(cfg.Count);
        var count = cfg.Count + 1;
        var postDominators = new bool[count, count];

        for (var block = 0; block < count; block++)
        {
            for (var candidate = 0; candidate < count; candidate++)
                postDominators[block, candidate] = block != virtualExit.Value;
        }

        postDominators[virtualExit.Value, virtualExit.Value] = true;

        var successors = BuildSuccessors(cfg, virtualExit);
        var changed = true;
        var next = new bool[count];
        while (changed)
        {
            changed = false;

            for (var block = cfg.Count - 1; block >= 0; block--)
            {
                for (var candidate = 0; candidate < count; candidate++)
                    next[candidate] = successors[block].Count > 0;

                foreach (var successor in successors[block])
                    for (var candidate = 0; candidate < count; candidate++)
                        next[candidate] &= postDominators[successor.Value, candidate];

                next[block] = true;

                for (var candidate = 0; candidate < count; candidate++)
                {
                    if (postDominators[block, candidate] == next[candidate])
                        continue;

                    postDominators[block, candidate] = next[candidate];
                    changed = true;
                }
            }
        }

        var immediatePostDominators = new BlockIndex[cfg.Count];
        for (var block = 0; block < cfg.Count; block++)
            immediatePostDominators[block] = FindImmediatePostDominator(block, postDominators, count);

        return new Avm1PostDominatorTree(virtualExit, immediatePostDominators, postDominators);
    }

    public bool PostDominates(BlockIndex postDominator, BlockIndex block)
    {
        if (!postDominator.IsValid || !block.IsValid)
            return false;

        if (postDominator.Value >= _postDominators.GetLength(1) || block.Value >= _postDominators.GetLength(0))
            return false;

        return _postDominators[block.Value, postDominator.Value];
    }

    private static List<BlockIndex>[] BuildSuccessors(Avm1ControlFlowGraph cfg, BlockIndex virtualExit)
    {
        var successors = new List<BlockIndex>[cfg.Count + 1];
        for (var i = 0; i < successors.Length; i++)
            successors[i] = [];

        foreach (var block in cfg.Blocks)
        {
            AddSuccessor(successors[block.Index.Value], block.FirstSuccessor);
            AddSuccessor(successors[block.Index.Value], block.SecondSuccessor);

            switch (block.Terminator)
            {
                case Avm1BlockTerminatorKind.ConditionalBranch:
                    AddVirtualExitForMissingSuccessor(successors[block.Index.Value], block.FirstSuccessor, virtualExit);
                    AddVirtualExitForMissingSuccessor(successors[block.Index.Value], block.SecondSuccessor, virtualExit);
                    break;
                case Avm1BlockTerminatorKind.Jump:
                    AddVirtualExitForMissingSuccessor(successors[block.Index.Value], block.FirstSuccessor, virtualExit);
                    break;
                case Avm1BlockTerminatorKind.Return:
                case Avm1BlockTerminatorKind.Throw:
                case Avm1BlockTerminatorKind.End:
                    AddSuccessor(successors[block.Index.Value], virtualExit);
                    break;
                case Avm1BlockTerminatorKind.FallThrough:
                    if (successors[block.Index.Value].Count == 0)
                        AddSuccessor(successors[block.Index.Value], virtualExit);

                    break;
            }
        }

        return successors;
    }

    private static void AddVirtualExitForMissingSuccessor(List<BlockIndex> successors, BlockIndex successor, BlockIndex virtualExit)
    {
        if (!successor.IsValid)
            AddSuccessor(successors, virtualExit);
    }

    private static void AddSuccessor(List<BlockIndex> successors, BlockIndex successor)
    {
        if (!successor.IsValid || successors.Contains(successor))
            return;

        successors.Add(successor);
    }

    private static BlockIndex FindImmediatePostDominator(int block, bool[,] postDominators, int count)
    {
        for (var candidate = 0; candidate < count; candidate++)
        {
            if (candidate == block || !postDominators[block, candidate])
                continue;

            var isClosest = true;
            for (var other = 0; other < count; other++)
            {
                if (other == block || other == candidate || !postDominators[block, other])
                    continue;

                if (!postDominators[candidate, other])
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
