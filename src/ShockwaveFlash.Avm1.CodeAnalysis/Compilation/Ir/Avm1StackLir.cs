using System.Globalization;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation.Ir;

public readonly record struct Avm1StackLirInstructionIndex(int Value)
{
    public static readonly Avm1StackLirInstructionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "stack" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1StackLirInstructionRange(int Start, int Count)
{
    public static readonly Avm1StackLirInstructionRange Empty = new(0, 0);
}

public readonly record struct Avm1StackLirStringIndex(int Value)
{
    public static readonly Avm1StackLirStringIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1StackLirConstantIndex(int Value)
{
    public static readonly Avm1StackLirConstantIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1StackLirTemporaryIndex(int Value)
{
    public static readonly Avm1StackLirTemporaryIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "temporary" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public enum Avm1StackLirTemporaryStorageKind : byte
{
    Register,
    ActivationName
}

public readonly record struct Avm1StackLirTemporaryStorage(
    Avm1StackLirTemporaryStorageKind Kind,
    byte Register,
    Avm1StackLirStringIndex Name)
{
    public static Avm1StackLirTemporaryStorage InRegister(byte register) =>
        new(
            Avm1StackLirTemporaryStorageKind.Register,
            register,
            Avm1StackLirStringIndex.Invalid);

    public static Avm1StackLirTemporaryStorage InActivation(
        Avm1StackLirStringIndex name) =>
        new(Avm1StackLirTemporaryStorageKind.ActivationName, 0, name);
}

public readonly record struct Avm1StackLirFunctionIndex(int Value)
{
    public static readonly Avm1StackLirFunctionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "function" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1StackLirTryIndex(int Value)
{
    public static readonly Avm1StackLirTryIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "try" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1StackLirWithIndex(int Value)
{
    public static readonly Avm1StackLirWithIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "with" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1StackLirUrlSiteIndex(int Value)
{
    public static readonly Avm1StackLirUrlSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public enum Avm1StackLirInstructionKind : byte
{
    PushConstant,
    PushName,
    PushTemporary,
    StoreTemporary,
    PushRegister,
    StoreRegister,
    DefineFunction,
    Try,
    With,
    SetTarget,
    GotoFrame,
    GotoFrame2,
    WaitForFrame,
    GetUrlImmediate,
    GetUrl2,
    Action,
    ParentBranch,
    Branch
}

public readonly record struct Avm1StackLirInstruction(
    Avm1StackLirInstructionIndex Index,
    Avm1StackLirInstructionKind Kind,
    Avm1StackLirConstantIndex Constant,
    Avm1StackLirStringIndex Name,
    Avm1StackLirTemporaryIndex Temporary,
    Avm1StackLirFunctionIndex Function,
    Avm1StackLirTryIndex TrySite,
    Avm1StackLirWithIndex WithSite,
    Avm1StackLirUrlSiteIndex UrlSite,
    Avm1MirCompletionSiteIndex CompletionSite,
    byte Register,
    bool Play,
    bool HasSceneBias,
    ushort SceneBias,
    ActionOpcode Opcode,
    Avm1MirBlockIndex Target,
    Avm1MirInstructionIndex MirInstruction);

public readonly record struct Avm1StackLirBlock(
    Avm1MirBlockIndex MirBlock,
    Avm1StackLirInstructionRange Instructions);

public readonly record struct Avm1StackLirConstant(
    Avm1StackLirConstantIndex Index,
    Avm1MirConstantKind Kind,
    bool BooleanValue,
    int IntegerValue,
    double NumberValue,
    Avm1StackLirStringIndex StringValue);

public readonly record struct Avm1StackLirFunction(
    Avm1StackLirFunctionIndex Index,
    SourceFunctionIndex SourceFunction,
    bool IsDeclaration);

public readonly record struct Avm1StackLirTry(
    Avm1StackLirTryIndex Index,
    Avm1MirTrySiteIndex MirTrySite);

public readonly record struct Avm1StackLirWith(
    Avm1StackLirWithIndex Index,
    Avm1MirWithSiteIndex MirWithSite);

public readonly record struct Avm1StackLirUrlSite(
    Avm1StackLirUrlSiteIndex Index,
    Avm1StackLirStringIndex Url,
    Avm1StackLirStringIndex Target,
    GetUrlFlags Flags);

public sealed class Avm1StackLirMethod
{
    private readonly Avm1StackLirInstruction[] _instructions;
    private readonly Avm1StackLirBlock[] _blocks;
    private readonly Avm1StackLirConstant[] _constants;
    private readonly Avm1StackLirFunction[] _functions;
    private readonly Avm1StackLirTry[] _trySites;
    private readonly Avm1StackLirWith[] _withSites;
    private readonly Avm1StackLirUrlSite[] _urlSites;
    private readonly string[] _strings;
    private readonly Avm1StackLirTemporaryStorage[] _temporaryStorages;
    private readonly byte[] _temporaryRegisters;

    internal Avm1StackLirMethod(
        Avm1StackLirInstruction[] instructions,
        Avm1StackLirBlock[] blocks,
        Avm1StackLirConstant[] constants,
        Avm1StackLirFunction[] functions,
        Avm1StackLirTry[] trySites,
        Avm1StackLirWith[] withSites,
        Avm1StackLirUrlSite[] urlSites,
        string[] strings,
        Avm1StackLirTemporaryStorage[] temporaryStorages,
        int maximumStackDepth)
    {
        _instructions = instructions;
        _blocks = blocks;
        _constants = constants;
        _functions = functions;
        _trySites = trySites;
        _withSites = withSites;
        _urlSites = urlSites;
        _strings = strings;
        _temporaryStorages = temporaryStorages;
        _temporaryRegisters = temporaryStorages
            .Where(storage =>
                storage.Kind is Avm1StackLirTemporaryStorageKind.Register)
            .Select(storage => storage.Register)
            .ToArray();
        MaximumStackDepth = maximumStackDepth;
    }

    public IReadOnlyList<Avm1StackLirInstruction> Instructions => _instructions;

    public IReadOnlyList<Avm1StackLirBlock> Blocks => _blocks;

    public IReadOnlyList<Avm1StackLirConstant> Constants => _constants;

    public IReadOnlyList<Avm1StackLirFunction> Functions => _functions;

    public IReadOnlyList<Avm1StackLirTry> TrySites => _trySites;

    public IReadOnlyList<Avm1StackLirWith> WithSites => _withSites;

    public IReadOnlyList<Avm1StackLirUrlSite> UrlSites => _urlSites;

    public IReadOnlyList<string> Strings => _strings;

    public IReadOnlyList<Avm1StackLirTemporaryStorage> TemporaryStorages =>
        _temporaryStorages;

    public IReadOnlyList<byte> TemporaryRegisters => _temporaryRegisters;

    public int MaximumStackDepth { get; }

    public Avm1StackLirInstruction this[Avm1StackLirInstructionIndex index] =>
        _instructions[index.Value];

    public Avm1StackLirConstant this[Avm1StackLirConstantIndex index] =>
        _constants[index.Value];

    public Avm1StackLirFunction this[Avm1StackLirFunctionIndex index] =>
        _functions[index.Value];

    public Avm1StackLirTry this[Avm1StackLirTryIndex index] =>
        _trySites[index.Value];

    public Avm1StackLirWith this[Avm1StackLirWithIndex index] =>
        _withSites[index.Value];

    public Avm1StackLirUrlSite this[Avm1StackLirUrlSiteIndex index] =>
        _urlSites[index.Value];

    public string this[Avm1StackLirStringIndex index] => _strings[index.Value];

    public Avm1StackLirTemporaryStorage this[
        Avm1StackLirTemporaryIndex index] =>
        _temporaryStorages[index.Value];
}
