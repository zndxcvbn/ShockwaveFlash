namespace ShockwaveFlash.Avm1.Compilation;

public enum Avm1SwdRecordKind : uint
{
    SourceFile = 0,
    OffsetMap = 1,
    Breakpoint = 2,
    DebugId = 3,
    RegisterMap = 5
}

public abstract class Avm1SwdRecord
{
    protected Avm1SwdRecord(Avm1SwdRecordKind kind)
    {
        Kind = kind;
    }

    public Avm1SwdRecordKind Kind { get; }
}

public sealed class Avm1SwdSourceFileRecord : Avm1SwdRecord
{
    public const uint ActionScriptBitmap = 1;

    public Avm1SwdSourceFileRecord(
        uint moduleId,
        uint bitmap,
        string path,
        string sourceText)
        : base(Avm1SwdRecordKind.SourceFile)
    {
        ValidateString(path, nameof(path));
        ValidateString(sourceText, nameof(sourceText));
        ModuleId = moduleId;
        Bitmap = bitmap;
        Path = path;
        SourceText = sourceText;
    }

    public uint ModuleId { get; }

    public uint Bitmap { get; }

    public string Path { get; }

    public string SourceText { get; }

    internal static void ValidateString(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "SWD strings cannot contain a null character.",
                parameterName);
        }
    }
}

public sealed class Avm1SwdOffsetMapRecord : Avm1SwdRecord
{
    public Avm1SwdOffsetMapRecord(uint moduleId, uint line, uint swfByteOffset)
        : base(Avm1SwdRecordKind.OffsetMap)
    {
        ModuleId = moduleId;
        Line = line;
        SwfByteOffset = swfByteOffset;
    }

    public uint ModuleId { get; }

    public uint Line { get; }

    public uint SwfByteOffset { get; }
}

public sealed class Avm1SwdBreakpointRecord : Avm1SwdRecord
{
    public Avm1SwdBreakpointRecord(ushort moduleId, ushort line)
        : base(Avm1SwdRecordKind.Breakpoint)
    {
        ModuleId = moduleId;
        Line = line;
    }

    public ushort ModuleId { get; }

    public ushort Line { get; }
}

public sealed class Avm1SwdDebugIdRecord : Avm1SwdRecord
{
    public Avm1SwdDebugIdRecord(Guid id)
        : base(Avm1SwdRecordKind.DebugId)
    {
        Id = id;
    }

    public Guid Id { get; }
}

public readonly record struct Avm1SwdRegisterName(byte Register, string Name);

public sealed class Avm1SwdRegisterMapRecord : Avm1SwdRecord
{
    private readonly Avm1SwdRegisterName[] _registers;

    public Avm1SwdRegisterMapRecord(
        uint swfByteOffset,
        IEnumerable<Avm1SwdRegisterName> registers)
        : base(Avm1SwdRecordKind.RegisterMap)
    {
        ArgumentNullException.ThrowIfNull(registers);
        _registers = registers.ToArray();
        if (_registers.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registers),
                "An SWD register map cannot contain more than 255 registers.");
        }
        foreach (var register in _registers)
        {
            Avm1SwdSourceFileRecord.ValidateString(
                register.Name,
                nameof(registers));
        }
        SwfByteOffset = swfByteOffset;
    }

    public uint SwfByteOffset { get; }

    public IReadOnlyList<Avm1SwdRegisterName> Registers => _registers;
}

public sealed class Avm1SwdFile
{
    public const byte MinimumVersion = 6;
    public const byte CurrentVersion = 7;

    private readonly Avm1SwdRecord[] _records;

    public Avm1SwdFile(byte version, IEnumerable<Avm1SwdRecord> records)
    {
        if (version < MinimumVersion)
            throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToArray();
        if (_records.Any(static record => record is null))
        {
            throw new ArgumentException(
                "An SWD file cannot contain a null record.",
                nameof(records));
        }
        Version = version;
    }

    public byte Version { get; }

    public IReadOnlyList<Avm1SwdRecord> Records => _records;
}

public sealed class Avm1SwdArtifact
{
    internal Avm1SwdArtifact(
        Avm1SwdFile file,
        ReadOnlyMemory<byte> bytes,
        Avm1LinkedCompilerDebugMap debugMap)
    {
        File = file;
        Bytes = bytes;
        DebugMap = debugMap;
    }

    public Avm1SwdFile File { get; }

    public ReadOnlyMemory<byte> Bytes { get; }

    public Avm1LinkedCompilerDebugMap DebugMap { get; }
}
