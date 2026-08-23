namespace ShockwaveFlash.Avm1.Compilation;

internal enum Avm1RegisterFileKind : byte
{
    Legacy,
    DefineFunction2
}

internal readonly record struct Avm1ConstantPoolContext(
    bool IsKnown,
    int? MinimumCount)
{
    public static readonly Avm1ConstantPoolContext Unknown = new(false, null);
    public static readonly Avm1ConstantPoolContext Missing = new(true, null);

    public bool IsGuaranteed => IsKnown && MinimumCount.HasValue;

    public static Avm1ConstantPoolContext FromInitial(ushort? count) =>
        count.HasValue ? Available(count.Value) : Missing;

    public static Avm1ConstantPoolContext Available(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new Avm1ConstantPoolContext(true, count);
    }

    public Avm1ConstantPoolContext Merge(Avm1ConstantPoolContext other)
    {
        if (!IsKnown || !other.IsKnown)
            return Unknown;
        if (!MinimumCount.HasValue || !other.MinimumCount.HasValue)
            return Missing;
        return Available(Math.Min(MinimumCount.Value, other.MinimumCount.Value));
    }

    public Avm1ConstantPoolContext Constrain(int? minimumCount)
    {
        if (!minimumCount.HasValue || !IsKnown)
            return this;
        if (!MinimumCount.HasValue)
            return Missing;
        return Available(Math.Min(MinimumCount.Value, minimumCount.Value));
    }
}

internal readonly record struct Avm1CodeUnitContext(
    Avm1RegisterFileKind RegisterFileKind,
    byte RegisterCount,
    Avm1ConstantPoolContext ConstantPool)
{
    public static Avm1CodeUnitContext Legacy(Avm1ConstantPoolContext constantPool) =>
        new(Avm1RegisterFileKind.Legacy, RegisterCount: 3, constantPool);

    public static Avm1CodeUnitContext DefineFunction2(
        byte registerCount,
        Avm1ConstantPoolContext constantPool) =>
        new(Avm1RegisterFileKind.DefineFunction2, registerCount, constantPool);

    public bool IsRegisterValid(byte register) =>
        RegisterFileKind switch
        {
            Avm1RegisterFileKind.Legacy => register <= RegisterCount,
            Avm1RegisterFileKind.DefineFunction2 =>
                register < RegisterCount,
            _ => false
        };

    public string ValidRegisterRange =>
        RegisterFileKind is Avm1RegisterFileKind.Legacy
            ? "0..3"
            : RegisterCount == 0
                ? "empty"
                : $"0..{RegisterCount - 1}";

    public Avm1CodeUnitContext WithConstantPool(
        Avm1ConstantPoolContext constantPool) =>
        this with { ConstantPool = constantPool };
}
