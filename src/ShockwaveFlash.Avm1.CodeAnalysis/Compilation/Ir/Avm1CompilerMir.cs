using System.Globalization;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation.Ir;

public readonly record struct Avm1MirInstructionIndex(int Value)
{
    public static readonly Avm1MirInstructionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "mir" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirBlockIndex(int Value)
{
    public static readonly Avm1MirBlockIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "block" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirValueIndex(int Value)
{
    public static readonly Avm1MirValueIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "value" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirStringIndex(int Value)
{
    public static readonly Avm1MirStringIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1MirConstantIndex(int Value)
{
    public static readonly Avm1MirConstantIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;
}

public readonly record struct Avm1MirLValueIndex(int Value)
{
    public static readonly Avm1MirLValueIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "lvalue" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirCallSiteIndex(int Value)
{
    public static readonly Avm1MirCallSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "call" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirAggregateSiteIndex(int Value)
{
    public static readonly Avm1MirAggregateSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "aggregate" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirUrlSiteIndex(int Value)
{
    public static readonly Avm1MirUrlSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "url-site" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirFunctionSiteIndex(int Value)
{
    public static readonly Avm1MirFunctionSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "function-site" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirTrySiteIndex(int Value)
{
    public static readonly Avm1MirTrySiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "try-site" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirWithSiteIndex(int Value)
{
    public static readonly Avm1MirWithSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "with-site" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirCompletionSiteIndex(int Value)
{
    public static readonly Avm1MirCompletionSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "completion-site" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirPhiSiteIndex(int Value)
{
    public static readonly Avm1MirPhiSiteIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "phi" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirControlTargetIndex(int Value)
{
    public static readonly Avm1MirControlTargetIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "control" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirTemporaryIndex(int Value)
{
    public static readonly Avm1MirTemporaryIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "temporary" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct Avm1MirValueRange(int Start, int Count)
{
    public static readonly Avm1MirValueRange Empty = new(0, 0);
}

public readonly record struct Avm1MirInstructionRange(int Start, int Count)
{
    public static readonly Avm1MirInstructionRange Empty = new(0, 0);
}

public readonly record struct Avm1MirPhiIncomingRange(int Start, int Count)
{
    public static readonly Avm1MirPhiIncomingRange Empty = new(0, 0);
}

public readonly record struct Avm1MirVisibleControlScopeRange(int Start, int Count)
{
    public static readonly Avm1MirVisibleControlScopeRange Empty = new(0, 0);
}

public enum Avm1MirInstructionKind : byte
{
    Constant,
    LoadTemporary,
    StoreTemporary,
    BeginEnumeration,
    EnumerateNext,
    EndEnumeration,
    LoadLValue,
    EvaluateName,
    LoadCurrentFunction,
    DeclareLocal,
    DefineFunction,
    Try,
    With,
    SetTargetImmediate,
    SetTarget,
    InitializePendingCompletion,
    LoadPendingCompletion,
    SetPendingCompletion,
    StoreLValue,
    DeleteLValue,
    Invoke,
    Aggregate,
    Intrinsic,
    StorePhi,
    Phi,
    Unary,
    Binary,
    Discard,
    Return,
    Throw,
    Break,
    Continue,
    ExternalControlBranch,
    Branch,
    BranchIfTrue,
    WaitForFrameImmediate,
    WaitForFrame,
    TimelineNextFrame,
    TimelinePreviousFrame,
    TimelinePlay,
    TimelineStop,
    TimelineStopSounds,
    TimelineToggleQuality,
    TimelineCall,
    TimelineGotoImmediateAndPlay,
    TimelineGotoImmediateAndStop,
    TimelineGotoAndPlay,
    TimelineGotoAndStop,
    GetUrlImmediate,
    GetUrl2,
    GetTime,
    Trace
}

public enum Avm1MirLValueKind : byte
{
    Name,
    ComputedName,
    Register,
    Member,
    CapturedMember
}

public enum Avm1MirInvocationKind : byte
{
    Call,
    Construct
}

public enum Avm1MirInvocationTargetKind : byte
{
    Name,
    Member,
    Value
}

public enum Avm1MirAggregateKind : byte
{
    Array,
    Object
}

public enum Avm1MirEvaluationOrder : byte
{
    Forward,
    Reverse
}

public enum Avm1MirInvocationTargetOrder : byte
{
    BeforeArguments,
    AfterArguments
}

public enum Avm1MirControlTargetKind : byte
{
    Loop,
    Switch
}

public enum Avm1MirCompletionKind : byte
{
    Break,
    Continue
}

public enum Avm1MirCompletionStorageKind : byte
{
    None,
    ActivationName,
    Register
}

public readonly record struct Avm1MirCompletionStorage(
    Avm1MirCompletionStorageKind Kind,
    Avm1MirStringIndex Name,
    byte Register)
{
    public static readonly Avm1MirCompletionStorage None = new(
        Avm1MirCompletionStorageKind.None,
        Avm1MirStringIndex.Invalid,
        0);

    public static Avm1MirCompletionStorage Activation(
        Avm1MirStringIndex name) =>
        new(Avm1MirCompletionStorageKind.ActivationName, name, 0);

    public static Avm1MirCompletionStorage PhysicalRegister(byte register) =>
        new(Avm1MirCompletionStorageKind.Register, Avm1MirStringIndex.Invalid, register);
}

public enum Avm1MirNameKind : byte
{
    None,
    BoundSymbol,
    DynamicName,
    QualifiedName
}

public enum Avm1MirOperator : byte
{
    None,
    LogicalNot,
    TypeOf,
    ToNumber,
    ToInteger,
    RandomNumber,
    StringLength,
    MbStringLength,
    CharToAscii,
    AsciiToChar,
    MbCharToAscii,
    MbAsciiToChar,
    StringExtract,
    MbStringExtract,
    GetProperty,
    SetProperty,
    CloneSprite,
    RemoveSprite,
    StartDrag,
    EndDrag,
    Increment,
    Decrement,
    Add,
    StringAdd,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    StrictEqual,
    StrictNotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    BitAnd,
    BitOr,
    BitXor,
    ShiftLeft,
    ShiftRight,
    ShiftRightUnsigned,
    InstanceOf,
    ToString,
    TargetPath
}

[Flags]
public enum Avm1MirEffect : ushort
{
    None = 0,
    ReadsActivation = 1 << 0,
    WritesActivation = 1 << 1,
    ReadsDynamicScope = 1 << 2,
    WritesDynamicScope = 1 << 3,
    MayInvokeUserCode = 1 << 4,
    MayThrow = 1 << 5,
    ReadsHeap = 1 << 6,
    WritesHeap = 1 << 7,
    Completion = 1 << 8,
    Allocates = 1 << 9,
    ControlFlow = 1 << 10,
    ReadsRuntimeState = 1 << 11,
    WritesRuntimeState = 1 << 12
}

public enum Avm1MirConstantKind : byte
{
    Undefined,
    Null,
    Boolean,
    Integer,
    Number,
    String
}

public readonly record struct Avm1MirInstruction(
    Avm1MirInstructionIndex Index,
    Avm1MirInstructionKind Kind,
    Avm1MirValueIndex Result,
    Avm1MirValueIndex Operand,
    Avm1MirValueIndex SecondaryOperand,
    Avm1MirLValueIndex LValue,
    Avm1MirCallSiteIndex CallSite,
    Avm1MirAggregateSiteIndex AggregateSite,
    Avm1MirUrlSiteIndex UrlSite,
    Avm1MirFunctionSiteIndex FunctionSite,
    Avm1MirTrySiteIndex TrySite,
    Avm1MirWithSiteIndex WithSite,
    Avm1MirCompletionSiteIndex CompletionSite,
    Avm1MirCompletionStorage CompletionStorage,
    Avm1MirPhiSiteIndex PhiSite,
    Avm1MirControlTargetIndex ControlTarget,
    Avm1MirTemporaryIndex Temporary,
    Avm1MirStringIndex Name,
    Avm1MirConstantIndex Constant,
    Avm1MirNameKind NameKind,
    Avm1MirOperator Operator,
    Avm1MirEffect Effects,
    Avm1MirBlockIndex Target,
    Avm1MirBlockIndex AlternativeTarget,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirBasicBlock(
    Avm1MirBlockIndex Index,
    Avm1MirInstructionRange Instructions,
    bool IsReachable);

public readonly record struct Avm1MirLValue(
    Avm1MirLValueIndex Index,
    Avm1MirLValueKind Kind,
    Avm1MirValueIndex Receiver,
    Avm1MirValueIndex Key,
    Avm1MirTemporaryIndex ReceiverTemporary,
    Avm1MirTemporaryIndex KeyTemporary,
    Avm1MirStringIndex Name,
    Avm1MirNameKind NameKind,
    SourceOriginIndex Origin,
    SourceSymbolIndex Symbol,
    byte Register);

public readonly record struct Avm1MirCallSite(
    Avm1MirCallSiteIndex Index,
    Avm1MirInvocationKind Kind,
    Avm1MirInvocationTargetKind TargetKind,
    Avm1MirValueIndex Target,
    Avm1MirValueIndex MemberName,
    Avm1MirStringIndex Name,
    Avm1MirNameKind NameKind,
    SourceSymbolIndex Symbol,
    Avm1MirValueRange Arguments,
    Avm1MirEvaluationOrder ArgumentEvaluationOrder,
    Avm1MirInvocationTargetOrder TargetEvaluationOrder,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirAggregateSite(
    Avm1MirAggregateSiteIndex Index,
    Avm1MirAggregateKind Kind,
    Avm1MirValueRange Values,
    Avm1MirEvaluationOrder ValueEvaluationOrder,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirUrlSite(
    Avm1MirUrlSiteIndex Index,
    Avm1MirStringIndex Url,
    Avm1MirStringIndex Target,
    GetUrlFlags Flags);

public readonly record struct Avm1MirFunctionSite(
    Avm1MirFunctionSiteIndex Index,
    SourceFunctionIndex Function,
    Avm1MirStringIndex Name,
    bool IsDeclaration,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirTrySite(
    Avm1MirTrySiteIndex Index,
    SourceStatementIndex Statement,
    SourceStatementIndex TryBody,
    SourceStatementIndex CatchBody,
    SourceStatementIndex FinallyBody,
    SourceSymbolIndex CatchSymbol,
    Avm1MirCompletionStorage CompletionStorage,
    Avm1MirVisibleControlScopeRange VisibleControlScopes,
    bool UsesPhysicalParentBranches,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirWithSite(
    Avm1MirWithSiteIndex Index,
    SourceStatementIndex Statement,
    SourceStatementIndex Body,
    Avm1MirCompletionStorage CompletionStorage,
    Avm1MirVisibleControlScopeRange VisibleControlScopes,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirVisibleControlScope(
    SourceStatementIndex Statement,
    Avm1MirControlTargetKind Kind,
    SourceLabelIndex Label,
    bool IsEnumeration);

public readonly record struct Avm1MirCompletionSite(
    Avm1MirCompletionSiteIndex Index,
    Avm1MirCompletionKind Kind,
    SourceStatementIndex TargetStatement,
    Avm1MirCompletionStorage Storage,
    int Token,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirPhiIncoming(
    Avm1MirBlockIndex Predecessor,
    Avm1MirValueIndex Value);

public readonly record struct Avm1MirPhiSite(
    Avm1MirPhiSiteIndex Index,
    Avm1MirBlockIndex MergeBlock,
    Avm1MirPhiIncomingRange Incomings,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirControlTarget(
    Avm1MirControlTargetIndex Index,
    Avm1MirControlTargetKind Kind,
    SourceStatementIndex Statement,
    Avm1MirBlockIndex BreakBlock,
    Avm1MirBlockIndex ContinueBlock,
    Avm1MirStringIndex Name,
    bool HasPhysicalReferences,
    SourceOriginIndex Origin);

public readonly record struct Avm1MirConstant(
    Avm1MirConstantIndex Index,
    Avm1MirConstantKind Kind,
    bool BooleanValue,
    int IntegerValue,
    double NumberValue,
    Avm1MirStringIndex StringValue);

public sealed class Avm1MirMethod
{
    private readonly Avm1MirInstruction[] _instructions;
    private readonly Avm1MirBasicBlock[] _blocks;
    private readonly Avm1MirBlockIndex[] _blockLayout;
    private readonly Avm1MirLValue[] _lValues;
    private readonly Avm1MirCallSite[] _callSites;
    private readonly Avm1MirValueIndex[] _callArguments;
    private readonly Avm1MirAggregateSite[] _aggregateSites;
    private readonly Avm1MirValueIndex[] _aggregateValues;
    private readonly Avm1MirUrlSite[] _urlSites;
    private readonly Avm1MirFunctionSite[] _functionSites;
    private readonly Avm1MirTrySite[] _trySites;
    private readonly Avm1MirVisibleControlScope[] _visibleControlScopes;
    private readonly Avm1MirWithSite[] _withSites;
    private readonly Avm1MirVisibleControlScope[] _withVisibleControlScopes;
    private readonly Avm1MirCompletionSite[] _completionSites;
    private readonly Avm1MirPhiSite[] _phiSites;
    private readonly Avm1MirPhiIncoming[] _phiIncomings;
    private readonly Avm1MirControlTarget[] _controlTargets;
    private readonly int _temporaryCount;
    private readonly Avm1MirConstant[] _constants;
    private readonly string[] _strings;

    internal Avm1MirMethod(
        Avm1MirInstruction[] instructions,
        Avm1MirBasicBlock[] blocks,
        Avm1MirBlockIndex[] blockLayout,
        Avm1MirBlockIndex entryBlock,
        Avm1MirLValue[] lValues,
        Avm1MirCallSite[] callSites,
        Avm1MirValueIndex[] callArguments,
        Avm1MirAggregateSite[] aggregateSites,
        Avm1MirValueIndex[] aggregateValues,
        Avm1MirUrlSite[] urlSites,
        Avm1MirFunctionSite[] functionSites,
        Avm1MirTrySite[] trySites,
        Avm1MirVisibleControlScope[] visibleControlScopes,
        Avm1MirWithSite[] withSites,
        Avm1MirVisibleControlScope[] withVisibleControlScopes,
        Avm1MirCompletionSite[] completionSites,
        Avm1MirPhiSite[] phiSites,
        Avm1MirPhiIncoming[] phiIncomings,
        Avm1MirControlTarget[] controlTargets,
        int temporaryCount,
        Avm1MirConstant[] constants,
        string[] strings,
        int valueCount)
    {
        _instructions = instructions;
        _blocks = blocks;
        _blockLayout = blockLayout;
        EntryBlock = entryBlock;
        _lValues = lValues;
        _callSites = callSites;
        _callArguments = callArguments;
        _aggregateSites = aggregateSites;
        _aggregateValues = aggregateValues;
        _urlSites = urlSites;
        _functionSites = functionSites;
        _trySites = trySites;
        _visibleControlScopes = visibleControlScopes;
        _withSites = withSites;
        _withVisibleControlScopes = withVisibleControlScopes;
        _completionSites = completionSites;
        _phiSites = phiSites;
        _phiIncomings = phiIncomings;
        _controlTargets = controlTargets;
        _temporaryCount = temporaryCount;
        _constants = constants;
        _strings = strings;
        ValueCount = valueCount;
    }

    public IReadOnlyList<Avm1MirInstruction> Instructions => _instructions;

    public IReadOnlyList<Avm1MirBasicBlock> Blocks => _blocks;

    public IReadOnlyList<Avm1MirBlockIndex> BlockLayout => _blockLayout;

    public Avm1MirBlockIndex EntryBlock { get; }

    public IReadOnlyList<Avm1MirLValue> LValues => _lValues;

    public IReadOnlyList<Avm1MirCallSite> CallSites => _callSites;

    public IReadOnlyList<Avm1MirValueIndex> CallArguments => _callArguments;

    public IReadOnlyList<Avm1MirAggregateSite> AggregateSites => _aggregateSites;

    public IReadOnlyList<Avm1MirValueIndex> AggregateValues => _aggregateValues;

    public IReadOnlyList<Avm1MirUrlSite> UrlSites => _urlSites;

    public IReadOnlyList<Avm1MirFunctionSite> FunctionSites => _functionSites;

    public IReadOnlyList<Avm1MirTrySite> TrySites => _trySites;

    public IReadOnlyList<Avm1MirVisibleControlScope> VisibleControlScopes =>
        _visibleControlScopes;

    public IReadOnlyList<Avm1MirWithSite> WithSites => _withSites;

    public IReadOnlyList<Avm1MirVisibleControlScope> WithVisibleControlScopes =>
        _withVisibleControlScopes;

    public IReadOnlyList<Avm1MirCompletionSite> CompletionSites => _completionSites;

    public IReadOnlyList<Avm1MirPhiSite> PhiSites => _phiSites;

    public IReadOnlyList<Avm1MirPhiIncoming> PhiIncomings => _phiIncomings;

    public IReadOnlyList<Avm1MirControlTarget> ControlTargets => _controlTargets;

    public int TemporaryCount => _temporaryCount;

    public IReadOnlyList<Avm1MirConstant> Constants => _constants;

    public IReadOnlyList<string> Strings => _strings;

    public int ValueCount { get; }

    public Avm1MirInstruction this[Avm1MirInstructionIndex index] =>
        _instructions[index.Value];

    public Avm1MirBasicBlock this[Avm1MirBlockIndex index] =>
        _blocks[index.Value];

    public Avm1MirLValue this[Avm1MirLValueIndex index] =>
        _lValues[index.Value];

    public Avm1MirCallSite this[Avm1MirCallSiteIndex index] =>
        _callSites[index.Value];

    public Avm1MirAggregateSite this[Avm1MirAggregateSiteIndex index] =>
        _aggregateSites[index.Value];

    public Avm1MirUrlSite this[Avm1MirUrlSiteIndex index] =>
        _urlSites[index.Value];

    public Avm1MirFunctionSite this[Avm1MirFunctionSiteIndex index] =>
        _functionSites[index.Value];

    public Avm1MirTrySite this[Avm1MirTrySiteIndex index] =>
        _trySites[index.Value];

    public Avm1MirWithSite this[Avm1MirWithSiteIndex index] =>
        _withSites[index.Value];

    public Avm1MirCompletionSite this[Avm1MirCompletionSiteIndex index] =>
        _completionSites[index.Value];

    public Avm1MirPhiSite this[Avm1MirPhiSiteIndex index] =>
        _phiSites[index.Value];

    public Avm1MirControlTarget this[Avm1MirControlTargetIndex index] =>
        _controlTargets[index.Value];

    public Avm1MirValueIndex GetCallArgument(
        Avm1MirCallSite callSite,
        int index) =>
        _callArguments[callSite.Arguments.Start + index];

    public Avm1MirValueIndex GetAggregateValue(
        Avm1MirAggregateSite aggregateSite,
        int index) =>
        _aggregateValues[aggregateSite.Values.Start + index];

    public Avm1MirPhiIncoming GetPhiIncoming(
        Avm1MirPhiSite phiSite,
        int index) =>
        _phiIncomings[phiSite.Incomings.Start + index];

    public Avm1MirVisibleControlScope GetVisibleControlScope(
        Avm1MirTrySite trySite,
        int index) =>
        _visibleControlScopes[trySite.VisibleControlScopes.Start + index];

    public Avm1MirVisibleControlScope GetVisibleControlScope(
        Avm1MirWithSite withSite,
        int index) =>
        _withVisibleControlScopes[withSite.VisibleControlScopes.Start + index];

    public Avm1MirConstant this[Avm1MirConstantIndex index] =>
        _constants[index.Value];

    public string this[Avm1MirStringIndex index] => _strings[index.Value];
}
