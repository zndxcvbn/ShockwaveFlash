using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1IrreducibleControlFlowAnalysis
{
    private readonly Avm1IrreducibleControlFlowRegion[] _regions;

    private Avm1IrreducibleControlFlowAnalysis(
        Avm1IrreducibleControlFlowRegion[] regions,
        int reachableBlockCount)
    {
        _regions = regions;
        ReachableBlockCount = reachableBlockCount;
    }

    public IReadOnlyList<Avm1IrreducibleControlFlowRegion> Regions => _regions;

    public bool IsIrreducible => _regions.Length != 0;

    public int ReachableBlockCount { get; }

    public static Avm1IrreducibleControlFlowAnalysis Build(
        Avm1ControlFlowGraph controlFlowGraph)
    {
        ArgumentNullException.ThrowIfNull(controlFlowGraph);
        if (controlFlowGraph.Count == 0)
            return new Avm1IrreducibleControlFlowAnalysis([], 0);

        var reachable = FindReachableBlocks(controlFlowGraph);
        var predecessors = BuildPredecessors(controlFlowGraph, reachable);
        var components = FindStronglyConnectedComponents(
            controlFlowGraph,
            reachable,
            predecessors);
        var componentByBlock = Enumerable.Repeat(-1, controlFlowGraph.Count).ToArray();
        for (var component = 0; component < components.Count; component++)
        {
            foreach (var block in components[component])
                componentByBlock[block.Value] = component;
        }

        var regions = new List<Avm1IrreducibleControlFlowRegion>();
        for (var component = 0; component < components.Count; component++)
        {
            var blocks = components[component];
            if (!IsCyclic(controlFlowGraph, blocks))
                continue;

            var entries = new HashSet<BlockIndex>();
            if (blocks.Any(block => block.Value == 0))
                entries.Add(new BlockIndex(0));

            for (var source = 0; source < controlFlowGraph.Count; source++)
            {
                if (!reachable[source] || componentByBlock[source] == component)
                    continue;

                AddEntry(
                    controlFlowGraph[new BlockIndex(source)].FirstSuccessor,
                    component,
                    componentByBlock,
                    entries);
                AddEntry(
                    controlFlowGraph[new BlockIndex(source)].SecondSuccessor,
                    component,
                    componentByBlock,
                    entries);
            }

            if (entries.Count <= 1)
                continue;

            regions.Add(new Avm1IrreducibleControlFlowRegion(
                blocks.OrderBy(block => block.Value).ToArray(),
                entries.OrderBy(block => block.Value).ToArray()));
        }

        return new Avm1IrreducibleControlFlowAnalysis(
            regions
                .OrderBy(region => region.Blocks[0].Value)
                .ToArray(),
            reachable.Count(value => value));
    }

    private static bool[] FindReachableBlocks(Avm1ControlFlowGraph controlFlowGraph)
    {
        var reachable = new bool[controlFlowGraph.Count];
        var pending = new Stack<BlockIndex>();
        reachable[0] = true;
        pending.Push(new BlockIndex(0));

        while (pending.Count > 0)
        {
            var block = controlFlowGraph[pending.Pop()];
            AddReachable(block.FirstSuccessor, reachable, pending);
            AddReachable(block.SecondSuccessor, reachable, pending);
        }

        return reachable;
    }

    private static void AddReachable(
        BlockIndex successor,
        bool[] reachable,
        Stack<BlockIndex> pending)
    {
        if (!successor.IsValid ||
            successor.Value >= reachable.Length ||
            reachable[successor.Value])
        {
            return;
        }

        reachable[successor.Value] = true;
        pending.Push(successor);
    }

    private static List<BlockIndex>[] BuildPredecessors(
        Avm1ControlFlowGraph controlFlowGraph,
        bool[] reachable)
    {
        var predecessors = new List<BlockIndex>[controlFlowGraph.Count];
        for (var i = 0; i < predecessors.Length; i++)
            predecessors[i] = [];

        for (var source = 0; source < controlFlowGraph.Count; source++)
        {
            if (!reachable[source])
                continue;

            var block = controlFlowGraph[new BlockIndex(source)];
            AddPredecessor(block.FirstSuccessor, source, reachable, predecessors);
            AddPredecessor(block.SecondSuccessor, source, reachable, predecessors);
        }

        return predecessors;
    }

    private static void AddPredecessor(
        BlockIndex successor,
        int source,
        bool[] reachable,
        List<BlockIndex>[] predecessors)
    {
        if (!successor.IsValid ||
            successor.Value >= predecessors.Length ||
            !reachable[successor.Value])
        {
            return;
        }

        predecessors[successor.Value].Add(new BlockIndex(source));
    }

    private static List<BlockIndex[]> FindStronglyConnectedComponents(
        Avm1ControlFlowGraph controlFlowGraph,
        bool[] reachable,
        List<BlockIndex>[] predecessors)
    {
        var finishOrder = BuildFinishOrder(controlFlowGraph, reachable);
        var assigned = new bool[controlFlowGraph.Count];
        var components = new List<BlockIndex[]>();
        var pending = new Stack<BlockIndex>();

        for (var i = finishOrder.Count - 1; i >= 0; i--)
        {
            var root = finishOrder[i];
            if (assigned[root.Value])
                continue;

            var blocks = new List<BlockIndex>();
            assigned[root.Value] = true;
            pending.Push(root);
            while (pending.Count > 0)
            {
                var block = pending.Pop();
                blocks.Add(block);
                foreach (var predecessor in predecessors[block.Value])
                {
                    if (assigned[predecessor.Value])
                        continue;

                    assigned[predecessor.Value] = true;
                    pending.Push(predecessor);
                }
            }

            components.Add(blocks.ToArray());
        }

        return components;
    }

    private static List<BlockIndex> BuildFinishOrder(
        Avm1ControlFlowGraph controlFlowGraph,
        bool[] reachable)
    {
        var visited = new bool[controlFlowGraph.Count];
        var nextSuccessor = new byte[controlFlowGraph.Count];
        var finishOrder = new List<BlockIndex>(reachable.Count(value => value));
        var pending = new Stack<BlockIndex>();

        visited[0] = true;
        pending.Push(new BlockIndex(0));
        while (pending.Count > 0)
        {
            var block = pending.Peek();
            var successor = nextSuccessor[block.Value]++ switch
            {
                0 => controlFlowGraph[block].FirstSuccessor,
                1 => controlFlowGraph[block].SecondSuccessor,
                _ => BlockIndex.Invalid
            };
            if (successor.IsValid &&
                successor.Value < reachable.Length &&
                reachable[successor.Value] &&
                !visited[successor.Value])
            {
                visited[successor.Value] = true;
                pending.Push(successor);
                continue;
            }

            if (nextSuccessor[block.Value] <= 2)
                continue;

            pending.Pop();
            finishOrder.Add(block);
        }

        return finishOrder;
    }

    private static bool IsCyclic(
        Avm1ControlFlowGraph controlFlowGraph,
        BlockIndex[] blocks)
    {
        if (blocks.Length > 1)
            return true;

        var block = controlFlowGraph[blocks[0]];
        return block.FirstSuccessor == block.Index ||
            block.SecondSuccessor == block.Index;
    }

    private static void AddEntry(
        BlockIndex successor,
        int component,
        int[] componentByBlock,
        HashSet<BlockIndex> entries)
    {
        if (successor.IsValid &&
            successor.Value < componentByBlock.Length &&
            componentByBlock[successor.Value] == component)
        {
            entries.Add(successor);
        }
    }
}

public sealed class Avm1IrreducibleControlFlowRegion
{
    internal Avm1IrreducibleControlFlowRegion(
        BlockIndex[] blocks,
        BlockIndex[] entries)
    {
        Blocks = blocks;
        Entries = entries;
    }

    public IReadOnlyList<BlockIndex> Blocks { get; }

    public IReadOnlyList<BlockIndex> Entries { get; }
}
