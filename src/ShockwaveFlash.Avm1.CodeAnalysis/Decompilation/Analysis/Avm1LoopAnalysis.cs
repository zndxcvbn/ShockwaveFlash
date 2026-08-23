using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1LoopAnalysis
{
    private Avm1LoopAnalysis(Avm1BackEdge[] backEdges, Avm1NaturalLoop[] loops)
    {
        BackEdges = backEdges;
        Loops = loops;
    }

    public IReadOnlyList<Avm1BackEdge> BackEdges { get; }

    public IReadOnlyList<Avm1NaturalLoop> Loops { get; }

    public static Avm1LoopAnalysis Build(Avm1ControlFlowGraph cfg, Avm1DominatorTree dominators)
    {
        var backEdges = new List<Avm1BackEdge>();
        var loops = new List<Avm1NaturalLoop>();
        var loopDominators = cfg.Blocks.Any(block => block.ExceptionSuccessor.IsValid)
            ? Avm1DominatorTree.BuildIncludingExceptionEdges(cfg)
            : dominators;
        var predecessors = BuildPredecessors(cfg, loopDominators);

        foreach (var block in cfg.Blocks)
        {
            AddBackEdge(cfg, loopDominators, predecessors, backEdges, loops, block.Index, block.FirstSuccessor);
            AddBackEdge(cfg, loopDominators, predecessors, backEdges, loops, block.Index, block.SecondSuccessor);
        }

        return new Avm1LoopAnalysis(backEdges.ToArray(), loops.ToArray());
    }

    private static void AddBackEdge(
        Avm1ControlFlowGraph cfg,
        Avm1DominatorTree dominators,
        List<BlockIndex>[] predecessors,
        List<Avm1BackEdge> backEdges,
        List<Avm1NaturalLoop> loops,
        BlockIndex tail,
        BlockIndex header)
    {
        if (!tail.IsValid || !header.IsValid)
            return;

        if (!dominators.Dominates(header, tail))
            return;

        backEdges.Add(new Avm1BackEdge(tail, header));
        loops.Add(BuildNaturalLoop(cfg, predecessors, tail, header));
    }

    private static Avm1NaturalLoop BuildNaturalLoop(
        Avm1ControlFlowGraph cfg,
        List<BlockIndex>[] predecessors,
        BlockIndex tail,
        BlockIndex header)
    {
        var loopBlocks = new HashSet<BlockIndex> { header, tail };
        var worklist = new Stack<BlockIndex>();
        if (tail != header)
            worklist.Push(tail);

        while (worklist.Count > 0)
        {
            var block = worklist.Pop();
            foreach (var predecessor in predecessors[block.Value])
            {
                if (!loopBlocks.Add(predecessor))
                    continue;

                worklist.Push(predecessor);
            }
        }

        var exits = new HashSet<BlockIndex>();
        foreach (var block in loopBlocks)
        {
            AddExit(cfg[block].FirstSuccessor, loopBlocks, exits);
            AddExit(cfg[block].SecondSuccessor, loopBlocks, exits);
        }

        return new Avm1NaturalLoop(
            header,
            tail,
            loopBlocks.OrderBy(b => b.Value).ToArray(),
            exits.OrderBy(b => b.Value).ToArray());
    }

    private static void AddExit(BlockIndex successor, HashSet<BlockIndex> loopBlocks, HashSet<BlockIndex> exits)
    {
        if (!successor.IsValid || loopBlocks.Contains(successor))
            return;

        exits.Add(successor);
    }

    private static List<BlockIndex>[] BuildPredecessors(
        Avm1ControlFlowGraph cfg,
        Avm1DominatorTree dominators)
    {
        var predecessors = new List<BlockIndex>[cfg.Count];
        for (var i = 0; i < predecessors.Length; i++)
            predecessors[i] = [];

        foreach (var block in cfg.Blocks)
        {
            if (!dominators.IsReachable(block.Index))
                continue;

            AddPredecessor(predecessors, block.FirstSuccessor, block.Index);
            AddPredecessor(predecessors, block.SecondSuccessor, block.Index);
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
}

public readonly record struct Avm1BackEdge(BlockIndex Tail, BlockIndex Header);

public readonly record struct Avm1NaturalLoop(
    BlockIndex Header,
    BlockIndex Tail,
    IReadOnlyList<BlockIndex> Blocks,
    IReadOnlyList<BlockIndex> Exits);
