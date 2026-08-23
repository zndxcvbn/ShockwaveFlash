namespace ShockwaveFlash.Avm1.Special;

public sealed class ActionTrailingData : Action
{
    public ReadOnlyMemory<byte> Data { get; }

    public ActionTrailingData(ReadOnlyMemory<byte> data)
        : base(data.IsEmpty ? ActionOpcode.End : (ActionOpcode)data.Span[0])
    {
        if (data.IsEmpty)
            throw new ArgumentException("Trailing AVM1 data cannot be empty.", nameof(data));

        Data = data;
    }
}
