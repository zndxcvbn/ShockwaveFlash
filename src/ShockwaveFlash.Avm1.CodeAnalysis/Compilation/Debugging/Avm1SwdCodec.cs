using System.Text;
using ShockwaveFlash.Exceptions;

namespace ShockwaveFlash.Avm1.Compilation;

public static class Avm1SwdCodec
{
    private static ReadOnlySpan<byte> Magic => "FWD"u8;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static Avm1SwdFile Decode(ReadOnlyMemory<byte> data)
    {
        var reader = new MemoryReader(data);
        if (!reader.ReadMemory(Magic.Length).Span.SequenceEqual(Magic))
            throw new SwfFormatException("Invalid SWD signature; expected 'FWD'.");

        var version = reader.ReadUInt8();
        if (version < Avm1SwdFile.MinimumVersion)
        {
            throw new SwfFormatException(
                $"SWD version {version} is unsupported; version 6 or later is required.");
        }

        var records = new List<Avm1SwdRecord>();
        while (reader.Remaining > 0)
        {
            var kind = reader.ReadUInt32();
            records.Add(kind switch
            {
                (uint)Avm1SwdRecordKind.SourceFile => DecodeSourceFile(reader),
                (uint)Avm1SwdRecordKind.OffsetMap => DecodeOffsetMap(reader),
                (uint)Avm1SwdRecordKind.Breakpoint => DecodeBreakpoint(reader),
                (uint)Avm1SwdRecordKind.DebugId => DecodeDebugId(reader),
                (uint)Avm1SwdRecordKind.RegisterMap => DecodeRegisterMap(reader),
                _ => throw new SwfFormatException(
                    $"Unknown SWD record kind {kind} at byte {reader.Position - 4}.")
            });
        }
        return new Avm1SwdFile(version, records);
    }

    public static ReadOnlyMemory<byte> Encode(Avm1SwdFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var writer = new MemoryWriter();
        writer.WriteBytes(Magic);
        writer.WriteUInt8(file.Version);
        Span<byte> debugIdBytes = stackalloc byte[16];
        foreach (var record in file.Records)
        {
            writer.WriteUInt32((uint)record.Kind);
            switch (record)
            {
                case Avm1SwdSourceFileRecord sourceFile:
                    writer.WriteUInt32(sourceFile.ModuleId);
                    writer.WriteUInt32(sourceFile.Bitmap);
                    WriteString(writer, sourceFile.Path);
                    WriteString(writer, sourceFile.SourceText);
                    break;
                case Avm1SwdOffsetMapRecord offsetMap:
                    writer.WriteUInt32(offsetMap.ModuleId);
                    writer.WriteUInt32(offsetMap.Line);
                    writer.WriteUInt32(offsetMap.SwfByteOffset);
                    break;
                case Avm1SwdBreakpointRecord breakpoint:
                    writer.WriteUInt16(breakpoint.ModuleId);
                    writer.WriteUInt16(breakpoint.Line);
                    break;
                case Avm1SwdDebugIdRecord debugId:
                    debugId.Id.TryWriteBytes(debugIdBytes);
                    writer.WriteBytes(debugIdBytes);
                    break;
                case Avm1SwdRegisterMapRecord registerMap:
                    writer.WriteUInt32(registerMap.SwfByteOffset);
                    writer.WriteUInt8((byte)registerMap.Registers.Count);
                    foreach (var register in registerMap.Registers)
                    {
                        writer.WriteUInt8(register.Register);
                        WriteString(writer, register.Name);
                    }
                    break;
                default:
                    throw new ArgumentException(
                        $"SWD record type {record.GetType().Name} is unsupported.",
                        nameof(file));
            }
        }
        return writer.WrittenMemory.ToArray();
    }

    private static Avm1SwdSourceFileRecord DecodeSourceFile(MemoryReader reader) =>
        new(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            ReadString(reader),
            ReadString(reader));

    private static Avm1SwdOffsetMapRecord DecodeOffsetMap(MemoryReader reader) =>
        new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());

    private static Avm1SwdBreakpointRecord DecodeBreakpoint(
        MemoryReader reader) =>
        new(reader.ReadUInt16(), reader.ReadUInt16());

    private static Avm1SwdDebugIdRecord DecodeDebugId(MemoryReader reader) =>
        new(new Guid(reader.ReadMemory(16).Span));

    private static Avm1SwdRegisterMapRecord DecodeRegisterMap(
        MemoryReader reader)
    {
        var offset = reader.ReadUInt32();
        var count = reader.ReadUInt8();
        var registers = new Avm1SwdRegisterName[count];
        for (var i = 0; i < registers.Length; i++)
        {
            registers[i] = new Avm1SwdRegisterName(
                reader.ReadUInt8(),
                ReadString(reader));
        }
        return new Avm1SwdRegisterMapRecord(offset, registers);
    }

    private static string ReadString(MemoryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = reader.ReadUInt8();
            if (value == 0)
                break;
            bytes.Add(value);
        }

        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new SwfFormatException("An SWD string is not valid UTF-8.", exception);
        }
    }

    private static void WriteString(MemoryWriter writer, string value)
    {
        Avm1SwdSourceFileRecord.ValidateString(value, nameof(value));
        writer.WriteBytes(StrictUtf8.GetBytes(value));
        writer.WriteUInt8(0);
    }
}
