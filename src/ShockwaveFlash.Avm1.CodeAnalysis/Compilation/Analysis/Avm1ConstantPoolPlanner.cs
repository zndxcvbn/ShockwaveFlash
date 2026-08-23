using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal sealed class Avm1ConstantPoolSegment
{
    private readonly string[] _entries;
    private readonly Dictionary<string, ushort> _indices;

    public static Avm1ConstantPoolSegment Empty { get; } = new([], -1, 0);

    public Avm1ConstantPoolSegment(
        string[] entries,
        int lastReferenceInstruction,
        long estimatedSavings)
    {
        _entries = entries;
        _indices = new Dictionary<string, ushort>(
            entries.Length,
            StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index++)
            _indices.Add(entries[index], (ushort)index);

        LastReferenceInstruction = lastReferenceInstruction;
        EstimatedSavings = estimatedSavings;
    }

    public IReadOnlyList<string> Entries => _entries;

    public bool IsEmpty => _entries.Length == 0;

    public int LastReferenceInstruction { get; }

    public long EstimatedSavings { get; }

    public PushValue CreatePushValue(string value)
    {
        if (!_indices.TryGetValue(value, out var index))
            return PushValue.String(value);

        return index < 256
            ? PushValue.Constant8((byte)index)
            : PushValue.Constant16(index);
    }
}

internal sealed class Avm1ConstantPoolPlan
{
    private readonly Avm1ConstantPoolSegment _globalSegment;
    private readonly Avm1ConstantPoolSegment[] _segmentsByBlock;

    public static Avm1ConstantPoolPlan Empty { get; } = new(
        Avm1ConstantPoolSegment.Empty,
        [],
        isSegmented: false);

    private Avm1ConstantPoolPlan(
        Avm1ConstantPoolSegment globalSegment,
        Avm1ConstantPoolSegment[] segmentsByBlock,
        bool isSegmented)
    {
        _globalSegment = globalSegment;
        _segmentsByBlock = segmentsByBlock;
        IsSegmented = isSegmented;
    }

    public bool IsEmpty =>
        _globalSegment.IsEmpty && _segmentsByBlock.Length == 0;

    public bool IsSegmented { get; }

    public int SegmentCount => IsEmpty
        ? 0
        : IsSegmented
            ? _segmentsByBlock.Count(segment => !segment.IsEmpty)
            : 1;

    public static Avm1ConstantPoolPlan Global(
        Avm1ConstantPoolSegment segment) =>
        segment.IsEmpty
            ? Empty
            : new Avm1ConstantPoolPlan(segment, [], isSegmented: false);

    public static Avm1ConstantPoolPlan Segmented(
        Avm1ConstantPoolSegment[] segmentsByBlock) =>
        segmentsByBlock.All(segment => segment.IsEmpty)
            ? Empty
            : new Avm1ConstantPoolPlan(
                Avm1ConstantPoolSegment.Empty,
                segmentsByBlock,
                isSegmented: true);

    public Avm1ConstantPoolSegment GetSegment(Avm1MirBlockIndex block)
    {
        if (!IsSegmented)
            return _globalSegment;
        if (!block.IsValid || block.Value >= _segmentsByBlock.Length)
            return Avm1ConstantPoolSegment.Empty;
        return _segmentsByBlock[block.Value];
    }

    public bool ShouldEmitAtBlockStart(
        Avm1MirBlockIndex block,
        Avm1MirBlockIndex entryBlock)
    {
        var segment = GetSegment(block);
        return !segment.IsEmpty && (IsSegmented || block == entryBlock);
    }

    public bool ShouldRestoreAfterTry(
        Avm1MirBlockIndex block,
        int instructionIndex)
    {
        var segment = GetSegment(block);
        return !segment.IsEmpty &&
            (!IsSegmented || instructionIndex < segment.LastReferenceInstruction);
    }
}

internal static class Avm1ConstantPoolPlanner
{
    private const int MaximumPoolPayloadLength = ushort.MaxValue;
    private const int MaximumSegmentedPoolPayloadLength =
        Avm1PhysicalLayout.MaximumTrampolineHopDistance - 3;
    private const int MaximumPoolEntries = ushort.MaxValue;

    public static Avm1ConstantPoolPlan Create(
        Avm1StackLirMethod stackLir,
        Avm1CompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(stackLir);
        ArgumentNullException.ThrowIfNull(options);
        if (options.OptimizationLevel is not Avm1OptimizationLevel.Basic ||
            options.SwfVersion < 5)
        {
            return Avm1ConstantPoolPlan.Empty;
        }

        var encoding = new Avm1Context(options.SwfVersion).Encoding;
        var globalCandidates = new Dictionary<string, CandidateBuilder>(
            StringComparer.Ordinal);
        var maximumBlockIndex = stackLir.Blocks.Count == 0
            ? -1
            : stackLir.Blocks.Max(block => block.MirBlock.Value);
        var segmentsByBlock = new Avm1ConstantPoolSegment[maximumBlockIndex + 1];
        Array.Fill(segmentsByBlock, Avm1ConstantPoolSegment.Empty);
        var protectedRegionCount = 0;
        long segmentedSavings = 0;

        foreach (var block in stackLir.Blocks)
        {
            var blockCandidates = new Dictionary<string, CandidateBuilder>(
                StringComparer.Ordinal);
            var blockProtectedRegionCount = 0;
            var instructionEnd = block.Instructions.Start + block.Instructions.Count;
            for (var instructionIndex = block.Instructions.Start;
                instructionIndex < instructionEnd;
                instructionIndex++)
            {
                var instruction = stackLir.Instructions[instructionIndex];
                if (instruction.Kind is Avm1StackLirInstructionKind.Try)
                {
                    protectedRegionCount++;
                    blockProtectedRegionCount++;
                }

                if (!TryGetString(stackLir, instruction, out var value) ||
                    value.Contains('\0'))
                {
                    continue;
                }

                AddOccurrence(globalCandidates, value, instructionIndex);
                AddOccurrence(blockCandidates, value, instructionIndex);
            }

            var segment = CreateSegment(
                blockCandidates,
                encoding,
                1 + blockProtectedRegionCount,
                MaximumSegmentedPoolPayloadLength);
            if (!segment.IsEmpty)
            {
                segmentsByBlock[block.MirBlock.Value] = segment;
                segmentedSavings += segment.EstimatedSavings;
            }
        }

        var globalSegment = CreateSegment(
            globalCandidates,
            encoding,
            1 + protectedRegionCount,
            MaximumPoolPayloadLength);
        if (segmentedSavings > globalSegment.EstimatedSavings)
            return Avm1ConstantPoolPlan.Segmented(segmentsByBlock);
        return Avm1ConstantPoolPlan.Global(globalSegment);
    }

    private static Avm1ConstantPoolSegment CreateSegment(
        Dictionary<string, CandidateBuilder> candidatesByValue,
        System.Text.Encoding encoding,
        int poolCopies,
        int maximumPayloadLength)
    {
        if (candidatesByValue.Count == 0)
            return Avm1ConstantPoolSegment.Empty;

        var candidates = candidatesByValue.Values
            .Select(candidate => candidate.ToCandidate(
                encoding.GetByteCount(candidate.Value),
                poolCopies))
            .Where(candidate =>
                candidate.Net8 > 0 &&
                candidate.ByteLength <=
                    maximumPayloadLength - sizeof(ushort) - 1)
            .ToArray();
        if (candidates.Length == 0)
            return Avm1ConstantPoolSegment.Empty;

        var eightBitCandidates = candidates
            .OrderByDescending(candidate => candidate.SlotBenefit)
            .ThenByDescending(candidate => candidate.Occurrences)
            .ThenByDescending(candidate => candidate.ByteLength)
            .ThenBy(candidate => candidate.FirstOccurrence)
            .ThenBy(candidate => candidate.Value, StringComparer.Ordinal)
            .Take(256)
            .ToArray();
        var eightBitValues = eightBitCandidates
            .Select(candidate => candidate.Value)
            .ToHashSet(StringComparer.Ordinal);

        var orderedCandidates = eightBitCandidates
            .Concat(candidates
                .Where(candidate =>
                    candidate.Net16 > 0 &&
                    !eightBitValues.Contains(candidate.Value))
                .OrderByDescending(candidate => candidate.Net16)
                .ThenByDescending(candidate => candidate.Occurrences)
                .ThenByDescending(candidate => candidate.ByteLength)
                .ThenBy(candidate => candidate.FirstOccurrence)
                .ThenBy(candidate => candidate.Value, StringComparer.Ordinal));

        var selected = new List<Candidate>(
            Math.Min(candidates.Length, MaximumPoolEntries));
        var payloadLength = sizeof(ushort);
        foreach (var candidate in orderedCandidates)
        {
            if (selected.Count >= MaximumPoolEntries)
                break;

            var entryLength = candidate.ByteLength + 1;
            if (entryLength > maximumPayloadLength - payloadLength)
                continue;

            selected.Add(candidate);
            payloadLength += entryLength;
        }

        var netSavings = CalculateNetSavings(selected, poolCopies);
        if (selected.Count == 0 || netSavings <= 0)
            return Avm1ConstantPoolSegment.Empty;

        return new Avm1ConstantPoolSegment(
            selected.Select(candidate => candidate.Value).ToArray(),
            selected.Max(candidate => candidate.LastOccurrence),
            netSavings);
    }

    private static void AddOccurrence(
        Dictionary<string, CandidateBuilder> candidates,
        string value,
        int instructionIndex)
    {
        if (candidates.TryGetValue(value, out var candidate))
        {
            candidate.AddOccurrence(instructionIndex);
            return;
        }

        candidates.Add(value, new CandidateBuilder(value, instructionIndex));
    }

    private static bool TryGetString(
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        out string value)
    {
        if (instruction.Kind is Avm1StackLirInstructionKind.PushName)
            return TryGetString(stackLir, instruction.Name, out value);

        if (instruction.Kind is Avm1StackLirInstructionKind.PushConstant &&
            instruction.Constant.IsValid &&
            instruction.Constant.Value < stackLir.Constants.Count)
        {
            var constant = stackLir[instruction.Constant];
            if (constant.Kind is Avm1MirConstantKind.String)
                return TryGetString(stackLir, constant.StringValue, out value);
        }

        if (instruction.Kind is (
                Avm1StackLirInstructionKind.PushTemporary or
                Avm1StackLirInstructionKind.StoreTemporary) &&
            instruction.Temporary.IsValid &&
            instruction.Temporary.Value < stackLir.TemporaryStorages.Count)
        {
            var storage = stackLir[instruction.Temporary];
            if (storage.Kind is Avm1StackLirTemporaryStorageKind.ActivationName)
                return TryGetString(stackLir, storage.Name, out value);
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetString(
        Avm1StackLirMethod stackLir,
        Avm1StackLirStringIndex index,
        out string value)
    {
        if (index.IsValid && index.Value < stackLir.Strings.Count)
        {
            value = stackLir[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static long CalculateNetSavings(
        IReadOnlyList<Candidate> selected,
        int poolCopies)
    {
        long grossSavings = 0;
        long poolSize = 5;
        for (var index = 0; index < selected.Count; index++)
        {
            var candidate = selected[index];
            var referencePenalty = index < 256 ? 0 : 1;
            grossSavings += (long)candidate.Occurrences *
                (candidate.ByteLength - referencePenalty);
            poolSize += candidate.ByteLength + 1;
        }
        return grossSavings - poolCopies * poolSize;
    }

    private sealed class CandidateBuilder
    {
        public CandidateBuilder(string value, int firstOccurrence)
        {
            Value = value;
            FirstOccurrence = firstOccurrence;
            LastOccurrence = firstOccurrence;
            Occurrences = 1;
        }

        public string Value { get; }

        public int FirstOccurrence { get; }

        public int LastOccurrence { get; private set; }

        public int Occurrences { get; private set; }

        public void AddOccurrence(int instructionIndex)
        {
            Occurrences++;
            LastOccurrence = instructionIndex;
        }

        public Candidate ToCandidate(int byteLength, int poolCopies)
        {
            var entryCost = (long)poolCopies * (byteLength + 1);
            var net8 = (long)Occurrences * byteLength - entryCost;
            var net16 = (long)Occurrences * (byteLength - 1) - entryCost;
            return new Candidate(
                Value,
                byteLength,
                FirstOccurrence,
                LastOccurrence,
                Occurrences,
                net8,
                net16,
                net8 - Math.Max(0, net16));
        }
    }

    private readonly record struct Candidate(
        string Value,
        int ByteLength,
        int FirstOccurrence,
        int LastOccurrence,
        int Occurrences,
        long Net8,
        long Net16,
        long SlotBenefit);
}
