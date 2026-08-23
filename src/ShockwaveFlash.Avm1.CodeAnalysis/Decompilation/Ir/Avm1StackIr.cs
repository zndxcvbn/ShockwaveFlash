using System.Globalization;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf2;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Ir;

public sealed class Avm1StackIr
{
    private Avm1StackIr(
        Avm1StackValue[] values,
        Avm1StackIrInstruction[] instructions,
        ValueIndex[] valueOperands,
        Avm1StackPhiNode[] phiNodes,
        Avm1Diagnostic[] diagnostics)
    {
        Values = values;
        Instructions = instructions;
        ValueOperands = valueOperands;
        PhiNodes = phiNodes;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Avm1StackValue> Values { get; }

    public IReadOnlyList<Avm1StackIrInstruction> Instructions { get; }

    public IReadOnlyList<ValueIndex> ValueOperands { get; }

    public IReadOnlyList<ValueIndex> CallArguments => ValueOperands;

    public IReadOnlyList<Avm1StackPhiNode> PhiNodes { get; }

    public IReadOnlyList<Avm1Diagnostic> Diagnostics { get; }

    internal Avm1StackValue[] ValueArray => (Avm1StackValue[])Values;

    internal Avm1StackIrInstruction[] InstructionArray =>
        (Avm1StackIrInstruction[])Instructions;

    public static Avm1StackIr Build(Avm1InstructionTable instructions)
    {
        var builder = new Builder(instructions);
        builder.Build();
        return builder.ToIr();
    }

    public static Avm1StackIr Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Analysis.Avm1FlowGraph flowGraph,
        Analysis.Avm1StackDepthAnalysis stackDepthAnalysis)
    {
        var linearIr = Build(instructions);
        return Ssa.Avm1StackSsa.Build(
            instructions,
            cfg,
            flowGraph,
            stackDepthAnalysis,
            linearIr,
            takeLinearOwnership: true);
    }

    internal static Avm1StackIr Create(
        Avm1StackValue[] values,
        Avm1StackIrInstruction[] instructions,
        ValueIndex[] valueOperands,
        Avm1StackPhiNode[] phiNodes,
        Avm1Diagnostic[] diagnostics)
    {
        return new Avm1StackIr(values, instructions, valueOperands, phiNodes, diagnostics);
    }

    private sealed class Builder
    {
        private readonly Avm1InstructionTable _instructions;
        private readonly List<Avm1StackValue> _values;
        private readonly List<Avm1StackIrInstruction> _ir;
        private readonly List<ValueIndex> _valueOperands;
        private readonly List<Avm1Diagnostic> _diagnostics = [];
        private readonly Stack<ValueIndex> _stack;
        private readonly bool[] _hasNonSequentialEntry;
        private readonly HashSet<int> _foldedTimelineActions = [];

        public Builder(Avm1InstructionTable instructions)
        {
            _instructions = instructions;
            var pushValueCount = 0;
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[new ActionIndex(index)].Action is ActionPush push)
                    pushValueCount = checked(pushValueCount + push.PushValues.Count);
            }

            var rowCapacity = checked(instructions.Count + pushValueCount);
            _values = new List<Avm1StackValue>(rowCapacity);
            _ir = new List<Avm1StackIrInstruction>(rowCapacity);
            _valueOperands = new List<ValueIndex>(pushValueCount);
            _stack = new Stack<ValueIndex>(Math.Min(pushValueCount, 4096));
            _hasNonSequentialEntry = FindNonSequentialEntries(instructions);
        }

        public void Build()
        {
            for (var i = 0; i < _instructions.Count; i++)
            {
                var instruction = _instructions[new ActionIndex(i)];
                if (_foldedTimelineActions.Contains(i))
                    AddInstruction(instruction.Index, Avm1StackIrOp.NoOp);
                else
                    Visit(instruction);
            }
        }

        public Avm1StackIr ToIr()
        {
            return new Avm1StackIr(_values.ToArray(), _ir.ToArray(), _valueOperands.ToArray(), [], _diagnostics.ToArray());
        }

        private void Visit(Avm1Instruction instruction)
        {
            switch (instruction.Action)
            {
                case ActionConstantPool or ActionEnd:
                    AddInstruction(instruction.Index, Avm1StackIrOp.NoOp);
                    break;

                case ActionNextFrame or ActionPreviousFrame or
                    ActionPlay or ActionStop or ActionToggleQuality or
                    ActionStopSounds:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.TimelineControl);
                    break;

                case ActionGotoFrame2:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.TimelineGoto,
                        ValueIndex.Invalid,
                        Pop(instruction.Index));
                    break;

                case ActionGotoFrame or ActionGoToLabel:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.TimelineGoto,
                        flags: TryFoldFollowingPlay(instruction)
                            ? Avm1IrInstructionFlags.TimelinePlay
                            : Avm1IrInstructionFlags.None);
                    break;

                case ActionCall:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.TimelineCall,
                        ValueIndex.Invalid,
                        Pop(instruction.Index));
                    break;

                case ActionGetURL:
                    AddInstruction(instruction.Index, Avm1StackIrOp.GetUrl);
                    break;

                case ActionGetURL2 { HasValidFlags: true }:
                    BinaryNoResult(instruction.Index, Avm1StackIrOp.GetUrl);
                    break;

                case ActionPush push:
                    foreach (var value in push.PushValues)
                    {
                        var result = value is PushValue.PushValueRegister register
                            ? AddPushedValue(
                                Avm1StackValueKind.Register,
                                value,
                                register.RegisterIndex)
                            : AddPushedValue(Avm1StackValueKind.Constant, value);
                        _stack.Push(result);
                        AddInstruction(instruction.Index, Avm1StackIrOp.Push, result);
                    }
                    break;

                case ActionPop:
                    AddInstruction(instruction.Index, Avm1StackIrOp.Pop, ValueIndex.Invalid, Pop(instruction.Index));
                    break;

                case ActionPushDuplicate:
                    {
                        var value = Peek(instruction.Index);
                        var duplicate = AddProducedValue(Avm1StackIrOp.Duplicate);
                        _stack.Push(duplicate);
                        AddInstruction(instruction.Index, Avm1StackIrOp.Duplicate, duplicate, value);
                        break;
                    }

                case ActionStackSwap:
                    {
                        var top = Pop(instruction.Index);
                        var next = Pop(instruction.Index);
                        _stack.Push(top);
                        _stack.Push(next);
                        AddInstruction(instruction.Index, Avm1StackIrOp.Swap, ValueIndex.Invalid, top, next);
                        break;
                    }

                case ActionStoreRegister store:
                    {
                        var value = Peek(instruction.Index);
                        AddInstruction(instruction.Index, Avm1StackIrOp.StoreRegister, value, value, ValueIndex.Invalid, store.RegisterNumber);
                        break;
                    }

                case ActionGetVariable:
                    UnaryStackResult(instruction.Index, Avm1StackIrOp.GetVariable);
                    break;

                case ActionSetVariable:
                    BinaryNoResult(instruction.Index, Avm1StackIrOp.SetVariable);
                    break;

                case ActionGetMember:
                    BinaryStackResult(instruction.Index, Avm1StackIrOp.GetMember);
                    break;

                case ActionSetMember:
                    TernaryNoResult(instruction.Index, Avm1StackIrOp.SetMember);
                    break;

                case ActionGetProperty:
                    BinaryIntrinsicResult(instruction.Index);
                    break;

                case ActionSetProperty or ActionCloneSprite:
                    TernaryNoResult(instruction.Index, Avm1StackIrOp.HostIntrinsic);
                    break;

                case ActionRemoveSprite:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.HostIntrinsic,
                        ValueIndex.Invalid,
                        Pop(instruction.Index),
                        operandCount: 1);
                    break;

                case ActionStartDrag:
                    StartDrag(instruction.Index);
                    break;

                case ActionEndDrag:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.HostIntrinsic,
                        operandCount: 0);
                    break;

                case ActionSetTarget setTarget:
                    {
                        var target = AddPushedValue(
                            Avm1StackValueKind.Constant,
                            new PushValue.PushValueString(setTarget.TargetName));
                        AddInstruction(
                            instruction.Index,
                            Avm1StackIrOp.TargetControl,
                            ValueIndex.Invalid,
                            target,
                            operandCount: 1);
                        break;
                    }

                case ActionSetTarget2:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.TargetControl,
                        ValueIndex.Invalid,
                        Pop(instruction.Index),
                        operandCount: 1);
                    break;

                case ActionDelete:
                    BinaryStackResult(instruction.Index, Avm1StackIrOp.Delete);
                    break;

                case ActionDelete2:
                    UnaryStackResult(instruction.Index, Avm1StackIrOp.Delete);
                    break;

                case ActionDefineLocal:
                    BinaryNoResult(instruction.Index, Avm1StackIrOp.DefineLocal);
                    break;

                case ActionDefineLocal2:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.DefineLocal,
                        ValueIndex.Invalid,
                        Pop(instruction.Index));
                    break;

                case ActionTrace:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.Trace,
                        ValueIndex.Invalid,
                        Pop(instruction.Index));
                    break;

                case ActionGetTime:
                    ZeroStackResult(instruction.Index, Avm1StackIrOp.GetTime);
                    break;

                case ActionWith:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.WithEnter,
                        ValueIndex.Invalid,
                        Pop(instruction.Index));
                    break;

                case Avm1WithEndAction:
                    AddInstruction(instruction.Index, Avm1StackIrOp.WithExit);
                    break;

                case ActionTry:
                    AddInstruction(instruction.Index, Avm1StackIrOp.TryEnter);
                    break;

                case Avm1TryEndAction:
                    AddInstruction(instruction.Index, Avm1StackIrOp.TryExit);
                    break;

                case Avm1CatchStartAction:
                    AddInstruction(instruction.Index, Avm1StackIrOp.CatchEnter);
                    break;

                case Avm1CatchEndAction:
                    AddInstruction(instruction.Index, Avm1StackIrOp.CatchExit);
                    break;

                case Avm1FinallyStartAction:
                    AddInstruction(instruction.Index, Avm1StackIrOp.FinallyEnter);
                    break;

                case Avm1FinallyEndAction:
                    AddInstruction(instruction.Index, Avm1StackIrOp.FinallyExit);
                    break;

                case ActionInitArray:
                    InitArray(instruction.Index);
                    break;

                case ActionInitObject:
                    InitObject(instruction.Index);
                    break;

                case ActionNewObject:
                    NewObject(instruction.Index);
                    break;

                case ActionNewMethod:
                    NewMethod(instruction.Index);
                    break;

                case ActionCallFunction:
                    CallFunction(instruction.Index);
                    break;

                case ActionCallMethod:
                    CallMethod(instruction.Index);
                    break;

                case ActionEnumerate or ActionEnumerate2:
                    Enumerate(instruction.Index);
                    break;

                case ActionDefineFunction or ActionDefineFunction2:
                    {
                        var result = AddProducedValue(Avm1StackIrOp.FunctionLiteral);
                        _stack.Push(result);
                        AddInstruction(instruction.Index, Avm1StackIrOp.FunctionLiteral, result);
                        break;
                    }

                case ActionCastOp:
                    BinaryStackResult(instruction.Index, Avm1StackIrOp.Cast);
                    break;

                case ActionExtends:
                    BinaryNoResult(instruction.Index, Avm1StackIrOp.Extends);
                    break;

                case ActionImplementsOp:
                    Implements(instruction.Index);
                    break;

                case ActionReturn:
                    AddInstruction(instruction.Index, Avm1StackIrOp.Return, ValueIndex.Invalid, Pop(instruction.Index));
                    break;

                case ActionThrow:
                    AddInstruction(instruction.Index, Avm1StackIrOp.Throw, ValueIndex.Invalid, Pop(instruction.Index));
                    break;

                case ActionIf:
                    AddInstruction(instruction.Index, Avm1StackIrOp.BranchIf, ValueIndex.Invalid, Pop(instruction.Index));
                    break;

                case ActionWaitForFrame:
                    AddInstruction(instruction.Index, Avm1StackIrOp.FrameLoaded);
                    break;

                case ActionWaitForFrame2:
                    AddInstruction(
                        instruction.Index,
                        Avm1StackIrOp.FrameLoaded,
                        ValueIndex.Invalid,
                        Pop(instruction.Index));
                    break;

                case ActionJump:
                    AddInstruction(instruction.Index, Avm1StackIrOp.Jump);
                    break;

                case ActionNot:
                    UnaryStackResult(instruction.Index, Avm1StackIrOp.Not);
                    break;

                case ActionIncrement:
                    UnaryStackResult(instruction.Index, Avm1StackIrOp.Increment);
                    break;

                case ActionDecrement:
                    UnaryStackResult(instruction.Index, Avm1StackIrOp.Decrement);
                    break;

                case ActionStringExtract or ActionMBStringExtract:
                    TernaryStackResult(instruction.Index, Avm1StackIrOp.Intrinsic);
                    break;

                case ActionTypeOf or ActionTargetPath or ActionToNumber or
                    ActionToString or ActionToInteger or ActionRandomNumber or
                    ActionStringLength or ActionMBStringLength or
                    ActionCharToAscii or ActionAsciiToChar or
                    ActionMBCharToAscii or ActionMBAsciiToChar:
                    UnaryStackResult(instruction.Index, Avm1StackIrOp.Unary);
                    break;

                case ActionAdd or ActionAdd2 or ActionStringAdd or ActionSubtract or ActionMultiply or ActionDivide or
                    ActionModulo or ActionEquals or ActionEquals2 or ActionStringEquals or ActionLess or ActionLess2 or
                    ActionStringLess or ActionGreater or ActionStringGreater or ActionAnd or ActionOr or ActionBitAnd or
                    ActionBitOr or ActionBitXor or ActionBitLShift or ActionBitRShift or ActionBitURShift or
                    ActionStrictEquals or ActionInstanceOf:
                    BinaryStackResult(instruction.Index, Avm1StackIrOp.Binary);
                    break;

                default:
                    AddInstruction(instruction.Index, Avm1StackIrOp.UnknownAction);
                    break;
            }
        }

        private void ZeroStackResult(ActionIndex action, Avm1StackIrOp op)
        {
            var result = AddProducedValue(op);
            _stack.Push(result);
            AddInstruction(action, op, result);
        }

        private void UnaryStackResult(ActionIndex action, Avm1StackIrOp op)
        {
            var operand = Pop(action);
            var result = AddProducedValue(op);
            _stack.Push(result);
            AddInstruction(action, op, result, operand);
        }

        private void BinaryStackResult(ActionIndex action, Avm1StackIrOp op)
        {
            var right = Pop(action);
            var left = Pop(action);
            var result = AddProducedValue(op);
            _stack.Push(result);
            AddInstruction(action, op, result, left, right);
        }

        private void BinaryIntrinsicResult(ActionIndex action)
        {
            var right = Pop(action);
            var left = Pop(action);
            var result = AddProducedValue(Avm1StackIrOp.HostIntrinsic);
            _stack.Push(result);
            AddInstruction(
                action,
                Avm1StackIrOp.HostIntrinsic,
                result,
                left,
                right,
                operandCount: 2);
        }

        private void StartDrag(ActionIndex action)
        {
            var target = Pop(action);
            var lockCenter = Pop(action);
            var constraint = Pop(action);
            if (!TryGetStaticTruthiness(constraint, out var constrained))
            {
                _diagnostics.Add(new Avm1Diagnostic(
                    Avm1DiagnosticSeverity.Warning,
                    action,
                    "StartDrag constraint flag is dynamic; preserving the action " +
                    "as opaque with the minimum three-value stack effect."));
                AddInstruction(action, Avm1StackIrOp.UnknownAction);
                return;
            }

            var operands = new List<ValueIndex>(constrained ? 6 : 2)
            {
                target,
                lockCenter
            };
            if (constrained)
            {
                var bottom = Pop(action);
                var right = Pop(action);
                var top = Pop(action);
                var left = Pop(action);
                operands.Add(left);
                operands.Add(top);
                operands.Add(right);
                operands.Add(bottom);
            }

            var operandStart = new ValueIndex(_valueOperands.Count);
            _valueOperands.AddRange(operands);
            AddInstruction(
                action,
                Avm1StackIrOp.VariadicHostIntrinsic,
                operandCount: operands.Count,
                operand2: operandStart);
        }

        private bool TryGetStaticTruthiness(ValueIndex value, out bool result)
        {
            result = false;
            if (!value.IsValid || value.Value >= _values.Count)
                return false;
            return _values[value.Value].TryGetStaticTruthiness(out result);
        }

        private void BinaryNoResult(ActionIndex action, Avm1StackIrOp op)
        {
            var right = Pop(action);
            var left = Pop(action);
            AddInstruction(action, op, ValueIndex.Invalid, left, right);
        }

        private void TernaryNoResult(ActionIndex action, Avm1StackIrOp op)
        {
            var value = Pop(action);
            var name = Pop(action);
            var target = Pop(action);
            AddInstruction(action, op, ValueIndex.Invalid, target, name, 3, value);
        }

        private void TernaryStackResult(ActionIndex action, Avm1StackIrOp op)
        {
            var third = Pop(action);
            var second = Pop(action);
            var first = Pop(action);
            var result = AddProducedValue(op);
            _stack.Push(result);
            AddInstruction(action, op, result, first, second, 3, third);
        }

        private void CallFunction(ActionIndex action)
        {
            var name = Pop(action);
            var argCount = Pop(action);
            var argStart = _valueOperands.Count;
            var count = TryGetConstantInteger(argCount);
            for (var i = 0; i < count; i++)
                _valueOperands.Add(Pop(action));

            var result = AddProducedValue(Avm1StackIrOp.CallFunction);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.CallFunction, result, name, argCount, count, new ValueIndex(argStart));
        }

        private void CallMethod(ActionIndex action)
        {
            var name = Pop(action);
            var target = Pop(action);
            var argCount = Pop(action);
            var argStart = _valueOperands.Count;
            var count = TryGetConstantInteger(argCount);
            for (var i = 0; i < count; i++)
                _valueOperands.Add(Pop(action));

            var result = AddProducedValue(Avm1StackIrOp.CallMethod);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.CallMethod, result, name, target, count, new ValueIndex(argStart));
        }

        private void InitArray(ActionIndex action)
        {
            var countValue = Pop(action);
            var operandStart = _valueOperands.Count;
            var count = TryGetConstantInteger(countValue);
            for (var i = 0; i < count; i++)
                _valueOperands.Add(Pop(action));

            var result = AddProducedValue(Avm1StackIrOp.InitArray);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.InitArray, result, countValue, ValueIndex.Invalid, count, new ValueIndex(operandStart));
        }

        private void InitObject(ActionIndex action)
        {
            var countValue = Pop(action);
            var operandStart = _valueOperands.Count;
            var count = TryGetConstantInteger(countValue);
            for (var i = 0; i < count; i++)
            {
                var value = Pop(action);
                var name = Pop(action);
                _valueOperands.Add(name);
                _valueOperands.Add(value);
            }

            var result = AddProducedValue(Avm1StackIrOp.InitObject);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.InitObject, result, countValue, ValueIndex.Invalid, count, new ValueIndex(operandStart));
        }

        private void NewObject(ActionIndex action)
        {
            var name = Pop(action);
            var argCount = Pop(action);
            var argStart = _valueOperands.Count;
            var count = TryGetConstantInteger(argCount);
            for (var i = 0; i < count; i++)
                _valueOperands.Add(Pop(action));

            var result = AddProducedValue(Avm1StackIrOp.NewObject);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.NewObject, result, name, argCount, count, new ValueIndex(argStart));
        }

        private void NewMethod(ActionIndex action)
        {
            var name = Pop(action);
            var target = Pop(action);
            var argCount = Pop(action);
            var argStart = _valueOperands.Count;
            var count = TryGetConstantInteger(argCount);
            for (var i = 0; i < count; i++)
                _valueOperands.Add(Pop(action));

            var result = AddProducedValue(Avm1StackIrOp.NewMethod);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.NewMethod, result, name, target, count, new ValueIndex(argStart));
        }

        private void Enumerate(ActionIndex action)
        {
            var target = Pop(action);
            var result = AddProducedValue(Avm1StackIrOp.Enumerate);
            _stack.Push(result);
            AddInstruction(action, Avm1StackIrOp.Enumerate, result, target);
        }

        private void Implements(ActionIndex action)
        {
            var constructor = Pop(action);
            var countValue = Pop(action);
            var operandStart = _valueOperands.Count;
            var count = TryGetConstantInteger(countValue);
            for (var i = 0; i < count; i++)
                _valueOperands.Add(Pop(action));

            AddInstruction(
                action,
                Avm1StackIrOp.Implements,
                ValueIndex.Invalid,
                constructor,
                countValue,
                count,
                count > 0 ? new ValueIndex(operandStart) : ValueIndex.Invalid);
        }

        private ValueIndex Pop(ActionIndex action)
        {
            if (_stack.TryPop(out var value))
                return value;

            _diagnostics.Add(new Avm1Diagnostic(Avm1DiagnosticSeverity.Warning, action, "Stack underflow; inserted unknown value."));
            return AddValue(Avm1StackValueKind.Unknown, "unknown");
        }

        private ValueIndex Peek(ActionIndex action)
        {
            if (_stack.TryPeek(out var value))
                return value;

            _diagnostics.Add(new Avm1Diagnostic(Avm1DiagnosticSeverity.Warning, action, "Stack underflow on peek; inserted unknown value."));
            return AddValue(Avm1StackValueKind.Unknown, "unknown");
        }

        private ValueIndex AddValue(Avm1StackValueKind kind, string? display, int intValue = -1)
        {
            var index = new ValueIndex(_values.Count);
            _values.Add(new Avm1StackValue(index, kind, display, intValue));
            return index;
        }

        private ValueIndex AddPushedValue(
            Avm1StackValueKind kind,
            PushValue value,
            int intValue = -1)
        {
            var index = new ValueIndex(_values.Count);
            _values.Add(Avm1StackValue.FromPush(index, kind, value, intValue));
            return index;
        }

        private ValueIndex AddProducedValue(Avm1StackIrOp producer)
        {
            var index = new ValueIndex(_values.Count);
            _values.Add(Avm1StackValue.FromProducer(index, producer));
            return index;
        }

        private void AddInstruction(
            ActionIndex action,
            Avm1StackIrOp op,
            ValueIndex? result = null,
            ValueIndex? operand0 = null,
            ValueIndex? operand1 = null,
            int operandCount = 0,
            ValueIndex? operand2 = null,
            Avm1IrInstructionFlags flags = Avm1IrInstructionFlags.None)
        {
            _ir.Add(new Avm1StackIrInstruction(
                action,
                op,
                result ?? ValueIndex.Invalid,
                operand0 ?? ValueIndex.Invalid,
                operand1 ?? ValueIndex.Invalid,
                operand2 ?? ValueIndex.Invalid,
                operandCount,
                flags,
                FlowContextIndex.Invalid));
        }

        private bool TryFoldFollowingPlay(Avm1Instruction instruction)
        {
            var nextValue = instruction.Index.Value + 1;
            if (nextValue >= _instructions.Count ||
                _hasNonSequentialEntry[nextValue])
            {
                return false;
            }

            var next = _instructions[new ActionIndex(nextValue)];
            if (next.Region != instruction.Region || next.Action is not ActionPlay)
                return false;

            _foldedTimelineActions.Add(nextValue);
            return true;
        }

        private static bool[] FindNonSequentialEntries(
            Avm1InstructionTable instructions)
        {
            var result = new bool[instructions.Count];
            for (var i = 0; i < instructions.Count; i++)
            {
                var action = new ActionIndex(i);
                Mark(instructions.GetBranchTargetAction(action));
                Mark(instructions.GetExplicitFirstSuccessor(action));
                Mark(instructions.GetExplicitSecondSuccessor(action));
            }

            return result;

            void Mark(ActionIndex target)
            {
                if (target.IsValid && target.Value < result.Length)
                    result[target.Value] = true;
            }
        }

        private int TryGetConstantInteger(ValueIndex value)
        {
            if (!value.IsValid || value.Value >= _values.Count)
                return 0;
            return _values[value.Value].GetPositiveInteger();
        }
    }

    internal static string FormatPushValue(PushValue value)
    {
        return value switch
        {
            PushValue.PushValueUndefined => "undefined",
            PushValue.PushValueNull => "null",
            PushValue.PushValueBoolean x => x.Value ? "true" : "false",
            PushValue.PushValueInteger x => x.Value.ToString(CultureInfo.InvariantCulture),
            PushValue.PushValueFloat x => x.Value.ToString(CultureInfo.InvariantCulture),
            PushValue.PushValueDouble x => x.Value.ToString(CultureInfo.InvariantCulture),
            PushValue.PushValueString x => "\"" + x.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            PushValue.PushValueRegister x => "r" + x.RegisterIndex.ToString(CultureInfo.InvariantCulture),
            PushValue.PushValueConstant8 x => "c" + x.ConstantIndex.ToString(CultureInfo.InvariantCulture),
            PushValue.PushValueConstant16 x => "c" + x.ConstantIndex.ToString(CultureInfo.InvariantCulture),
            _ => "unknown"
        };
    }
}

public enum Avm1StackValueKind
{
    Unknown,
    Constant,
    Register,
    Temporary,
    Phi,
    Projection
}

public readonly record struct Avm1StackValue
{
    private readonly object? _displayPayload;

    public Avm1StackValue(
        ValueIndex index,
        Avm1StackValueKind kind,
        string? display,
        int intValue)
    {
        Index = index;
        Kind = kind;
        _displayPayload = display;
        IntValue = intValue;
    }

    private Avm1StackValue(
        ValueIndex index,
        Avm1StackValueKind kind,
        object? displayPayload,
        int intValue)
    {
        Index = index;
        Kind = kind;
        _displayPayload = displayPayload;
        IntValue = intValue;
    }

    public ValueIndex Index { get; init; }

    public Avm1StackValueKind Kind { get; init; }

    public string? Display
    {
        get => _displayPayload switch
        {
            string display => display,
            PushValue value => Avm1StackIr.FormatPushValue(value),
            _ => null
        };
        init => _displayPayload = value;
    }

    public int IntValue { get; init; }

    internal static Avm1StackValue FromPush(
        ValueIndex index,
        Avm1StackValueKind kind,
        PushValue value,
        int intValue) =>
        new(index, kind, value, intValue);

    internal static Avm1StackValue FromProducer(
        ValueIndex index,
        Avm1StackIrOp producer) =>
        new(
            index,
            Avm1StackValueKind.Temporary,
            Enum.GetName(producer) ?? producer.ToString(),
            -1);

    internal bool TryGetStaticTruthiness(out bool result)
    {
        result = false;
        if (Kind is not Avm1StackValueKind.Constant)
            return false;

        switch (_displayPayload)
        {
            case PushValue.PushValueUndefined or PushValue.PushValueNull:
                return true;
            case PushValue.PushValueBoolean value:
                result = value.Value;
                return true;
            case PushValue.PushValueString value:
                result = value.Value.Length != 0;
                return true;
            case PushValue.PushValueInteger value:
                result = value.Value != 0;
                return true;
            case PushValue.PushValueFloat value:
                result = value.Value != 0 && !float.IsNaN(value.Value);
                return true;
            case PushValue.PushValueDouble value:
                result = value.Value != 0 && !double.IsNaN(value.Value);
                return true;
        }

        var display = Display;
        if (display is null)
            return false;
        if (display is "false" or "undefined" or "null")
            return true;
        if (display == "true")
        {
            result = true;
            return true;
        }
        if (display.Length >= 2 && display[0] == '"' && display[^1] == '"')
        {
            result = display.Length > 2;
            return true;
        }
        if (!double.TryParse(
            display,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number))
        {
            return false;
        }

        result = number != 0 && !double.IsNaN(number);
        return true;
    }

    internal int GetPositiveInteger()
    {
        if (Kind is not Avm1StackValueKind.Constant)
            return 0;
        if (_displayPayload is PushValue.PushValueInteger integer)
            return integer.Value > 0 ? integer.Value : 0;

        return int.TryParse(
                Display,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) && value > 0
            ? value
            : 0;
    }

    internal bool IsProducedBy(Avm1StackIrOp producer) =>
        Kind is Avm1StackValueKind.Temporary &&
        string.Equals(
            _displayPayload as string,
            Enum.GetName(producer) ?? producer.ToString(),
            StringComparison.Ordinal);

    internal bool IsNullConstant =>
        Kind is Avm1StackValueKind.Constant &&
        (_displayPayload is PushValue.PushValueNull ||
         string.Equals(_displayPayload as string, "null", StringComparison.Ordinal));

    public void Deconstruct(
        out ValueIndex index,
        out Avm1StackValueKind kind,
        out string? display,
        out int intValue)
    {
        index = Index;
        kind = Kind;
        display = Display;
        intValue = IntValue;
    }
}

public enum Avm1StackIrOp
{
    UnknownAction,
    NoOp,
    Phi,
    Push,
    Pop,
    Duplicate,
    Swap,
    StoreRegister,
    GetVariable,
    SetVariable,
    GetMember,
    SetMember,
    Delete,
    DefineLocal,
    Trace,
    TimelineControl,
    TimelineCall,
    TimelineGoto,
    GetUrl,
    Intrinsic,
    HostIntrinsic,
    VariadicHostIntrinsic,
    TargetControl,
    GetTime,
    WithEnter,
    WithExit,
    TryEnter,
    TryExit,
    CatchEnter,
    CatchExit,
    FinallyEnter,
    FinallyExit,
    InitArray,
    InitObject,
    NewObject,
    NewMethod,
    CallFunction,
    CallMethod,
    FunctionLiteral,
    Cast,
    Extends,
    Implements,
    Enumerate,
    Return,
    Throw,
    BranchIf,
    FrameLoaded,
    Jump,
    Not,
    Unary,
    Increment,
    Decrement,
    Binary
}

public readonly record struct Avm1StackIrInstruction(
    ActionIndex Action,
    Avm1StackIrOp Op,
    ValueIndex Result,
    ValueIndex Operand0,
    ValueIndex Operand1,
    ValueIndex Operand2,
    int OperandCount,
    Avm1IrInstructionFlags Flags,
    FlowContextIndex Context);

[Flags]
public enum Avm1IrInstructionFlags : byte
{
    None = 0,
    ContextProjection = 1,
    TimelinePlay = 2
}

public readonly record struct Avm1StackPhiNode(
    ValueIndex Result,
    BlockIndex Block,
    int Slot,
    IReadOnlyList<BlockIndex> Predecessors,
    IReadOnlyList<ValueIndex> IncomingValues,
    Avm1FlowState State,
    IReadOnlyList<Avm1FlowState> IncomingStates,
    FlowContextIndex Context,
    IReadOnlyList<FlowContextIndex> IncomingContexts);
