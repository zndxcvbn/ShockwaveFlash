using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1RegionAnalysis
{
    private Avm1RegionAnalysis(
        Avm1IfRegion[] ifRegions,
        Avm1WhileRegion[] whileRegions,
        Avm1DoWhileRegion[] doWhileRegions)
    {
        IfRegions = ifRegions;
        WhileRegions = whileRegions;
        DoWhileRegions = doWhileRegions;
    }

    public IReadOnlyList<Avm1IfRegion> IfRegions { get; }

    public IReadOnlyList<Avm1WhileRegion> WhileRegions { get; }

    public IReadOnlyList<Avm1DoWhileRegion> DoWhileRegions { get; }

    public static Avm1RegionAnalysis Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1PostDominatorTree postDominators,
        Avm1LoopAnalysis loops)
    {
        var doWhileRegions = BuildDoWhileRegions(instructions, cfg, loops);
        var whileRegions = BuildWhileRegions(instructions, cfg, loops, doWhileRegions);
        var ifRegions = BuildIfRegions(cfg, postDominators, whileRegions, doWhileRegions);

        return new Avm1RegionAnalysis(ifRegions, whileRegions, doWhileRegions);
    }

    private static Avm1IfRegion[] BuildIfRegions(
        Avm1ControlFlowGraph cfg,
        Avm1PostDominatorTree postDominators,
        IReadOnlyList<Avm1WhileRegion> whileRegions,
        IReadOnlyList<Avm1DoWhileRegion> doWhileRegions)
    {
        var loopConditions = whileRegions
            .Where(region => region.ConditionBlock == region.Header)
            .Select(region => region.Header)
            .Concat(doWhileRegions.Select(r => r.ConditionBlock))
            .ToHashSet();
        var ifRegions = new List<Avm1IfRegion>();
        foreach (var block in cfg.Blocks)
        {
            if (block.Terminator != Avm1BlockTerminatorKind.ConditionalBranch)
                continue;

            if (loopConditions.Contains(block.Index))
                continue;

            if (!block.FirstSuccessor.IsValid || !block.SecondSuccessor.IsValid)
            {
                if (block.FirstSuccessor.IsValid != block.SecondSuccessor.IsValid)
                {
                    ifRegions.Add(new Avm1IfRegion(
                        block.Index,
                        block.FirstSuccessor,
                        block.SecondSuccessor,
                        BlockIndex.Invalid,
                        HasElse: false));
                }

                continue;
            }

            if (block.Index.Value >= postDominators.ImmediatePostDominators.Count)
                continue;

            var merge = postDominators.ImmediatePostDominators[block.Index.Value];
            if (merge == postDominators.VirtualExit)
            {
                if (TryFindVirtualExitContinuation(cfg, block, out var inferredContinuation))
                {
                    ifRegions.Add(new Avm1IfRegion(
                        block.Index,
                        block.FirstSuccessor,
                        block.SecondSuccessor,
                        inferredContinuation,
                        HasElse: false));
                    continue;
                }

                if (TryFindPartialMerge(cfg, block, out var partialMerge))
                {
                    ifRegions.Add(new Avm1IfRegion(
                        block.Index,
                        block.FirstSuccessor,
                        block.SecondSuccessor,
                        partialMerge,
                        HasElse: partialMerge != block.FirstSuccessor &&
                            partialMerge != block.SecondSuccessor));
                    continue;
                }

                var fallThroughReturns = AlwaysTerminatesExplicitly(
                    cfg,
                    block.FirstSuccessor,
                    block.SecondSuccessor);
                var branchReturns = AlwaysTerminatesExplicitly(
                    cfg,
                    block.SecondSuccessor,
                    block.FirstSuccessor);

                if (fallThroughReturns != branchReturns)
                {
                    var continuation = fallThroughReturns
                        ? block.SecondSuccessor
                        : block.FirstSuccessor;
                    ifRegions.Add(new Avm1IfRegion(
                        block.Index,
                        block.FirstSuccessor,
                        block.SecondSuccessor,
                        continuation,
                        HasElse: false));
                }
                else
                {
                    ifRegions.Add(new Avm1IfRegion(
                        block.Index,
                        block.FirstSuccessor,
                        block.SecondSuccessor,
                        BlockIndex.Invalid,
                        HasElse: true));
                }
                continue;
            }

            if (!merge.IsValid)
                continue;

            if (merge == block.Index)
                continue;

            var fallThroughIsMerge = merge == block.FirstSuccessor;
            var branchIsMerge = merge == block.SecondSuccessor;
            if (fallThroughIsMerge && branchIsMerge)
                continue;

            if (fallThroughIsMerge || branchIsMerge)
            {
                var body = fallThroughIsMerge ? block.SecondSuccessor : block.FirstSuccessor;
                if (!postDominators.PostDominates(merge, body))
                    continue;

                ifRegions.Add(new Avm1IfRegion(block.Index, block.FirstSuccessor, block.SecondSuccessor, merge, HasElse: false));
                continue;
            }

            if (!postDominators.PostDominates(merge, block.FirstSuccessor) ||
                !postDominators.PostDominates(merge, block.SecondSuccessor))
            {
                continue;
            }

            ifRegions.Add(new Avm1IfRegion(block.Index, block.FirstSuccessor, block.SecondSuccessor, merge, HasElse: true));
        }

        return ifRegions.ToArray();
    }

    private static bool TryFindVirtualExitContinuation(
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock header,
        out BlockIndex continuation)
    {
        var firstReachesSecond = CanReachWithoutHeader(
            cfg,
            header.FirstSuccessor,
            header.SecondSuccessor,
            header.Index);
        var secondReachesFirst = CanReachWithoutHeader(
            cfg,
            header.SecondSuccessor,
            header.FirstSuccessor,
            header.Index);
        if (firstReachesSecond != secondReachesFirst)
        {
            continuation = firstReachesSecond
                ? header.SecondSuccessor
                : header.FirstSuccessor;
            return true;
        }

        if (IsAdjacentLinearTerminalArm(
                cfg,
                header.FirstSuccessor,
                header.SecondSuccessor))
        {
            continuation = header.SecondSuccessor;
            return true;
        }

        if (IsAdjacentLinearTerminalArm(
                cfg,
                header.SecondSuccessor,
                header.FirstSuccessor))
        {
            continuation = header.FirstSuccessor;
            return true;
        }

        continuation = BlockIndex.Invalid;
        return false;
    }

    private static bool TryFindPartialMerge(
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock header,
        out BlockIndex merge)
    {
        var firstDistances = CollectReachableDistances(cfg, header.FirstSuccessor, header.Index);
        var secondDistances = CollectReachableDistances(cfg, header.SecondSuccessor, header.Index);
        var bestScore = int.MaxValue;
        var bestMaximumDistance = int.MaxValue;
        merge = BlockIndex.Invalid;

        for (var candidate = header.Index.Value + 1; candidate < cfg.Count; candidate++)
        {
            if (firstDistances[candidate] < 0 || secondDistances[candidate] < 0)
                continue;

            var candidateIndex = new BlockIndex(candidate);
            if (!AllPathsReachOrTerminate(cfg, header.FirstSuccessor, candidateIndex, header.Index) ||
                !AllPathsReachOrTerminate(cfg, header.SecondSuccessor, candidateIndex, header.Index))
            {
                continue;
            }

            var score = firstDistances[candidate] + secondDistances[candidate];
            var maximumDistance = Math.Max(firstDistances[candidate], secondDistances[candidate]);
            if (score > bestScore ||
                (score == bestScore && maximumDistance >= bestMaximumDistance))
            {
                continue;
            }

            bestScore = score;
            bestMaximumDistance = maximumDistance;
            merge = candidateIndex;
        }

        return merge.IsValid;
    }

    private static int[] CollectReachableDistances(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex forbidden)
    {
        var distances = new int[cfg.Count];
        Array.Fill(distances, -1);
        if (!start.IsValid || start == forbidden)
            return distances;

        var pending = new Queue<BlockIndex>();
        distances[start.Value] = 0;
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
        {
            var block = cfg[current];
            Add(block.FirstSuccessor);
            Add(block.SecondSuccessor);

            void Add(BlockIndex successor)
            {
                if (!successor.IsValid || successor == forbidden || distances[successor.Value] >= 0)
                    return;

                distances[successor.Value] = distances[current.Value] + 1;
                pending.Enqueue(successor);
            }
        }

        return distances;
    }

    private static bool AllPathsReachOrTerminate(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex merge,
        BlockIndex forbidden)
    {
        var states = new byte[cfg.Count];
        return Visit(start);

        bool Visit(BlockIndex current)
        {
            if (current == merge)
                return true;
            if (!current.IsValid || current == forbidden)
                return false;

            ref var state = ref states[current.Value];
            if (state == 1)
                return false;
            if (state >= 2)
                return state == 2;

            state = 1;
            var block = cfg[current];
            var result = block.Terminator switch
            {
                Avm1BlockTerminatorKind.Return or Avm1BlockTerminatorKind.Throw => true,
                Avm1BlockTerminatorKind.FallThrough or Avm1BlockTerminatorKind.Jump =>
                    Visit(block.FirstSuccessor),
                Avm1BlockTerminatorKind.ConditionalBranch =>
                    Visit(block.FirstSuccessor) && Visit(block.SecondSuccessor),
                _ => false
            };
            state = result ? (byte)2 : (byte)3;
            return result;
        }
    }

    private static bool CanReachWithoutHeader(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex target,
        BlockIndex header)
    {
        var pending = new Stack<BlockIndex>();
        var visited = new HashSet<BlockIndex>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!block.IsValid || block == header || !visited.Add(block))
                continue;
            if (block == target)
                return true;

            var node = cfg[block];
            if (node.FirstSuccessor.IsValid)
                pending.Push(node.FirstSuccessor);
            if (node.SecondSuccessor.IsValid)
                pending.Push(node.SecondSuccessor);
        }

        return false;
    }

    private static bool IsAdjacentLinearTerminalArm(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex continuation)
    {
        if (IsLinearTerminalArm(cfg, continuation, start))
            return false;

        var visited = new HashSet<BlockIndex>();
        var current = start;
        while (current.IsValid && current != continuation && visited.Add(current))
        {
            var block = cfg[current];
            if (block.Terminator is Avm1BlockTerminatorKind.Return or Avm1BlockTerminatorKind.Throw)
                return continuation.Value == current.Value + 1;

            if (block.Terminator is not (Avm1BlockTerminatorKind.FallThrough or
                Avm1BlockTerminatorKind.Jump) ||
                !block.FirstSuccessor.IsValid)
            {
                return false;
            }

            current = block.FirstSuccessor;
        }

        return false;
    }

    private static bool IsLinearTerminalArm(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex forbidden)
    {
        var visited = new HashSet<BlockIndex>();
        var current = start;
        while (current.IsValid && current != forbidden && visited.Add(current))
        {
            var block = cfg[current];
            if (block.Terminator is Avm1BlockTerminatorKind.Return or Avm1BlockTerminatorKind.Throw)
                return true;

            if (block.Terminator is not (Avm1BlockTerminatorKind.FallThrough or
                Avm1BlockTerminatorKind.Jump) ||
                !block.FirstSuccessor.IsValid)
            {
                return false;
            }

            current = block.FirstSuccessor;
        }

        return false;
    }

    private static bool AlwaysTerminatesExplicitly(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex continuation)
    {
        var pending = new Stack<BlockIndex>();
        var visited = new HashSet<BlockIndex>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!block.IsValid || block == continuation)
                return false;
            if (!visited.Add(block))
                continue;

            var node = cfg[block];
            switch (node.Terminator)
            {
                case Avm1BlockTerminatorKind.Return:
                case Avm1BlockTerminatorKind.Throw:
                    break;
                case Avm1BlockTerminatorKind.FallThrough:
                case Avm1BlockTerminatorKind.Jump:
                    if (!node.FirstSuccessor.IsValid)
                        return false;
                    pending.Push(node.FirstSuccessor);
                    break;
                case Avm1BlockTerminatorKind.ConditionalBranch:
                    if (!node.FirstSuccessor.IsValid || !node.SecondSuccessor.IsValid)
                        return false;
                    pending.Push(node.FirstSuccessor);
                    pending.Push(node.SecondSuccessor);
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static Avm1WhileRegion[] BuildWhileRegions(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1LoopAnalysis loops,
        IReadOnlyList<Avm1DoWhileRegion> doWhileRegions)
    {
        var doWhileHeaders = doWhileRegions.Select(r => r.Header).ToHashSet();
        var whileRegions = new List<Avm1WhileRegion>();
        var dominators = Avm1DominatorTree.Build(cfg);
        foreach (var loopGroup in loops.Loops.GroupBy(loop => loop.Header))
        {
            if (doWhileHeaders.Contains(loopGroup.Key))
                continue;

            var header = cfg[loopGroup.Key];
            if (header.Terminator != Avm1BlockTerminatorKind.ConditionalBranch)
                continue;

            var loopBlocks = loopGroup
                .SelectMany(loop => loop.Blocks)
                .ToHashSet();

            var firstInLoop = IsLoopBlock(header.FirstSuccessor, loopBlocks);
            var secondInLoop = IsLoopBlock(header.SecondSuccessor, loopBlocks);
            BlockIndex conditionBlock;
            BlockIndex bodyEntry;
            BlockIndex exit;
            HashSet<BlockIndex> conditionBlocks;
            if (firstInLoop != secondInLoop)
            {
                conditionBlock = loopGroup.Key;
                bodyEntry = firstInLoop ? header.FirstSuccessor : header.SecondSuccessor;
                exit = firstInLoop ? header.SecondSuccessor : header.FirstSuccessor;
                conditionBlocks = [loopGroup.Key];
            }
            else if (!TryFindShortCircuitWhileCondition(
                         instructions,
                         cfg,
                         loopGroup.Key,
                         loopBlocks,
                         out conditionBlock,
                         out bodyEntry,
                         out exit,
                         out conditionBlocks))
            {
                continue;
            }

            if (!bodyEntry.IsValid)
                continue;

            exit = ResolveLoopExitTrampoline(
                instructions,
                cfg,
                exit,
                loopBlocks);
            ExpandLoopBodyToExit(
                cfg,
                dominators,
                loopGroup.Key,
                bodyEntry,
                exit,
                loopBlocks);

            var blocks = loopBlocks
                .Where(block => block != loopGroup.Key)
                .OrderBy(block => block.Value)
                .ToArray();
            var bodyBlocks = loopBlocks
                .Where(block => !conditionBlocks.Contains(block))
                .OrderBy(block => block.Value)
                .ToArray();
            if (!bodyBlocks.Contains(bodyEntry))
                continue;
            bodyBlocks = RotateToEntry(bodyBlocks, bodyEntry);

            whileRegions.Add(new Avm1WhileRegion(
                loopGroup.Key,
                conditionBlock,
                bodyEntry,
                exit,
                blocks,
                bodyBlocks));
        }

        return whileRegions.ToArray();
    }

    private static void ExpandLoopBodyToExit(
        Avm1ControlFlowGraph cfg,
        Avm1DominatorTree dominators,
        BlockIndex header,
        BlockIndex bodyEntry,
        BlockIndex exit,
        HashSet<BlockIndex> loopBlocks)
    {
        var visited = new HashSet<BlockIndex>();
        var pending = new Stack<BlockIndex>();
        pending.Push(bodyEntry);
        while (pending.TryPop(out var blockIndex))
        {
            if (!blockIndex.IsValid ||
                blockIndex == header ||
                blockIndex == exit ||
                !visited.Add(blockIndex) ||
                !dominators.Dominates(header, blockIndex))
            {
                continue;
            }

            loopBlocks.Add(blockIndex);
            var block = cfg[blockIndex];
            pending.Push(block.FirstSuccessor);
            pending.Push(block.SecondSuccessor);
        }
    }

    private static BlockIndex[] RotateToEntry(
        BlockIndex[] blocks,
        BlockIndex entry)
    {
        var entryPosition = Array.IndexOf(blocks, entry);
        if (entryPosition <= 0)
            return blocks;

        var result = new BlockIndex[blocks.Length];
        Array.Copy(
            blocks,
            entryPosition,
            result,
            0,
            blocks.Length - entryPosition);
        Array.Copy(
            blocks,
            0,
            result,
            blocks.Length - entryPosition,
            entryPosition);
        return result;
    }

    private static BlockIndex ResolveLoopExitTrampoline(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        BlockIndex exit,
        HashSet<BlockIndex> loopBlocks)
    {
        var visited = new HashSet<BlockIndex>();
        while (exit.IsValid &&
            !loopBlocks.Contains(exit) &&
            visited.Add(exit))
        {
            var block = cfg[exit];
            if (block.Terminator is not Avm1BlockTerminatorKind.Jump ||
                block.EndAction.Value != block.StartAction.Value + 1 ||
                instructions[block.StartAction].Action is not ActionJump ||
                !block.FirstSuccessor.IsValid ||
                block.SecondSuccessor.IsValid)
            {
                break;
            }

            exit = block.FirstSuccessor;
        }

        return exit;
    }

    private static bool TryFindShortCircuitWhileCondition(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        BlockIndex header,
        HashSet<BlockIndex> loopBlocks,
        out BlockIndex conditionBlock,
        out BlockIndex bodyEntry,
        out BlockIndex exit,
        out HashSet<BlockIndex> conditionBlocks)
    {
        conditionBlock = BlockIndex.Invalid;
        bodyEntry = BlockIndex.Invalid;
        exit = BlockIndex.Invalid;
        conditionBlocks = [];

        var current = header;
        while (current.IsValid && conditionBlocks.Add(current))
        {
            var block = cfg[current];
            if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch)
                return false;

            var firstInLoop = IsLoopBlock(block.FirstSuccessor, loopBlocks);
            var secondInLoop = IsLoopBlock(block.SecondSuccessor, loopBlocks);
            if (firstInLoop != secondInLoop)
            {
                conditionBlock = current;
                bodyEntry = firstInLoop ? block.FirstSuccessor : block.SecondSuccessor;
                exit = firstInLoop ? block.SecondSuccessor : block.FirstSuccessor;
                return conditionBlock != header;
            }

            if (!firstInLoop ||
                !TryGetShortCircuitContinuation(
                    instructions,
                    cfg,
                    block,
                    block.FirstSuccessor,
                    block.SecondSuccessor,
                    out var bridge,
                    out var continuation,
                    out var bypass) ||
                !conditionBlocks.Add(bridge))
            {
                return false;
            }

            if (bypass.IsValid)
                conditionBlocks.Add(bypass);

            current = continuation;
        }

        return false;
    }

    private static bool HasShortCircuitStackBridge(
        Avm1InstructionTable instructions,
        Avm1BasicBlock branch,
        Avm1BasicBlock bridge)
    {
        var branchAction = branch.EndAction.Value - 2;
        if (branchAction < branch.StartAction.Value ||
            bridge.StartAction.Value >= bridge.EndAction.Value ||
            instructions[bridge.StartAction].Action.Opcode is not ActionOpcode.Pop)
        {
            return false;
        }

        if (instructions[new ActionIndex(branchAction)].Action.Opcode is ActionOpcode.Not)
            branchAction--;

        return branchAction >= branch.StartAction.Value &&
            instructions[new ActionIndex(branchAction)].Action.Opcode is ActionOpcode.PushDuplicate;
    }

    private static bool HasShortCircuitBridge(
        Avm1InstructionTable instructions,
        Avm1BasicBlock branch,
        Avm1BasicBlock bridge,
        Avm1BasicBlock continuation) =>
        HasShortCircuitStackBridge(instructions, branch, bridge) ||
        HasShortCircuitRegisterBridge(
            instructions,
            branch,
            bridge,
            continuation);

    private static bool HasShortCircuitRegisterBridge(
        Avm1InstructionTable instructions,
        Avm1BasicBlock branch,
        Avm1BasicBlock bridge,
        Avm1BasicBlock continuation)
    {
        if (!TryFindLastStoredRegister(
                instructions,
                branch,
                out _,
                out var branchRegister) ||
            !TryFindLastStoredRegister(
                instructions,
                bridge,
                out var bridgeStore,
                out var bridgeRegister) ||
            branchRegister != bridgeRegister ||
            bridgeStore.Value + 1 >= bridge.EndAction.Value ||
            instructions[new ActionIndex(bridgeStore.Value + 1)].Action.Opcode is not
                ActionOpcode.Pop ||
            continuation.StartAction.Value >= continuation.EndAction.Value ||
            instructions[continuation.StartAction].Action is not ActionPush
            {
                PushValues: [PushValue.PushValueRegister pushed]
            } ||
            pushed.RegisterIndex != branchRegister)
        {
            return false;
        }

        return true;
    }

    private static bool TryFindLastStoredRegister(
        Avm1InstructionTable instructions,
        Avm1BasicBlock block,
        out ActionIndex action,
        out byte register)
    {
        action = ActionIndex.Invalid;
        register = 0;
        for (var index = block.EndAction.Value - 2;
            index >= block.StartAction.Value;
            index--)
        {
            if (instructions[new ActionIndex(index)].Action is not
                ActionStoreRegister store)
            {
                continue;
            }

            action = new ActionIndex(index);
            register = store.RegisterNumber;
            return true;
        }
        return false;
    }

    private static bool TryGetShortCircuitContinuation(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock branch,
        BlockIndex first,
        BlockIndex second,
        out BlockIndex bridge,
        out BlockIndex continuation,
        out BlockIndex bypass)
    {
        bypass = BlockIndex.Invalid;
        if (IsLinearBridge(cfg, first, second) &&
            HasShortCircuitBridge(instructions, branch, cfg[first], cfg[second]))
        {
            bridge = first;
            continuation = second;
            return true;
        }

        if (IsLinearBridge(cfg, second, first) &&
            HasShortCircuitBridge(instructions, branch, cfg[second], cfg[first]))
        {
            bridge = second;
            continuation = first;
            return true;
        }

        if (TryGetLinearSuccessor(cfg, first, out var firstContinuation) &&
            TryGetLinearSuccessor(cfg, second, out var secondContinuation) &&
            firstContinuation == secondContinuation)
        {
            if (IsShortCircuitBypass(instructions, cfg[first]) &&
                HasShortCircuitBridge(
                    instructions,
                    branch,
                    cfg[second],
                    cfg[firstContinuation]))
            {
                bridge = second;
                continuation = firstContinuation;
                bypass = first;
                return true;
            }

            if (IsShortCircuitBypass(instructions, cfg[second]) &&
                HasShortCircuitBridge(
                    instructions,
                    branch,
                    cfg[first],
                    cfg[firstContinuation]))
            {
                bridge = first;
                continuation = firstContinuation;
                bypass = second;
                return true;
            }
        }

        bridge = BlockIndex.Invalid;
        continuation = BlockIndex.Invalid;
        return false;
    }

    private static bool TryGetLinearSuccessor(
        Avm1ControlFlowGraph cfg,
        BlockIndex block,
        out BlockIndex successor)
    {
        successor = BlockIndex.Invalid;
        if (!block.IsValid)
            return false;

        var candidate = cfg[block];
        if (!candidate.FirstSuccessor.IsValid || candidate.SecondSuccessor.IsValid)
            return false;

        successor = candidate.FirstSuccessor;
        return true;
    }

    private static bool IsShortCircuitBypass(
        Avm1InstructionTable instructions,
        Avm1BasicBlock block) =>
        block.Terminator is Avm1BlockTerminatorKind.Jump &&
        block.EndAction.Value == block.StartAction.Value + 1 &&
        instructions[block.StartAction].Action is ActionJump;

    private static bool IsLinearBridge(
        Avm1ControlFlowGraph cfg,
        BlockIndex candidate,
        BlockIndex continuation)
    {
        if (!candidate.IsValid || candidate == continuation)
            return false;

        var block = cfg[candidate];
        return block.Terminator is Avm1BlockTerminatorKind.FallThrough or Avm1BlockTerminatorKind.Jump &&
            block.FirstSuccessor == continuation &&
            !block.SecondSuccessor.IsValid;
    }

    private static Avm1DoWhileRegion[] BuildDoWhileRegions(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1LoopAnalysis loops)
    {
        var regions = new List<Avm1DoWhileRegion>();
        foreach (var loopGroup in loops.Loops.GroupBy(loop => loop.Header))
        {
            var loopBlocks = loopGroup
                .SelectMany(loop => loop.Blocks)
                .ToHashSet();

            var conditionBlocks = loopGroup
                .Select(loop => loop.Tail)
                .Distinct()
                .Where(tail => IsPostTestCondition(cfg[tail], loopGroup.Key, loopBlocks))
                .ToArray();

            if (conditionBlocks.Length != 1)
                continue;

            var conditionBlock = conditionBlocks[0];
            var condition = cfg[conditionBlock];
            var exit = condition.FirstSuccessor == loopGroup.Key
                ? condition.SecondSuccessor
                : condition.FirstSuccessor;

            var blocks = loopBlocks.OrderBy(block => block.Value).ToArray();
            var conditionPrefixBlocks = FindPostTestConditionPrefixBlocks(
                instructions,
                cfg,
                loopBlocks,
                conditionBlock)
                .OrderBy(block => block.Value)
                .ToArray();

            regions.Add(new Avm1DoWhileRegion(
                loopGroup.Key,
                conditionBlock,
                exit,
                blocks,
                conditionPrefixBlocks));
        }

        return regions.ToArray();
    }

    private static HashSet<BlockIndex> FindPostTestConditionPrefixBlocks(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        HashSet<BlockIndex> loopBlocks,
        BlockIndex finalCondition)
    {
        var conditionBlocks = new HashSet<BlockIndex> { finalCondition };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in loopBlocks)
            {
                if (conditionBlocks.Contains(candidate))
                    continue;

                var block = cfg[candidate];
                if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
                    !TryGetShortCircuitContinuation(
                        instructions,
                        cfg,
                        block,
                        block.FirstSuccessor,
                        block.SecondSuccessor,
                        out var bridge,
                        out var continuation,
                        out var bypass) ||
                    !conditionBlocks.Contains(continuation) ||
                    !loopBlocks.Contains(bridge) ||
                    (bypass.IsValid && !loopBlocks.Contains(bypass)))
                {
                    continue;
                }

                changed |= conditionBlocks.Add(candidate);
                changed |= conditionBlocks.Add(bridge);
                if (bypass.IsValid)
                    changed |= conditionBlocks.Add(bypass);
            }
        }

        conditionBlocks.Remove(finalCondition);
        return conditionBlocks;
    }

    private static bool IsPostTestCondition(
        Avm1BasicBlock tail,
        BlockIndex header,
        HashSet<BlockIndex> loopBlocks)
    {
        if (tail.Terminator != Avm1BlockTerminatorKind.ConditionalBranch)
            return false;

        var firstIsHeader = tail.FirstSuccessor == header;
        var secondIsHeader = tail.SecondSuccessor == header;
        if (firstIsHeader == secondIsHeader)
            return false;

        var exit = firstIsHeader ? tail.SecondSuccessor : tail.FirstSuccessor;
        return !exit.IsValid || !loopBlocks.Contains(exit);
    }

    private static bool IsLoopBlock(BlockIndex block, HashSet<BlockIndex> loopBlocks)
    {
        return block.IsValid && loopBlocks.Contains(block);
    }
}

public readonly record struct Avm1IfRegion(
    BlockIndex Header,
    BlockIndex FallThroughEntry,
    BlockIndex BranchEntry,
    BlockIndex Merge,
    bool HasElse);

public readonly record struct Avm1WhileRegion(
    BlockIndex Header,
    BlockIndex ConditionBlock,
    BlockIndex BodyEntry,
    BlockIndex Exit,
    IReadOnlyList<BlockIndex> Blocks,
    IReadOnlyList<BlockIndex> BodyBlocks);

public readonly record struct Avm1DoWhileRegion(
    BlockIndex Header,
    BlockIndex ConditionBlock,
    BlockIndex Exit,
    IReadOnlyList<BlockIndex> Blocks,
    IReadOnlyList<BlockIndex> ConditionPrefixBlocks);
