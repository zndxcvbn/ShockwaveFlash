using System.Buffers.Binary;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Exceptions;
using ShockwaveFlash.IO.Binary;

namespace ShockwaveFlash.Avm1.Decompilation.Ir;

public sealed class Avm1InstructionTable
{
    private readonly Dictionary<int, ActionIndex> _offsetToAction;
    private readonly ActionIndex[] _branchTargets;
    private readonly ActionIndex[] _explicitFirstSuccessors;
    private readonly ActionIndex[] _explicitSecondSuccessors;

    private Avm1InstructionTable(
        Avm1Instruction[] instructions,
        Dictionary<int, ActionIndex> offsetToAction,
        ActionIndex[] branchTargets,
        ActionIndex[] explicitFirstSuccessors,
        ActionIndex[] explicitSecondSuccessors,
        Avm1WithInstructionRegion[] withRegions,
        Avm1TryInstructionRegion[] tryRegions,
        int byteLength,
        ReadOnlyMemory<byte> rootBytecode,
        byte swfVersion)
    {
        Instructions = instructions;
        _offsetToAction = offsetToAction;
        _branchTargets = branchTargets;
        _explicitFirstSuccessors = explicitFirstSuccessors;
        _explicitSecondSuccessors = explicitSecondSuccessors;
        WithRegions = withRegions;
        TryRegions = tryRegions;
        ByteLength = byteLength;
        RootBytecode = rootBytecode;
        SwfVersion = swfVersion;
    }

    public IReadOnlyList<Avm1Instruction> Instructions { get; }

    public IReadOnlyList<Avm1WithInstructionRegion> WithRegions { get; }

    public IReadOnlyList<Avm1TryInstructionRegion> TryRegions { get; }

    public int Count => Instructions.Count;

    public int ByteLength { get; }

    public ReadOnlyMemory<byte> RootBytecode { get; }

    public byte SwfVersion { get; }

    public Avm1Instruction this[ActionIndex index] => Instructions[index.Value];

    public static Avm1InstructionTable Build(IReadOnlyList<Action> actions, byte swfVersion)
    {
        var builder = new Builder(swfVersion);
        return builder.Build(actions, Action.EncodeCollection(actions, swfVersion));
    }

    internal static Avm1InstructionTable Build(
        IReadOnlyList<Action> actions,
        ReadOnlyMemory<byte> rootBytecode,
        byte swfVersion)
    {
        var builder = new Builder(swfVersion);
        return builder.Build(actions, rootBytecode);
    }

    public bool TryGetActionAtOffset(int offset, out ActionIndex action)
    {
        return _offsetToAction.TryGetValue(offset, out action);
    }

    public ActionIndex GetBranchTargetAction(ActionIndex branchAction) =>
        branchAction.IsValid && branchAction.Value < _branchTargets.Length
            ? _branchTargets[branchAction.Value]
            : ActionIndex.Invalid;

    public ActionIndex GetExplicitFirstSuccessor(ActionIndex action) =>
        action.IsValid && action.Value < _explicitFirstSuccessors.Length
            ? _explicitFirstSuccessors[action.Value]
            : ActionIndex.Invalid;

    public ActionIndex GetExplicitSecondSuccessor(ActionIndex action) =>
        action.IsValid && action.Value < _explicitSecondSuccessors.Length
            ? _explicitSecondSuccessors[action.Value]
            : ActionIndex.Invalid;

    public int GetBranchTargetOffset(ActionIndex branchAction)
    {
        var target = GetBranchTargetAction(branchAction);
        if (target.Value == Count)
            return ByteLength;
        if (target.IsValid && target.Value < Count)
            return this[target].Offset;

        var instruction = this[branchAction];
        return instruction.Action switch
        {
            ActionIf actionIf => instruction.Offset + instruction.Length + actionIf.BranchOffset,
            ActionJump actionJump => instruction.Offset + instruction.Length + actionJump.BranchOffset,
            _ => instruction.Offset
        };
    }

    public bool IsTerminator(ActionIndex action)
    {
        return this[action].Action is ActionJump or ActionReturn or ActionThrow or ActionEnd;
    }

    private sealed class Builder(byte swfVersion)
    {
        private readonly MemoryWriter _trailerWriter = new();
        private readonly List<Avm1Instruction> _rows = [];
        private readonly List<ActionIndex> _branchTargets = [];
        private readonly List<ActionIndex> _explicitFirstSuccessors = [];
        private readonly List<ActionIndex> _explicitSecondSuccessors = [];
        private readonly List<Dictionary<int, ActionIndex>> _actionsByRegionOffset = [];
        private readonly List<(ActionIndex Action, CodeRegionIndex Region, int TargetOffset)> _branchFixups = [];
        private readonly List<(
            ActionIndex Action,
            CodeRegionIndex Region,
            int FallThroughOffset,
            int SkipTargetOffset)> _skipFixups = [];
        private readonly List<Avm1WithInstructionRegion> _withRegions = [];
        private readonly List<Avm1TryInstructionRegion> _tryRegions = [];
        private readonly Dictionary<int, ActionIndex> _actionsByPhysicalOffset = [];

        public Avm1InstructionTable Build(
            IReadOnlyList<Action> actions,
            ReadOnlyMemory<byte> rootBytecode)
        {
            var byteLength = AddRegion(
                actions,
                CodeRegionIndex.Root,
                rootBytecode,
                physicalBaseOffset: 0);
            GetRegionOffsets(CodeRegionIndex.Root)[byteLength] = new ActionIndex(_rows.Count);
            _actionsByPhysicalOffset[byteLength] = new ActionIndex(_rows.Count);

            foreach (var fixup in _branchFixups)
            {
                if (GetRegionOffsets(fixup.Region).TryGetValue(fixup.TargetOffset, out var target))
                {
                    _branchTargets[fixup.Action.Value] = target;
                    continue;
                }

                var instruction = _rows[fixup.Action.Value];
                var physicalTarget = checked(
                    instruction.Offset - instruction.LocalOffset +
                    fixup.TargetOffset);
                if (_actionsByPhysicalOffset.TryGetValue(
                    physicalTarget,
                    out target))
                {
                    _branchTargets[fixup.Action.Value] = target;
                }
            }

            foreach (var fixup in _skipFixups)
            {
                var offsets = GetRegionOffsets(fixup.Region);
                var first = offsets.TryGetValue(
                    fixup.FallThroughOffset,
                    out var fallThrough)
                        ? fallThrough
                        : ActionIndex.Invalid;
                var second = offsets.TryGetValue(
                    fixup.SkipTargetOffset,
                    out var skipTarget)
                        ? skipTarget
                        : ActionIndex.Invalid;
                SetExplicitSuccessors(fixup.Action, first, second);
            }

            return new Avm1InstructionTable(
                _rows.ToArray(),
                _actionsByPhysicalOffset,
                _branchTargets.ToArray(),
                _explicitFirstSuccessors.ToArray(),
                _explicitSecondSuccessors.ToArray(),
                _withRegions.ToArray(),
                _tryRegions.ToArray(),
                byteLength,
                rootBytecode,
                swfVersion);
        }

        private int AddRegion(
            IReadOnlyList<Action> actions,
            CodeRegionIndex region,
            ReadOnlyMemory<byte> bytecode,
            int physicalBaseOffset)
        {
            var offsets = GetRegionOffsets(region);
            offsets.EnsureCapacity(actions.Count + 1);
            _rows.EnsureCapacity(_rows.Count + actions.Count);
            _branchTargets.EnsureCapacity(_branchTargets.Count + actions.Count);
            _explicitFirstSuccessors.EnsureCapacity(
                _explicitFirstSuccessors.Count + actions.Count);
            _explicitSecondSuccessors.EnsureCapacity(
                _explicitSecondSuccessors.Count + actions.Count);
            if (region == CodeRegionIndex.Root)
                _actionsByPhysicalOffset.EnsureCapacity(actions.Count + 1);

            var lengths = GetEncodedLengths(actions, bytecode);
            var localOffset = 0;
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                var length = lengths[i];
                var physicalOffset = physicalBaseOffset + localOffset;
                var kind = action switch
                {
                    ActionWith => Avm1InstructionKind.WithEnter,
                    ActionTry => Avm1InstructionKind.TryEnter,
                    _ => Avm1InstructionKind.Action
                };
                var index = AddInstruction(
                    region,
                    physicalOffset,
                    localOffset,
                    length,
                    kind,
                    action);
                offsets[localOffset] = index;
                RecordPhysicalOffset(region, physicalOffset, index);
                AddBranchFixup(index, region, localOffset, length, action);
                AddSkipFixup(
                    index,
                    region,
                    lengths,
                    i,
                    localOffset,
                    length,
                    action);

                if (action is ActionWith withAction)
                    AddWithBody(index, withAction, physicalOffset, length);
                else if (action is ActionTry tryAction)
                    AddTryBodies(index, tryAction, physicalOffset, length);

                localOffset += length;
            }

            return localOffset;
        }

        private void AddWithBody(
            ActionIndex enterAction,
            ActionWith withAction,
            int physicalOffset,
            int encodedLength)
        {
            var nestedRegion = AddCodeRegion();
            var headerLength = encodedLength - withAction.Body.Length;
            var nestedActions = Avm1Decompiler.DecodeActions(withAction.Body, swfVersion);
            var bodyStart = new ActionIndex(_rows.Count);
            AddRegion(
                nestedActions,
                nestedRegion,
                withAction.Body,
                physicalOffset + headerLength);

            var exitAction = AddInstruction(
                nestedRegion,
                physicalOffset + encodedLength,
                withAction.Body.Length,
                length: 0,
                Avm1InstructionKind.WithExit,
                Avm1WithEndAction.Instance);
            GetRegionOffsets(nestedRegion)[withAction.Body.Length] = exitAction;
            RecordPhysicalOffset(nestedRegion, physicalOffset + encodedLength, exitAction);
            _withRegions.Add(new Avm1WithInstructionRegion(
                enterAction,
                bodyStart.Value == exitAction.Value ? exitAction : bodyStart,
                exitAction,
                nestedRegion));
        }

        private void AddTryBodies(
            ActionIndex enterAction,
            ActionTry tryAction,
            int physicalOffset,
            int encodedLength)
        {
            var hasCatch = tryAction.Flags.HasFlag(TryFlags.CatchBlock) || !tryAction.CatchBody.IsEmpty;
            var hasFinally = tryAction.Flags.HasFlag(TryFlags.FinallyBlock) || !tryAction.FinallyBody.IsEmpty;
            var bodyLength = tryAction.TryBody.Length + tryAction.CatchBody.Length + tryAction.FinallyBody.Length;
            var headerLength = encodedLength - bodyLength;
            var tryPhysicalOffset = physicalOffset + headerLength;
            var catchPhysicalOffset = tryPhysicalOffset + tryAction.TryBody.Length;
            var finallyPhysicalOffset = catchPhysicalOffset + tryAction.CatchBody.Length;

            var tryRegion = AddCodeRegion();
            var tryBodyStart = new ActionIndex(_rows.Count);
            AddRegion(
                Avm1Decompiler.DecodeActions(tryAction.TryBody, swfVersion),
                tryRegion,
                tryAction.TryBody,
                tryPhysicalOffset);
            var tryExit = AddInstruction(
                tryRegion,
                catchPhysicalOffset,
                tryAction.TryBody.Length,
                length: 0,
                Avm1InstructionKind.TryExit,
                Avm1TryEndAction.Instance);
            GetRegionOffsets(tryRegion)[tryAction.TryBody.Length] = tryExit;
            RecordPhysicalOffset(tryRegion, catchPhysicalOffset, tryExit);
            if (tryBodyStart == tryExit)
                tryBodyStart = tryExit;

            var catchRegion = CodeRegionIndex.Invalid;
            var catchEnter = ActionIndex.Invalid;
            var catchBodyStart = ActionIndex.Invalid;
            var catchExit = ActionIndex.Invalid;
            if (hasCatch)
            {
                catchRegion = AddCodeRegion();
                catchEnter = AddInstruction(
                    catchRegion,
                    catchPhysicalOffset,
                    localOffset: 0,
                    length: 0,
                    Avm1InstructionKind.CatchEnter,
                    new Avm1CatchStartAction(
                        tryAction.Flags.HasFlag(TryFlags.CatchInRegister)
                            ? tryAction.CatchRegister
                            : -1));
                catchBodyStart = new ActionIndex(_rows.Count);
                AddRegion(
                    Avm1Decompiler.DecodeActions(tryAction.CatchBody, swfVersion),
                    catchRegion,
                    tryAction.CatchBody,
                    catchPhysicalOffset);
                catchExit = AddInstruction(
                    catchRegion,
                    finallyPhysicalOffset,
                    tryAction.CatchBody.Length,
                    length: 0,
                    Avm1InstructionKind.CatchExit,
                    Avm1CatchEndAction.Instance);
                GetRegionOffsets(catchRegion)[tryAction.CatchBody.Length] = catchExit;
                RecordPhysicalOffset(catchRegion, finallyPhysicalOffset, catchExit);
                if (catchBodyStart == catchExit)
                    catchBodyStart = catchExit;
            }

            var finallyRegion = CodeRegionIndex.Invalid;
            var finallyEnter = ActionIndex.Invalid;
            var finallyBodyStart = ActionIndex.Invalid;
            var finallyExit = ActionIndex.Invalid;
            if (hasFinally)
            {
                finallyRegion = AddCodeRegion();
                finallyEnter = AddInstruction(
                    finallyRegion,
                    finallyPhysicalOffset,
                    localOffset: 0,
                    length: 0,
                    Avm1InstructionKind.FinallyEnter,
                    Avm1FinallyStartAction.Instance);
                finallyBodyStart = new ActionIndex(_rows.Count);
                AddRegion(
                    Avm1Decompiler.DecodeActions(tryAction.FinallyBody, swfVersion),
                    finallyRegion,
                    tryAction.FinallyBody,
                    finallyPhysicalOffset);
                finallyExit = AddInstruction(
                    finallyRegion,
                    physicalOffset + encodedLength,
                    tryAction.FinallyBody.Length,
                    length: 0,
                    Avm1InstructionKind.FinallyExit,
                    Avm1FinallyEndAction.Instance);
                GetRegionOffsets(finallyRegion)[tryAction.FinallyBody.Length] = finallyExit;
                RecordPhysicalOffset(finallyRegion, physicalOffset + encodedLength, finallyExit);
                if (finallyBodyStart == finallyExit)
                    finallyBodyStart = finallyExit;
            }

            var continuation = new ActionIndex(_rows.Count);
            SetExplicitSuccessors(enterAction, tryBodyStart);
            SetExplicitSuccessors(tryExit, hasFinally ? finallyEnter : continuation);
            if (hasCatch)
            {
                SetExplicitSuccessors(catchEnter, catchBodyStart);
                SetExplicitSuccessors(catchExit, hasFinally ? finallyEnter : continuation);
            }
            if (hasFinally)
            {
                SetExplicitSuccessors(finallyEnter, finallyBodyStart);
                SetExplicitSuccessors(finallyExit, continuation);
            }

            _tryRegions.Add(new Avm1TryInstructionRegion(
                enterAction,
                tryBodyStart,
                tryExit,
                catchEnter,
                catchBodyStart,
                catchExit,
                finallyEnter,
                finallyBodyStart,
                finallyExit,
                continuation,
                tryRegion,
                catchRegion,
                finallyRegion));
        }

        private ActionIndex AddInstruction(
            CodeRegionIndex region,
            int physicalOffset,
            int localOffset,
            int length,
            Avm1InstructionKind kind,
            Action action)
        {
            var index = new ActionIndex(_rows.Count);
            _rows.Add(new Avm1Instruction(
                index,
                region,
                physicalOffset,
                localOffset,
                length,
                kind,
                action));
            _branchTargets.Add(ActionIndex.Invalid);
            _explicitFirstSuccessors.Add(ActionIndex.Invalid);
            _explicitSecondSuccessors.Add(ActionIndex.Invalid);
            return index;
        }

        private CodeRegionIndex AddCodeRegion()
        {
            var region = new CodeRegionIndex(_actionsByRegionOffset.Count);
            _actionsByRegionOffset.Add([]);
            return region;
        }

        private void SetExplicitSuccessors(
            ActionIndex action,
            ActionIndex first,
            ActionIndex? second = null)
        {
            _explicitFirstSuccessors[action.Value] = first;
            _explicitSecondSuccessors[action.Value] = second.HasValue && second.Value.IsValid
                ? second.Value
                : ActionIndex.Invalid;
        }

        private void AddBranchFixup(
            ActionIndex index,
            CodeRegionIndex region,
            int localOffset,
            int length,
            Action action)
        {
            var relativeOffset = action switch
            {
                ActionIf actionIf => actionIf.BranchOffset,
                ActionJump actionJump => actionJump.BranchOffset,
                _ => (short?)null
            };
            if (relativeOffset.HasValue)
            {
                _branchFixups.Add((
                    index,
                    region,
                    localOffset + length + relativeOffset.Value));
            }
        }

        private void AddSkipFixup(
            ActionIndex index,
            CodeRegionIndex region,
            int[] actionLengths,
            int actionPosition,
            int localOffset,
            int length,
            Action action)
        {
            var skipCount = action switch
            {
                ActionWaitForFrame wait => wait.SkipCount,
                ActionWaitForFrame2 wait => wait.SkipCount,
                _ => -1
            };
            if (skipCount < 0)
                return;

            var skipTargetOffset = checked(localOffset + length);
            var skippedEnd = Math.Min(
                actionLengths.Length,
                actionPosition + 1 + skipCount);
            for (var position = actionPosition + 1;
                position < skippedEnd;
                position++)
            {
                skipTargetOffset = checked(
                    skipTargetOffset + actionLengths[position]);
            }

            _skipFixups.Add((
                index,
                region,
                localOffset + length,
                skipTargetOffset));
        }

        private Dictionary<int, ActionIndex> GetRegionOffsets(CodeRegionIndex region)
        {
            while (_actionsByRegionOffset.Count <= region.Value)
                _actionsByRegionOffset.Add([]);
            return _actionsByRegionOffset[region.Value];
        }

        private void RecordPhysicalOffset(
            CodeRegionIndex region,
            int physicalOffset,
            ActionIndex index)
        {
            if (region == CodeRegionIndex.Root || !_actionsByPhysicalOffset.ContainsKey(physicalOffset))
                _actionsByPhysicalOffset[physicalOffset] = index;
        }

        private int[] GetEncodedLengths(
            IReadOnlyList<Action> actions,
            ReadOnlyMemory<byte> bytecode)
        {
            var result = new int[actions.Count];
            var bytes = bytecode.Span;
            var offset = 0;
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                int length;
                if (action is ActionTrailingData trailingData)
                {
                    length = trailingData.Data.Length;
                }
                else if ((byte)action.Opcode < 128)
                {
                    length = 1;
                }
                else
                {
                    if (offset + 3 > bytes.Length)
                    {
                        throw new SwfFormatException(
                            $"AVM1 action {action.Opcode} header exceeds its code region.");
                    }

                    var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(
                        bytes.Slice(offset + 1, sizeof(ushort)));
                    length = checked(3 + payloadLength + GetTrailerLength(action));
                }

                if (length < 0 || offset + length > bytes.Length)
                {
                    throw new SwfFormatException(
                        $"AVM1 action {action.Opcode} exceeds its code region.");
                }

                result[index] = length;
                offset += length;
            }

            if (offset != bytes.Length)
            {
                throw new SwfFormatException(
                    $"AVM1 code region contains {bytes.Length - offset} unindexed bytes.");
            }

            return result;
        }

        private int GetTrailerLength(Action action)
        {
            var knownLength = action switch
            {
                ActionWith withAction => withAction.Body.Length,
                ActionDefineFunction function => function.Body.Length,
                ActionDefineFunction2 function => function.Body.Length,
                ActionTry tryAction => checked(
                    tryAction.TryBody.Length +
                    tryAction.CatchBody.Length +
                    tryAction.FinallyBody.Length),
                _ => 0
            };
            if (knownLength != 0 || action is
                ActionWith or ActionDefineFunction or ActionDefineFunction2 or ActionTry)
            {
                return knownLength;
            }

            _trailerWriter.Reset();
            action.EncodeTrailer(_trailerWriter);
            return _trailerWriter.Position;
        }

    }
}

public enum Avm1InstructionKind : byte
{
    Action,
    WithEnter,
    WithExit,
    TryEnter,
    TryExit,
    CatchEnter,
    CatchExit,
    FinallyEnter,
    FinallyExit
}

public readonly record struct Avm1Instruction(
    ActionIndex Index,
    CodeRegionIndex Region,
    int Offset,
    int LocalOffset,
    int Length,
    Avm1InstructionKind Kind,
    Action Action)
{
    public int EndOffset => Offset + Length;
}

public readonly record struct Avm1WithInstructionRegion(
    ActionIndex EnterAction,
    ActionIndex BodyStartAction,
    ActionIndex ExitAction,
    CodeRegionIndex BodyRegion);

public readonly record struct Avm1TryInstructionRegion(
    ActionIndex EnterAction,
    ActionIndex TryBodyStartAction,
    ActionIndex TryExitAction,
    ActionIndex CatchEnterAction,
    ActionIndex CatchBodyStartAction,
    ActionIndex CatchExitAction,
    ActionIndex FinallyEnterAction,
    ActionIndex FinallyBodyStartAction,
    ActionIndex FinallyExitAction,
    ActionIndex ContinuationAction,
    CodeRegionIndex TryBodyRegion,
    CodeRegionIndex CatchBodyRegion,
    CodeRegionIndex FinallyBodyRegion);

internal sealed class Avm1WithEndAction : Action
{
    public static readonly Avm1WithEndAction Instance = new();

    private Avm1WithEndAction() : base(ActionOpcode.With)
    {
    }
}

internal sealed class Avm1TryEndAction : Action
{
    public static readonly Avm1TryEndAction Instance = new();

    private Avm1TryEndAction() : base(ActionOpcode.Try)
    {
    }
}

internal sealed class Avm1CatchStartAction : Action
{
    public Avm1CatchStartAction(int catchRegister) : base(ActionOpcode.Try)
    {
        CatchRegister = catchRegister;
    }

    public int CatchRegister { get; }
}

internal sealed class Avm1CatchEndAction : Action
{
    public static readonly Avm1CatchEndAction Instance = new();

    private Avm1CatchEndAction() : base(ActionOpcode.Try)
    {
    }
}

internal sealed class Avm1FinallyStartAction : Action
{
    public static readonly Avm1FinallyStartAction Instance = new();

    private Avm1FinallyStartAction() : base(ActionOpcode.Try)
    {
    }
}

internal sealed class Avm1FinallyEndAction : Action
{
    public static readonly Avm1FinallyEndAction Instance = new();

    private Avm1FinallyEndAction() : base(ActionOpcode.Try)
    {
    }
}
