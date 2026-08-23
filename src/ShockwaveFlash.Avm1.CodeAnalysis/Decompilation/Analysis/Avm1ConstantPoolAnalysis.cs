using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Swf5;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ConstantPoolAnalysis
{
    private readonly PoolState[] _entries;

    private Avm1ConstantPoolAnalysis(PoolState[] entries)
    {
        _entries = entries;
    }

    public bool TryGetEntries(
        ActionIndex action,
        out IReadOnlyList<string> entries)
    {
        if (action.IsValid &&
            action.Value < _entries.Length &&
            _entries[action.Value].IsKnown)
        {
            entries = _entries[action.Value].Entries;
            return true;
        }

        entries = [];
        return false;
    }

    public static Avm1ConstantPoolAnalysis Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg)
    {
        var actionEntries = new PoolState[instructions.Count];
        if (cfg.Count == 0)
            return new Avm1ConstantPoolAnalysis(actionEntries);

        var blockEntries = new PoolState?[cfg.Count];
        blockEntries[0] = PoolState.Known([]);
        var queued = new bool[cfg.Count];
        var worklist = new Queue<BlockIndex>();
        Enqueue(new BlockIndex(0));

        while (worklist.TryDequeue(out var blockIndex))
        {
            queued[blockIndex.Value] = false;
            var state = blockEntries[blockIndex.Value]!.Value;
            var block = cfg[blockIndex];
            for (var actionValue = block.StartAction.Value;
                 actionValue < block.EndAction.Value;
                 actionValue++)
            {
                var action = new ActionIndex(actionValue);
                actionEntries[actionValue] = Merge(
                    actionEntries[actionValue],
                    state);
                if (instructions[action].Action is ActionConstantPool pool)
                    state = PoolState.Known(pool.Constants);
            }

            Propagate(block.FirstSuccessor, state);
            Propagate(block.SecondSuccessor, state);
            Propagate(block.ExceptionSuccessor, state);
            foreach (var completion in cfg.GetCompletionEdges(blockIndex))
                Propagate(completion.Target, state);
        }

        return new Avm1ConstantPoolAnalysis(actionEntries);

        void Propagate(BlockIndex target, PoolState incoming)
        {
            if (!target.IsValid || target.Value >= blockEntries.Length)
                return;

            var existing = blockEntries[target.Value];
            var merged = existing.HasValue
                ? Merge(existing.Value, incoming)
                : incoming;
            if (existing.HasValue && Same(existing.Value, merged))
                return;

            blockEntries[target.Value] = merged;
            Enqueue(target);
        }

        void Enqueue(BlockIndex block)
        {
            if (queued[block.Value])
                return;
            queued[block.Value] = true;
            worklist.Enqueue(block);
        }
    }

    private static PoolState Merge(PoolState left, PoolState right)
    {
        if (!left.IsInitialized)
            return right;
        if (!right.IsInitialized)
            return left;
        if (!left.IsKnown || !right.IsKnown)
            return PoolState.Unknown;
        return left.Entries.SequenceEqual(right.Entries)
            ? left
            : PoolState.Unknown;
    }

    private static bool Same(PoolState left, PoolState right) =>
        left.IsInitialized == right.IsInitialized &&
        left.IsKnown == right.IsKnown &&
        (!left.IsKnown || left.Entries.SequenceEqual(right.Entries));

    private readonly record struct PoolState(
        bool IsInitialized,
        bool IsKnown,
        IReadOnlyList<string> Entries)
    {
        public static readonly PoolState Unknown = new(true, false, []);

        public static PoolState Known(IReadOnlyList<string> entries) =>
            new(true, true, entries.ToArray());
    }
}
