namespace ShockwaveFlash.Avm1.Decompilation.Ir;

public sealed class Avm1TacIr
{
    private Avm1TacIr(
        Avm1TacInstruction[] instructions,
        ValueIndex[] valueOperands,
        Avm1StackPhiNode[] phiNodes)
    {
        Instructions = instructions;
        ValueOperands = valueOperands;
        PhiNodes = phiNodes;
    }

    public IReadOnlyList<Avm1TacInstruction> Instructions { get; }

    public IReadOnlyList<ValueIndex> ValueOperands { get; }

    public IReadOnlyList<ValueIndex> CallArguments => ValueOperands;

    public IReadOnlyList<Avm1StackPhiNode> PhiNodes { get; }

    public int Count => Instructions.Count;

    public Avm1TacInstruction this[IrIndex index] => Instructions[index.Value];

    public static Avm1TacIr Build(Avm1InstructionTable instructions, Avm1StackIr stackIr)
    {
        var rows = new List<Avm1TacInstruction>(stackIr.Instructions.Count);
        var phiByResult = stackIr.PhiNodes.ToDictionary(phi => phi.Result);

        foreach (var instruction in stackIr.Instructions)
        {
            var action = instructions[instruction.Action].Action;
            var op = instruction.Op switch
            {
                Avm1StackIrOp.NoOp => Avm1TacOp.NoOp,
                Avm1StackIrOp.Phi => Avm1TacOp.Phi,
                Avm1StackIrOp.Push when IsRegisterPush(action, instruction.Result, stackIr) => Avm1TacOp.LoadRegister,
                Avm1StackIrOp.Push => Avm1TacOp.LoadConstant,
                Avm1StackIrOp.StoreRegister => Avm1TacOp.StoreRegister,
                Avm1StackIrOp.GetVariable => Avm1TacOp.GetVariable,
                Avm1StackIrOp.SetVariable => Avm1TacOp.SetVariable,
                Avm1StackIrOp.GetMember => Avm1TacOp.GetMember,
                Avm1StackIrOp.SetMember => Avm1TacOp.SetMember,
                Avm1StackIrOp.Delete => Avm1TacOp.Delete,
                Avm1StackIrOp.DefineLocal => Avm1TacOp.DefineLocal,
                Avm1StackIrOp.Trace => Avm1TacOp.Trace,
                Avm1StackIrOp.TimelineControl => Avm1TacOp.TimelineControl,
                Avm1StackIrOp.TimelineCall => Avm1TacOp.TimelineCall,
                Avm1StackIrOp.TimelineGoto => Avm1TacOp.TimelineGoto,
                Avm1StackIrOp.GetUrl => Avm1TacOp.GetUrl,
                Avm1StackIrOp.Intrinsic => Avm1TacOp.Intrinsic,
                Avm1StackIrOp.HostIntrinsic => Avm1TacOp.HostIntrinsic,
                Avm1StackIrOp.VariadicHostIntrinsic =>
                    Avm1TacOp.VariadicHostIntrinsic,
                Avm1StackIrOp.TargetControl => Avm1TacOp.TargetControl,
                Avm1StackIrOp.GetTime => Avm1TacOp.GetTime,
                Avm1StackIrOp.WithEnter => Avm1TacOp.WithEnter,
                Avm1StackIrOp.WithExit => Avm1TacOp.WithExit,
                Avm1StackIrOp.TryEnter => Avm1TacOp.TryEnter,
                Avm1StackIrOp.TryExit => Avm1TacOp.TryExit,
                Avm1StackIrOp.CatchEnter => Avm1TacOp.CatchEnter,
                Avm1StackIrOp.CatchExit => Avm1TacOp.CatchExit,
                Avm1StackIrOp.FinallyEnter => Avm1TacOp.FinallyEnter,
                Avm1StackIrOp.FinallyExit => Avm1TacOp.FinallyExit,
                Avm1StackIrOp.InitArray => Avm1TacOp.InitArray,
                Avm1StackIrOp.InitObject => Avm1TacOp.InitObject,
                Avm1StackIrOp.NewObject => Avm1TacOp.NewObject,
                Avm1StackIrOp.NewMethod => Avm1TacOp.NewMethod,
                Avm1StackIrOp.CallFunction => Avm1TacOp.CallFunction,
                Avm1StackIrOp.CallMethod => Avm1TacOp.CallMethod,
                Avm1StackIrOp.FunctionLiteral => Avm1TacOp.FunctionLiteral,
                Avm1StackIrOp.Cast => Avm1TacOp.Cast,
                Avm1StackIrOp.Extends => Avm1TacOp.Extends,
                Avm1StackIrOp.Implements => Avm1TacOp.Implements,
                Avm1StackIrOp.Enumerate => Avm1TacOp.Enumerate,
                Avm1StackIrOp.Return => Avm1TacOp.Return,
                Avm1StackIrOp.Throw => Avm1TacOp.Throw,
                Avm1StackIrOp.BranchIf => Avm1TacOp.BranchIf,
                Avm1StackIrOp.FrameLoaded => Avm1TacOp.FrameLoaded,
                Avm1StackIrOp.Jump => Avm1TacOp.Jump,
                Avm1StackIrOp.Not => Avm1TacOp.Unary,
                Avm1StackIrOp.Unary => Avm1TacOp.Unary,
                Avm1StackIrOp.Increment => Avm1TacOp.Unary,
                Avm1StackIrOp.Decrement => Avm1TacOp.Unary,
                Avm1StackIrOp.Binary => Avm1TacOp.Binary,
                Avm1StackIrOp.Pop => Avm1TacOp.Pop,
                Avm1StackIrOp.Duplicate => Avm1TacOp.Copy,
                Avm1StackIrOp.Swap => Avm1TacOp.StackOnly,
                _ => Avm1TacOp.Unknown
            };

            rows.Add(new Avm1TacInstruction(
                new IrIndex(rows.Count),
                instruction.Action,
                op,
                action.Opcode,
                instruction.Result,
                instruction.Operand0,
                instruction.Operand1,
                instruction.Operand2,
                GetIntOperand(action, instruction, stackIr, phiByResult),
                instruction.OperandCount,
                instruction.Flags,
                instruction.Context));
        }

        return new Avm1TacIr(rows.ToArray(), stackIr.ValueOperands.ToArray(), stackIr.PhiNodes.ToArray());
    }

    private static bool IsRegisterPush(Action action, ValueIndex result, Avm1StackIr stackIr)
    {
        return action is Swf4.ActionPush &&
            result.IsValid &&
            stackIr.Values[result.Value].Kind is Avm1StackValueKind.Register;
    }

    private static int GetIntOperand(
        Action action,
        Avm1StackIrInstruction instruction,
        Avm1StackIr stackIr,
        Dictionary<ValueIndex, Avm1StackPhiNode> phiByResult)
    {
        if (instruction.Op is Avm1StackIrOp.Phi &&
            phiByResult.TryGetValue(instruction.Result, out var phi))
        {
            return phi.Block.Value;
        }

        if (action is Swf5.ActionStoreRegister store)
            return store.RegisterNumber;

        if (action is Swf4.ActionGotoFrame2 gotoFrame)
            return gotoFrame.Play ? 1 : 0;

        if (action is Swf3.ActionWaitForFrame waitForFrame)
            return waitForFrame.Frame;

        if (action is Swf1.ActionGotoFrame or Swf3.ActionGoToLabel)
        {
            return instruction.Flags.HasFlag(
                Avm1IrInstructionFlags.TimelinePlay) ? 1 : 0;
        }

        if (action is Swf4.ActionGetURL2 getUrl)
            return (byte)getUrl.Flags;

        if (action is Avm1CatchStartAction catchStart)
            return catchStart.CatchRegister;

        if (action is Swf4.ActionPush &&
            instruction.Result.IsValid &&
            stackIr.Values[instruction.Result.Value].Kind is Avm1StackValueKind.Register)
        {
            return stackIr.Values[instruction.Result.Value].IntValue;
        }

        return -1;
    }
}

public enum Avm1TacOp
{
    Unknown,
    NoOp,
    StackOnly,
    Phi,
    LoadConstant,
    LoadRegister,
    StoreRegister,
    Copy,
    Pop,
    Unary,
    Binary,
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
    BranchIf,
    FrameLoaded,
    Jump,
    Return,
    Throw
}

public readonly record struct Avm1TacInstruction(
    IrIndex Index,
    ActionIndex Action,
    Avm1TacOp Op,
    ActionOpcode Opcode,
    ValueIndex Result,
    ValueIndex Operand0,
    ValueIndex Operand1,
    ValueIndex Operand2,
    int IntOperand,
    int OperandCount,
    Avm1IrInstructionFlags Flags,
    FlowContextIndex Context);
