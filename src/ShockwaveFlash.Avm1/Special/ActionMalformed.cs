namespace ShockwaveFlash.Avm1.Special;

public sealed class ActionMalformed : Action
{
    public ReadOnlyMemory<byte> Data { get; }

    public string Reason { get; }

    public ActionMalformed(
        ActionOpcode opcode,
        ReadOnlyMemory<byte> data,
        string reason) : base(opcode)
    {
        Data = data;
        Reason = reason;
    }

    public override void Encode(MemoryWriter writer, Avm1Context context)
    {
        writer.WriteMemory(Data);
    }
}
