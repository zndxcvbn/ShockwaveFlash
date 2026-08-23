using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ReachabilityAnalysis
{
    private readonly bool[] _reachableBlocks;
    private readonly bool[] _reachableFlowPoints;

    private Avm1ReachabilityAnalysis(
        bool[] reachableBlocks,
        bool[] reachableFlowPoints,
        int reachableBlockCount,
        int reachableFlowPointCount)
    {
        _reachableBlocks = reachableBlocks;
        _reachableFlowPoints = reachableFlowPoints;
        ReachableBlockCount = reachableBlockCount;
        ReachableFlowPointCount = reachableFlowPointCount;
    }

    public int ReachableBlockCount { get; }

    public int ReachableFlowPointCount { get; }

    public static Avm1ReachabilityAnalysis Build(
        Avm1ControlFlowGraph controlFlowGraph,
        Avm1FlowGraph flowGraph)
    {
        ArgumentNullException.ThrowIfNull(controlFlowGraph);
        ArgumentNullException.ThrowIfNull(flowGraph);

        var reachableBlocks = new bool[controlFlowGraph.Count];
        var reachablePoints = new bool[flowGraph.Count];
        if (controlFlowGraph.Count == 0 ||
            !flowGraph.TryGetPoint(
                new BlockIndex(0),
                flowGraph.NormalContext,
                out var entry))
        {
            return new Avm1ReachabilityAnalysis(
                reachableBlocks,
                reachablePoints,
                reachableBlockCount: 0,
                reachableFlowPointCount: 0);
        }

        var pending = new FlowPointIndex[flowGraph.Count];
        var pendingCount = 0;
        var reachableBlockCount = 0;
        var reachablePointCount = 0;
        Add(entry);
        while (pendingCount > 0)
        {
            var point = pending[--pendingCount];
            var block = flowGraph[point].Block;
            if (block.IsValid &&
                block.Value < reachableBlocks.Length &&
                !reachableBlocks[block.Value])
            {
                reachableBlocks[block.Value] = true;
                reachableBlockCount++;
            }

            foreach (var transition in flowGraph.GetTransitions(point))
                Add(transition.Target);
        }

        return new Avm1ReachabilityAnalysis(
            reachableBlocks,
            reachablePoints,
            reachableBlockCount,
            reachablePointCount);

        void Add(FlowPointIndex point)
        {
            if (!point.IsValid ||
                point.Value >= reachablePoints.Length ||
                reachablePoints[point.Value])
            {
                return;
            }

            reachablePoints[point.Value] = true;
            reachablePointCount++;
            pending[pendingCount++] = point;
        }
    }

    public bool IsReachable(BlockIndex block) =>
        block.IsValid &&
        block.Value < _reachableBlocks.Length &&
        _reachableBlocks[block.Value];

    public bool IsReachable(FlowPointIndex point) =>
        point.IsValid &&
        point.Value < _reachableFlowPoints.Length &&
        _reachableFlowPoints[point.Value];
}
