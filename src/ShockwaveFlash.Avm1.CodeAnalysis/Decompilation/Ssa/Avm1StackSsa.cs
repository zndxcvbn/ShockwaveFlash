using System.Globalization;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf2;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;

namespace ShockwaveFlash.Avm1.Decompilation.Ssa;

public static class Avm1StackSsa
{
    internal static Avm1StackIr Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1FlowGraph flowGraph,
        Avm1StackDepthAnalysis stackDepthAnalysis,
        Avm1StackIr linearIr,
        bool takeLinearOwnership = false)
    {
        return new Builder(
            instructions,
            cfg,
            flowGraph,
            stackDepthAnalysis,
            linearIr,
            takeLinearOwnership).Build();
    }

    private sealed class Builder
    {
        private readonly Avm1InstructionTable _instructions;
        private readonly Avm1ControlFlowGraph _cfg;
        private readonly Avm1FlowGraph _flowGraph;
        private readonly Avm1StackDepthAnalysis _stackDepthAnalysis;
        private readonly bool _usesFlowContexts;
        private readonly Avm1StackIrInstruction[] _rows;
        private readonly Avm1StackValue[] _baseValues;
        private readonly bool _ownsBaseValues;
        private List<Avm1StackValue>? _additionalValues;
        private readonly RowRange[] _rowsByAction;
        private readonly bool[] _hasMultipleContextsByBlock;
        private readonly Dictionary<int, ValueIndex[]> _sideOperands = [];
        private readonly Dictionary<(int Row, int Context), ProjectionBuilder> _projections = [];
        private readonly Dictionary<FlowPredecessor, ValueIndex[]>[] _incomingByPoint;
        private readonly ValueIndex[][] _entryStacks;
        private readonly bool[] _hasEntry;
        private readonly bool[] _processed;
        private readonly bool[] _queued;
        private readonly bool[] _reachableFromEntry;
        private readonly Queue<int> _worklist = [];
        private readonly Dictionary<(int Block, Avm1FlowState State, int Context, int Slot), PhiBuilder> _phis = [];
        private readonly Dictionary<(int Action, int Ordinal), ValueIndex> _unknowns = [];
        private readonly Dictionary<int, EnumerationPredicate> _enumerationPredicates = [];
        private readonly Dictionary<(int Block, Avm1FlowState State, int Context, int Predecessor, Avm1FlowState PredecessorState, int PredecessorContext, int Slot), ValueIndex> _mergeUnknowns = [];
        private readonly List<Avm1Diagnostic> _diagnostics = [];
        private readonly HashSet<(int Action, string Message)> _diagnosticKeys = [];
        private bool _captureRows;
        private ValueIndex _branchCondition = ValueIndex.Invalid;

        public Builder(
            Avm1InstructionTable instructions,
            Avm1ControlFlowGraph cfg,
            Avm1FlowGraph flowGraph,
            Avm1StackDepthAnalysis stackDepthAnalysis,
            Avm1StackIr linearIr,
            bool takeLinearOwnership)
        {
            _instructions = instructions;
            _cfg = cfg;
            _flowGraph = flowGraph;
            _stackDepthAnalysis = stackDepthAnalysis;
            _usesFlowContexts = cfg.CompletionEdges.Count != 0;
            _captureRows = !_usesFlowContexts;
            _rows = takeLinearOwnership
                ? linearIr.InstructionArray
                : linearIr.InstructionArray.ToArray();
            _baseValues = linearIr.ValueArray;
            _ownsBaseValues = takeLinearOwnership;
            _rowsByAction = CreateRowsByAction(instructions.Count, _rows);
            _hasMultipleContextsByBlock = FindBlocksWithMultipleContexts(cfg, flowGraph);
            var pointCount = _usesFlowContexts
                ? flowGraph.Count
                : cfg.Count;
            _incomingByPoint = new Dictionary<FlowPredecessor, ValueIndex[]>[pointCount];
            _entryStacks = new ValueIndex[pointCount][];
            _hasEntry = new bool[pointCount];
            _processed = new bool[pointCount];
            _queued = new bool[pointCount];
            var dominators = cfg.Blocks.Any(block => block.ExceptionSuccessor.IsValid)
                ? Avm1DominatorTree.BuildIncludingExceptionEdges(cfg)
                : Avm1DominatorTree.Build(cfg);
            _reachableFromEntry = Enumerable.Range(0, cfg.Count)
                .Select(index => dominators.IsReachable(new BlockIndex(index)))
                .ToArray();

            for (var i = 0; i < pointCount; i++)
            {
                _incomingByPoint[i] = [];
                _entryStacks[i] = [];
            }
        }

        private static bool[] FindBlocksWithMultipleContexts(
            Avm1ControlFlowGraph cfg,
            Avm1FlowGraph flowGraph)
        {
            var result = new bool[cfg.Count];
            var firstContext = Enumerable.Repeat(FlowContextIndex.Invalid, cfg.Count).ToArray();
            foreach (var point in flowGraph.Points)
            {
                ref var seen = ref firstContext[point.Block.Value];
                if (!seen.IsValid)
                    seen = point.Context;
                else if (seen != point.Context)
                    result[point.Block.Value] = true;
            }

            return result;
        }

        public Avm1StackIr Build()
        {
            if (_cfg.Count == 0)
                return Avm1StackIr.Create(CreateValues(), _rows, [], [], []);

            if (_usesFlowContexts && _flowGraph.TryGetPoint(
                new BlockIndex(0),
                _flowGraph.NormalContext,
                out var entryPoint))
            {
                SetIncoming(
                    entryPoint.Value,
                    new BlockIndex(0),
                    Avm1FlowState.Normal,
                    _flowGraph.NormalContext,
                    FlowPredecessor.Invalid,
                    []);
            }
            else
            {
                SetIncoming(
                    new BlockIndex(0),
                    Avm1FlowState.Normal,
                    FlowPredecessor.Invalid,
                    []);
            }
            DrainWorklist();

            if (!_usesFlowContexts)
            {
                for (var block = 0; block < _cfg.Count; block++)
                {
                    if (HasProcessedState(block))
                        continue;

                    SetIncoming(
                        new BlockIndex(block),
                        Avm1FlowState.Normal,
                        FlowPredecessor.Invalid,
                        CreateSeedStack(block));
                    DrainWorklist();
                }
            }

            if (_usesFlowContexts)
                CaptureRows();
            return CreateResult();
        }

        private static RowRange[] CreateRowsByAction(
            int actionCount,
            Avm1StackIrInstruction[] rows)
        {
            var result = new RowRange[actionCount];
            var rowIndex = 0;
            while (rowIndex < rows.Length)
            {
                var action = rows[rowIndex].Action;
                var start = rowIndex;
                do
                {
                    rowIndex++;
                }
                while (rowIndex < rows.Length && rows[rowIndex].Action == action);

                if (action.IsValid && action.Value < result.Length)
                    result[action.Value] = new RowRange(start, rowIndex - start);
            }

            return result;
        }

        private void DrainWorklist()
        {
            while (_worklist.TryDequeue(out var point))
            {
                _queued[point] = false;
                ProcessPoint(point);
            }
        }

        private void ProcessPoint(int point)
        {
            if (_usesFlowContexts)
            {
                ProcessContextPoint(new FlowPointIndex(point));
                return;
            }

            var blockIndex = new BlockIndex(point);
            const Avm1FlowState state = Avm1FlowState.Normal;
            var block = _cfg[blockIndex];
            var stack = new List<ValueIndex>(_entryStacks[point]);
            _branchCondition = ValueIndex.Invalid;

            for (var action = block.StartAction.Value; action < block.EndAction.Value; action++)
                ProcessAction(
                    new ActionIndex(action),
                    stack,
                    FlowContextIndex.Invalid,
                    projectResults: false);

            _processed[point] = true;
            var exitStack = stack.ToArray();
            PropagateNormal(
                block.FirstSuccessor,
                blockIndex,
                GetNormalExitStack(block, block.FirstSuccessor, exitStack));
            PropagateNormal(
                block.SecondSuccessor,
                blockIndex,
                GetNormalExitStack(block, block.SecondSuccessor, exitStack));
            if (block.ExceptionSuccessor.IsValid)
            {
                SetIncoming(
                    block.ExceptionSuccessor,
                    Avm1FlowState.Normal,
                    new FlowPredecessor(blockIndex, state),
                    GetExceptionalExitStack(block, state, exitStack));
            }
        }

        private void ProcessContextPoint(FlowPointIndex pointIndex)
        {
            var point = _flowGraph[pointIndex];
            var context = _flowGraph.GetContext(point.Context);
            var block = _cfg[point.Block];
            var stack = new List<ValueIndex>(_entryStacks[pointIndex.Value]);
            var projectResults = _hasMultipleContextsByBlock[point.Block.Value];
            _branchCondition = ValueIndex.Invalid;

            for (var action = block.StartAction.Value; action < block.EndAction.Value; action++)
                ProcessAction(new ActionIndex(action), stack, point.Context, projectResults);

            _processed[pointIndex.Value] = true;
            var exitStack = stack.ToArray();
            foreach (var transition in _flowGraph.GetTransitions(pointIndex))
            {
                if (!transition.Target.IsValid)
                    continue;

                var targetPoint = _flowGraph[transition.Target];
                var transitionStack = transition.Kind switch
                {
                    Avm1FlowTransitionKind.Exception => GetExceptionalExitStack(
                        block,
                        transition.StackSource,
                        transition.TargetContext,
                        exitStack),
                    Avm1FlowTransitionKind.Completion => GetUnwindStack(
                        transition.StackSource,
                        transition.TargetContext,
                        exitStack),
                    _ => GetNormalExitStack(block, targetPoint.Block, exitStack)
                };
                var targetState = _flowGraph.GetContext(targetPoint.Context).State;
                SetIncoming(
                    transition.Target.Value,
                    targetPoint.Block,
                    targetState,
                    targetPoint.Context,
                    new FlowPredecessor(
                        point.Block,
                        context.State,
                        point.Context,
                        pointIndex),
                    transitionStack);
            }
        }

        private ValueIndex[] GetExceptionalExitStack(
            Avm1BasicBlock block,
            Avm1FlowState state,
            ValueIndex[] normalExitStack)
        {
            if (block.ExceptionStackSource.IsValid &&
                TryGetEntryStack(block.ExceptionStackSource, state, out var unwindStack))
            {
                return unwindStack;
            }

            var action = new ActionIndex(block.EndAction.Value - 1);
            var rows = _rowsByAction[action.Value];
            if (rows.Count == 0 || normalExitStack.Length == 0)
                return normalExitStack;

            for (var i = rows.Count - 1; i >= 0; i--)
            {
                var result = _rows[rows.Start + i].Result;
                if (!result.IsValid || normalExitStack[^1] != result)
                    continue;

                return normalExitStack[..^1];
            }

            return normalExitStack;
        }

        private ValueIndex[] GetExceptionalExitStack(
            Avm1BasicBlock block,
            BlockIndex stackSource,
            FlowContextIndex targetContext,
            ValueIndex[] normalExitStack)
        {
            if (stackSource.IsValid &&
                TryGetEntryStack(stackSource, targetContext, out var unwindStack))
            {
                return unwindStack;
            }

            var action = new ActionIndex(block.EndAction.Value - 1);
            var rows = _rowsByAction[action.Value];
            if (rows.Count == 0 || normalExitStack.Length == 0)
                return normalExitStack;

            for (var i = rows.Count - 1; i >= 0; i--)
            {
                var result = _rows[rows.Start + i].Result;
                if (!result.IsValid || normalExitStack[^1] != result)
                    continue;

                return normalExitStack[..^1];
            }

            return normalExitStack;
        }

        private ValueIndex[] GetUnwindStack(
            BlockIndex stackSource,
            FlowContextIndex targetContext,
            ValueIndex[] fallback)
        {
            return stackSource.IsValid &&
                TryGetEntryStack(stackSource, targetContext, out var unwindStack)
                    ? unwindStack
                    : fallback;
        }

        private void ProcessAction(
            ActionIndex actionIndex,
            List<ValueIndex> stack,
            FlowContextIndex context,
            bool projectResults)
        {
            var rowIndices = _rowsByAction[actionIndex.Value];
            if (rowIndices.Count == 0)
                return;

            var rowIndex = rowIndices.Start;
            var action = _instructions[actionIndex].Action;
            var underflowOrdinal = 0;

            switch (action)
            {
                case ActionConstantPool or ActionEnd:
                    SetRow(rowIndex, ValueIndex.Invalid);
                    break;

                case ActionNextFrame or ActionPreviousFrame or
                    ActionPlay or ActionStop or ActionToggleQuality or
                    ActionStopSounds:
                    SetRow(rowIndex, ValueIndex.Invalid);
                    break;

                case ActionPush:
                    for (var rowOffset = 0; rowOffset < rowIndices.Count; rowOffset++)
                        stack.Add(_rows[rowIndices.Start + rowOffset].Result);
                    break;

                case ActionPop:
                    if (stack.Count == 0 &&
                        _cfg.TryGetBlockForAction(actionIndex, out var popBlock) &&
                        (Avm1StackDepthAnalysis.IsOptionalMergePop(
                             _instructions,
                             _cfg,
                             popBlock) ||
                         Avm1StackDepthAnalysis.IsTerminalEpiloguePop(
                             _instructions,
                             _cfg,
                             popBlock)))
                    {
                        SetRow(rowIndex, ValueIndex.Invalid);
                    }
                    else
                    {
                        SetRow(
                            rowIndex,
                            ValueIndex.Invalid,
                            Pop(stack, actionIndex, ref underflowOrdinal));
                    }
                    break;

                case ActionPushDuplicate:
                    {
                        var operand = Peek(stack, actionIndex, ref underflowOrdinal);
                        var result = GetResult(
                            rowIndex,
                            context,
                            projectResults,
                            operand,
                            ValueIndex.Invalid);
                        stack.Add(result);
                        SetRow(rowIndex, result, operand);
                        break;
                    }

                case ActionStackSwap:
                    {
                        var top = Pop(stack, actionIndex, ref underflowOrdinal);
                        var next = Pop(stack, actionIndex, ref underflowOrdinal);
                        stack.Add(top);
                        stack.Add(next);
                        SetRow(rowIndex, ValueIndex.Invalid, top, next);
                        break;
                    }

                case ActionStoreRegister:
                    {
                        var value = Peek(stack, actionIndex, ref underflowOrdinal);
                        SetRow(rowIndex, value, value);
                        break;
                    }

                case ActionGetVariable:
                    _ = UnaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _);
                    break;

                case ActionSetVariable:
                    BinaryNoResult(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionGetMember:
                    _ = BinaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _,
                        out _);
                    break;

                case ActionSetMember:
                    {
                        var value = Pop(stack, actionIndex, ref underflowOrdinal);
                        var name = Pop(stack, actionIndex, ref underflowOrdinal);
                        var target = Pop(stack, actionIndex, ref underflowOrdinal);
                        SetRow(rowIndex, ValueIndex.Invalid, target, name, value, 3);
                        break;
                    }

                case ActionGetProperty:
                    {
                        var property = Pop(stack, actionIndex, ref underflowOrdinal);
                        var target = Pop(stack, actionIndex, ref underflowOrdinal);
                        var result = _rows[rowIndex].Result;
                        stack.Add(result);
                        SetRow(rowIndex, result, target, property, operandCount: 2);
                        break;
                    }

                case ActionSetProperty or ActionCloneSprite:
                    {
                        var value = Pop(stack, actionIndex, ref underflowOrdinal);
                        var second = Pop(stack, actionIndex, ref underflowOrdinal);
                        var target = Pop(stack, actionIndex, ref underflowOrdinal);
                        SetRow(
                            rowIndex,
                            ValueIndex.Invalid,
                            target,
                            second,
                            value,
                            operandCount: 3);
                        break;
                    }

                case ActionRemoveSprite:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal),
                        operandCount: 1);
                    break;

                case ActionStartDrag:
                    StartDrag(
                        rowIndex,
                        actionIndex,
                        stack,
                        ref underflowOrdinal);
                    break;

                case ActionEndDrag:
                    SetRow(rowIndex, ValueIndex.Invalid, operandCount: 0);
                    break;

                case ActionSetTarget:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        _rows[rowIndex].Operand0,
                        operandCount: 1);
                    break;

                case ActionSetTarget2:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal),
                        operandCount: 1);
                    break;

                case ActionDelete:
                    _ = BinaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _,
                        out _);
                    break;

                case ActionDelete2:
                    _ = UnaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _);
                    break;

                case ActionDefineLocal:
                    BinaryNoResult(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionDefineLocal2:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionTrace:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionGotoFrame2:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionCall:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionGetURL2:
                    BinaryNoResult(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionGetTime:
                    {
                        var result = _rows[rowIndex].Result;
                        stack.Add(result);
                        SetRow(rowIndex, result);
                        break;
                    }

                case ActionWith:
                    SetRow(
                        rowIndex,
                        ValueIndex.Invalid,
                        Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case Avm1WithEndAction:
                    SetRow(rowIndex, ValueIndex.Invalid);
                    break;

                case ActionTry:
                case Avm1TryEndAction:
                case Avm1CatchStartAction:
                case Avm1CatchEndAction:
                case Avm1FinallyStartAction:
                case Avm1FinallyEndAction:
                    SetRow(rowIndex, ValueIndex.Invalid);
                    break;

                case ActionInitArray:
                    InitArray(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionInitObject:
                    InitObject(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionNewObject:
                    CallLike(rowIndex, actionIndex, stack, ref underflowOrdinal, hasTarget: false);
                    break;

                case ActionNewMethod:
                    CallLike(rowIndex, actionIndex, stack, ref underflowOrdinal, hasTarget: true);
                    break;

                case ActionCallFunction:
                    CallLike(rowIndex, actionIndex, stack, ref underflowOrdinal, hasTarget: false);
                    break;

                case ActionCallMethod:
                    CallLike(rowIndex, actionIndex, stack, ref underflowOrdinal, hasTarget: true);
                    break;

                case ActionEnumerate or ActionEnumerate2:
                    _ = UnaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _);
                    break;

                case ActionDefineFunction or ActionDefineFunction2:
                    {
                        var result = _rows[rowIndex].Result;
                        stack.Add(result);
                        SetRow(rowIndex, result);
                        break;
                    }

                case ActionCastOp:
                    _ = BinaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _,
                        out _);
                    break;

                case ActionExtends:
                    BinaryNoResult(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionImplementsOp:
                    Implements(rowIndex, actionIndex, stack, ref underflowOrdinal);
                    break;

                case ActionReturn:
                    SetRow(rowIndex, ValueIndex.Invalid, Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionThrow:
                    SetRow(rowIndex, ValueIndex.Invalid, Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionIf:
                    _branchCondition = Pop(stack, actionIndex, ref underflowOrdinal);
                    SetRow(rowIndex, ValueIndex.Invalid, _branchCondition);
                    break;

                case ActionWaitForFrame:
                    SetRow(rowIndex, ValueIndex.Invalid);
                    break;

                case ActionWaitForFrame2:
                    SetRow(rowIndex, ValueIndex.Invalid, Pop(stack, actionIndex, ref underflowOrdinal));
                    break;

                case ActionJump:
                    SetRow(rowIndex, ValueIndex.Invalid);
                    break;

                case ActionNot or ActionIncrement or ActionDecrement:
                    {
                        var unaryResult = UnaryResult(
                            rowIndex,
                            actionIndex,
                            stack,
                            context,
                            projectResults,
                            ref underflowOrdinal,
                            out var unaryOperand);
                        if (action is ActionNot &&
                            _enumerationPredicates.TryGetValue(
                                unaryOperand.Value,
                                out var predicate))
                        {
                            _enumerationPredicates[unaryResult.Value] =
                                predicate with { TrueMeansEnd = !predicate.TrueMeansEnd };
                        }
                        break;
                    }

                case ActionStringExtract or ActionMBStringExtract:
                    TernaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        ref underflowOrdinal);
                    break;

                case ActionTypeOf or ActionTargetPath or ActionToNumber or
                    ActionToString or ActionToInteger or ActionRandomNumber or
                    ActionStringLength or ActionMBStringLength or
                    ActionCharToAscii or ActionAsciiToChar or
                    ActionMBCharToAscii or ActionMBAsciiToChar:
                    _ = UnaryResult(
                        rowIndex,
                        actionIndex,
                        stack,
                        context,
                        projectResults: false,
                        ref underflowOrdinal,
                        out _);
                    break;

                case ActionAdd or ActionAdd2 or ActionStringAdd or ActionSubtract or ActionMultiply or ActionDivide or
                    ActionModulo or ActionEquals or ActionEquals2 or ActionStringEquals or ActionLess or ActionLess2 or
                    ActionStringLess or ActionGreater or ActionStringGreater or ActionAnd or ActionOr or ActionBitAnd or
                    ActionBitOr or ActionBitXor or ActionBitLShift or ActionBitRShift or ActionBitURShift or
                    ActionStrictEquals or ActionInstanceOf:
                    {
                        var binaryResult = BinaryResult(
                            rowIndex,
                            actionIndex,
                            stack,
                            context,
                            projectResults: action is not ActionInstanceOf && projectResults,
                            ref underflowOrdinal,
                            out var left,
                            out var right);
                        if (action is ActionEquals or ActionEquals2 or ActionStrictEquals &&
                            TryCreateEnumerationEndPredicate(left, right, out var predicate))
                        {
                            _enumerationPredicates[binaryResult.Value] = predicate;
                        }
                        break;
                    }

                default:
                    SetRow(rowIndex, _rows[rowIndex].Result);
                    break;
            }
        }

        private ValueIndex UnaryResult(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            FlowContextIndex context,
            bool projectResults,
            ref int underflowOrdinal,
            out ValueIndex operand)
        {
            operand = Pop(stack, action, ref underflowOrdinal);
            var result = GetResult(
                rowIndex,
                context,
                projectResults,
                operand,
                ValueIndex.Invalid);
            stack.Add(result);
            SetRow(rowIndex, result, operand);
            return result;
        }

        private ValueIndex BinaryResult(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            FlowContextIndex context,
            bool projectResults,
            ref int underflowOrdinal,
            out ValueIndex left,
            out ValueIndex right)
        {
            right = Pop(stack, action, ref underflowOrdinal);
            left = Pop(stack, action, ref underflowOrdinal);
            var result = GetResult(rowIndex, context, projectResults, left, right);
            stack.Add(result);
            SetRow(rowIndex, result, left, right);
            return result;
        }

        private void TernaryResult(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal)
        {
            var third = Pop(stack, action, ref underflowOrdinal);
            var second = Pop(stack, action, ref underflowOrdinal);
            var first = Pop(stack, action, ref underflowOrdinal);
            var result = _rows[rowIndex].Result;
            stack.Add(result);
            SetRow(rowIndex, result, first, second, third, operandCount: 3);
        }

        private void StartDrag(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal)
        {
            var target = Pop(stack, action, ref underflowOrdinal);
            var lockCenter = Pop(stack, action, ref underflowOrdinal);
            var constraint = Pop(stack, action, ref underflowOrdinal);
            if (!TryGetStaticTruthiness(constraint, out var constrained))
            {
                AddDiagnostic(
                    action,
                    "StartDrag constraint flag is dynamic; preserving the action " +
                    "as opaque with the minimum three-value stack effect.");
                SetRow(rowIndex, ValueIndex.Invalid);
                return;
            }

            var operands = new List<ValueIndex>(constrained ? 6 : 2)
            {
                target,
                lockCenter
            };
            if (constrained)
            {
                var bottom = Pop(stack, action, ref underflowOrdinal);
                var right = Pop(stack, action, ref underflowOrdinal);
                var top = Pop(stack, action, ref underflowOrdinal);
                var left = Pop(stack, action, ref underflowOrdinal);
                operands.Add(left);
                operands.Add(top);
                operands.Add(right);
                operands.Add(bottom);
            }

            _sideOperands[rowIndex] = operands.ToArray();
            SetRow(
                rowIndex,
                ValueIndex.Invalid,
                operandCount: operands.Count);
        }

        private bool TryGetStaticTruthiness(ValueIndex value, out bool result)
        {
            result = false;
            if (!value.IsValid || value.Value >= ValueCount)
                return false;
            return GetValue(value.Value).TryGetStaticTruthiness(out result);
        }

        private ValueIndex GetResult(
            int rowIndex,
            FlowContextIndex context,
            bool projectResult,
            ValueIndex operand0,
            ValueIndex operand1)
        {
            var canonical = _rows[rowIndex].Result;
            if (!projectResult || !context.IsValid || _captureRows || !canonical.IsValid)
                return canonical;
            if (!CanProjectResult(rowIndex, operand0, operand1))
                return canonical;

            var key = (rowIndex, context.Value);
            if (!_projections.TryGetValue(key, out var projection))
            {
                var result = AddValue(
                    Avm1StackValueKind.Projection,
                    $"project(v{canonical.Value},c{context.Value})");
                projection = new ProjectionBuilder(result, rowIndex, context);
                _projections.Add(key, projection);
            }

            projection.SetOperands(operand0, operand1);
            return projection.Result;
        }

        private bool CanProjectResult(
            int rowIndex,
            ValueIndex operand0,
            ValueIndex operand1)
        {
            return _rows[rowIndex].Op switch
            {
                Avm1StackIrOp.Duplicate => true,
                Avm1StackIrOp.Not or
                Avm1StackIrOp.Increment or
                Avm1StackIrOp.Decrement => IsReplaySafeValue(operand0),
                Avm1StackIrOp.Binary =>
                    IsReplaySafeValue(operand0) && IsReplaySafeValue(operand1),
                _ => false
            };
        }

        private bool IsReplaySafeValue(ValueIndex value)
        {
            if (!value.IsValid || value.Value >= ValueCount)
                return false;

            return GetValue(value.Value).Kind is
                Avm1StackValueKind.Constant or
                Avm1StackValueKind.Projection;
        }

        private void BinaryNoResult(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal)
        {
            var right = Pop(stack, action, ref underflowOrdinal);
            var left = Pop(stack, action, ref underflowOrdinal);
            SetRow(rowIndex, ValueIndex.Invalid, left, right);
        }

        private void InitArray(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal)
        {
            var countValue = Pop(stack, action, ref underflowOrdinal);
            var count = GetConstantCount(countValue);
            var operands = PopValues(stack, action, count, ref underflowOrdinal);
            var result = _rows[rowIndex].Result;
            stack.Add(result);
            _sideOperands[rowIndex] = operands;
            SetRow(rowIndex, result, countValue, ValueIndex.Invalid, ValueIndex.Invalid, count);
        }

        private void InitObject(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal)
        {
            var countValue = Pop(stack, action, ref underflowOrdinal);
            var count = GetConstantCount(countValue);
            var operands = new ValueIndex[count * 2];
            for (var i = 0; i < count; i++)
            {
                operands[i * 2 + 1] = Pop(stack, action, ref underflowOrdinal);
                operands[i * 2] = Pop(stack, action, ref underflowOrdinal);
            }

            var result = _rows[rowIndex].Result;
            stack.Add(result);
            _sideOperands[rowIndex] = operands;
            SetRow(rowIndex, result, countValue, ValueIndex.Invalid, ValueIndex.Invalid, count);
        }

        private void CallLike(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal,
            bool hasTarget)
        {
            var name = Pop(stack, action, ref underflowOrdinal);
            var target = hasTarget
                ? Pop(stack, action, ref underflowOrdinal)
                : ValueIndex.Invalid;
            var countValue = Pop(stack, action, ref underflowOrdinal);
            var count = GetConstantCount(countValue);
            var operands = PopValues(stack, action, count, ref underflowOrdinal);
            var result = _rows[rowIndex].Result;
            stack.Add(result);
            _sideOperands[rowIndex] = operands;

            if (hasTarget)
                SetRow(rowIndex, result, name, target, ValueIndex.Invalid, count);
            else
                SetRow(rowIndex, result, name, countValue, ValueIndex.Invalid, count);
        }

        private void Implements(
            int rowIndex,
            ActionIndex action,
            List<ValueIndex> stack,
            ref int underflowOrdinal)
        {
            var constructor = Pop(stack, action, ref underflowOrdinal);
            var countValue = Pop(stack, action, ref underflowOrdinal);
            var count = GetConstantCount(countValue);
            _sideOperands[rowIndex] = PopValues(stack, action, count, ref underflowOrdinal);
            SetRow(
                rowIndex,
                ValueIndex.Invalid,
                constructor,
                countValue,
                ValueIndex.Invalid,
                count);
        }

        private ValueIndex[] PopValues(
            List<ValueIndex> stack,
            ActionIndex action,
            int count,
            ref int underflowOrdinal)
        {
            var result = new ValueIndex[count];
            for (var i = 0; i < result.Length; i++)
                result[i] = Pop(stack, action, ref underflowOrdinal);
            return result;
        }

        private int GetConstantCount(ValueIndex value)
        {
            if (!value.IsValid || value.Value >= ValueCount)
                return 0;
            return GetValue(value.Value).GetPositiveInteger();
        }

        private ValueIndex Pop(
            List<ValueIndex> stack,
            ActionIndex action,
            ref int underflowOrdinal)
        {
            if (stack.Count > 0)
            {
                var last = stack.Count - 1;
                var value = stack[last];
                stack.RemoveAt(last);
                return value;
            }

            return GetUnknown(action, underflowOrdinal++, "Stack underflow; inserted unknown value.");
        }

        private ValueIndex Peek(
            List<ValueIndex> stack,
            ActionIndex action,
            ref int underflowOrdinal)
        {
            if (stack.Count > 0)
                return stack[^1];

            return GetUnknown(action, underflowOrdinal++, "Stack underflow on peek; inserted unknown value.");
        }

        private ValueIndex GetUnknown(ActionIndex action, int ordinal, string message)
        {
            var key = (action.Value, ordinal);
            if (!_unknowns.TryGetValue(key, out var value))
            {
                value = AddValue(Avm1StackValueKind.Unknown, "unknown");
                _unknowns.Add(key, value);
            }

            AddDiagnostic(action, message);
            return value;
        }

        private ValueIndex[] CreateSeedStack(int block)
        {
            if (block >= _stackDepthAnalysis.HasKnownEntry.Count ||
                !_stackDepthAnalysis.HasKnownEntry[block] ||
                _stackDepthAnalysis.EntryDepths[block] <= 0)
            {
                return [];
            }

            var depth = _stackDepthAnalysis.EntryDepths[block];
            var result = new ValueIndex[depth];
            var action = _cfg[new BlockIndex(block)].StartAction;
            for (var slot = 0; slot < depth; slot++)
                result[slot] = GetUnknown(action, slot, "Unreachable block has an unknown incoming stack value.");
            return result;
        }

        private ValueIndex[] CreateSeedStack(Avm1FlowPoint point)
        {
            var depth = _stackDepthAnalysis.GetFlowEntryDepth(point.Index);
            if (depth <= 0)
                return [];

            var result = new ValueIndex[depth];
            var action = _cfg[point.Block].StartAction;
            for (var slot = 0; slot < depth; slot++)
                result[slot] = GetUnknown(action, slot, "Unreachable block has an unknown incoming stack value.");
            return result;
        }

        private void PropagateNormal(
            BlockIndex target,
            BlockIndex source,
            ValueIndex[] stack)
        {
            SetIncoming(
                target,
                Avm1FlowState.Normal,
                new FlowPredecessor(source, Avm1FlowState.Normal),
                stack);
        }

        private ValueIndex[] GetNormalExitStack(
            Avm1BasicBlock block,
            BlockIndex target,
            ValueIndex[] stack)
        {
            if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
                !target.IsValid ||
                !_enumerationPredicates.TryGetValue(
                    _branchCondition.Value,
                    out var predicate))
            {
                return stack;
            }

            bool branchTaken;
            if (target == block.SecondSuccessor)
                branchTaken = true;
            else if (target == block.FirstSuccessor)
                branchTaken = false;
            else
                return stack;

            if (branchTaken == predicate.TrueMeansEnd)
                return stack;

            var result = new ValueIndex[stack.Length + 1];
            stack.CopyTo(result, 0);
            result[^1] = predicate.Stream;
            return result;
        }

        private bool TryGetEntryStack(
            BlockIndex block,
            Avm1FlowState state,
            out ValueIndex[] stack)
        {
            var point = GetPoint(block);
            if (_hasEntry[point])
            {
                stack = _entryStacks[point];
                return true;
            }

            stack = [];
            return false;
        }

        private bool TryGetEntryStack(
            BlockIndex block,
            FlowContextIndex context,
            out ValueIndex[] stack)
        {
            var candidate = context;
            while (candidate.IsValid)
            {
                if (_flowGraph.TryGetPoint(block, candidate, out var point) &&
                    _hasEntry[point.Value])
                {
                    stack = _entryStacks[point.Value];
                    return true;
                }

                candidate = _flowGraph.GetContext(candidate).Parent;
            }

            stack = [];
            return false;
        }

        private void SetIncoming(
            BlockIndex block,
            Avm1FlowState state,
            FlowPredecessor predecessor,
            ValueIndex[] stack)
        {
            if (!block.IsValid || block.Value >= _cfg.Count)
                return;

            var point = GetPoint(block);
            SetIncoming(
                point,
                block,
                state,
                FlowContextIndex.Invalid,
                predecessor,
                stack);
        }

        private void SetIncoming(
            int point,
            BlockIndex block,
            Avm1FlowState state,
            FlowContextIndex context,
            FlowPredecessor predecessor,
            ValueIndex[] stack)
        {
            if (point < 0 || point >= _entryStacks.Length)
                return;
            if (predecessor.Block.IsValid &&
                predecessor.Block.Value < _reachableFromEntry.Length &&
                block.Value < _reachableFromEntry.Length &&
                !_reachableFromEntry[predecessor.Block.Value] &&
                _reachableFromEntry[block.Value])
            {
                return;
            }

            var incoming = _incomingByPoint[point];
            if (incoming.TryGetValue(predecessor, out var previous) && ValuesEqual(previous, stack))
                return;

            incoming[predecessor] = stack.ToArray();
            var merged = MergeIncoming(
                block,
                state,
                context,
                incoming,
                diagnoseDepthMismatch: true);
            if (_hasEntry[point] && ValuesEqual(_entryStacks[point], merged))
                return;

            _entryStacks[point] = merged;
            _hasEntry[point] = true;
            Enqueue(point);
        }

        private ValueIndex[] MergeIncoming(
            BlockIndex block,
            Avm1FlowState state,
            FlowContextIndex context,
            IReadOnlyDictionary<FlowPredecessor, ValueIndex[]> incoming,
            bool diagnoseDepthMismatch)
        {
            var ordered = incoming
                .OrderBy(pair => pair.Key.Block.Value)
                .ThenBy(pair => pair.Key.State)
                .ThenBy(pair => pair.Key.Context.Value)
                .ThenBy(pair => pair.Key.Point.Value)
                .ToArray();
            var minimumDepth = ordered.Min(pair => pair.Value.Length);
            var maximumDepth = ordered.Max(pair => pair.Value.Length);
            var hasDynamicEnumerationSlot = ordered.Any(pair =>
                pair.Value.Skip(minimumDepth).Any(IsEnumerationValue));
            var depth = hasDynamicEnumerationSlot ? maximumDepth : minimumDepth;
            if (diagnoseDepthMismatch &&
                ordered.Any(pair => pair.Value.Length != depth) &&
                !hasDynamicEnumerationSlot &&
                !Avm1StackDepthAnalysis.IsOptionalMergePop(
                    _instructions,
                    _cfg,
                    block))
            {
                AddDiagnostic(
                    _cfg[block].StartAction,
                    $"Inconsistent stack depth while constructing stack SSA for block {block.Value} in {state} flow.");
            }

            var merged = new ValueIndex[depth];
            for (var slot = 0; slot < depth; slot++)
            {
                var incomingValues = ordered
                    .Select(pair => slot < pair.Value.Length
                        ? pair.Value[slot]
                        : GetMergeUnknown(block, state, context, pair.Key, slot))
                    .ToArray();
                var first = incomingValues[0];
                var needsPhi = incomingValues.Any(value => value != first);
                var key = (block.Value, state, context.Value, slot);
                _phis.TryGetValue(key, out var phi);
                if (!needsPhi && phi is null)
                {
                    merged[slot] = first;
                    continue;
                }

                phi ??= CreatePhi(block, state, context, slot);
                phi.SetIncoming(
                    ordered.Select(pair => pair.Key.Block).ToArray(),
                    ordered.Select(pair => pair.Key.State).ToArray(),
                    ordered.Select(pair => pair.Key.Context).ToArray(),
                    incomingValues);
                merged[slot] = phi.Result;
            }

            return merged;
        }

        private bool IsEnumerationValue(ValueIndex value)
        {
            return value.IsValid &&
                value.Value < ValueCount &&
                GetValue(value.Value).IsProducedBy(Avm1StackIrOp.Enumerate);
        }

        private bool TryCreateEnumerationEndPredicate(
            ValueIndex left,
            ValueIndex right,
            out EnumerationPredicate predicate)
        {
            if (IsEnumerationValue(left) && IsNullConstant(right))
            {
                predicate = new EnumerationPredicate(left, TrueMeansEnd: true);
                return true;
            }
            if (IsEnumerationValue(right) && IsNullConstant(left))
            {
                predicate = new EnumerationPredicate(right, TrueMeansEnd: true);
                return true;
            }

            predicate = default;
            return false;
        }

        private bool IsNullConstant(ValueIndex value)
        {
            return value.IsValid &&
                value.Value < ValueCount &&
                GetValue(value.Value).IsNullConstant;
        }

        private ValueIndex GetMergeUnknown(
            BlockIndex block,
            Avm1FlowState state,
            FlowContextIndex context,
            FlowPredecessor predecessor,
            int slot)
        {
            var key = (
                block.Value,
                state,
                context.Value,
                predecessor.Block.Value,
                predecessor.State,
                predecessor.Context.Value,
                slot);
            if (_mergeUnknowns.TryGetValue(key, out var value))
                return value;

            value = AddValue(Avm1StackValueKind.Unknown, "unknown");
            _mergeUnknowns.Add(key, value);
            return value;
        }

        private PhiBuilder CreatePhi(
            BlockIndex block,
            Avm1FlowState state,
            FlowContextIndex context,
            int slot)
        {
            var result = AddValue(
                Avm1StackValueKind.Phi,
                $"phi(b{block.Value},{state},s{slot})");
            var phi = new PhiBuilder(result, block, slot, state, context);
            _phis.Add((block.Value, state, context.Value, slot), phi);
            return phi;
        }

        private void Enqueue(int point)
        {
            if (_queued[point])
                return;

            _queued[point] = true;
            _worklist.Enqueue(point);
        }

        private bool HasProcessedState(int block)
        {
            return _processed[block];
        }

        private void CaptureRows()
        {
            var aggregateEntries = BuildAggregateEntryStacks();
            _sideOperands.Clear();
            _captureRows = true;
            for (var block = 0; block < _cfg.Count; block++)
            {
                var stack = new List<ValueIndex>(aggregateEntries[block]);
                var basicBlock = _cfg[new BlockIndex(block)];
                for (var action = basicBlock.StartAction.Value; action < basicBlock.EndAction.Value; action++)
                    ProcessAction(
                        new ActionIndex(action),
                        stack,
                        FlowContextIndex.Invalid,
                        projectResults: false);
            }
        }

        private ValueIndex[][] BuildAggregateEntryStacks()
        {
            var result = new ValueIndex[_cfg.Count][];
            var pointsByBlock = _flowGraph.Points
                .Where(point => _hasEntry[point.Index.Value])
                .GroupBy(point => point.Block.Value)
                .ToDictionary(group => group.Key, group => group.ToArray());
            for (var block = 0; block < _cfg.Count; block++)
            {
                if (!pointsByBlock.TryGetValue(block, out var points) || points.Length == 0)
                {
                    result[block] = CreateSeedStack(block);
                    continue;
                }

                if (points.Length == 1)
                {
                    result[block] = _entryStacks[points[0].Index.Value];
                    continue;
                }

                var incoming = points.ToDictionary(
                    point => new FlowPredecessor(
                        point.Block,
                        _flowGraph.GetContext(point.Context).State,
                        point.Context,
                        point.Index),
                    point => _entryStacks[point.Index.Value]);
                result[block] = MergeIncoming(
                    new BlockIndex(block),
                    Avm1FlowState.Merged,
                    FlowContextIndex.Invalid,
                    incoming,
                    diagnoseDepthMismatch: false);
            }

            return result;
        }

        private static int GetPoint(BlockIndex block) => block.Value;

        private Avm1StackIr CreateResult()
        {
            var phiNodes = _phis.Values
                .OrderBy(phi => phi.Block.Value)
                .ThenBy(phi => phi.Slot)
                .ThenBy(phi => phi.State)
                .ThenBy(phi => phi.Context.Value)
                .Select(phi => phi.ToNode())
                .ToArray();
            var phiByAction = phiNodes
                .GroupBy(phi => _cfg[phi.Block].StartAction.Value)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var projectionsByRow = _projections.Values
                .GroupBy(projection => projection.RowIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(projection => projection.Context.Value).ToArray());

            var rows = new List<Avm1StackIrInstruction>(
                _rows.Length + phiNodes.Length + _projections.Count);
            var valueOperands = new List<ValueIndex>();

            for (var action = 0; action < _instructions.Count; action++)
            {
                if (phiByAction.TryGetValue(action, out var actionPhis))
                {
                    foreach (var phi in actionPhis)
                    {
                        var operandStart = AppendOperands(valueOperands, phi.IncomingValues);
                        rows.Add(new Avm1StackIrInstruction(
                            new ActionIndex(action),
                            Avm1StackIrOp.Phi,
                            phi.Result,
                            ValueIndex.Invalid,
                            ValueIndex.Invalid,
                            operandStart,
                            phi.IncomingValues.Count,
                            Avm1IrInstructionFlags.None,
                            FlowContextIndex.Invalid));
                    }
                }

                var actionRows = _rowsByAction[action];
                for (var rowOffset = 0; rowOffset < actionRows.Count; rowOffset++)
                {
                    var rowIndex = actionRows.Start + rowOffset;
                    var row = _rows[rowIndex];
                    if (UsesSideTable(row.Op))
                    {
                        var operands = _sideOperands.GetValueOrDefault(rowIndex) ?? [];
                        row = row with { Operand2 = AppendOperands(valueOperands, operands) };
                    }

                    rows.Add(row);
                    if (projectionsByRow.TryGetValue(rowIndex, out var projections))
                    {
                        foreach (var projection in projections)
                            rows.Add(projection.ToInstruction(row));
                    }
                }
            }

            return Avm1StackIr.Create(
                CreateValues(),
                rows.ToArray(),
                valueOperands.ToArray(),
                phiNodes,
                _diagnostics.ToArray());
        }

        private static ValueIndex AppendOperands(
            List<ValueIndex> destination,
            IReadOnlyList<ValueIndex> operands)
        {
            if (operands.Count == 0)
                return ValueIndex.Invalid;

            var start = new ValueIndex(destination.Count);
            for (var i = 0; i < operands.Count; i++)
                destination.Add(operands[i]);
            return start;
        }

        private static bool UsesSideTable(Avm1StackIrOp op)
        {
            return op is Avm1StackIrOp.CallFunction or Avm1StackIrOp.CallMethod or
                Avm1StackIrOp.NewObject or Avm1StackIrOp.NewMethod or
                Avm1StackIrOp.InitArray or Avm1StackIrOp.InitObject or
                Avm1StackIrOp.Implements or
                Avm1StackIrOp.VariadicHostIntrinsic;
        }

        private ValueIndex AddValue(Avm1StackValueKind kind, string display)
        {
            var result = new ValueIndex(ValueCount);
            (_additionalValues ??= []).Add(
                new Avm1StackValue(result, kind, display, -1));
            return result;
        }

        private int ValueCount =>
            _baseValues.Length + (_additionalValues?.Count ?? 0);

        private Avm1StackValue GetValue(int index) =>
            index < _baseValues.Length
                ? _baseValues[index]
                : _additionalValues![index - _baseValues.Length];

        private Avm1StackValue[] CreateValues()
        {
            if (_additionalValues is null || _additionalValues.Count == 0)
                return _ownsBaseValues ? _baseValues : _baseValues.ToArray();

            var result = new Avm1StackValue[ValueCount];
            _baseValues.CopyTo(result, 0);
            _additionalValues.CopyTo(result, _baseValues.Length);
            return result;
        }

        private void SetRow(
            int rowIndex,
            ValueIndex result,
            ValueIndex? operand0 = null,
            ValueIndex? operand1 = null,
            ValueIndex? operand2 = null,
            int operandCount = 0)
        {
            if (!_captureRows)
                return;

            _rows[rowIndex] = _rows[rowIndex] with
            {
                Result = result,
                Operand0 = operand0 ?? ValueIndex.Invalid,
                Operand1 = operand1 ?? ValueIndex.Invalid,
                Operand2 = operand2 ?? ValueIndex.Invalid,
                OperandCount = operandCount
            };
        }

        private void AddDiagnostic(ActionIndex action, string message)
        {
            if (!_diagnosticKeys.Add((action.Value, message)))
                return;

            _diagnostics.Add(new Avm1Diagnostic(Avm1DiagnosticSeverity.Warning, action, message));
        }

        private static bool ValuesEqual(ValueIndex[] left, ValueIndex[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private readonly record struct EnumerationPredicate(
            ValueIndex Stream,
            bool TrueMeansEnd);

        private readonly record struct RowRange(int Start, int Count);

        private sealed class PhiBuilder(
            ValueIndex result,
            BlockIndex block,
            int slot,
            Avm1FlowState state,
            FlowContextIndex context)
        {
            private BlockIndex[] _predecessors = [];
            private Avm1FlowState[] _predecessorStates = [];
            private FlowContextIndex[] _predecessorContexts = [];
            private ValueIndex[] _incomingValues = [];

            public ValueIndex Result { get; } = result;

            public BlockIndex Block { get; } = block;

            public int Slot { get; } = slot;

            public Avm1FlowState State { get; } = state;

            public FlowContextIndex Context { get; } = context;

            public void SetIncoming(
                BlockIndex[] predecessors,
                Avm1FlowState[] predecessorStates,
                FlowContextIndex[] predecessorContexts,
                ValueIndex[] incomingValues)
            {
                _predecessors = predecessors;
                _predecessorStates = predecessorStates;
                _predecessorContexts = predecessorContexts;
                _incomingValues = incomingValues;
            }

            public Avm1StackPhiNode ToNode()
            {
                return new Avm1StackPhiNode(
                    Result,
                    Block,
                    Slot,
                    _predecessors,
                    _incomingValues,
                    State,
                    _predecessorStates,
                    Context,
                    _predecessorContexts);
            }
        }

        private sealed class ProjectionBuilder(
            ValueIndex result,
            int rowIndex,
            FlowContextIndex context)
        {
            private ValueIndex _operand0 = ValueIndex.Invalid;
            private ValueIndex _operand1 = ValueIndex.Invalid;

            public ValueIndex Result { get; } = result;

            public int RowIndex { get; } = rowIndex;

            public FlowContextIndex Context { get; } = context;

            public void SetOperands(ValueIndex operand0, ValueIndex operand1)
            {
                _operand0 = operand0;
                _operand1 = operand1;
            }

            public Avm1StackIrInstruction ToInstruction(Avm1StackIrInstruction source) =>
                source with
                {
                    Result = Result,
                    Operand0 = _operand0,
                    Operand1 = _operand1,
                    Operand2 = ValueIndex.Invalid,
                    OperandCount = 0,
                    Flags = Avm1IrInstructionFlags.ContextProjection,
                    Context = Context
                };
        }

        private readonly record struct FlowPredecessor(
            BlockIndex Block,
            Avm1FlowState State,
            FlowContextIndex Context,
            FlowPointIndex Point)
        {
            public static readonly FlowPredecessor Invalid = new(
                BlockIndex.Invalid,
                Avm1FlowState.Normal,
                FlowContextIndex.Invalid,
                FlowPointIndex.Invalid);

            public FlowPredecessor(BlockIndex block, Avm1FlowState state)
                : this(block, state, FlowContextIndex.Invalid, FlowPointIndex.Invalid)
            {
            }
        }
    }
}
