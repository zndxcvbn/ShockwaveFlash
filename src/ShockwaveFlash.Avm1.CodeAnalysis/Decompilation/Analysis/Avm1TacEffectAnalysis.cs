using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

[Flags]
public enum Avm1TacEffect : ushort
{
    None = 0,
    ReadsState = 1 << 0,
    WritesState = 1 << 1,
    WritesRegister = 1 << 2,
    MayInvokeUserCode = 1 << 3,
    MayThrow = 1 << 4,
    Allocates = 1 << 5,
    ChangesScope = 1 << 6,
    Control = 1 << 7,
    DebugOutput = 1 << 8,
    Unknown = 1 << 9
}

public static class Avm1TacEffectAnalysis
{
    private const Avm1TacEffect ObservableEffects =
        Avm1TacEffect.WritesState |
        Avm1TacEffect.MayInvokeUserCode |
        Avm1TacEffect.MayThrow |
        Avm1TacEffect.ChangesScope |
        Avm1TacEffect.Control |
        Avm1TacEffect.DebugOutput |
        Avm1TacEffect.Unknown;

    private const Avm1TacEffect OrderingBarriers =
        ObservableEffects |
        Avm1TacEffect.WritesRegister;

    private const Avm1TacEffect SourceEvaluationEffects =
        Avm1TacEffect.WritesState |
        Avm1TacEffect.MayInvokeUserCode |
        Avm1TacEffect.MayThrow |
        Avm1TacEffect.DebugOutput |
        Avm1TacEffect.Unknown;

    public static Avm1TacEffect GetEffects(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values = null)
    {
        return instruction.Op switch
        {
            Avm1TacOp.Unknown => Avm1TacEffect.Unknown,
            Avm1TacOp.StoreRegister => Avm1TacEffect.WritesRegister,
            Avm1TacOp.Unary => GetUnaryEffects(instruction, values),
            Avm1TacOp.Binary => GetBinaryEffects(instruction, values),
            Avm1TacOp.GetVariable when IsStableActivationName(
                instruction.Operand0,
                values) => Avm1TacEffect.None,
            Avm1TacOp.GetVariable or Avm1TacOp.GetMember =>
                Avm1TacEffect.ReadsState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.SetVariable or Avm1TacOp.SetMember or
                Avm1TacOp.DefineLocal or Avm1TacOp.Delete or
                Avm1TacOp.Extends or Avm1TacOp.Implements =>
                Avm1TacEffect.WritesState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.Trace =>
                Avm1TacEffect.DebugOutput |
                Avm1TacEffect.MayInvokeUserCode,
            Avm1TacOp.TimelineControl or Avm1TacOp.TimelineGoto =>
                Avm1TacEffect.WritesState,
            Avm1TacOp.TimelineCall =>
                Avm1TacEffect.ReadsState |
                Avm1TacEffect.WritesState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.GetUrl =>
                Avm1TacEffect.WritesState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.Intrinsic =>
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.HostIntrinsic when instruction.Opcode is
                ActionOpcode.GetProperty =>
                Avm1TacEffect.ReadsState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.HostIntrinsic =>
                Avm1TacEffect.WritesState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.VariadicHostIntrinsic =>
                Avm1TacEffect.WritesState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.TargetControl =>
                Avm1TacEffect.ChangesScope |
                Avm1TacEffect.Control,
            Avm1TacOp.GetTime => Avm1TacEffect.ReadsState,
            Avm1TacOp.WithEnter or Avm1TacOp.WithExit or
                Avm1TacOp.TryEnter or Avm1TacOp.TryExit or
                Avm1TacOp.CatchEnter or Avm1TacOp.CatchExit or
                Avm1TacOp.FinallyEnter or Avm1TacOp.FinallyExit =>
                Avm1TacEffect.ChangesScope |
                Avm1TacEffect.Control,
            Avm1TacOp.InitArray or Avm1TacOp.InitObject or
                Avm1TacOp.FunctionLiteral => Avm1TacEffect.Allocates,
            Avm1TacOp.NewObject or Avm1TacOp.NewMethod =>
                Avm1TacEffect.Allocates |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow |
                Avm1TacEffect.ReadsState,
            Avm1TacOp.CallFunction or Avm1TacOp.CallMethod =>
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow |
                Avm1TacEffect.ReadsState,
            Avm1TacOp.Cast =>
                Avm1TacEffect.ReadsState |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.Enumerate =>
                Avm1TacEffect.ReadsState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow,
            Avm1TacOp.BranchIf or Avm1TacOp.FrameLoaded or Avm1TacOp.Jump or
                Avm1TacOp.Return or Avm1TacOp.Throw => Avm1TacEffect.Control,
            _ => Avm1TacEffect.None
        };
    }

    public static bool HasObservableEffect(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values = null) =>
        (GetEffects(instruction, values) & ObservableEffects) != 0;

    public static bool ReadsMutableState(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values = null) =>
        (GetEffects(instruction, values) & Avm1TacEffect.ReadsState) != 0;

    public static bool IsSourceStatementRoot(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values = null)
    {
        if (instruction.Op is Avm1TacOp.StoreRegister or
            Avm1TacOp.TargetControl or
            Avm1TacOp.Return or Avm1TacOp.Throw)
            return true;
        if (instruction.Op is Avm1TacOp.BranchIf or Avm1TacOp.FrameLoaded or
            Avm1TacOp.Jump or
            Avm1TacOp.WithEnter or Avm1TacOp.WithExit or
            Avm1TacOp.TryEnter or Avm1TacOp.TryExit or
            Avm1TacOp.CatchEnter or Avm1TacOp.CatchExit or
            Avm1TacOp.FinallyEnter or Avm1TacOp.FinallyExit)
        {
            return false;
        }

        return (GetEffects(instruction, values) & SourceEvaluationEffects) != 0;
    }

    public static bool IsOrderingBarrier(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values = null) =>
        (GetEffects(instruction, values) & OrderingBarriers) != 0;

    public static bool DefinesValue(Avm1TacOp op) =>
        op is Avm1TacOp.Phi or Avm1TacOp.LoadConstant or
            Avm1TacOp.LoadRegister or Avm1TacOp.Copy or
            Avm1TacOp.Unary or Avm1TacOp.Intrinsic or
            Avm1TacOp.HostIntrinsic or Avm1TacOp.Binary or
            Avm1TacOp.GetVariable or Avm1TacOp.GetMember or
            Avm1TacOp.Delete or Avm1TacOp.GetTime or
            Avm1TacOp.InitArray or Avm1TacOp.InitObject or
            Avm1TacOp.NewObject or Avm1TacOp.NewMethod or
            Avm1TacOp.CallFunction or Avm1TacOp.CallMethod or
            Avm1TacOp.FunctionLiteral or Avm1TacOp.Cast or
            Avm1TacOp.Enumerate;

    public static bool HasValueOperandSideTable(Avm1TacOp op) =>
        op is Avm1TacOp.Phi or Avm1TacOp.CallFunction or
            Avm1TacOp.CallMethod or Avm1TacOp.NewObject or
            Avm1TacOp.NewMethod or Avm1TacOp.InitArray or
            Avm1TacOp.InitObject or Avm1TacOp.Implements or
            Avm1TacOp.VariadicHostIntrinsic;

    private static Avm1TacEffect GetUnaryEffects(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values)
    {
        if (instruction.Opcode is ActionOpcode.RandomNumber)
        {
            return Avm1TacEffect.ReadsState |
                Avm1TacEffect.WritesState |
                Avm1TacEffect.MayInvokeUserCode |
                Avm1TacEffect.MayThrow;
        }

        if (instruction.Opcode is ActionOpcode.Not or
            ActionOpcode.TypeOf or ActionOpcode.TargetPath)
        {
            return Avm1TacEffect.None;
        }

        return IsKnownPrimitive(instruction.Operand0, values)
            ? Avm1TacEffect.None
            : Avm1TacEffect.MayInvokeUserCode | Avm1TacEffect.MayThrow;
    }

    private static Avm1TacEffect GetBinaryEffects(
        in Avm1TacInstruction instruction,
        Avm1ValueAnalysis? values)
    {
        if (instruction.Opcode is ActionOpcode.StrictEquals or
            ActionOpcode.And or ActionOpcode.Or)
        {
            return Avm1TacEffect.None;
        }

        if (instruction.Opcode is ActionOpcode.InstanceOf)
            return Avm1TacEffect.ReadsState | Avm1TacEffect.MayThrow;

        return IsKnownPrimitive(instruction.Operand0, values) &&
            IsKnownPrimitive(instruction.Operand1, values)
                ? Avm1TacEffect.None
                : Avm1TacEffect.MayInvokeUserCode | Avm1TacEffect.MayThrow;
    }

    private static bool IsKnownPrimitive(
        ValueIndex value,
        Avm1ValueAnalysis? values)
    {
        if (values is null || !value.IsValid)
            return false;

        return values[value].Type is
            Avm1InferredType.Undefined or
            Avm1InferredType.Null or
            Avm1InferredType.Boolean or
            Avm1InferredType.Integer or
            Avm1InferredType.Number or
            Avm1InferredType.String;
    }

    private static bool IsStableActivationName(
        ValueIndex value,
        Avm1ValueAnalysis? values)
    {
        if (values is null || !value.IsValid)
            return false;

        var fact = values[value];
        return fact.ConstantKind is Avm1ConstantKind.String &&
            fact.StringValue is "this";
    }
}
