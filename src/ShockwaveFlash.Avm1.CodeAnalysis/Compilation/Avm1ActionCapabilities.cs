using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Types;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation;

public static class Avm1ActionCapabilities
{
    public static bool TryGetMinimumSwfVersion(
        ActionOpcode opcode,
        out byte minimumSwfVersion)
    {
        minimumSwfVersion = opcode switch
        {
            ActionOpcode.End or
            ActionOpcode.NextFrame or
            ActionOpcode.PreviousFrame or
            ActionOpcode.Play or
            ActionOpcode.Stop or
            ActionOpcode.ToggleQuality or
            ActionOpcode.GotoFrame or
            ActionOpcode.GetURL => 1,

            ActionOpcode.StopSounds => 2,

            ActionOpcode.WaitForFrame or
            ActionOpcode.SetTarget or
            ActionOpcode.GoToLabel => 3,

            ActionOpcode.Add or
            ActionOpcode.Subtract or
            ActionOpcode.Multiply or
            ActionOpcode.Divide or
            ActionOpcode.Equals or
            ActionOpcode.Less or
            ActionOpcode.And or
            ActionOpcode.Or or
            ActionOpcode.Not or
            ActionOpcode.StringEquals or
            ActionOpcode.StringLength or
            ActionOpcode.StringExtract or
            ActionOpcode.Pop or
            ActionOpcode.ToInteger or
            ActionOpcode.GetVariable or
            ActionOpcode.SetVariable or
            ActionOpcode.SetTarget2 or
            ActionOpcode.StringAdd or
            ActionOpcode.GetProperty or
            ActionOpcode.SetProperty or
            ActionOpcode.CloneSprite or
            ActionOpcode.RemoveSprite or
            ActionOpcode.Trace or
            ActionOpcode.StartDrag or
            ActionOpcode.EndDrag or
            ActionOpcode.StringLess or
            ActionOpcode.RandomNumber or
            ActionOpcode.MBStringLength or
            ActionOpcode.CharToAscii or
            ActionOpcode.AsciiToChar or
            ActionOpcode.GetTime or
            ActionOpcode.MBStringExtract or
            ActionOpcode.MBCharToAscii or
            ActionOpcode.MBAsciiToChar or
            ActionOpcode.WaitForFrame2 or
            ActionOpcode.Push or
            ActionOpcode.Jump or
            ActionOpcode.GetURL2 or
            ActionOpcode.If or
            ActionOpcode.Call or
            ActionOpcode.GotoFrame2 => 4,

            ActionOpcode.Delete or
            ActionOpcode.Delete2 or
            ActionOpcode.DefineLocal or
            ActionOpcode.CallFunction or
            ActionOpcode.Return or
            ActionOpcode.Modulo or
            ActionOpcode.NewObject or
            ActionOpcode.DefineLocal2 or
            ActionOpcode.InitArray or
            ActionOpcode.InitObject or
            ActionOpcode.TypeOf or
            ActionOpcode.TargetPath or
            ActionOpcode.Enumerate or
            ActionOpcode.Add2 or
            ActionOpcode.Less2 or
            ActionOpcode.Equals2 or
            ActionOpcode.ToNumber or
            ActionOpcode.ToString or
            ActionOpcode.PushDuplicate or
            ActionOpcode.StackSwap or
            ActionOpcode.GetMember or
            ActionOpcode.SetMember or
            ActionOpcode.Increment or
            ActionOpcode.Decrement or
            ActionOpcode.CallMethod or
            ActionOpcode.NewMethod or
            ActionOpcode.BitAnd or
            ActionOpcode.BitOr or
            ActionOpcode.BitXor or
            ActionOpcode.BitLShift or
            ActionOpcode.BitRShift or
            ActionOpcode.BitURShift or
            ActionOpcode.StoreRegister or
            ActionOpcode.ConstantPool or
            ActionOpcode.DefineFunction or
            ActionOpcode.With => 5,

            ActionOpcode.InstanceOf or
            ActionOpcode.Enumerate2 or
            ActionOpcode.StrictEquals or
            ActionOpcode.Greater or
            ActionOpcode.StringGreater => 6,

            ActionOpcode.Throw or
            ActionOpcode.CastOp or
            ActionOpcode.ImplementsOp or
            ActionOpcode.Extends or
            ActionOpcode.DefineFunction2 or
            ActionOpcode.Try => 7,

            _ => 0
        };

        return minimumSwfVersion != 0;
    }

    public static byte GetMinimumSwfVersion(ActionOpcode opcode)
    {
        if (TryGetMinimumSwfVersion(opcode, out var minimumSwfVersion))
            return minimumSwfVersion;

        throw new ArgumentOutOfRangeException(
            nameof(opcode),
            opcode,
            "The AVM1 opcode is not present in the capability table.");
    }

    public static bool TryGetMinimumSwfVersion(
        PushValue value,
        out byte minimumSwfVersion)
    {
        ArgumentNullException.ThrowIfNull(value);

        minimumSwfVersion = value switch
        {
            PushValue.PushValueString or
            PushValue.PushValueFloat => 4,

            PushValue.PushValueNull or
            PushValue.PushValueUndefined or
            PushValue.PushValueRegister or
            PushValue.PushValueBoolean or
            PushValue.PushValueDouble or
            PushValue.PushValueInteger or
            PushValue.PushValueConstant8 or
            PushValue.PushValueConstant16 => 5,

            _ => 0
        };

        return minimumSwfVersion != 0;
    }

    public static byte GetMinimumSwfVersion(PushValue value)
    {
        if (TryGetMinimumSwfVersion(value, out var minimumSwfVersion))
            return minimumSwfVersion;

        throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "The AVM1 push-value form is not present in the capability table.");
    }

    public static bool TryGetMinimumSwfVersion(
        Avm1Action action,
        out byte minimumSwfVersion)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!TryGetMinimumSwfVersion(action.Opcode, out minimumSwfVersion))
            return false;

        if (action is not ActionPush push)
            return true;

        foreach (var value in push.PushValues)
        {
            if (!TryGetMinimumSwfVersion(value, out var valueMinimumSwfVersion))
            {
                minimumSwfVersion = 0;
                return false;
            }

            if (valueMinimumSwfVersion > minimumSwfVersion)
                minimumSwfVersion = valueMinimumSwfVersion;
        }

        return true;
    }

    public static byte GetMinimumSwfVersion(Avm1Action action)
    {
        if (TryGetMinimumSwfVersion(action, out var minimumSwfVersion))
            return minimumSwfVersion;

        throw new ArgumentOutOfRangeException(
            nameof(action),
            action,
            "The AVM1 action form is not present in the capability table.");
    }

    public static bool IsSupported(Avm1Action action, byte swfVersion) =>
        TryGetMinimumSwfVersion(action, out var minimumSwfVersion) &&
        swfVersion >= minimumSwfVersion;
}
