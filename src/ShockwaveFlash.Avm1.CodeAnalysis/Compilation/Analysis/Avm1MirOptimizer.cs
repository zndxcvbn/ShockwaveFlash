using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal static class Avm1MirOptimizer
{
    private enum ValueFactKind : byte
    {
        Unknown,
        Constant,
        Overdefined
    }

    private readonly record struct ConstantValue(
        Avm1MirConstantKind Kind,
        bool BooleanValue,
        int IntegerValue,
        long NumberBits,
        string? StringValue)
    {
        public double NumberValue => BitConverter.Int64BitsToDouble(NumberBits);

        public static ConstantValue Undefined() =>
            new(Avm1MirConstantKind.Undefined, false, 0, 0, null);

        public static ConstantValue Null() =>
            new(Avm1MirConstantKind.Null, false, 0, 0, null);

        public static ConstantValue Boolean(bool value) =>
            new(Avm1MirConstantKind.Boolean, value, 0, 0, null);

        public static ConstantValue Integer(int value) =>
            new(Avm1MirConstantKind.Integer, false, value, 0, null);

        public static ConstantValue Number(double value)
        {
            if (!IsNegativeZero(value) &&
                double.IsFinite(value) &&
                value == Math.Truncate(value) &&
                value is >= int.MinValue and <= int.MaxValue)
            {
                return Integer((int)value);
            }

            return new(
                Avm1MirConstantKind.Number,
                false,
                0,
                BitConverter.DoubleToInt64Bits(value),
                null);
        }

        public static ConstantValue String(string value) =>
            new(Avm1MirConstantKind.String, false, 0, 0, value);

        private static bool IsNegativeZero(double value) =>
            value == 0d && BitConverter.DoubleToInt64Bits(value) < 0;
    }

    private readonly record struct ValueFact(
        ValueFactKind Kind,
        ConstantValue Constant)
    {
        public static readonly ValueFact Unknown = new(
            ValueFactKind.Unknown,
            default);

        public static readonly ValueFact Overdefined = new(
            ValueFactKind.Overdefined,
            default);

        public static ValueFact ForConstant(ConstantValue value) =>
            new(ValueFactKind.Constant, value);
    }

    private readonly record struct ControlFlowEdge(
        Avm1MirBlockIndex Predecessor,
        Avm1MirBlockIndex Successor);

    private enum TraceEdgePreference : byte
    {
        Unconditional,
        ConditionalFalse,
        ConditionalTrue
    }

    private readonly record struct TraceEdge(
        Avm1MirBlockIndex Source,
        Avm1MirBlockIndex Target,
        TraceEdgePreference Preference,
        int SourceLayoutIndex,
        int TargetLayoutIndex);

    private sealed record SparseConstantAnalysis(
        ValueFact[] Facts,
        bool[] ExecutableBlocks,
        HashSet<ControlFlowEdge> ExecutableEdges);

    public static Avm1MirMethod Optimize(
        Avm1MirMethod mir,
        Avm1CompilationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mir);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        return options.OptimizationLevel switch
        {
            Avm1OptimizationLevel.None => mir,
            Avm1OptimizationLevel.Basic =>
                OptimizeBasic(mir, options.SwfVersion, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.OptimizationLevel,
                "Unsupported AVM1 optimization level.")
        };
    }

    private static Avm1MirMethod OptimizeBasic(
        Avm1MirMethod mir,
        byte swfVersion,
        CancellationToken cancellationToken)
    {
        mir = OptimizeTemporaryCopies(mir, cancellationToken);
        var analysis = AnalyzeSparseConstants(
            mir,
            swfVersion,
            cancellationToken);
        var constants = mir.Constants.ToList();
        var strings = mir.Strings.ToList();
        var constantIndices = CreateConstantIndex(mir);
        var stringIndices = CreateStringIndex(mir);
        var transformed = RewriteControlFlow(
            mir,
            analysis,
            swfVersion,
            constants,
            strings,
            constantIndices,
            stringIndices,
            cancellationToken);
        transformed = EliminateTrivialPhiTransports(
            transformed,
            cancellationToken);
        transformed = CoalesceTrivialBlocks(
            transformed,
            cancellationToken);
        var instructions = transformed.Instructions.ToArray();
        var live = FindLiveInstructions(
            transformed,
            instructions,
            cancellationToken);
        var rebuilt = live.All(value => value)
            ? transformed
            : RebuildInstructions(
                transformed,
                instructions,
                live,
                cancellationToken);
        return ScheduleBlockTraces(rebuilt, cancellationToken);
    }

    private static Avm1MirMethod ScheduleBlockTraces(
        Avm1MirMethod mir,
        CancellationToken cancellationToken)
    {
        if (mir.BlockLayout.Count < 2 || !CanScheduleBlockTraces(mir))
            return mir;

        var layoutPositions = new int[mir.Blocks.Count];
        Array.Fill(layoutPositions, -1);
        for (var layoutIndex = 0;
            layoutIndex < mir.BlockLayout.Count;
            layoutIndex++)
        {
            layoutPositions[mir.BlockLayout[layoutIndex].Value] = layoutIndex;
        }

        var candidates = new List<TraceEdge>(mir.Blocks.Count * 2);
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = mir[blockIndex];
            if (!block.IsReachable || block.Instructions.Count == 0)
                continue;

            var terminator = mir.Instructions[
                block.Instructions.Start + block.Instructions.Count - 1];
            switch (terminator.Kind)
            {
                case Avm1MirInstructionKind.Branch:
                case Avm1MirInstructionKind.Break:
                case Avm1MirInstructionKind.Continue:
                    AddCandidate(
                        blockIndex,
                        terminator.Target,
                        TraceEdgePreference.Unconditional);
                    break;

                case Avm1MirInstructionKind.BranchIfTrue:
                    AddCandidate(
                        blockIndex,
                        terminator.AlternativeTarget,
                        TraceEdgePreference.ConditionalFalse);
                    AddCandidate(
                        blockIndex,
                        terminator.Target,
                        TraceEdgePreference.ConditionalTrue);
                    break;

                case Avm1MirInstructionKind.WaitForFrameImmediate:
                case Avm1MirInstructionKind.WaitForFrame:
                    // The loaded successor is encoded as physical fallthrough.
                    AddCandidate(
                        blockIndex,
                        terminator.Target,
                        TraceEdgePreference.Unconditional);
                    AddCandidate(
                        blockIndex,
                        terminator.AlternativeTarget,
                        TraceEdgePreference.ConditionalFalse);
                    break;

                case Avm1MirInstructionKind.EnumerateNext:
                    AddCandidate(
                        blockIndex,
                        terminator.Target,
                        TraceEdgePreference.ConditionalFalse);
                    AddCandidate(
                        blockIndex,
                        terminator.AlternativeTarget,
                        TraceEdgePreference.ConditionalTrue);
                    break;

                case Avm1MirInstructionKind.EndEnumeration:
                    AddCandidate(
                        blockIndex,
                        terminator.Target,
                        TraceEdgePreference.ConditionalFalse);
                    break;
            }
        }

        if (candidates.Count == 0)
            return mir;

        var next = new Avm1MirBlockIndex[mir.Blocks.Count];
        var previous = new Avm1MirBlockIndex[mir.Blocks.Count];
        Array.Fill(next, Avm1MirBlockIndex.Invalid);
        Array.Fill(previous, Avm1MirBlockIndex.Invalid);
        foreach (var edge in candidates
                     .OrderBy(edge => edge.Preference)
                     .ThenBy(edge => edge.SourceLayoutIndex)
                     .ThenBy(edge => edge.TargetLayoutIndex)
                     .ThenBy(edge => edge.Source.Value)
                     .ThenBy(edge => edge.Target.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (next[edge.Source.Value].IsValid ||
                previous[edge.Target.Value].IsValid ||
                edge.Target == mir.EntryBlock ||
                CreatesTraceCycle(edge.Source, edge.Target, next))
            {
                continue;
            }

            next[edge.Source.Value] = edge.Target;
            previous[edge.Target.Value] = edge.Source;
        }

        var scheduled = new bool[mir.Blocks.Count];
        var layout = new List<Avm1MirBlockIndex>(mir.BlockLayout.Count);
        AppendTrace(mir.EntryBlock);
        foreach (var blockIndex in mir.BlockLayout)
        {
            if (mir[blockIndex].IsReachable &&
                !scheduled[blockIndex.Value] &&
                !previous[blockIndex.Value].IsValid)
            {
                AppendTrace(blockIndex);
            }
        }
        foreach (var blockIndex in mir.BlockLayout)
        {
            if (mir[blockIndex].IsReachable && !scheduled[blockIndex.Value])
                AppendTrace(blockIndex);
        }
        foreach (var blockIndex in mir.BlockLayout)
        {
            if (!scheduled[blockIndex.Value])
            {
                layout.Add(blockIndex);
                scheduled[blockIndex.Value] = true;
            }
        }

        if (layout.Count != mir.BlockLayout.Count ||
            layout.SequenceEqual(mir.BlockLayout))
        {
            return mir;
        }

        return ReorderBlockLayout(
            mir,
            layout.ToArray(),
            cancellationToken);

        void AddCandidate(
            Avm1MirBlockIndex source,
            Avm1MirBlockIndex target,
            TraceEdgePreference preference)
        {
            if (!target.IsValid ||
                target.Value >= mir.Blocks.Count ||
                target == source ||
                !mir[target].IsReachable)
            {
                return;
            }

            candidates.Add(new TraceEdge(
                source,
                target,
                preference,
                layoutPositions[source.Value],
                layoutPositions[target.Value]));
        }

        void AppendTrace(Avm1MirBlockIndex start)
        {
            var current = start;
            while (current.IsValid && !scheduled[current.Value])
            {
                layout.Add(current);
                scheduled[current.Value] = true;
                current = next[current.Value];
            }
        }
    }

    private static Avm1MirMethod ReorderBlockLayout(
        Avm1MirMethod mir,
        Avm1MirBlockIndex[] blockLayout,
        CancellationToken cancellationToken)
    {
        var instructions = new List<Avm1MirInstruction>(
            mir.Instructions.Count);
        var blocks = mir.Blocks.ToArray();
        foreach (var blockIndex in blockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = mir[blockIndex];
            var start = instructions.Count;
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var instructionIndex = block.Instructions.Start;
                instructionIndex < end;
                instructionIndex++)
            {
                AppendInstruction(instructions, mir.Instructions[instructionIndex]);
            }
            blocks[blockIndex.Value] = block with
            {
                Instructions = new Avm1MirInstructionRange(
                    start,
                    instructions.Count - start)
            };
        }

        return CreateMethod(
            mir,
            instructions.ToArray(),
            blocks,
            mir.PhiSites.ToArray(),
            mir.PhiIncomings.ToArray(),
            mir.Constants.ToArray(),
            mir.Strings.ToArray(),
            blockLayout: blockLayout);
    }

    private static bool CanScheduleBlockTraces(Avm1MirMethod mir)
    {
        if (mir.ControlTargets.Any(target => target.HasPhysicalReferences) ||
            mir.TrySites.Any(site => site.UsesPhysicalParentBranches) ||
            HasImplicitReachableExit(mir) ||
            HasReachableControlFlowCycle(mir) ||
            mir.Instructions.Any(instruction =>
                instruction.Kind is Avm1MirInstructionKind.ExternalControlBranch ||
                instruction.PhiSite.IsValid ||
                instruction.Kind is Avm1MirInstructionKind.Phi or
                    Avm1MirInstructionKind.StorePhi))
        {
            return false;
        }

        var temporaryBlocks = new Avm1MirBlockIndex[mir.TemporaryCount];
        Array.Fill(temporaryBlocks, Avm1MirBlockIndex.Invalid);
        foreach (var blockIndex in mir.BlockLayout)
        {
            var block = mir[blockIndex];
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var instructionIndex = block.Instructions.Start;
                instructionIndex < end;
                instructionIndex++)
            {
                var instruction = mir.Instructions[instructionIndex];
                if (!RecordTemporary(instruction.Temporary, blockIndex))
                    return false;
                if (instruction.LValue.IsValid &&
                    instruction.LValue.Value < mir.LValues.Count)
                {
                    var lValue = mir[instruction.LValue];
                    if (!RecordTemporary(
                            lValue.ReceiverTemporary,
                            blockIndex) ||
                        !RecordTemporary(lValue.KeyTemporary, blockIndex))
                    {
                        return false;
                    }
                }
            }
        }
        return true;

        bool RecordTemporary(
            Avm1MirTemporaryIndex temporary,
            Avm1MirBlockIndex block)
        {
            if (!temporary.IsValid)
                return true;
            if (temporary.Value >= temporaryBlocks.Length)
                return false;

            ref var owner = ref temporaryBlocks[temporary.Value];
            if (!owner.IsValid)
            {
                owner = block;
                return true;
            }
            return owner == block;
        }
    }

    private static bool HasImplicitReachableExit(Avm1MirMethod mir)
    {
        foreach (var block in mir.Blocks)
        {
            if (!block.IsReachable)
                continue;
            if (block.Instructions.Count == 0)
                return true;

            var terminator = mir.Instructions[
                block.Instructions.Start + block.Instructions.Count - 1];
            if (!IsMirTerminator(terminator.Kind))
                return true;
        }
        return false;
    }

    private static bool HasReachableControlFlowCycle(Avm1MirMethod mir)
    {
        var indegrees = new int[mir.Blocks.Count];
        var reachableCount = 0;
        Span<Avm1MirBlockIndex> successors = stackalloc Avm1MirBlockIndex[2];
        foreach (var blockIndex in mir.BlockLayout)
        {
            var block = mir[blockIndex];
            if (!block.IsReachable)
                continue;
            reachableCount++;
            if (block.Instructions.Count == 0)
                continue;

            var terminator = mir.Instructions[
                block.Instructions.Start + block.Instructions.Count - 1];
            var successorCount = GetSuccessors(terminator, successors);
            for (var i = 0; i < successorCount; i++)
            {
                var successor = successors[i];
                if (successor.IsValid &&
                    successor.Value < mir.Blocks.Count &&
                    mir[successor].IsReachable)
                {
                    indegrees[successor.Value]++;
                }
            }
        }

        var pending = new Queue<Avm1MirBlockIndex>();
        foreach (var blockIndex in mir.BlockLayout)
        {
            if (mir[blockIndex].IsReachable && indegrees[blockIndex.Value] == 0)
                pending.Enqueue(blockIndex);
        }

        var visited = 0;
        while (pending.TryDequeue(out var blockIndex))
        {
            visited++;
            var block = mir[blockIndex];
            if (block.Instructions.Count == 0)
                continue;

            var terminator = mir.Instructions[
                block.Instructions.Start + block.Instructions.Count - 1];
            var successorCount = GetSuccessors(terminator, successors);
            for (var i = 0; i < successorCount; i++)
            {
                var successor = successors[i];
                if (!successor.IsValid ||
                    successor.Value >= mir.Blocks.Count ||
                    !mir[successor].IsReachable)
                {
                    continue;
                }
                if (--indegrees[successor.Value] == 0)
                    pending.Enqueue(successor);
            }
        }
        return visited != reachableCount;
    }

    private static int GetSuccessors(
        Avm1MirInstruction terminator,
        Span<Avm1MirBlockIndex> successors)
    {
        switch (terminator.Kind)
        {
            case Avm1MirInstructionKind.Branch:
            case Avm1MirInstructionKind.Break:
            case Avm1MirInstructionKind.Continue:
            case Avm1MirInstructionKind.EndEnumeration:
                successors[0] = terminator.Target;
                return 1;

            case Avm1MirInstructionKind.BranchIfTrue:
            case Avm1MirInstructionKind.WaitForFrameImmediate:
            case Avm1MirInstructionKind.WaitForFrame:
            case Avm1MirInstructionKind.EnumerateNext:
                successors[0] = terminator.Target;
                if (terminator.AlternativeTarget == terminator.Target)
                    return 1;
                successors[1] = terminator.AlternativeTarget;
                return 2;

            default:
                return 0;
        }
    }

    private static bool CreatesTraceCycle(
        Avm1MirBlockIndex source,
        Avm1MirBlockIndex target,
        Avm1MirBlockIndex[] next)
    {
        var current = target;
        for (var count = 0; current.IsValid && count < next.Length; count++)
        {
            if (current == source)
                return true;
            current = next[current.Value];
        }
        return false;
    }

    private static bool CanEncodeConstant(ConstantValue value, byte swfVersion)
    {
        var minimumVersion = value.Kind is Avm1MirConstantKind.String
            ? (byte)4
            : (byte)5;
        return swfVersion >= minimumVersion;
    }

    private static SparseConstantAnalysis AnalyzeSparseConstants(
        Avm1MirMethod mir,
        byte swfVersion,
        CancellationToken cancellationToken)
    {
        var facts = new ValueFact[mir.ValueCount];
        var executableBlocks = new bool[mir.Blocks.Count];
        var executableEdges = new HashSet<ControlFlowEdge>();
        var instructionBlocks = new Avm1MirBlockIndex[mir.Instructions.Count];
        var consumers = new List<int>?[mir.ValueCount];
        foreach (var blockIndex in mir.BlockLayout)
        {
            var block = mir[blockIndex];
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var i = block.Instructions.Start; i < end; i++)
            {
                instructionBlocks[i] = blockIndex;
                VisitDependencies(mir, mir.Instructions[i], value =>
                {
                    if (value.IsValid && value.Value < consumers.Length)
                        (consumers[value.Value] ??= []).Add(i);
                });
            }
        }

        if (!mir.EntryBlock.IsValid || mir.EntryBlock.Value >= mir.Blocks.Count)
        {
            return new SparseConstantAnalysis(
                facts,
                executableBlocks,
                executableEdges);
        }

        var worklist = new Queue<int>();
        var queued = new bool[mir.Instructions.Count];
        executableBlocks[mir.EntryBlock.Value] = true;
        EnqueueBlock(mir.EntryBlock);
        while (true)
        {
            while (worklist.TryDequeue(out var instructionIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                queued[instructionIndex] = false;
                var blockIndex = instructionBlocks[instructionIndex];
                if (!IsExecutable(executableBlocks, blockIndex))
                    continue;

                var instruction = mir.Instructions[instructionIndex];
                if (instruction.Result.IsValid &&
                    instruction.Result.Value < facts.Length)
                {
                    var candidate = EvaluateInstruction(
                        mir,
                        instruction,
                        facts,
                        executableEdges,
                        swfVersion);
                    if (MergeFact(
                            ref facts[instruction.Result.Value],
                            candidate) &&
                        consumers[instruction.Result.Value] is { } uses)
                    {
                        foreach (var consumer in uses)
                            EnqueueInstruction(consumer);
                    }
                }

                var block = mir[blockIndex];
                if (block.Instructions.Count != 0)
                {
                    var terminatorIndex =
                        block.Instructions.Start + block.Instructions.Count - 1;
                    if (instructionIndex == terminatorIndex)
                    {
                        MarkSuccessors(
                            instruction,
                            blockIndex,
                            facts,
                            executableBlocks,
                            executableEdges,
                            EnqueueBlock);
                    }
                }
            }

            var openedUnknownBranch = false;
            foreach (var blockIndex in mir.BlockLayout)
            {
                if (!IsExecutable(executableBlocks, blockIndex))
                    continue;

                var block = mir[blockIndex];
                if (block.Instructions.Count == 0)
                    continue;

                var terminator = mir.Instructions[
                    block.Instructions.Start + block.Instructions.Count - 1];
                if (terminator.Kind is not Avm1MirInstructionKind.BranchIfTrue ||
                    GetFact(facts, terminator.Operand).Kind is not
                        ValueFactKind.Unknown)
                {
                    continue;
                }

                openedUnknownBranch |= MarkEdge(
                    blockIndex,
                    terminator.Target,
                    executableBlocks,
                    executableEdges,
                    EnqueueBlock);
                openedUnknownBranch |= MarkEdge(
                    blockIndex,
                    terminator.AlternativeTarget,
                    executableBlocks,
                    executableEdges,
                    EnqueueBlock);
            }

            if (!openedUnknownBranch)
                break;
        }

        return new SparseConstantAnalysis(
            facts,
            executableBlocks,
            executableEdges);

        void EnqueueBlock(Avm1MirBlockIndex blockIndex)
        {
            if (!blockIndex.IsValid || blockIndex.Value >= mir.Blocks.Count)
                return;

            var block = mir[blockIndex];
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var i = block.Instructions.Start; i < end; i++)
                EnqueueInstruction(i);
        }

        void EnqueueInstruction(int instructionIndex)
        {
            if ((uint)instructionIndex >= (uint)queued.Length ||
                queued[instructionIndex])
            {
                return;
            }

            queued[instructionIndex] = true;
            worklist.Enqueue(instructionIndex);
        }
    }

    private static ValueFact EvaluateInstruction(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        IReadOnlyList<ValueFact> facts,
        IReadOnlySet<ControlFlowEdge> executableEdges,
        byte swfVersion)
    {
        if (instruction.Kind is Avm1MirInstructionKind.Constant)
        {
            return TryGetConstant(mir, instruction.Constant, out var constant)
                ? ValueFact.ForConstant(constant)
                : ValueFact.Overdefined;
        }

        if (instruction.Kind is Avm1MirInstructionKind.StorePhi)
            return GetFact(facts, instruction.Operand);

        if (instruction.Kind is Avm1MirInstructionKind.StoreTemporary)
            return GetFact(facts, instruction.Operand);

        if (instruction.Kind is Avm1MirInstructionKind.Phi)
            return EvaluatePhi(mir, instruction, facts, executableEdges);

        if (instruction.Kind is not (
                Avm1MirInstructionKind.Unary or
                Avm1MirInstructionKind.Binary) ||
            !IsOperationSupported(instruction, swfVersion) ||
            (instruction.Effects &
                ~(Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow)) != 0)
        {
            return ValueFact.Overdefined;
        }

        var operand = GetFact(facts, instruction.Operand);
        if (operand.Kind is ValueFactKind.Overdefined)
            return ValueFact.Overdefined;
        if (operand.Kind is ValueFactKind.Unknown)
            return ValueFact.Unknown;

        if (instruction.Kind is Avm1MirInstructionKind.Unary)
        {
            return TryFoldUnary(
                instruction.Operator,
                operand.Constant,
                out var unaryResult)
                ? ValueFact.ForConstant(unaryResult)
                : ValueFact.Overdefined;
        }

        var secondary = GetFact(facts, instruction.SecondaryOperand);
        if (secondary.Kind is ValueFactKind.Overdefined)
            return ValueFact.Overdefined;
        if (secondary.Kind is ValueFactKind.Unknown)
            return ValueFact.Unknown;

        return TryFoldBinary(
            instruction.Operator,
            operand.Constant,
            secondary.Constant,
            out var binaryResult)
            ? ValueFact.ForConstant(binaryResult)
            : ValueFact.Overdefined;
    }

    private static ValueFact EvaluatePhi(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        IReadOnlyList<ValueFact> facts,
        IReadOnlySet<ControlFlowEdge> executableEdges)
    {
        if (!instruction.PhiSite.IsValid ||
            instruction.PhiSite.Value >= mir.PhiSites.Count)
        {
            return ValueFact.Overdefined;
        }

        var site = mir[instruction.PhiSite];
        var result = ValueFact.Unknown;
        for (var i = 0; i < site.Incomings.Count; i++)
        {
            var incoming = mir.GetPhiIncoming(site, i);
            if (!executableEdges.Contains(new ControlFlowEdge(
                    incoming.Predecessor,
                    site.MergeBlock)))
            {
                continue;
            }

            result = MergeFacts(result, GetFact(facts, incoming.Value));
            if (result.Kind is ValueFactKind.Overdefined)
                break;
        }

        return result;
    }

    private static bool MarkSuccessors(
        Avm1MirInstruction terminator,
        Avm1MirBlockIndex block,
        IReadOnlyList<ValueFact> facts,
        bool[] executableBlocks,
        HashSet<ControlFlowEdge> executableEdges,
        Action<Avm1MirBlockIndex> enqueueBlock)
    {
        switch (terminator.Kind)
        {
            case Avm1MirInstructionKind.Branch:
            case Avm1MirInstructionKind.Break:
            case Avm1MirInstructionKind.Continue:
            case Avm1MirInstructionKind.EndEnumeration:
                return MarkEdge(
                    block,
                    terminator.Target,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);

            case Avm1MirInstructionKind.BranchIfTrue:
                var condition = GetFact(facts, terminator.Operand);
                if (condition.Kind is ValueFactKind.Constant)
                {
                    return MarkEdge(
                        block,
                        ToBoolean(condition.Constant)
                            ? terminator.Target
                            : terminator.AlternativeTarget,
                        executableBlocks,
                        executableEdges,
                        enqueueBlock);
                }
                if (condition.Kind is ValueFactKind.Unknown)
                    return false;

                var changed = MarkEdge(
                    block,
                    terminator.Target,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);
                changed |= MarkEdge(
                    block,
                    terminator.AlternativeTarget,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);
                return changed;

            case Avm1MirInstructionKind.WaitForFrameImmediate:
            case Avm1MirInstructionKind.WaitForFrame:
                var waitChanged = MarkEdge(
                    block,
                    terminator.Target,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);
                waitChanged |= MarkEdge(
                    block,
                    terminator.AlternativeTarget,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);
                return waitChanged;

            case Avm1MirInstructionKind.EnumerateNext:
                var enumerationChanged = MarkEdge(
                    block,
                    terminator.Target,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);
                enumerationChanged |= MarkEdge(
                    block,
                    terminator.AlternativeTarget,
                    executableBlocks,
                    executableEdges,
                    enqueueBlock);
                return enumerationChanged;

            default:
                return false;
        }
    }

    private static bool MarkEdge(
        Avm1MirBlockIndex predecessor,
        Avm1MirBlockIndex successor,
        bool[] executableBlocks,
        HashSet<ControlFlowEdge> executableEdges,
        Action<Avm1MirBlockIndex> enqueueBlock)
    {
        if (!successor.IsValid || successor.Value >= executableBlocks.Length)
            return false;

        var changed = executableEdges.Add(new ControlFlowEdge(
            predecessor,
            successor));
        if (!executableBlocks[successor.Value])
        {
            executableBlocks[successor.Value] = true;
            changed = true;
        }
        if (changed)
            enqueueBlock(successor);
        return changed;
    }

    private static bool IsExecutable(
        IReadOnlyList<bool> executableBlocks,
        Avm1MirBlockIndex block) =>
        block.IsValid &&
        block.Value < executableBlocks.Count &&
        executableBlocks[block.Value];

    private static ValueFact GetFact(
        IReadOnlyList<ValueFact> facts,
        Avm1MirValueIndex value) =>
        value.IsValid && value.Value < facts.Count
            ? facts[value.Value]
            : ValueFact.Overdefined;

    private static bool MergeFact(
        ref ValueFact current,
        ValueFact incoming)
    {
        var merged = MergeFacts(current, incoming);
        if (merged == current)
            return false;

        current = merged;
        return true;
    }

    private static ValueFact MergeFacts(ValueFact current, ValueFact incoming)
    {
        if (current.Kind is ValueFactKind.Overdefined ||
            incoming.Kind is ValueFactKind.Unknown)
        {
            return current;
        }
        if (incoming.Kind is ValueFactKind.Overdefined ||
            current.Kind is ValueFactKind.Unknown)
        {
            return incoming;
        }

        return current.Constant == incoming.Constant
            ? current
            : ValueFact.Overdefined;
    }

    private static bool IsOperationSupported(
        Avm1MirInstruction instruction,
        byte swfVersion)
    {
        if (!TryGetOperationOpcode(instruction, out var opcode))
            return instruction.Kind is Avm1MirInstructionKind.Constant;

        return Avm1ActionCapabilities.TryGetMinimumSwfVersion(
                opcode,
                out var minimumVersion) &&
            swfVersion >= minimumVersion;
    }

    private static bool TryGetOperationOpcode(
        Avm1MirInstruction instruction,
        out ActionOpcode opcode)
    {
        opcode = instruction.Operator switch
        {
            Avm1MirOperator.LogicalNot => ActionOpcode.Not,
            Avm1MirOperator.TypeOf => ActionOpcode.TypeOf,
            Avm1MirOperator.ToNumber => ActionOpcode.ToNumber,
            Avm1MirOperator.ToInteger => ActionOpcode.ToInteger,
            Avm1MirOperator.RandomNumber => ActionOpcode.RandomNumber,
            Avm1MirOperator.StringLength => ActionOpcode.StringLength,
            Avm1MirOperator.MbStringLength => ActionOpcode.MBStringLength,
            Avm1MirOperator.CharToAscii => ActionOpcode.CharToAscii,
            Avm1MirOperator.AsciiToChar => ActionOpcode.AsciiToChar,
            Avm1MirOperator.MbCharToAscii => ActionOpcode.MBCharToAscii,
            Avm1MirOperator.MbAsciiToChar => ActionOpcode.MBAsciiToChar,
            Avm1MirOperator.ToString => ActionOpcode.ToString,
            Avm1MirOperator.TargetPath => ActionOpcode.TargetPath,
            Avm1MirOperator.Increment => ActionOpcode.Increment,
            Avm1MirOperator.Decrement => ActionOpcode.Decrement,
            Avm1MirOperator.Add => ActionOpcode.Add2,
            Avm1MirOperator.StringAdd => ActionOpcode.StringAdd,
            Avm1MirOperator.Subtract => ActionOpcode.Subtract,
            Avm1MirOperator.Multiply => ActionOpcode.Multiply,
            Avm1MirOperator.Equal or Avm1MirOperator.NotEqual =>
                ActionOpcode.Equals2,
            Avm1MirOperator.StrictEqual or Avm1MirOperator.StrictNotEqual =>
                ActionOpcode.StrictEquals,
            Avm1MirOperator.Less or Avm1MirOperator.GreaterOrEqual =>
                ActionOpcode.Less2,
            Avm1MirOperator.Greater or Avm1MirOperator.LessOrEqual =>
                ActionOpcode.Greater,
            Avm1MirOperator.BitAnd => ActionOpcode.BitAnd,
            Avm1MirOperator.BitOr => ActionOpcode.BitOr,
            Avm1MirOperator.BitXor => ActionOpcode.BitXor,
            Avm1MirOperator.ShiftLeft => ActionOpcode.BitLShift,
            Avm1MirOperator.ShiftRight => ActionOpcode.BitRShift,
            Avm1MirOperator.ShiftRightUnsigned => ActionOpcode.BitURShift,
            _ => default
        };
        return instruction.Operator is
            Avm1MirOperator.LogicalNot or
            Avm1MirOperator.TypeOf or
            Avm1MirOperator.ToNumber or
            Avm1MirOperator.ToInteger or
            Avm1MirOperator.RandomNumber or
            Avm1MirOperator.StringLength or
            Avm1MirOperator.MbStringLength or
            Avm1MirOperator.CharToAscii or
            Avm1MirOperator.AsciiToChar or
            Avm1MirOperator.MbCharToAscii or
            Avm1MirOperator.MbAsciiToChar or
            Avm1MirOperator.ToString or
            Avm1MirOperator.TargetPath or
            Avm1MirOperator.Increment or
            Avm1MirOperator.Decrement or
            Avm1MirOperator.Add or
            Avm1MirOperator.StringAdd or
            Avm1MirOperator.Subtract or
            Avm1MirOperator.Multiply or
            Avm1MirOperator.Equal or
            Avm1MirOperator.NotEqual or
            Avm1MirOperator.StrictEqual or
            Avm1MirOperator.StrictNotEqual or
            Avm1MirOperator.Less or
            Avm1MirOperator.LessOrEqual or
            Avm1MirOperator.Greater or
            Avm1MirOperator.GreaterOrEqual or
            Avm1MirOperator.BitAnd or
            Avm1MirOperator.BitOr or
            Avm1MirOperator.BitXor or
            Avm1MirOperator.ShiftLeft or
            Avm1MirOperator.ShiftRight or
            Avm1MirOperator.ShiftRightUnsigned;
    }

    private static bool TryFoldUnary(
        Avm1MirOperator @operator,
        ConstantValue operand,
        out ConstantValue result)
    {
        result = default;
        switch (@operator)
        {
            case Avm1MirOperator.LogicalNot:
                result = ConstantValue.Boolean(!ToBoolean(operand));
                return true;

            case Avm1MirOperator.TypeOf:
                result = ConstantValue.String(operand.Kind switch
                {
                    Avm1MirConstantKind.Undefined => "undefined",
                    Avm1MirConstantKind.Null => "object",
                    Avm1MirConstantKind.Boolean => "boolean",
                    Avm1MirConstantKind.Integer or
                        Avm1MirConstantKind.Number => "number",
                    Avm1MirConstantKind.String => "string",
                    _ => throw new ArgumentOutOfRangeException(nameof(operand))
                });
                return true;

            case Avm1MirOperator.ToNumber:
                return TryGetNumber(operand, out result);

            case Avm1MirOperator.Increment:
            case Avm1MirOperator.Decrement:
                if (!TryGetNumericValue(operand, out var number))
                    return false;
                result = ConstantValue.Number(
                    number + (@operator is Avm1MirOperator.Increment ? 1d : -1d));
                return true;

            default:
                return false;
        }
    }

    private static bool TryFoldBinary(
        Avm1MirOperator @operator,
        ConstantValue left,
        ConstantValue right,
        out ConstantValue result)
    {
        result = default;
        if (@operator is Avm1MirOperator.StrictEqual or
            Avm1MirOperator.StrictNotEqual)
        {
            var equal = StrictEquals(left, right);
            result = ConstantValue.Boolean(
                @operator is Avm1MirOperator.StrictEqual ? equal : !equal);
            return true;
        }

        if (@operator is Avm1MirOperator.Equal or Avm1MirOperator.NotEqual)
        {
            if (!TryEqualSameRuntimeType(left, right, out var equal))
                return false;
            result = ConstantValue.Boolean(
                @operator is Avm1MirOperator.Equal ? equal : !equal);
            return true;
        }

        if (@operator is Avm1MirOperator.Add &&
            left.Kind is Avm1MirConstantKind.String &&
            right.Kind is Avm1MirConstantKind.String)
        {
            result = ConstantValue.String(left.StringValue + right.StringValue);
            return true;
        }

        if (TryGetNumericValue(left, out var leftNumber) &&
            TryGetNumericValue(right, out var rightNumber))
        {
            switch (@operator)
            {
                case Avm1MirOperator.Add:
                    result = ConstantValue.Number(leftNumber + rightNumber);
                    return true;
                case Avm1MirOperator.Subtract:
                    result = ConstantValue.Number(leftNumber - rightNumber);
                    return true;
                case Avm1MirOperator.Multiply:
                    result = ConstantValue.Number(leftNumber * rightNumber);
                    return true;
                case Avm1MirOperator.Less:
                    result = ConstantValue.Boolean(leftNumber < rightNumber);
                    return true;
                case Avm1MirOperator.LessOrEqual:
                    result = ConstantValue.Boolean(leftNumber <= rightNumber);
                    return true;
                case Avm1MirOperator.Greater:
                    result = ConstantValue.Boolean(leftNumber > rightNumber);
                    return true;
                case Avm1MirOperator.GreaterOrEqual:
                    result = ConstantValue.Boolean(leftNumber >= rightNumber);
                    return true;
            }
        }

        if (left.Kind is not Avm1MirConstantKind.Integer ||
            right.Kind is not Avm1MirConstantKind.Integer)
        {
            return false;
        }

        var shift = right.IntegerValue & 31;
        result = @operator switch
        {
            Avm1MirOperator.BitAnd =>
                ConstantValue.Integer(left.IntegerValue & right.IntegerValue),
            Avm1MirOperator.BitOr =>
                ConstantValue.Integer(left.IntegerValue | right.IntegerValue),
            Avm1MirOperator.BitXor =>
                ConstantValue.Integer(left.IntegerValue ^ right.IntegerValue),
            Avm1MirOperator.ShiftLeft =>
                ConstantValue.Integer(unchecked(left.IntegerValue << shift)),
            Avm1MirOperator.ShiftRight =>
                ConstantValue.Integer(left.IntegerValue >> shift),
            Avm1MirOperator.ShiftRightUnsigned =>
                ConstantValue.Number((uint)left.IntegerValue >> shift),
            _ => default
        };
        return @operator is
            Avm1MirOperator.BitAnd or
            Avm1MirOperator.BitOr or
            Avm1MirOperator.BitXor or
            Avm1MirOperator.ShiftLeft or
            Avm1MirOperator.ShiftRight or
            Avm1MirOperator.ShiftRightUnsigned;
    }

    private static bool ToBoolean(ConstantValue value) =>
        value.Kind switch
        {
            Avm1MirConstantKind.Undefined or Avm1MirConstantKind.Null => false,
            Avm1MirConstantKind.Boolean => value.BooleanValue,
            Avm1MirConstantKind.Integer => value.IntegerValue != 0,
            Avm1MirConstantKind.Number =>
                value.NumberValue != 0d && !double.IsNaN(value.NumberValue),
            Avm1MirConstantKind.String => value.StringValue!.Length != 0,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static bool TryGetNumber(
        ConstantValue value,
        out ConstantValue result)
    {
        result = value.Kind switch
        {
            Avm1MirConstantKind.Undefined => ConstantValue.Number(double.NaN),
            Avm1MirConstantKind.Null => ConstantValue.Integer(0),
            Avm1MirConstantKind.Boolean =>
                ConstantValue.Integer(value.BooleanValue ? 1 : 0),
            Avm1MirConstantKind.Integer or Avm1MirConstantKind.Number => value,
            _ => default
        };
        return value.Kind is not Avm1MirConstantKind.String;
    }

    private static bool TryGetNumericValue(
        ConstantValue value,
        out double result)
    {
        if (value.Kind is Avm1MirConstantKind.Integer)
        {
            result = value.IntegerValue;
            return true;
        }
        if (value.Kind is Avm1MirConstantKind.Number)
        {
            result = value.NumberValue;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool StrictEquals(ConstantValue left, ConstantValue right)
    {
        if (TryGetNumericValue(left, out var leftNumber) &&
            TryGetNumericValue(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }
        if (left.Kind != right.Kind)
            return false;

        return left.Kind switch
        {
            Avm1MirConstantKind.Undefined or Avm1MirConstantKind.Null => true,
            Avm1MirConstantKind.Boolean =>
                left.BooleanValue == right.BooleanValue,
            Avm1MirConstantKind.String =>
                string.Equals(
                    left.StringValue,
                    right.StringValue,
                    StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool TryEqualSameRuntimeType(
        ConstantValue left,
        ConstantValue right,
        out bool result)
    {
        if (TryGetNumericValue(left, out var leftNumber) &&
            TryGetNumericValue(right, out var rightNumber))
        {
            result = leftNumber == rightNumber;
            return true;
        }
        if (left.Kind != right.Kind)
        {
            result = false;
            return false;
        }

        result = StrictEquals(left, right);
        return true;
    }

    private static bool TryGetConstant(
        Avm1MirMethod mir,
        Avm1MirConstantIndex index,
        out ConstantValue result)
    {
        if (!index.IsValid || index.Value >= mir.Constants.Count)
        {
            result = default;
            return false;
        }

        var constant = mir[index];
        result = constant.Kind switch
        {
            Avm1MirConstantKind.Undefined => ConstantValue.Undefined(),
            Avm1MirConstantKind.Null => ConstantValue.Null(),
            Avm1MirConstantKind.Boolean =>
                ConstantValue.Boolean(constant.BooleanValue),
            Avm1MirConstantKind.Integer =>
                ConstantValue.Integer(constant.IntegerValue),
            Avm1MirConstantKind.Number =>
                ConstantValue.Number(constant.NumberValue),
            Avm1MirConstantKind.String when
                constant.StringValue.IsValid &&
                constant.StringValue.Value < mir.Strings.Count =>
                ConstantValue.String(mir[constant.StringValue]),
            _ => default
        };
        return constant.Kind is not Avm1MirConstantKind.String ||
            constant.StringValue.IsValid &&
            constant.StringValue.Value < mir.Strings.Count;
    }

    private static Dictionary<ConstantValue, Avm1MirConstantIndex>
        CreateConstantIndex(Avm1MirMethod mir)
    {
        var result = new Dictionary<ConstantValue, Avm1MirConstantIndex>();
        foreach (var constant in mir.Constants)
        {
            if (TryGetConstant(mir, constant.Index, out var value))
                result.TryAdd(value, constant.Index);
        }
        return result;
    }

    private static Dictionary<string, Avm1MirStringIndex> CreateStringIndex(
        Avm1MirMethod mir)
    {
        var result = new Dictionary<string, Avm1MirStringIndex>(
            StringComparer.Ordinal);
        for (var i = 0; i < mir.Strings.Count; i++)
            result.TryAdd(mir.Strings[i], new Avm1MirStringIndex(i));
        return result;
    }

    private static Avm1MirConstantIndex InternConstant(
        ConstantValue value,
        List<Avm1MirConstant> constants,
        List<string> strings,
        Dictionary<ConstantValue, Avm1MirConstantIndex> constantIndices,
        Dictionary<string, Avm1MirStringIndex> stringIndices)
    {
        if (constantIndices.TryGetValue(value, out var existing))
            return existing;

        var stringValue = Avm1MirStringIndex.Invalid;
        if (value.Kind is Avm1MirConstantKind.String)
        {
            var text = value.StringValue!;
            if (!stringIndices.TryGetValue(text, out stringValue))
            {
                stringValue = new Avm1MirStringIndex(strings.Count);
                strings.Add(text);
                stringIndices.Add(text, stringValue);
            }
        }

        var index = new Avm1MirConstantIndex(constants.Count);
        constants.Add(new Avm1MirConstant(
            index,
            value.Kind,
            value.BooleanValue,
            value.IntegerValue,
            value.Kind is Avm1MirConstantKind.Number
                ? value.NumberValue
                : 0,
            stringValue));
        constantIndices.Add(value, index);
        return index;
    }

    private static Avm1MirMethod RewriteControlFlow(
        Avm1MirMethod mir,
        SparseConstantAnalysis analysis,
        byte swfVersion,
        List<Avm1MirConstant> constants,
        List<string> strings,
        Dictionary<ConstantValue, Avm1MirConstantIndex> constantIndices,
        Dictionary<string, Avm1MirStringIndex> stringIndices,
        CancellationToken cancellationToken)
    {
        var executableBlocks = analysis.ExecutableBlocks.ToArray();
        var executableEdges = new HashSet<ControlFlowEdge>(
            analysis.ExecutableEdges);
        PreserveRequiredPhiStructure(
            mir,
            executableBlocks,
            executableEdges,
            cancellationToken);
        var (phiSites, phiIncomings, phiMap) = RebuildPhiSites(
            mir,
            executableBlocks,
            executableEdges);
        if (LosesExecutablePhiReference(mir, executableBlocks, phiMap))
            return mir;

        var instructions = new List<Avm1MirInstruction>();
        var blocks = mir.Blocks.ToArray();
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = mir[blockIndex];
            var start = instructions.Count;
            if (IsExecutable(executableBlocks, blockIndex))
            {
                var end = block.Instructions.Start + block.Instructions.Count;
                for (var i = block.Instructions.Start; i < end; i++)
                {
                    RewriteInstruction(
                        mir.Instructions[i],
                        analysis.Facts,
                        phiSites,
                        phiMap,
                        swfVersion,
                        constants,
                        strings,
                        constantIndices,
                        stringIndices,
                        instructions);
                }
            }

            blocks[blockIndex.Value] = block with
            {
                Instructions = new Avm1MirInstructionRange(
                    start,
                    instructions.Count - start),
                IsReachable = IsExecutable(executableBlocks, blockIndex)
            };
        }

        return CreateMethod(
            mir,
            instructions.ToArray(),
            blocks,
            phiSites,
            phiIncomings,
            constants.ToArray(),
            strings.ToArray());
    }

    private static Avm1MirMethod EliminateTrivialPhiTransports(
        Avm1MirMethod mir,
        CancellationToken cancellationToken)
    {
        if (mir.PhiSites.Count == 0)
            return mir;

        var blockInstructions = CreateBlockInstructionLists(mir);
        var blocks = mir.Blocks.ToArray();
        var phiSites = mir.PhiSites.ToArray();
        var phiIncomings = CreatePhiIncomingLists(mir);

        var aliases = new int[mir.ValueCount];
        for (var valueIndex = 0; valueIndex < aliases.Length; valueIndex++)
            aliases[valueIndex] = valueIndex;

        var eliminatedSites = new bool[phiSites.Length];
        var controlTargets = mir.ControlTargets.ToArray();
        var changed = false;
        while (true)
        {
            var eliminatedOne = false;
            for (var siteIndex = 0; siteIndex < phiSites.Length; siteIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (eliminatedSites[siteIndex] ||
                    phiIncomings[siteIndex].Count != 1 ||
                    !TryFindTrivialPhiTransport(
                        blockInstructions,
                        blocks,
                        phiSites[siteIndex],
                        phiIncomings[siteIndex][0],
                        new Avm1MirPhiSiteIndex(siteIndex),
                        aliases,
                        out var predecessor,
                        out var mergeBlock,
                        out var storeIndex))
                {
                    continue;
                }
                if (IsPhysicalControlTarget(controlTargets, mergeBlock))
                    continue;

                // The transport can disappear only when its producer and merge
                // become one MIR block, so the incoming value remains on stack.
                var predecessorInstructions = blockInstructions[predecessor.Value];
                var mergeInstructions = blockInstructions[mergeBlock.Value];
                var store = predecessorInstructions[storeIndex];
                var branch = predecessorInstructions[^1];
                var merged = new List<Avm1MirInstruction>(
                    storeIndex + mergeInstructions.Count - 1);
                for (var instructionIndex = 0;
                    instructionIndex < storeIndex;
                    instructionIndex++)
                {
                    merged.Add(predecessorInstructions[instructionIndex]);
                }
                for (var instructionIndex = 1;
                    instructionIndex < mergeInstructions.Count;
                    instructionIndex++)
                {
                    merged.Add(mergeInstructions[instructionIndex]);
                }

                var mergeTailTerminates = merged.Count > 0 &&
                    IsMirTerminator(merged[^1].Kind);
                if (!mergeTailTerminates &&
                    mir.BlockLayout[^1] != mergeBlock)
                {
                    continue;
                }

                SetValueAlias(aliases, store.Result, store.Operand);
                SetValueAlias(
                    aliases,
                    mergeInstructions[0].Result,
                    phiIncomings[siteIndex][0].Value);

                if (!mergeTailTerminates)
                {
                    merged.Add(CreateBranch(
                        branch,
                        mergeBlock,
                        Avm1MirPhiSiteIndex.Invalid));
                }

                blockInstructions[predecessor.Value] = merged;
                blockInstructions[mergeBlock.Value] = [];
                eliminatedSites[siteIndex] = true;
                RemoveEliminatedPhiReferences(
                    blockInstructions,
                    new Avm1MirPhiSiteIndex(siteIndex),
                    aliases);

                if (mergeTailTerminates)
                {
                    blocks[mergeBlock.Value] = blocks[mergeBlock.Value] with
                    {
                        IsReachable = false
                    };
                    RemapBlockReferences(
                        blockInstructions,
                        phiSites,
                        phiIncomings,
                        controlTargets,
                        mergeBlock,
                        predecessor);
                }

                changed = true;
                eliminatedOne = true;
                break;
            }

            if (!eliminatedOne)
                break;
        }

        if (!changed)
            return mir;

        var phiMap = new Avm1MirPhiSiteIndex[phiSites.Length];
        Array.Fill(phiMap, Avm1MirPhiSiteIndex.Invalid);
        var compactedSites = new List<Avm1MirPhiSite>();
        var compactedIncomings = new List<Avm1MirPhiIncoming>();
        for (var siteIndex = 0; siteIndex < phiSites.Length; siteIndex++)
        {
            if (eliminatedSites[siteIndex])
                continue;

            var newSiteIndex = new Avm1MirPhiSiteIndex(compactedSites.Count);
            phiMap[siteIndex] = newSiteIndex;
            var incomingStart = compactedIncomings.Count;
            foreach (var incoming in phiIncomings[siteIndex])
            {
                compactedIncomings.Add(incoming with
                {
                    Value = ResolveValueAlias(aliases, incoming.Value)
                });
            }
            var site = phiSites[siteIndex];
            compactedSites.Add(site with
            {
                Index = newSiteIndex,
                Incomings = new Avm1MirPhiIncomingRange(
                    incomingStart,
                    compactedIncomings.Count - incomingStart)
            });
        }

        if (HasRequiredEliminatedPhiReference(
            blockInstructions,
            eliminatedSites))
        {
            return mir;
        }

        var instructions = new List<Avm1MirInstruction>();
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = instructions.Count;
            foreach (var instruction in blockInstructions[blockIndex.Value])
            {
                AppendInstruction(instructions, instruction with
                {
                    Operand = ResolveValueAlias(aliases, instruction.Operand),
                    SecondaryOperand = ResolveValueAlias(
                        aliases,
                        instruction.SecondaryOperand),
                    PhiSite = MapPhiSite(instruction.PhiSite, phiMap)
                });
            }
            blocks[blockIndex.Value] = blocks[blockIndex.Value] with
            {
                Instructions = new Avm1MirInstructionRange(
                    start,
                    instructions.Count - start)
            };
        }

        var lValues = mir.LValues
            .Select(lValue => lValue with
            {
                Receiver = ResolveValueAlias(aliases, lValue.Receiver),
                Key = ResolveValueAlias(aliases, lValue.Key)
            })
            .ToArray();
        var callSites = mir.CallSites
            .Select(callSite => callSite with
            {
                Target = ResolveValueAlias(aliases, callSite.Target),
                MemberName = ResolveValueAlias(aliases, callSite.MemberName)
            })
            .ToArray();
        var callArguments = mir.CallArguments
            .Select(value => ResolveValueAlias(aliases, value))
            .ToArray();
        var aggregateValues = mir.AggregateValues
            .Select(value => ResolveValueAlias(aliases, value))
            .ToArray();

        return CreateMethod(
            mir,
            instructions.ToArray(),
            blocks,
            compactedSites.ToArray(),
            compactedIncomings.ToArray(),
            mir.Constants.ToArray(),
            mir.Strings.ToArray(),
            lValues,
            callSites,
            callArguments,
            aggregateValues,
            controlTargets);
    }

    private static Avm1MirMethod CoalesceTrivialBlocks(
        Avm1MirMethod mir,
        CancellationToken cancellationToken)
    {
        if (mir.Blocks.Count < 2)
            return mir;

        var blockInstructions = CreateBlockInstructionLists(mir);
        var blocks = mir.Blocks.ToArray();
        var phiSites = mir.PhiSites.ToArray();
        var phiIncomings = CreatePhiIncomingLists(mir);
        var controlTargets = mir.ControlTargets.ToArray();
        var changed = false;
        while (true)
        {
            var coalescedOne = false;
            foreach (var predecessor in mir.BlockLayout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsReachableBlock(blocks, predecessor))
                    continue;

                var predecessorInstructions = blockInstructions[predecessor.Value];
                if (predecessorInstructions.Count == 0)
                    continue;

                var branch = predecessorInstructions[^1];
                var target = branch.Target;
                if (branch.Kind is not Avm1MirInstructionKind.Branch ||
                    branch.PhiSite.IsValid ||
                    !IsReachableBlock(blocks, target) ||
                    target == predecessor ||
                    target == mir.EntryBlock ||
                    IsPhysicalControlTarget(controlTargets, target) ||
                    !HasSingleReachablePredecessor(
                        blockInstructions,
                        blocks,
                        target,
                        predecessor))
                {
                    continue;
                }

                var targetInstructions = blockInstructions[target.Value];
                if (targetInstructions.Count == 0 ||
                    !IsMirTerminator(targetInstructions[^1].Kind) ||
                    targetInstructions[^1].Kind is
                        Avm1MirInstructionKind.EndEnumeration ||
                    targetInstructions.Any(instruction =>
                        instruction.Kind is Avm1MirInstructionKind.Phi))
                {
                    continue;
                }

                // Both original blocks meet at an empty operand-stack boundary.
                // Removing the branch preserves instruction and effect order.
                var merged = new List<Avm1MirInstruction>(
                    predecessorInstructions.Count - 1 +
                    targetInstructions.Count);
                for (var instructionIndex = 0;
                    instructionIndex < predecessorInstructions.Count - 1;
                    instructionIndex++)
                {
                    merged.Add(predecessorInstructions[instructionIndex]);
                }
                merged.AddRange(targetInstructions);
                blockInstructions[predecessor.Value] = merged;
                blockInstructions[target.Value] = [];
                blocks[target.Value] = blocks[target.Value] with
                {
                    IsReachable = false
                };
                RemapBlockReferences(
                    blockInstructions,
                    phiSites,
                    phiIncomings,
                    controlTargets,
                    target,
                    predecessor);

                changed = true;
                coalescedOne = true;
                break;
            }

            if (!coalescedOne)
                break;
        }

        if (!changed || HasDuplicatePhiPredecessors(phiIncomings))
            return mir;

        var flattenedIncomings = new List<Avm1MirPhiIncoming>();
        for (var siteIndex = 0; siteIndex < phiSites.Length; siteIndex++)
        {
            var start = flattenedIncomings.Count;
            flattenedIncomings.AddRange(phiIncomings[siteIndex]);
            phiSites[siteIndex] = phiSites[siteIndex] with
            {
                Incomings = new Avm1MirPhiIncomingRange(
                    start,
                    flattenedIncomings.Count - start)
            };
        }

        var instructions = new List<Avm1MirInstruction>();
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = instructions.Count;
            foreach (var instruction in blockInstructions[blockIndex.Value])
                AppendInstruction(instructions, instruction);
            blocks[blockIndex.Value] = blocks[blockIndex.Value] with
            {
                Instructions = new Avm1MirInstructionRange(
                    start,
                    instructions.Count - start)
            };
        }

        return CreateMethod(
            mir,
            instructions.ToArray(),
            blocks,
            phiSites,
            flattenedIncomings.ToArray(),
            mir.Constants.ToArray(),
            mir.Strings.ToArray(),
            controlTargets: controlTargets);
    }

    private static List<Avm1MirInstruction>[] CreateBlockInstructionLists(
        Avm1MirMethod mir)
    {
        var result = new List<Avm1MirInstruction>[mir.Blocks.Count];
        foreach (var blockIndex in mir.BlockLayout)
        {
            var block = mir[blockIndex];
            var instructions = new List<Avm1MirInstruction>(
                block.Instructions.Count);
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var instructionIndex = block.Instructions.Start;
                instructionIndex < end;
                instructionIndex++)
            {
                instructions.Add(mir.Instructions[instructionIndex]);
            }
            result[blockIndex.Value] = instructions;
        }
        return result;
    }

    private static List<Avm1MirPhiIncoming>[] CreatePhiIncomingLists(
        Avm1MirMethod mir)
    {
        var result = new List<Avm1MirPhiIncoming>[mir.PhiSites.Count];
        for (var siteIndex = 0; siteIndex < mir.PhiSites.Count; siteIndex++)
        {
            var site = mir.PhiSites[siteIndex];
            var incomings = new List<Avm1MirPhiIncoming>(site.Incomings.Count);
            for (var incomingIndex = 0;
                incomingIndex < site.Incomings.Count;
                incomingIndex++)
            {
                incomings.Add(mir.GetPhiIncoming(site, incomingIndex));
            }
            result[siteIndex] = incomings;
        }
        return result;
    }

    private static bool HasDuplicatePhiPredecessors(
        IReadOnlyList<List<Avm1MirPhiIncoming>> phiIncomings)
    {
        foreach (var incomings in phiIncomings)
        {
            var predecessors = new HashSet<Avm1MirBlockIndex>();
            foreach (var incoming in incomings)
            {
                if (!predecessors.Add(incoming.Predecessor))
                    return true;
            }
        }
        return false;
    }

    private static bool TryFindTrivialPhiTransport(
        List<Avm1MirInstruction>[] blockInstructions,
        IReadOnlyList<Avm1MirBasicBlock> blocks,
        Avm1MirPhiSite site,
        Avm1MirPhiIncoming incoming,
        Avm1MirPhiSiteIndex siteIndex,
        int[] aliases,
        out Avm1MirBlockIndex predecessor,
        out Avm1MirBlockIndex mergeBlock,
        out int storeIndex)
    {
        predecessor = incoming.Predecessor;
        mergeBlock = site.MergeBlock;
        storeIndex = -1;
        if (!IsReachableBlock(blocks, predecessor) ||
            !IsReachableBlock(blocks, mergeBlock) ||
            predecessor == mergeBlock ||
            !HasSingleReachablePredecessor(
                blockInstructions,
                blocks,
                mergeBlock,
                predecessor))
        {
            return false;
        }

        var predecessorInstructions = blockInstructions[predecessor.Value];
        var mergeInstructions = blockInstructions[mergeBlock.Value];
        if (predecessorInstructions.Count < 2 ||
            mergeInstructions.Count == 0 ||
            mergeInstructions[0].Kind is not Avm1MirInstructionKind.Phi ||
            mergeInstructions[0].PhiSite != siteIndex)
        {
            return false;
        }

        var branch = predecessorInstructions[^1];
        if (branch.Kind is not Avm1MirInstructionKind.Branch ||
            branch.Target != mergeBlock ||
            branch.PhiSite != siteIndex)
        {
            return false;
        }

        var storeCount = 0;
        for (var instructionIndex = 0;
            instructionIndex < predecessorInstructions.Count - 1;
            instructionIndex++)
        {
            var instruction = predecessorInstructions[instructionIndex];
            if (instruction.Kind is not Avm1MirInstructionKind.StorePhi ||
                instruction.PhiSite != siteIndex)
            {
                continue;
            }
            storeCount++;
            storeIndex = instructionIndex;
        }
        if (storeCount != 1 ||
            ResolveValueAlias(
                aliases,
                predecessorInstructions[storeIndex].Operand) !=
            ResolveValueAlias(aliases, incoming.Value))
        {
            return false;
        }

        var store = predecessorInstructions[storeIndex];
        if (store.Result.IsValid)
        {
            if (storeIndex != predecessorInstructions.Count - 3)
                return false;

            var discard = predecessorInstructions[storeIndex + 1];
            if (discard.Kind is not Avm1MirInstructionKind.Discard ||
                ResolveValueAlias(aliases, discard.Operand) !=
                ResolveValueAlias(aliases, store.Result))
            {
                return false;
            }
        }
        else if (storeIndex != predecessorInstructions.Count - 2)
        {
            return false;
        }

        var phiCount = 0;
        for (var blockIndex = 0;
            blockIndex < blockInstructions.Length;
            blockIndex++)
        {
            foreach (var instruction in blockInstructions[blockIndex])
            {
                if (instruction.Kind is Avm1MirInstructionKind.Phi &&
                    instruction.PhiSite == siteIndex)
                {
                    phiCount++;
                }
            }
        }
        return phiCount == 1;
    }

    private static bool HasSingleReachablePredecessor(
        List<Avm1MirInstruction>[] blockInstructions,
        IReadOnlyList<Avm1MirBasicBlock> blocks,
        Avm1MirBlockIndex target,
        Avm1MirBlockIndex expected)
    {
        var predecessor = Avm1MirBlockIndex.Invalid;
        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            if (!blocks[blockIndex].IsReachable ||
                blockInstructions[blockIndex].Count == 0 ||
                !HasSuccessor(blockInstructions[blockIndex][^1], target))
            {
                continue;
            }

            var candidate = new Avm1MirBlockIndex(blockIndex);
            if (predecessor.IsValid && predecessor != candidate)
                return false;
            predecessor = candidate;
        }
        return predecessor == expected;
    }

    private static bool IsPhysicalControlTarget(
        IReadOnlyList<Avm1MirControlTarget> controlTargets,
        Avm1MirBlockIndex block) =>
        controlTargets.Any(target =>
            target.HasPhysicalReferences &&
            (target.BreakBlock == block || target.ContinueBlock == block));

    private static bool HasSuccessor(
        Avm1MirInstruction terminator,
        Avm1MirBlockIndex target) =>
        terminator.Kind switch
        {
            Avm1MirInstructionKind.Branch or
            Avm1MirInstructionKind.Break or
            Avm1MirInstructionKind.Continue or
            Avm1MirInstructionKind.EndEnumeration =>
                terminator.Target == target,
            Avm1MirInstructionKind.BranchIfTrue or
            Avm1MirInstructionKind.WaitForFrameImmediate or
            Avm1MirInstructionKind.WaitForFrame or
            Avm1MirInstructionKind.EnumerateNext =>
                terminator.Target == target ||
                terminator.AlternativeTarget == target,
            _ => false
        };

    private static bool IsMirTerminator(Avm1MirInstructionKind kind) =>
        kind is
            Avm1MirInstructionKind.Return or
            Avm1MirInstructionKind.Throw or
            Avm1MirInstructionKind.Break or
            Avm1MirInstructionKind.Continue or
            Avm1MirInstructionKind.ExternalControlBranch or
            Avm1MirInstructionKind.Branch or
            Avm1MirInstructionKind.BranchIfTrue or
            Avm1MirInstructionKind.WaitForFrameImmediate or
            Avm1MirInstructionKind.WaitForFrame or
            Avm1MirInstructionKind.EnumerateNext or
            Avm1MirInstructionKind.EndEnumeration;

    private static bool IsReachableBlock(
        IReadOnlyList<Avm1MirBasicBlock> blocks,
        Avm1MirBlockIndex block) =>
        block.IsValid &&
        block.Value < blocks.Count &&
        blocks[block.Value].IsReachable;

    private static void RemoveEliminatedPhiReferences(
        List<Avm1MirInstruction>[] blockInstructions,
        Avm1MirPhiSiteIndex eliminatedSite,
        int[] aliases)
    {
        for (var blockIndex = 0;
            blockIndex < blockInstructions.Length;
            blockIndex++)
        {
            var source = blockInstructions[blockIndex];
            var rewritten = new List<Avm1MirInstruction>(source.Count);
            foreach (var instruction in source)
            {
                if (instruction.PhiSite != eliminatedSite)
                {
                    rewritten.Add(instruction);
                    continue;
                }

                if (instruction.Kind is Avm1MirInstructionKind.StorePhi)
                {
                    if (instruction.Result.IsValid)
                    {
                        SetValueAlias(
                            aliases,
                            instruction.Result,
                            instruction.Operand);
                    }
                    else
                    {
                        rewritten.Add(CreateDiscard(
                            instruction.Operand,
                            instruction.Origin));
                    }
                    continue;
                }

                rewritten.Add(instruction with
                {
                    PhiSite = Avm1MirPhiSiteIndex.Invalid
                });
            }
            blockInstructions[blockIndex] = rewritten;
        }
    }

    private static void RemapBlockReferences(
        List<Avm1MirInstruction>[] blockInstructions,
        Avm1MirPhiSite[] phiSites,
        List<Avm1MirPhiIncoming>[] phiIncomings,
        Avm1MirControlTarget[] controlTargets,
        Avm1MirBlockIndex from,
        Avm1MirBlockIndex to)
    {
        for (var blockIndex = 0;
            blockIndex < blockInstructions.Length;
            blockIndex++)
        {
            var instructions = blockInstructions[blockIndex];
            for (var instructionIndex = 0;
                instructionIndex < instructions.Count;
                instructionIndex++)
            {
                var instruction = instructions[instructionIndex];
                instructions[instructionIndex] = instruction with
                {
                    Target = instruction.Target == from
                        ? to
                        : instruction.Target,
                    AlternativeTarget = instruction.AlternativeTarget == from
                        ? to
                        : instruction.AlternativeTarget
                };
            }
        }

        for (var siteIndex = 0; siteIndex < phiSites.Length; siteIndex++)
        {
            var site = phiSites[siteIndex];
            if (site.MergeBlock == from)
                phiSites[siteIndex] = site with { MergeBlock = to };

            var incomings = phiIncomings[siteIndex];
            for (var incomingIndex = 0;
                incomingIndex < incomings.Count;
                incomingIndex++)
            {
                var incoming = incomings[incomingIndex];
                if (incoming.Predecessor == from)
                {
                    incomings[incomingIndex] = incoming with
                    {
                        Predecessor = to
                    };
                }
            }
        }

        for (var targetIndex = 0;
            targetIndex < controlTargets.Length;
            targetIndex++)
        {
            var target = controlTargets[targetIndex];
            controlTargets[targetIndex] = target with
            {
                BreakBlock = target.BreakBlock == from
                    ? to
                    : target.BreakBlock,
                ContinueBlock = target.ContinueBlock == from
                    ? to
                    : target.ContinueBlock
            };
        }
    }

    private static bool HasRequiredEliminatedPhiReference(
        List<Avm1MirInstruction>[] blockInstructions,
        bool[] eliminatedSites)
    {
        foreach (var instructions in blockInstructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Kind is not (
                        Avm1MirInstructionKind.StorePhi or
                        Avm1MirInstructionKind.Phi) ||
                    !instruction.PhiSite.IsValid ||
                    instruction.PhiSite.Value >= eliminatedSites.Length)
                {
                    continue;
                }
                if (eliminatedSites[instruction.PhiSite.Value])
                    return true;
            }
        }
        return false;
    }

    private static Avm1MirValueIndex ResolveValueAlias(
        int[] aliases,
        Avm1MirValueIndex value)
    {
        if (!value.IsValid || value.Value >= aliases.Length)
            return value;

        var root = value.Value;
        while (aliases[root] != root)
            root = aliases[root];

        var current = value.Value;
        while (aliases[current] != current)
        {
            var next = aliases[current];
            aliases[current] = root;
            current = next;
        }
        return new Avm1MirValueIndex(root);
    }

    private static void SetValueAlias(
        int[] aliases,
        Avm1MirValueIndex result,
        Avm1MirValueIndex source)
    {
        if (!result.IsValid ||
            result.Value >= aliases.Length ||
            !source.IsValid ||
            source.Value >= aliases.Length)
        {
            return;
        }
        aliases[result.Value] = ResolveValueAlias(aliases, source).Value;
    }

    private static Avm1MirMethod OptimizeTemporaryCopies(
        Avm1MirMethod mir,
        CancellationToken cancellationToken)
    {
        if (mir.TemporaryCount == 0)
            return mir;

        var blockInstructions = CreateBlockInstructionLists(mir);
        var changed = false;
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockInstructionList = blockInstructions[blockIndex.Value];
            for (var instructionIndex = 0;
                instructionIndex + 1 < blockInstructionList.Count;
                instructionIndex++)
            {
                var store = blockInstructionList[instructionIndex];
                var load = blockInstructionList[instructionIndex + 1];
                if (store.Kind is not Avm1MirInstructionKind.StoreTemporary ||
                    store.Result.IsValid ||
                    load.Kind is not Avm1MirInstructionKind.LoadTemporary ||
                    store.Temporary != load.Temporary ||
                    !load.Result.IsValid)
                {
                    continue;
                }

                blockInstructionList[instructionIndex] = store with
                {
                    Result = load.Result
                };
                blockInstructionList.RemoveAt(instructionIndex + 1);
                changed = true;
            }
        }

        var usedTemporaries = new bool[mir.TemporaryCount];
        foreach (var blockInstructionList in blockInstructions)
        {
            if (blockInstructionList is null)
                continue;

            foreach (var instruction in blockInstructionList)
            {
                if (instruction.Kind is not Avm1MirInstructionKind.StoreTemporary)
                    MarkTemporaryUsed(instruction.Temporary);
            }
        }
        foreach (var lValue in mir.LValues)
        {
            MarkTemporaryUsed(lValue.ReceiverTemporary);
            MarkTemporaryUsed(lValue.KeyTemporary);
        }

        var aliases = new int[mir.ValueCount];
        for (var valueIndex = 0; valueIndex < aliases.Length; valueIndex++)
            aliases[valueIndex] = valueIndex;

        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = blockInstructions[blockIndex.Value];
            var rewritten = new List<Avm1MirInstruction>(source.Count);
            foreach (var instruction in source)
            {
                if (instruction.Kind is Avm1MirInstructionKind.StoreTemporary &&
                    instruction.Temporary.IsValid &&
                    instruction.Temporary.Value < usedTemporaries.Length &&
                    !usedTemporaries[instruction.Temporary.Value])
                {
                    if (instruction.Result.IsValid)
                    {
                        SetValueAlias(
                            aliases,
                            instruction.Result,
                            instruction.Operand);
                    }
                    else
                    {
                        rewritten.Add(CreateDiscard(
                            instruction.Operand,
                            instruction.Origin));
                    }
                    changed = true;
                    continue;
                }

                rewritten.Add(instruction);
            }
            blockInstructions[blockIndex.Value] = rewritten;
        }

        if (!changed)
            return mir;

        var optimizedInstructions = new List<Avm1MirInstruction>(
            mir.Instructions.Count);
        var blocks = mir.Blocks.ToArray();
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = optimizedInstructions.Count;
            foreach (var instruction in blockInstructions[blockIndex.Value])
            {
                AppendInstruction(optimizedInstructions, instruction with
                {
                    Operand = ResolveValueAlias(aliases, instruction.Operand),
                    SecondaryOperand = ResolveValueAlias(
                        aliases,
                        instruction.SecondaryOperand)
                });
            }
            blocks[blockIndex.Value] = blocks[blockIndex.Value] with
            {
                Instructions = new Avm1MirInstructionRange(
                    start,
                    optimizedInstructions.Count - start)
            };
        }

        var lValues = mir.LValues
            .Select(lValue => lValue with
            {
                Receiver = ResolveValueAlias(aliases, lValue.Receiver),
                Key = ResolveValueAlias(aliases, lValue.Key)
            })
            .ToArray();
        var callSites = mir.CallSites
            .Select(callSite => callSite with
            {
                Target = ResolveValueAlias(aliases, callSite.Target),
                MemberName = ResolveValueAlias(aliases, callSite.MemberName)
            })
            .ToArray();
        var callArguments = mir.CallArguments
            .Select(value => ResolveValueAlias(aliases, value))
            .ToArray();
        var aggregateValues = mir.AggregateValues
            .Select(value => ResolveValueAlias(aliases, value))
            .ToArray();
        var phiIncomings = mir.PhiIncomings
            .Select(incoming => incoming with
            {
                Value = ResolveValueAlias(aliases, incoming.Value)
            })
            .ToArray();

        return CreateMethod(
            mir,
            optimizedInstructions.ToArray(),
            blocks,
            mir.PhiSites.ToArray(),
            phiIncomings,
            mir.Constants.ToArray(),
            mir.Strings.ToArray(),
            lValues,
            callSites,
            callArguments,
            aggregateValues);

        void MarkTemporaryUsed(Avm1MirTemporaryIndex temporary)
        {
            if (temporary.IsValid && temporary.Value < usedTemporaries.Length)
                usedTemporaries[temporary.Value] = true;
        }
    }

    private static void PreserveRequiredPhiStructure(
        Avm1MirMethod mir,
        bool[] executableBlocks,
        HashSet<ControlFlowEdge> executableEdges,
        CancellationToken cancellationToken)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var blockIndex in mir.BlockLayout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExecutable(executableBlocks, blockIndex))
                    continue;

                var block = mir[blockIndex];
                var end = block.Instructions.Start + block.Instructions.Count;
                for (var i = block.Instructions.Start; i < end; i++)
                {
                    var instruction = mir.Instructions[i];
                    if (instruction.Kind is not (
                            Avm1MirInstructionKind.StorePhi or
                            Avm1MirInstructionKind.Phi) ||
                        !instruction.PhiSite.IsValid ||
                        instruction.PhiSite.Value >= mir.PhiSites.Count)
                    {
                        continue;
                    }

                    var site = mir[instruction.PhiSite];
                    if (!executableBlocks[site.MergeBlock.Value])
                    {
                        executableBlocks[site.MergeBlock.Value] = true;
                        changed = true;
                    }
                    if (HasExecutableIncoming(mir, site, executableEdges))
                        continue;

                    for (var incomingIndex = 0;
                        incomingIndex < site.Incomings.Count;
                        incomingIndex++)
                    {
                        var incoming = mir.GetPhiIncoming(site, incomingIndex);
                        if (!executableBlocks[incoming.Predecessor.Value])
                        {
                            executableBlocks[incoming.Predecessor.Value] = true;
                            changed = true;
                        }
                        changed |= executableEdges.Add(new ControlFlowEdge(
                            incoming.Predecessor,
                            site.MergeBlock));
                    }
                }
            }
        }
    }

    private static bool HasExecutableIncoming(
        Avm1MirMethod mir,
        Avm1MirPhiSite site,
        IReadOnlySet<ControlFlowEdge> executableEdges)
    {
        for (var i = 0; i < site.Incomings.Count; i++)
        {
            var incoming = mir.GetPhiIncoming(site, i);
            if (executableEdges.Contains(new ControlFlowEdge(
                    incoming.Predecessor,
                    site.MergeBlock)))
            {
                return true;
            }
        }
        return false;
    }

    private static (
        Avm1MirPhiSite[] Sites,
        Avm1MirPhiIncoming[] Incomings,
        Avm1MirPhiSiteIndex[] Map) RebuildPhiSites(
        Avm1MirMethod mir,
        IReadOnlyList<bool> executableBlocks,
        IReadOnlySet<ControlFlowEdge> executableEdges)
    {
        var sites = new List<Avm1MirPhiSite>();
        var incomings = new List<Avm1MirPhiIncoming>();
        var map = new Avm1MirPhiSiteIndex[mir.PhiSites.Count];
        Array.Fill(map, Avm1MirPhiSiteIndex.Invalid);
        foreach (var oldSite in mir.PhiSites)
        {
            if (!IsExecutable(executableBlocks, oldSite.MergeBlock))
                continue;

            var start = incomings.Count;
            for (var i = 0; i < oldSite.Incomings.Count; i++)
            {
                var incoming = mir.GetPhiIncoming(oldSite, i);
                if (executableEdges.Contains(new ControlFlowEdge(
                        incoming.Predecessor,
                        oldSite.MergeBlock)))
                {
                    incomings.Add(incoming);
                }
            }
            if (incomings.Count == start)
                continue;

            var index = new Avm1MirPhiSiteIndex(sites.Count);
            map[oldSite.Index.Value] = index;
            sites.Add(new Avm1MirPhiSite(
                index,
                oldSite.MergeBlock,
                new Avm1MirPhiIncomingRange(
                    start,
                    incomings.Count - start),
                oldSite.Origin));
        }

        return (sites.ToArray(), incomings.ToArray(), map);
    }

    private static bool LosesExecutablePhiReference(
        Avm1MirMethod mir,
        IReadOnlyList<bool> executableBlocks,
        IReadOnlyList<Avm1MirPhiSiteIndex> phiMap)
    {
        foreach (var blockIndex in mir.BlockLayout)
        {
            if (!IsExecutable(executableBlocks, blockIndex))
                continue;

            var block = mir[blockIndex];
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var i = block.Instructions.Start; i < end; i++)
            {
                var instruction = mir.Instructions[i];
                if (instruction.Kind is (
                        Avm1MirInstructionKind.StorePhi or
                        Avm1MirInstructionKind.Phi) &&
                    instruction.PhiSite.IsValid &&
                    !MapPhiSite(instruction.PhiSite, phiMap).IsValid)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void RewriteInstruction(
        Avm1MirInstruction instruction,
        IReadOnlyList<ValueFact> facts,
        Avm1MirPhiSite[] phiSites,
        IReadOnlyList<Avm1MirPhiSiteIndex> phiMap,
        byte swfVersion,
        List<Avm1MirConstant> constants,
        List<string> strings,
        Dictionary<ConstantValue, Avm1MirConstantIndex> constantIndices,
        Dictionary<string, Avm1MirStringIndex> stringIndices,
        List<Avm1MirInstruction> output)
    {
        var mappedPhi = MapPhiSite(instruction.PhiSite, phiMap);
        if (instruction.Kind is Avm1MirInstructionKind.BranchIfTrue &&
            GetFact(facts, instruction.Operand) is
            {
                Kind: ValueFactKind.Constant
            } condition)
        {
            AppendInstruction(
                output,
                CreateDiscard(instruction.Operand, instruction.Origin));
            var target = ToBoolean(condition.Constant)
                ? instruction.Target
                : instruction.AlternativeTarget;
            var branchPhi = mappedPhi.IsValid &&
                mappedPhi.Value < phiSites.Length &&
                phiSites[mappedPhi.Value].MergeBlock == target
                ? mappedPhi
                : Avm1MirPhiSiteIndex.Invalid;
            AppendInstruction(
                output,
                CreateBranch(instruction, target, branchPhi));
            return;
        }

        var fact = instruction.Result.IsValid &&
            instruction.Result.Value < facts.Count
            ? facts[instruction.Result.Value]
            : ValueFact.Unknown;
        if (instruction.Kind is (
                Avm1MirInstructionKind.Unary or
                Avm1MirInstructionKind.Binary) &&
            fact.Kind is ValueFactKind.Constant &&
            CanEncodeConstant(fact.Constant, swfVersion))
        {
            if (instruction.Kind is Avm1MirInstructionKind.Binary)
            {
                AppendInstruction(
                    output,
                    CreateDiscard(
                        instruction.SecondaryOperand,
                        instruction.Origin));
            }
            AppendInstruction(
                output,
                CreateDiscard(instruction.Operand, instruction.Origin));
            var constant = InternConstant(
                fact.Constant,
                constants,
                strings,
                constantIndices,
                stringIndices);
            AppendInstruction(output, CreateConstant(instruction, constant));
            return;
        }

        AppendInstruction(output, instruction with { PhiSite = mappedPhi });
    }

    private static Avm1MirPhiSiteIndex MapPhiSite(
        Avm1MirPhiSiteIndex oldSite,
        IReadOnlyList<Avm1MirPhiSiteIndex> map) =>
        oldSite.IsValid && oldSite.Value < map.Count
            ? map[oldSite.Value]
            : Avm1MirPhiSiteIndex.Invalid;

    private static Avm1MirInstruction CreateDiscard(
        Avm1MirValueIndex value,
        SourceOriginIndex origin) =>
        new(
            Avm1MirInstructionIndex.Invalid,
            Avm1MirInstructionKind.Discard,
            Avm1MirValueIndex.Invalid,
            value,
            Avm1MirValueIndex.Invalid,
            Avm1MirLValueIndex.Invalid,
            Avm1MirCallSiteIndex.Invalid,
            Avm1MirAggregateSiteIndex.Invalid,
            Avm1MirUrlSiteIndex.Invalid,
            Avm1MirFunctionSiteIndex.Invalid,
            Avm1MirTrySiteIndex.Invalid,
            Avm1MirWithSiteIndex.Invalid,
            Avm1MirCompletionSiteIndex.Invalid,
            Avm1MirCompletionStorage.None,
            Avm1MirPhiSiteIndex.Invalid,
            Avm1MirControlTargetIndex.Invalid,
            Avm1MirTemporaryIndex.Invalid,
            Avm1MirStringIndex.Invalid,
            Avm1MirConstantIndex.Invalid,
            Avm1MirNameKind.None,
            Avm1MirOperator.None,
            Avm1MirEffect.None,
            Avm1MirBlockIndex.Invalid,
            Avm1MirBlockIndex.Invalid,
            origin);

    private static Avm1MirInstruction CreateBranch(
        Avm1MirInstruction source,
        Avm1MirBlockIndex target,
        Avm1MirPhiSiteIndex phiSite) =>
        source with
        {
            Index = Avm1MirInstructionIndex.Invalid,
            Kind = Avm1MirInstructionKind.Branch,
            Operand = Avm1MirValueIndex.Invalid,
            SecondaryOperand = Avm1MirValueIndex.Invalid,
            PhiSite = phiSite,
            Target = target,
            AlternativeTarget = Avm1MirBlockIndex.Invalid
        };

    private static Avm1MirInstruction CreateConstant(
        Avm1MirInstruction source,
        Avm1MirConstantIndex constant) =>
        source with
        {
            Index = Avm1MirInstructionIndex.Invalid,
            Kind = Avm1MirInstructionKind.Constant,
            Operand = Avm1MirValueIndex.Invalid,
            SecondaryOperand = Avm1MirValueIndex.Invalid,
            Constant = constant,
            Operator = Avm1MirOperator.None,
            Effects = Avm1MirEffect.None
        };

    private static void AppendInstruction(
        List<Avm1MirInstruction> instructions,
        Avm1MirInstruction instruction) =>
        instructions.Add(instruction with
        {
            Index = new Avm1MirInstructionIndex(instructions.Count)
        });

    private static Avm1MirMethod RebuildInstructions(
        Avm1MirMethod mir,
        Avm1MirInstruction[] instructions,
        bool[] live,
        CancellationToken cancellationToken)
    {
        var optimized = new List<Avm1MirInstruction>(
            live.Count(value => value));
        var blocks = mir.Blocks.ToArray();
        foreach (var blockIndex in mir.BlockLayout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = mir[blockIndex];
            var start = optimized.Count;
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var i = block.Instructions.Start; i < end; i++)
            {
                if (live[i])
                    AppendInstruction(optimized, instructions[i]);
            }

            blocks[blockIndex.Value] = block with
            {
                Instructions = new Avm1MirInstructionRange(
                    start,
                    optimized.Count - start)
            };
        }

        return CreateMethod(
            mir,
            optimized.ToArray(),
            blocks,
            mir.PhiSites.ToArray(),
            mir.PhiIncomings.ToArray(),
            mir.Constants.ToArray(),
            mir.Strings.ToArray());
    }

    private static Avm1MirMethod CreateMethod(
        Avm1MirMethod source,
        Avm1MirInstruction[] instructions,
        Avm1MirBasicBlock[] blocks,
        Avm1MirPhiSite[] phiSites,
        Avm1MirPhiIncoming[] phiIncomings,
        Avm1MirConstant[] constants,
        string[] strings,
        Avm1MirLValue[]? lValues = null,
        Avm1MirCallSite[]? callSites = null,
        Avm1MirValueIndex[]? callArguments = null,
        Avm1MirValueIndex[]? aggregateValues = null,
        Avm1MirControlTarget[]? controlTargets = null,
        Avm1MirBlockIndex[]? blockLayout = null) =>
        new(
            instructions,
            blocks,
            blockLayout ?? source.BlockLayout.ToArray(),
            source.EntryBlock,
            lValues ?? source.LValues.ToArray(),
            callSites ?? source.CallSites.ToArray(),
            callArguments ?? source.CallArguments.ToArray(),
            source.AggregateSites.ToArray(),
            aggregateValues ?? source.AggregateValues.ToArray(),
            source.UrlSites.ToArray(),
            source.FunctionSites.ToArray(),
            source.TrySites.ToArray(),
            source.VisibleControlScopes.ToArray(),
            source.WithSites.ToArray(),
            source.WithVisibleControlScopes.ToArray(),
            source.CompletionSites.ToArray(),
            phiSites,
            phiIncomings,
            controlTargets ?? source.ControlTargets.ToArray(),
            source.TemporaryCount,
            constants,
            strings,
            source.ValueCount);

    private static bool[] FindLiveInstructions(
        Avm1MirMethod mir,
        Avm1MirInstruction[] instructions,
        CancellationToken cancellationToken)
    {
        var definitions = new int[mir.ValueCount];
        Array.Fill(definitions, -1);
        for (var i = 0; i < instructions.Length; i++)
        {
            var result = instructions[i].Result;
            if (result.IsValid && result.Value < definitions.Length)
                definitions[result.Value] = i;
        }

        var consumers = new List<int>?[mir.ValueCount];
        for (var i = 0; i < instructions.Length; i++)
        {
            VisitDependencies(mir, instructions[i], value =>
            {
                if (!value.IsValid || value.Value >= consumers.Length)
                    return;
                (consumers[value.Value] ??= []).Add(i);
            });
        }

        var live = new bool[instructions.Length];
        for (var i = 0; i < instructions.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instruction = instructions[i];
            if (instruction.Kind is Avm1MirInstructionKind.Discard ||
                IsDeadScalarCandidate(instruction))
            {
                continue;
            }

            MarkInstruction(i);
        }

        return live;

        void MarkInstruction(int index)
        {
            if (live[index])
                return;
            live[index] = true;

            var instruction = instructions[index];
            VisitDependencies(mir, instruction, MarkValue);
            if (!instruction.Result.IsValid ||
                instruction.Result.Value >= consumers.Length ||
                consumers[instruction.Result.Value] is not { } uses)
            {
                return;
            }

            foreach (var consumer in uses)
                MarkInstruction(consumer);
        }

        void MarkValue(Avm1MirValueIndex value)
        {
            if (!value.IsValid ||
                value.Value >= definitions.Length ||
                definitions[value.Value] < 0)
            {
                return;
            }

            MarkInstruction(definitions[value.Value]);
        }
    }

    private static bool IsDeadScalarCandidate(Avm1MirInstruction instruction) =>
        instruction.Effects is Avm1MirEffect.None &&
        instruction.Kind is
            Avm1MirInstructionKind.Constant or
            Avm1MirInstructionKind.Unary or
            Avm1MirInstructionKind.Binary;

    private static void VisitDependencies(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        Action<Avm1MirValueIndex> visit)
    {
        visit(instruction.Operand);
        visit(instruction.SecondaryOperand);

        if (instruction.LValue.IsValid &&
            instruction.LValue.Value < mir.LValues.Count)
        {
            var lValue = mir[instruction.LValue];
            visit(lValue.Receiver);
            visit(lValue.Key);
        }

        if (instruction.CallSite.IsValid &&
            instruction.CallSite.Value < mir.CallSites.Count)
        {
            var callSite = mir[instruction.CallSite];
            visit(callSite.Target);
            visit(callSite.MemberName);
            for (var i = 0; i < callSite.Arguments.Count; i++)
                visit(mir.GetCallArgument(callSite, i));
        }

        if (instruction.AggregateSite.IsValid &&
            instruction.AggregateSite.Value < mir.AggregateSites.Count)
        {
            var aggregate = mir[instruction.AggregateSite];
            for (var i = 0; i < aggregate.Values.Count; i++)
                visit(mir.GetAggregateValue(aggregate, i));
        }

        if (instruction.PhiSite.IsValid &&
            instruction.PhiSite.Value < mir.PhiSites.Count &&
            instruction.Kind is Avm1MirInstructionKind.Phi)
        {
            var phi = mir[instruction.PhiSite];
            for (var i = 0; i < phi.Incomings.Count; i++)
                visit(mir.GetPhiIncoming(phi, i).Value);
        }
    }
}
