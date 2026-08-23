using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Special;

namespace ShockwaveFlash.Avm1.Decompilation.Ir;

public sealed class Avm1ControlFlowGraph
{
    private readonly Dictionary<int, BlockIndex> _actionToBlock;
    private readonly Avm1CompletionEdge[] _completionEdges;

    private Avm1ControlFlowGraph(
        Avm1BasicBlock[] blocks,
        Dictionary<int, BlockIndex> actionToBlock,
        Avm1CompletionEdge[] completionEdges)
    {
        Blocks = blocks;
        _actionToBlock = actionToBlock;
        _completionEdges = completionEdges;
    }

    public IReadOnlyList<Avm1BasicBlock> Blocks { get; }

    public IReadOnlyList<Avm1CompletionEdge> CompletionEdges => _completionEdges;

    public int Count => Blocks.Count;

    public Avm1BasicBlock this[BlockIndex index] => Blocks[index.Value];

    public static Avm1ControlFlowGraph Build(Avm1InstructionTable instructions)
    {
        if (instructions.Count == 0)
            return new Avm1ControlFlowGraph([], [], []);

        var leaders = new SortedSet<int> { 0, instructions.Count };

        foreach (var region in instructions.WithRegions)
        {
            leaders.Add(region.EnterAction.Value);
            leaders.Add(region.BodyStartAction.Value);
            leaders.Add(region.ExitAction.Value);
            leaders.Add(region.ExitAction.Value + 1);
        }

        foreach (var region in instructions.TryRegions)
        {
            AddBoundaryLeader(leaders, region.EnterAction, instructions.Count, splitAfter: true);
            AddBoundaryLeader(leaders, region.TryBodyStartAction, instructions.Count);
            AddBoundaryLeader(leaders, region.TryExitAction, instructions.Count, splitAfter: true);
            AddBoundaryLeader(leaders, region.CatchEnterAction, instructions.Count, splitAfter: true);
            AddBoundaryLeader(leaders, region.CatchBodyStartAction, instructions.Count);
            AddBoundaryLeader(leaders, region.CatchExitAction, instructions.Count, splitAfter: true);
            AddBoundaryLeader(leaders, region.FinallyEnterAction, instructions.Count, splitAfter: true);
            AddBoundaryLeader(leaders, region.FinallyBodyStartAction, instructions.Count);
            AddBoundaryLeader(leaders, region.FinallyExitAction, instructions.Count, splitAfter: true);
            AddBoundaryLeader(leaders, region.ContinuationAction, instructions.Count);
        }

        var exceptionHandlers = CreateExceptionHandlers(instructions);
        for (var i = 0; i < exceptionHandlers.Length; i++)
        {
            if (!exceptionHandlers[i].HandlerAction.IsValid ||
                !MayThrow(instructions[new ActionIndex(i)].Action))
            {
                continue;
            }

            leaders.Add(i);
            leaders.Add(i + 1);
        }

        for (var i = 0; i < instructions.Count; i++)
        {
            var index = new ActionIndex(i);
            var instruction = instructions[index];
            var next = i + 1;

            if (instruction.Action is ActionIf)
            {
                AddBranchTarget(instructions, leaders, index);
                if (next <= instructions.Count)
                    leaders.Add(next);
            }
            else if (instruction.Action is ActionJump)
            {
                AddBranchTarget(instructions, leaders, index);
                if (next <= instructions.Count)
                    leaders.Add(next);
            }
            else if (instruction.Action is ActionWaitForFrame or ActionWaitForFrame2)
            {
                AddExplicitSuccessorTargets(instructions, leaders, index);
                if (next <= instructions.Count)
                    leaders.Add(next);
            }
            else if (instruction.Action is ActionReturn or ActionThrow or ActionEnd)
            {
                if (next <= instructions.Count)
                    leaders.Add(next);
            }
        }

        var leaderArray = leaders.ToArray();
        var blocks = new List<Avm1BasicBlock>(leaderArray.Length - 1);
        var actionToBlock = new Dictionary<int, BlockIndex>();

        for (var i = 0; i < leaderArray.Length - 1; i++)
        {
            var start = leaderArray[i];
            var endExclusive = leaderArray[i + 1];
            if (start >= instructions.Count || start == endExclusive)
                continue;

            var blockIndex = new BlockIndex(blocks.Count);
            for (var action = start; action < endExclusive; action++)
                actionToBlock[action] = blockIndex;

            var startInstruction = instructions[new ActionIndex(start)];
            var lastInstruction = instructions[new ActionIndex(endExclusive - 1)];
            var terminator = ClassifyTerminator(lastInstruction);
            var firstSuccessor = BlockIndex.Invalid;
            var secondSuccessor = BlockIndex.Invalid;

            blocks.Add(new Avm1BasicBlock(
                blockIndex,
                new ActionIndex(start),
                new ActionIndex(endExclusive),
                startInstruction.Offset,
                lastInstruction.EndOffset,
                terminator,
                firstSuccessor,
                secondSuccessor,
                BlockIndex.Invalid,
                BlockIndex.Invalid,
                CompletionEdgeIndex.Invalid,
                0));
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var lastAction = new ActionIndex(block.EndAction.Value - 1);
            var lastInstruction = instructions[lastAction];
            var firstSuccessor = BlockIndex.Invalid;
            var secondSuccessor = BlockIndex.Invalid;

            if (HasExplicitSuccessors(lastInstruction.Kind) ||
                lastInstruction.Action is ActionWaitForFrame or ActionWaitForFrame2)
            {
                firstSuccessor = FindBlockAtAction(
                    actionToBlock,
                    instructions.GetExplicitFirstSuccessor(lastAction));
                secondSuccessor = FindBlockAtAction(
                    actionToBlock,
                    instructions.GetExplicitSecondSuccessor(lastAction));
            }
            else switch (lastInstruction.Action)
            {
                case ActionIf:
                    firstSuccessor = FindBlockAtAction(actionToBlock, block.EndAction);
                    secondSuccessor = FindBlockAtAction(
                        actionToBlock,
                        instructions.GetBranchTargetAction(lastAction));
                    break;
                case ActionJump:
                    firstSuccessor = FindBlockAtAction(
                        actionToBlock,
                        instructions.GetBranchTargetAction(lastAction));
                    break;
                case ActionReturn or ActionThrow or ActionEnd:
                    break;
                default:
                    firstSuccessor = FindBlockAtAction(actionToBlock, block.EndAction);
                    break;
            }

            blocks[i] = block with
            {
                FirstSuccessor = firstSuccessor,
                SecondSuccessor = secondSuccessor
            };
        }

        AddExceptionalSuccessors(
            blocks,
            actionToBlock,
            instructions,
            exceptionHandlers);
        var completionEdges = AddCompletionEdges(
            blocks,
            actionToBlock,
            instructions,
            exceptionHandlers);

        return new Avm1ControlFlowGraph(blocks.ToArray(), actionToBlock, completionEdges);
    }

    public bool TryGetBlockForAction(ActionIndex action, out BlockIndex block)
    {
        return _actionToBlock.TryGetValue(action.Value, out block);
    }

    public ReadOnlySpan<Avm1CompletionEdge> GetCompletionEdges(BlockIndex block)
    {
        var basicBlock = this[block];
        return basicBlock.CompletionEdgeCount == 0
            ? []
            : _completionEdges.AsSpan(
                basicBlock.CompletionEdgeStart.Value,
                basicBlock.CompletionEdgeCount);
    }

    private static void AddBranchTarget(Avm1InstructionTable instructions, SortedSet<int> leaders, ActionIndex branchAction)
    {
        var target = instructions.GetBranchTargetAction(branchAction);
        if (target.IsValid && target.Value <= instructions.Count)
            leaders.Add(target.Value);
    }

    private static void AddExplicitSuccessorTargets(
        Avm1InstructionTable instructions,
        SortedSet<int> leaders,
        ActionIndex action)
    {
        var first = instructions.GetExplicitFirstSuccessor(action);
        var second = instructions.GetExplicitSecondSuccessor(action);
        if (first.IsValid && first.Value <= instructions.Count)
            leaders.Add(first.Value);
        if (second.IsValid && second.Value <= instructions.Count)
            leaders.Add(second.Value);
    }

    private static void AddBoundaryLeader(
        SortedSet<int> leaders,
        ActionIndex action,
        int instructionCount,
        bool splitAfter = false)
    {
        if (!action.IsValid || action.Value > instructionCount)
            return;

        leaders.Add(action.Value);
        if (splitAfter && action.Value < instructionCount)
            leaders.Add(action.Value + 1);
    }

    private static bool HasExplicitSuccessors(Avm1InstructionKind kind) => kind is
        Avm1InstructionKind.TryEnter or
        Avm1InstructionKind.TryExit or
        Avm1InstructionKind.CatchEnter or
        Avm1InstructionKind.CatchExit or
        Avm1InstructionKind.FinallyEnter or
        Avm1InstructionKind.FinallyExit;

    private static void AddExceptionalSuccessors(
        List<Avm1BasicBlock> blocks,
        Dictionary<int, BlockIndex> actionToBlock,
        Avm1InstructionTable instructions,
        Avm1ExceptionHandler[] exceptionHandlers)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var throwingAction = new ActionIndex(block.EndAction.Value - 1);
            var handler = exceptionHandlers[throwingAction.Value];
            if (!handler.HandlerAction.IsValid ||
                !MayThrow(instructions[throwingAction].Action) ||
                !actionToBlock.TryGetValue(handler.HandlerAction.Value, out var target))
            {
                continue;
            }

            var stackSource = handler.StackSourceAction.IsValid &&
                actionToBlock.TryGetValue(handler.StackSourceAction.Value, out var source)
                    ? source
                    : BlockIndex.Invalid;
            blocks[i] = block with
            {
                ExceptionSuccessor = target,
                ExceptionStackSource = stackSource
            };
        }
    }

    private static Avm1ExceptionHandler[] CreateExceptionHandlers(
        Avm1InstructionTable instructions)
    {
        var handlers = Enumerable.Repeat(
            Avm1ExceptionHandler.Invalid,
            instructions.Count).ToArray();

        foreach (var region in instructions.TryRegions
            .OrderByDescending(GetTryRegionSpan))
        {
            var catchesException = region.CatchEnterAction.IsValid;
            AssignExceptionHandler(
                handlers,
                region.TryBodyStartAction,
                region.TryExitAction,
                catchesException ? region.CatchEnterAction : region.FinallyEnterAction,
                region.EnterAction);

            if (region.CatchBodyStartAction.IsValid && region.FinallyEnterAction.IsValid)
            {
                AssignExceptionHandler(
                    handlers,
                    region.CatchBodyStartAction,
                    region.CatchExitAction,
                    region.FinallyEnterAction,
                    region.EnterAction);
            }
        }

        return handlers;
    }

    private static Avm1CompletionEdge[] AddCompletionEdges(
        List<Avm1BasicBlock> blocks,
        Dictionary<int, BlockIndex> actionToBlock,
        Avm1InstructionTable instructions,
        Avm1ExceptionHandler[] exceptionHandlers)
    {
        var pending = new List<(BlockIndex Source, Avm1CompletionEdge Edge)>();
        var seen = new HashSet<(int Source, Avm1CompletionKind Kind, int Target, int StackSource)>();
        var returnHandlers = CreateReturnHandlers(instructions);
        var finallyRegions = instructions.TryRegions
            .Where(region => region.FinallyEnterAction.IsValid)
            .ToDictionary(region => region.FinallyEnterAction.Value);

        for (var i = 0; i < instructions.Count; i++)
        {
            var action = new ActionIndex(i);
            if (instructions[action].Action is ActionReturn && returnHandlers[i].IsValid)
            {
                AddReturnCompletionRoute(
                    action,
                    returnHandlers[i],
                    returnHandlers,
                    finallyRegions,
                    actionToBlock,
                    pending,
                    seen);
            }

            var exceptionHandler = exceptionHandlers[i];
            if (!exceptionHandler.HandlerAction.IsValid ||
                !MayThrow(instructions[action].Action) ||
                instructions[exceptionHandler.HandlerAction].Kind is not Avm1InstructionKind.FinallyEnter)
            {
                continue;
            }

            AddThrowCompletionRoute(
                exceptionHandler.HandlerAction,
                exceptionHandlers,
                finallyRegions,
                instructions,
                actionToBlock,
                pending,
                seen);
        }

        var ordered = pending
            .OrderBy(item => item.Source.Value)
            .ThenBy(item => item.Edge.Kind)
            .ThenBy(item => item.Edge.Target.Value)
            .ThenBy(item => item.Edge.StackSource.Value)
            .ToArray();
        var edges = new Avm1CompletionEdge[ordered.Length];
        var cursor = 0;
        while (cursor < ordered.Length)
        {
            var source = ordered[cursor].Source;
            var start = cursor;
            while (cursor < ordered.Length && ordered[cursor].Source == source)
            {
                edges[cursor] = ordered[cursor].Edge;
                cursor++;
            }

            blocks[source.Value] = blocks[source.Value] with
            {
                CompletionEdgeStart = new CompletionEdgeIndex(start),
                CompletionEdgeCount = cursor - start
            };
        }

        return edges;
    }

    private static ActionIndex[] CreateReturnHandlers(Avm1InstructionTable instructions)
    {
        var handlers = Enumerable.Repeat(ActionIndex.Invalid, instructions.Count).ToArray();
        foreach (var region in instructions.TryRegions.OrderByDescending(GetTryRegionSpan))
        {
            if (!region.FinallyEnterAction.IsValid)
                continue;

            AssignActionHandler(
                handlers,
                region.TryBodyStartAction,
                region.TryExitAction,
                region.FinallyEnterAction);
            AssignActionHandler(
                handlers,
                region.CatchBodyStartAction,
                region.CatchExitAction,
                region.FinallyEnterAction);
        }

        return handlers;
    }

    private static void AddReturnCompletionRoute(
        ActionIndex sourceAction,
        ActionIndex firstFinallyEnter,
        ActionIndex[] returnHandlers,
        Dictionary<int, Avm1TryInstructionRegion> finallyRegions,
        Dictionary<int, BlockIndex> actionToBlock,
        List<(BlockIndex Source, Avm1CompletionEdge Edge)> pending,
        HashSet<(int Source, Avm1CompletionKind Kind, int Target, int StackSource)> seen)
    {
        var finallyEnter = firstFinallyEnter;
        var visited = new HashSet<int>();
        while (finallyEnter.IsValid && visited.Add(finallyEnter.Value))
        {
            if (!finallyRegions.TryGetValue(finallyEnter.Value, out var region))
                return;

            TryAddCompletionEdge(
                sourceAction,
                finallyEnter,
                ActionIndex.Invalid,
                Avm1CompletionKind.Return,
                actionToBlock,
                pending,
                seen);

            sourceAction = region.FinallyExitAction;
            finallyEnter = sourceAction.IsValid && sourceAction.Value < returnHandlers.Length
                ? returnHandlers[sourceAction.Value]
                : ActionIndex.Invalid;
        }

        TryAddCompletionEdge(
            sourceAction,
            ActionIndex.Invalid,
            ActionIndex.Invalid,
            Avm1CompletionKind.Return,
            actionToBlock,
            pending,
            seen);
    }

    private static void AddThrowCompletionRoute(
        ActionIndex firstFinallyEnter,
        Avm1ExceptionHandler[] exceptionHandlers,
        Dictionary<int, Avm1TryInstructionRegion> finallyRegions,
        Avm1InstructionTable instructions,
        Dictionary<int, BlockIndex> actionToBlock,
        List<(BlockIndex Source, Avm1CompletionEdge Edge)> pending,
        HashSet<(int Source, Avm1CompletionKind Kind, int Target, int StackSource)> seen)
    {
        var finallyEnter = firstFinallyEnter;
        var visited = new HashSet<int>();
        while (finallyEnter.IsValid && visited.Add(finallyEnter.Value))
        {
            if (!finallyRegions.TryGetValue(finallyEnter.Value, out var region))
                return;

            var sourceAction = region.FinallyExitAction;
            var nextHandler = sourceAction.IsValid && sourceAction.Value < exceptionHandlers.Length
                ? exceptionHandlers[sourceAction.Value]
                : Avm1ExceptionHandler.Invalid;
            if (!nextHandler.HandlerAction.IsValid)
            {
                TryAddCompletionEdge(
                    sourceAction,
                    ActionIndex.Invalid,
                    ActionIndex.Invalid,
                    Avm1CompletionKind.Throw,
                    actionToBlock,
                    pending,
                    seen);
                return;
            }

            TryAddCompletionEdge(
                sourceAction,
                nextHandler.HandlerAction,
                nextHandler.StackSourceAction,
                Avm1CompletionKind.Throw,
                actionToBlock,
                pending,
                seen);

            if (instructions[nextHandler.HandlerAction].Kind is Avm1InstructionKind.CatchEnter)
                return;

            if (instructions[nextHandler.HandlerAction].Kind is not Avm1InstructionKind.FinallyEnter)
                return;

            finallyEnter = nextHandler.HandlerAction;
        }
    }

    private static void TryAddCompletionEdge(
        ActionIndex sourceAction,
        ActionIndex targetAction,
        ActionIndex stackSourceAction,
        Avm1CompletionKind kind,
        Dictionary<int, BlockIndex> actionToBlock,
        List<(BlockIndex Source, Avm1CompletionEdge Edge)> pending,
        HashSet<(int Source, Avm1CompletionKind Kind, int Target, int StackSource)> seen)
    {
        if (!sourceAction.IsValid ||
            !actionToBlock.TryGetValue(sourceAction.Value, out var source))
        {
            return;
        }

        var target = targetAction.IsValid && actionToBlock.TryGetValue(targetAction.Value, out var targetBlock)
            ? targetBlock
            : BlockIndex.Invalid;
        var stackSource = stackSourceAction.IsValid &&
            actionToBlock.TryGetValue(stackSourceAction.Value, out var stackSourceBlock)
                ? stackSourceBlock
                : BlockIndex.Invalid;
        if (!seen.Add((source.Value, kind, target.Value, stackSource.Value)))
            return;

        pending.Add((source, new Avm1CompletionEdge(kind, target, stackSource)));
    }

    private static void AssignActionHandler(
        ActionIndex[] handlers,
        ActionIndex startAction,
        ActionIndex endAction,
        ActionIndex handlerAction)
    {
        if (!startAction.IsValid || !endAction.IsValid || !handlerAction.IsValid)
            return;

        var start = Math.Max(0, startAction.Value);
        var end = Math.Min(handlers.Length, endAction.Value);
        for (var i = start; i < end; i++)
            handlers[i] = handlerAction;
    }

    private static int GetTryRegionSpan(Avm1TryInstructionRegion region)
    {
        var end = region.FinallyExitAction.IsValid
            ? region.FinallyExitAction
            : region.CatchExitAction.IsValid
                ? region.CatchExitAction
                : region.TryExitAction;
        return end.Value - region.EnterAction.Value;
    }

    private static void AssignExceptionHandler(
        Avm1ExceptionHandler[] handlers,
        ActionIndex startAction,
        ActionIndex endAction,
        ActionIndex handlerAction,
        ActionIndex stackSourceAction)
    {
        if (!startAction.IsValid ||
            !endAction.IsValid ||
            !handlerAction.IsValid)
        {
            return;
        }

        var start = Math.Max(0, startAction.Value);
        var end = Math.Min(handlers.Length, endAction.Value);
        var handler = new Avm1ExceptionHandler(handlerAction, stackSourceAction);
        for (var i = start; i < end; i++)
            handlers[i] = handler;
    }

    private static bool MayThrow(Action action)
    {
        if (action is Avm1WithEndAction or
            Avm1TryEndAction or
            Avm1CatchStartAction or
            Avm1CatchEndAction or
            Avm1FinallyStartAction or
            Avm1FinallyEndAction)
        {
            return false;
        }

        return action.Opcode is not (
            ActionOpcode.End or
            ActionOpcode.Pop or
            ActionOpcode.Return or
            ActionOpcode.TypeOf or
            ActionOpcode.PushDuplicate or
            ActionOpcode.StackSwap or
            ActionOpcode.StoreRegister or
            ActionOpcode.ConstantPool or
            ActionOpcode.DefineFunction2 or
            ActionOpcode.Try or
            ActionOpcode.Push or
            ActionOpcode.Jump or
            ActionOpcode.DefineFunction or
            ActionOpcode.If or
            ActionOpcode.WaitForFrame or
            ActionOpcode.WaitForFrame2);
    }

    private static Avm1BlockTerminatorKind ClassifyTerminator(Avm1Instruction instruction) =>
        instruction.Action switch
        {
            ActionIf => Avm1BlockTerminatorKind.ConditionalBranch,
            ActionWaitForFrame or ActionWaitForFrame2 =>
                Avm1BlockTerminatorKind.ConditionalBranch,
            ActionJump => Avm1BlockTerminatorKind.Jump,
            ActionReturn => Avm1BlockTerminatorKind.Return,
            ActionThrow => Avm1BlockTerminatorKind.Throw,
            ActionEnd => Avm1BlockTerminatorKind.End,
            _ => Avm1BlockTerminatorKind.FallThrough
        };

    private static BlockIndex FindBlockAtAction(Dictionary<int, BlockIndex> actionToBlock, ActionIndex action)
    {
        return actionToBlock.TryGetValue(action.Value, out var block) ? block : BlockIndex.Invalid;
    }

    private readonly record struct Avm1ExceptionHandler(
        ActionIndex HandlerAction,
        ActionIndex StackSourceAction)
    {
        public static readonly Avm1ExceptionHandler Invalid = new(
            ActionIndex.Invalid,
            ActionIndex.Invalid);
    }
}

public enum Avm1BlockTerminatorKind
{
    FallThrough,
    ConditionalBranch,
    Jump,
    Return,
    Throw,
    End
}

public readonly record struct Avm1BasicBlock(
    BlockIndex Index,
    ActionIndex StartAction,
    ActionIndex EndAction,
    int StartOffset,
    int EndOffset,
    Avm1BlockTerminatorKind Terminator,
    BlockIndex FirstSuccessor,
    BlockIndex SecondSuccessor,
    BlockIndex ExceptionSuccessor,
    BlockIndex ExceptionStackSource,
    CompletionEdgeIndex CompletionEdgeStart,
    int CompletionEdgeCount);

public enum Avm1CompletionKind
{
    Return,
    Throw
}

public readonly record struct Avm1CompletionEdge(
    Avm1CompletionKind Kind,
    BlockIndex Target,
    BlockIndex StackSource);
