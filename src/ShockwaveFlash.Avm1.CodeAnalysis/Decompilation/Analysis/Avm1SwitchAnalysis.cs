using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1SwitchAnalysis
{
    private Avm1SwitchAnalysis(Avm1SwitchRegion[] regions)
    {
        Regions = regions;
    }

    public IReadOnlyList<Avm1SwitchRegion> Regions { get; }

    public static Avm1SwitchAnalysis Build(
        Avm1ControlFlowGraph cfg,
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegionAnalysis regionAnalysis)
    {
        var ifByHeader = regionAnalysis.IfRegions.ToDictionary(region => region.Header);
        var valueGraph = new ValueGraph(tacIr, registerSsa, valueAnalysis);
        var claimedHeaders = new HashSet<BlockIndex>();
        var regions = new List<Avm1SwitchRegion>();

        foreach (var root in regionAnalysis.IfRegions.OrderBy(region => region.Header.Value))
        {
            if (claimedHeaders.Contains(root.Header) ||
                !TryBuildRegion(
                    cfg,
                    tacIr,
                    valueAnalysis,
                    ifByHeader,
                    valueGraph,
                    regionAnalysis,
                    root,
                    out var region))
            {
                continue;
            }

            regions.Add(region);
            foreach (var header in region.Headers)
                claimedHeaders.Add(header);
        }

        return new Avm1SwitchAnalysis(regions.ToArray());
    }

    private static bool TryBuildRegion(
        Avm1ControlFlowGraph cfg,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Dictionary<BlockIndex, Avm1IfRegion> ifByHeader,
        ValueGraph valueGraph,
        Avm1RegionAnalysis regionAnalysis,
        Avm1IfRegion root,
        out Avm1SwitchRegion region)
    {
        region = default;
        if (!TryExtractComparisonFromHeader(
            cfg,
            tacIr,
            valueGraph,
            root.Header,
            out var firstComparison))
            return false;

        var cases = new List<Avm1SwitchCaseRegion>();
        var candidateDiscriminators = new List<ValueIndex>();
        var headers = new List<BlockIndex> { root.Header };
        var seenHeaders = new HashSet<BlockIndex> { root.Header };
        var discriminator = ValueIndex.Invalid;
        var defaultEntry = firstComparison.MissEntry;

        while (defaultEntry.IsValid &&
            ifByHeader.ContainsKey(defaultEntry) &&
            seenHeaders.Add(defaultEntry) &&
            IsPureComparisonHeader(tacIr, cfg[defaultEntry]) &&
            TryExtractComparisonFromHeader(
                cfg,
                tacIr,
                valueGraph,
                defaultEntry,
                out var nextComparison))
        {
            if (!discriminator.IsValid)
            {
                if (!TryChooseDiscriminator(
                    valueAnalysis,
                    valueGraph,
                    firstComparison,
                    nextComparison,
                    out discriminator,
                    out var firstDiscriminator,
                    out var firstCaseValue,
                    out var nextDiscriminator,
                    out var nextCaseValue))
                {
                    break;
                }

                AddCase(firstComparison, firstDiscriminator, firstCaseValue);
                AddCase(nextComparison, nextDiscriminator, nextCaseValue);
            }
            else
            {
                if (!TryOrientComparison(
                    valueAnalysis,
                    valueGraph,
                    discriminator,
                    nextComparison,
                    out var nextDiscriminator,
                    out var nextCaseValue))
                {
                    break;
                }

                if (cases.Any(existing =>
                    valueGraph.AreEquivalentStable(existing.Value, nextCaseValue)))
                {
                    return false;
                }

                AddCase(nextComparison, nextDiscriminator, nextCaseValue);
            }

            headers.Add(nextComparison.Header);
            defaultEntry = nextComparison.MissEntry;
        }

        if (cases.Count < 2 || !discriminator.IsValid)
            return false;

        var headerSet = headers.ToHashSet();
        var dispatchStores = valueGraph.GetTransparentDispatchStores(
            candidateDiscriminators,
            headerSet,
            cfg);
        if (cases.Count == 2 && dispatchStores.Length == 0)
        {
            return false;
        }

        discriminator = valueGraph.ResolveTransparentDispatchSource(
            discriminator,
            dispatchStores);

        var bodyEntries = cases.Select(@case => @case.BodyEntry).ToHashSet();
        defaultEntry = ResolveTransparentDefaultEntry(
            cfg,
            defaultEntry,
            headerSet,
            bodyEntries);

        if (!TryDetermineMerge(
            cfg,
            ifByHeader,
            cases,
            defaultEntry,
            headerSet,
            bodyEntries,
            regionAnalysis,
            root.Header,
            out var merge))
        {
            return false;
        }

        if (defaultEntry != merge &&
            IsTransparentPathToBoundary(cfg, defaultEntry, merge, headerSet))
        {
            defaultEntry = merge;
        }

        if (defaultEntry.IsValid && defaultEntry != merge)
            bodyEntries.Add(defaultEntry);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var isGrouped =
                (i + 1 < cases.Count && cases[i + 1].BodyEntry == @case.BodyEntry) ||
                (i + 1 == cases.Count && defaultEntry == @case.BodyEntry);
            if (isGrouped)
            {
                cases[i] = @case with { ExitFlags = Avm1SwitchCaseExitFlags.Grouped };
                continue;
            }

            var nextBody = FindNextDistinctBody(cases, i + 1, @case.BodyEntry);
            if (!nextBody.IsValid && defaultEntry != merge)
                nextBody = defaultEntry;

            if (!TryClassifyCaseExit(
                cfg,
                @case.BodyEntry,
                merge,
                nextBody,
                root.Header,
                regionAnalysis,
                headerSet,
                bodyEntries,
                out var boundary,
                out var exitFlags))
            {
                return false;
            }

            cases[i] = @case with { BodyBoundary = boundary, ExitFlags = exitFlags };
        }

        if (defaultEntry != merge &&
            !ReachesMergeOrTerminates(cfg, defaultEntry, merge, headerSet))
        {
            return false;
        }

        region = new Avm1SwitchRegion(
            root.Header,
            merge,
            discriminator,
            cases.ToArray(),
            defaultEntry,
            headers.ToArray(),
            dispatchStores);
        return true;

        void AddCase(
            RawSwitchComparison comparison,
            ValueIndex candidateDiscriminator,
            ValueIndex caseValue)
        {
            candidateDiscriminators.Add(candidateDiscriminator);
            cases.Add(new Avm1SwitchCaseRegion(
                comparison.Header,
                caseValue,
                comparison.BodyEntry,
                BodyBoundary: BlockIndex.Invalid,
                Avm1SwitchCaseExitFlags.Break));
        }
    }

    private static bool IsPureComparisonHeader(Avm1TacIr tacIr, Avm1BasicBlock block)
    {
        foreach (var instruction in tacIr.Instructions)
        {
            if (!instruction.Action.IsValid ||
                instruction.Action.Value < block.StartAction.Value ||
                instruction.Action.Value >= block.EndAction.Value)
            {
                continue;
            }

            if (instruction.Op is not (
                Avm1TacOp.NoOp or
                Avm1TacOp.Phi or
                Avm1TacOp.LoadConstant or
                Avm1TacOp.LoadRegister or
                Avm1TacOp.Copy or
                Avm1TacOp.Pop or
                Avm1TacOp.StackOnly or
                Avm1TacOp.Unary or
                Avm1TacOp.Binary or
                Avm1TacOp.GetVariable or
                Avm1TacOp.GetMember or
                Avm1TacOp.Cast or
                Avm1TacOp.BranchIf))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct RawSwitchComparison(
        BlockIndex Header,
        ValueIndex Left,
        ValueIndex Right,
        ActionOpcode Opcode,
        BlockIndex BodyEntry,
        BlockIndex MissEntry);

    private static bool TryExtractComparisonFromHeader(
        Avm1ControlFlowGraph cfg,
        Avm1TacIr tacIr,
        ValueGraph valueGraph,
        BlockIndex header,
        out RawSwitchComparison comparison)
    {
        comparison = default;
        var block = cfg[header];
        var firstIsCase = TryExtractComparison(
            cfg,
            tacIr,
            valueGraph,
            header,
            block.FirstSuccessor,
            out var firstLeft,
            out var firstRight,
            out var firstOpcode);
        var secondIsCase = TryExtractComparison(
            cfg,
            tacIr,
            valueGraph,
            header,
            block.SecondSuccessor,
            out var secondLeft,
            out var secondRight,
            out var secondOpcode);
        if (firstIsCase == secondIsCase)
            return false;

        comparison = new RawSwitchComparison(
            header,
            firstIsCase ? firstLeft : secondLeft,
            firstIsCase ? firstRight : secondRight,
            firstIsCase ? firstOpcode : secondOpcode,
            firstIsCase ? block.FirstSuccessor : block.SecondSuccessor,
            firstIsCase ? block.SecondSuccessor : block.FirstSuccessor);
        return true;
    }

    private static bool TryChooseDiscriminator(
        Avm1ValueAnalysis valueAnalysis,
        ValueGraph valueGraph,
        RawSwitchComparison first,
        RawSwitchComparison second,
        out ValueIndex discriminator,
        out ValueIndex firstDiscriminator,
        out ValueIndex firstCaseValue,
        out ValueIndex secondDiscriminator,
        out ValueIndex secondCaseValue)
    {
        var firstLeftWorks = TryOrientComparison(
            valueAnalysis,
            valueGraph,
            first.Left,
            first,
            out var firstLeftDiscriminator,
            out var firstRightCase);
        var secondLeftWorks = TryOrientComparison(
            valueAnalysis,
            valueGraph,
            first.Left,
            second,
            out var secondLeftDiscriminator,
            out var secondRightCase);
        var leftWorks = firstLeftWorks && secondLeftWorks;
        var firstRightWorks = TryOrientComparison(
            valueAnalysis,
            valueGraph,
            first.Right,
            first,
            out var firstRightDiscriminator,
            out var firstLeftCase);
        var secondRightWorks = TryOrientComparison(
            valueAnalysis,
            valueGraph,
            first.Right,
            second,
            out var secondRightDiscriminator,
            out var secondLeftCase);
        var rightWorks = firstRightWorks && secondRightWorks;
        if (leftWorks == rightWorks)
        {
            discriminator = ValueIndex.Invalid;
            firstDiscriminator = ValueIndex.Invalid;
            firstCaseValue = ValueIndex.Invalid;
            secondDiscriminator = ValueIndex.Invalid;
            secondCaseValue = ValueIndex.Invalid;
            return false;
        }

        discriminator = leftWorks ? first.Left : first.Right;
        firstDiscriminator = leftWorks ? firstLeftDiscriminator : firstRightDiscriminator;
        firstCaseValue = leftWorks ? firstRightCase : firstLeftCase;
        secondDiscriminator = leftWorks ? secondLeftDiscriminator : secondRightDiscriminator;
        secondCaseValue = leftWorks ? secondRightCase : secondLeftCase;
        return !valueGraph.AreEquivalentStable(firstCaseValue, secondCaseValue);
    }

    private static bool TryOrientComparison(
        Avm1ValueAnalysis valueAnalysis,
        ValueGraph valueGraph,
        ValueIndex discriminator,
        RawSwitchComparison comparison,
        out ValueIndex matchedDiscriminator,
        out ValueIndex caseValue)
    {
        var leftMatches = valueGraph.AreEquivalentStable(discriminator, comparison.Left);
        var rightMatches = valueGraph.AreEquivalentStable(discriminator, comparison.Right);
        if (leftMatches == rightMatches)
        {
            matchedDiscriminator = ValueIndex.Invalid;
            caseValue = ValueIndex.Invalid;
            return false;
        }

        matchedDiscriminator = leftMatches ? comparison.Left : comparison.Right;
        caseValue = leftMatches ? comparison.Right : comparison.Left;
        return valueGraph.IsStable(caseValue) &&
            IsSwitchCompatibleEquality(
                comparison.Opcode,
                valueAnalysis[matchedDiscriminator],
                valueAnalysis[caseValue]);
    }

    private static BlockIndex FindNextDistinctBody(
        List<Avm1SwitchCaseRegion> cases,
        int start,
        BlockIndex currentBody)
    {
        for (var i = start; i < cases.Count; i++)
            if (cases[i].BodyEntry != currentBody)
                return cases[i].BodyEntry;
        return BlockIndex.Invalid;
    }

    private static BlockIndex ResolveTransparentDefaultEntry(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        HashSet<BlockIndex> headers,
        HashSet<BlockIndex> caseBodyEntries)
    {
        var current = entry;
        var visited = new HashSet<BlockIndex>();
        while (current.IsValid &&
            !headers.Contains(current) &&
            visited.Add(current))
        {
            if (caseBodyEntries.Contains(current))
                return current;

            var block = cfg[current];
            if (block.Terminator is not Avm1BlockTerminatorKind.Jump ||
                block.EndAction.Value != block.StartAction.Value + 1)
            {
                break;
            }

            current = block.FirstSuccessor;
        }

        return entry;
    }

    private static bool IsTransparentPathToBoundary(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        BlockIndex boundary,
        HashSet<BlockIndex> headers)
    {
        var current = entry;
        var visited = new HashSet<BlockIndex>();
        while (current != boundary)
        {
            if (!current.IsValid ||
                headers.Contains(current) ||
                !visited.Add(current))
            {
                return false;
            }

            var block = cfg[current];
            if (block.Terminator is not Avm1BlockTerminatorKind.Jump ||
                block.EndAction.Value != block.StartAction.Value + 1)
            {
                return false;
            }

            current = block.FirstSuccessor;
        }

        return true;
    }

    private static bool TryDetermineMerge(
        Avm1ControlFlowGraph cfg,
        Dictionary<BlockIndex, Avm1IfRegion> ifByHeader,
        List<Avm1SwitchCaseRegion> cases,
        BlockIndex defaultEntry,
        HashSet<BlockIndex> headers,
        HashSet<BlockIndex> caseBodyEntries,
        Avm1RegionAnalysis regionAnalysis,
        BlockIndex switchHeader,
        out BlockIndex merge)
    {
        merge = BlockIndex.Invalid;
        var origins = cases.Select(@case => @case.BodyEntry).Distinct().ToList();
        if (defaultEntry.IsValid && !origins.Contains(defaultEntry))
            origins.Add(defaultEntry);

        var candidates = new Dictionary<BlockIndex, int>();
        foreach (var header in headers)
        {
            if (ifByHeader.TryGetValue(header, out var region))
                AddCandidate(region.Merge, jumpVote: false);
        }

        var seenJumpEdges = new HashSet<(BlockIndex Source, BlockIndex Target)>();
        foreach (var origin in origins)
            CollectJumpTargets(origin);

        var bestReached = -1;
        var bestVotes = -1;
        var bestLoopPenalty = int.MaxValue;
        foreach (var (candidate, votes) in candidates)
        {
            if (candidate == defaultEntry && votes == 0)
                continue;

            var reached = 0;
            var reachedFromDefault = false;
            var valid = true;
            foreach (var origin in origins)
            {
                if (CanReachBoundary(cfg, origin, candidate, headers))
                {
                    reached++;
                    reachedFromDefault |= origin == defaultEntry;
                    continue;
                }

                if (!AlwaysTerminatesBefore(
                    cfg,
                    origin,
                    candidate,
                    headers,
                    regionAnalysis,
                    switchHeader))
                {
                    valid = false;
                    break;
                }
            }

            // A join reached only from one explicit case belongs to that case
            // (for example, a conditional expression feeding its return).
            // A default-only continuation can still be the canonical switch
            // tail when every explicit case terminates.
            if (!valid ||
                reached == 0 ||
                reached == 1 && !reachedFromDefault)
                continue;

            var loopPenalty = IsEnclosingLoopImplicitContinueTarget(
                regionAnalysis,
                switchHeader,
                candidate)
                    ? 1
                    : 0;
            if (loopPenalty < bestLoopPenalty ||
                (loopPenalty == bestLoopPenalty &&
                    (reached > bestReached ||
                     (reached == bestReached &&
                        (votes > bestVotes ||
                         (votes == bestVotes &&
                            (!merge.IsValid || candidate.Value < merge.Value)))))))
            {
                merge = candidate;
                bestReached = reached;
                bestVotes = votes;
                bestLoopPenalty = loopPenalty;
            }
        }

        if (merge.IsValid)
            return true;

        return origins.Count > 0 && origins.All(origin =>
            AlwaysTerminatesBefore(
                cfg,
                origin,
                BlockIndex.Invalid,
                headers,
                regionAnalysis,
                switchHeader));

        void AddCandidate(BlockIndex candidate, bool jumpVote)
        {
            if (!candidate.IsValid ||
                headers.Contains(candidate) ||
                caseBodyEntries.Contains(candidate) ||
                IsEnclosingLoopBoundaryTarget(
                    regionAnalysis,
                    switchHeader,
                    candidate))
                return;

            candidates.TryGetValue(candidate, out var votes);
            candidates[candidate] = votes + (jumpVote ? 1 : 0);
        }

        void CollectJumpTargets(BlockIndex origin)
        {
            var visited = new HashSet<BlockIndex>();
            var worklist = new Queue<BlockIndex>();
            worklist.Enqueue(origin);
            while (worklist.TryDequeue(out var block))
            {
                if (!block.IsValid || headers.Contains(block) || !visited.Add(block))
                    continue;
                if (block != origin &&
                    IsEnclosingLoopBoundaryTarget(
                        regionAnalysis,
                        switchHeader,
                        block))
                {
                    continue;
                }

                var node = cfg[block];
                if (node.Terminator is Avm1BlockTerminatorKind.Jump && node.FirstSuccessor.IsValid)
                {
                    var edge = (block, node.FirstSuccessor);
                    if (seenJumpEdges.Add(edge))
                        AddCandidate(node.FirstSuccessor, jumpVote: true);
                }

                switch (node.Terminator)
                {
                    case Avm1BlockTerminatorKind.FallThrough:
                    case Avm1BlockTerminatorKind.Jump:
                        worklist.Enqueue(node.FirstSuccessor);
                        break;
                    case Avm1BlockTerminatorKind.ConditionalBranch:
                        worklist.Enqueue(node.FirstSuccessor);
                        worklist.Enqueue(node.SecondSuccessor);
                        break;
                }
            }
        }
    }

    private static bool TryClassifyCaseExit(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        BlockIndex merge,
        BlockIndex expectedNextBody,
        BlockIndex switchHeader,
        Avm1RegionAnalysis regionAnalysis,
        HashSet<BlockIndex> headers,
        HashSet<BlockIndex> bodyEntries,
        out BlockIndex boundary,
        out Avm1SwitchCaseExitFlags exitFlags)
    {
        boundary = merge;
        exitFlags = default;
        var destinations = new HashSet<BlockIndex>();
        var visited = new HashSet<BlockIndex>();
        var worklist = new Queue<BlockIndex>();
        var terminates = false;
        worklist.Enqueue(entry);

        while (worklist.TryDequeue(out var block))
        {
            if (block == merge)
            {
                destinations.Add(block);
                continue;
            }

            if (block != entry && bodyEntries.Contains(block))
            {
                destinations.Add(block);
                continue;
            }

            if (block != entry &&
                IsEnclosingLoopControlTarget(regionAnalysis, switchHeader, block))
            {
                destinations.Add(block);
                continue;
            }

            if (!block.IsValid || headers.Contains(block))
                return false;
            if (!visited.Add(block))
                continue;

            var node = cfg[block];
            switch (node.Terminator)
            {
                case Avm1BlockTerminatorKind.Return:
                case Avm1BlockTerminatorKind.Throw:
                case Avm1BlockTerminatorKind.End:
                    terminates = true;
                    break;
                case Avm1BlockTerminatorKind.FallThrough:
                case Avm1BlockTerminatorKind.Jump:
                    worklist.Enqueue(node.FirstSuccessor);
                    break;
                case Avm1BlockTerminatorKind.ConditionalBranch:
                    worklist.Enqueue(node.FirstSuccessor);
                    worklist.Enqueue(node.SecondSuccessor);
                    break;
                default:
                    return false;
            }
        }

        foreach (var destination in destinations)
        {
            if (destination == merge)
            {
                exitFlags |= Avm1SwitchCaseExitFlags.Break;
                continue;
            }

            if (expectedNextBody.IsValid && destination == expectedNextBody)
            {
                boundary = expectedNextBody;
                exitFlags |= Avm1SwitchCaseExitFlags.FallThrough;
                continue;
            }

            if (IsEnclosingLoopControlTarget(regionAnalysis, switchHeader, destination))
            {
                if (destinations.Count == 1)
                    boundary = destination;
                exitFlags |= Avm1SwitchCaseExitFlags.Escaping;
                continue;
            }

            return false;
        }

        if (terminates)
            exitFlags |= Avm1SwitchCaseExitFlags.Terminating;

        return exitFlags is not Avm1SwitchCaseExitFlags.None;
    }

    private static bool IsEnclosingLoopControlTarget(
        Avm1RegionAnalysis regionAnalysis,
        BlockIndex switchHeader,
        BlockIndex target) =>
        IsEnclosingLoopBoundaryTarget(
            regionAnalysis,
            switchHeader,
            target) ||
        IsEnclosingLoopImplicitContinueTarget(
            regionAnalysis,
            switchHeader,
            target);

    private static bool IsEnclosingLoopBoundaryTarget(
        Avm1RegionAnalysis regionAnalysis,
        BlockIndex switchHeader,
        BlockIndex target)
    {
        if (!target.IsValid)
            return false;

        foreach (var loop in regionAnalysis.WhileRegions)
        {
            if (ContainsBlock(loop.Blocks, switchHeader) &&
                (target == loop.Header || target == loop.Exit))
            {
                return true;
            }
        }

        foreach (var loop in regionAnalysis.DoWhileRegions)
        {
            if (ContainsBlock(loop.Blocks, switchHeader) &&
                (target == loop.ConditionBlock || target == loop.Exit))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEnclosingLoopImplicitContinueTarget(
        Avm1RegionAnalysis regionAnalysis,
        BlockIndex switchHeader,
        BlockIndex target)
    {
        if (!target.IsValid)
            return false;

        foreach (var loop in regionAnalysis.WhileRegions)
        {
            if (ContainsBlock(loop.Blocks, switchHeader) &&
                loop.BodyBlocks.Count > 0 &&
                target == loop.BodyBlocks[^1])
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsBlock(IReadOnlyList<BlockIndex> blocks, BlockIndex target)
    {
        for (var i = 0; i < blocks.Count; i++)
            if (blocks[i] == target)
                return true;
        return false;
    }

    private static bool CanReachBoundary(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex boundary,
        HashSet<BlockIndex> headers)
    {
        if (!start.IsValid || !boundary.IsValid)
            return false;

        var visited = new HashSet<BlockIndex>();
        var worklist = new Queue<BlockIndex>();
        worklist.Enqueue(start);
        while (worklist.TryDequeue(out var block))
        {
            if (block == boundary)
                return true;
            if (!block.IsValid || headers.Contains(block) || !visited.Add(block))
                continue;

            var node = cfg[block];
            worklist.Enqueue(node.FirstSuccessor);
            worklist.Enqueue(node.SecondSuccessor);
        }

        return false;
    }

    private static bool AlwaysTerminatesBefore(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex boundary,
        HashSet<BlockIndex> headers,
        Avm1RegionAnalysis regionAnalysis,
        BlockIndex switchHeader)
    {
        var memo = new Dictionary<BlockIndex, bool>();
        var visiting = new HashSet<BlockIndex>();
        return Visit(start);

        bool Visit(BlockIndex block)
        {
            if (!block.IsValid)
                return !boundary.IsValid;
            if (block == boundary || headers.Contains(block))
                return false;
            if (IsEnclosingLoopControlTarget(
                regionAnalysis,
                switchHeader,
                block))
            {
                return true;
            }
            if (memo.TryGetValue(block, out var known))
                return known;
            if (!visiting.Add(block))
                return false;

            var node = cfg[block];
            var result = node.Terminator switch
            {
                Avm1BlockTerminatorKind.Return or
                    Avm1BlockTerminatorKind.Throw or
                    Avm1BlockTerminatorKind.End => true,
                Avm1BlockTerminatorKind.FallThrough or Avm1BlockTerminatorKind.Jump =>
                    Visit(node.FirstSuccessor),
                Avm1BlockTerminatorKind.ConditionalBranch =>
                    Visit(node.FirstSuccessor) && Visit(node.SecondSuccessor),
                _ => false
            };

            visiting.Remove(block);
            memo[block] = result;
            return result;
        }
    }

    private static bool TryExtractComparison(
        Avm1ControlFlowGraph cfg,
        Avm1TacIr tacIr,
        ValueGraph valueGraph,
        BlockIndex header,
        BlockIndex bodyEntry,
        out ValueIndex left,
        out ValueIndex right,
        out ActionOpcode opcode)
    {
        left = ValueIndex.Invalid;
        right = ValueIndex.Invalid;
        opcode = default;
        var block = cfg[header];
        if (bodyEntry != block.FirstSuccessor && bodyEntry != block.SecondSuccessor)
            return false;

        var branch = tacIr.Instructions.LastOrDefault(instruction =>
            instruction.Op is Avm1TacOp.BranchIf &&
            instruction.Action.Value >= block.StartAction.Value &&
            instruction.Action.Value < block.EndAction.Value);
        if (branch.Op is not Avm1TacOp.BranchIf || !branch.Operand0.IsValid)
            return false;

        var condition = branch.Operand0;
        var negated = bodyEntry == block.FirstSuccessor;
        while (valueGraph.TryGetDefinition(condition, out var definition))
        {
            if (definition.Op is Avm1TacOp.Copy)
            {
                condition = definition.Operand0;
                continue;
            }

            if (definition.Op is Avm1TacOp.Unary && definition.Opcode is ActionOpcode.Not)
            {
                condition = definition.Operand0;
                negated = !negated;
                continue;
            }

            if (definition.Op is not Avm1TacOp.Binary ||
                negated ||
                definition.Opcode is not (
                    ActionOpcode.Equals or
                    ActionOpcode.Equals2 or
                    ActionOpcode.StringEquals or
                    ActionOpcode.StrictEquals))
            {
                return false;
            }

            left = definition.Operand0;
            right = definition.Operand1;
            opcode = definition.Opcode;
            return true;
        }

        return false;
    }

    private static bool IsSwitchCompatibleEquality(
        ActionOpcode opcode,
        Avm1ValueFact discriminator,
        Avm1ValueFact caseValue)
    {
        // AS2 switch uses strict equality, so loose bytecode comparisons need a proven common primitive domain.
        if (opcode is ActionOpcode.StrictEquals)
            return true;

        if (IsNumeric(discriminator.Type) && IsNumeric(caseValue.Type))
            return true;

        return discriminator.Type == caseValue.Type && discriminator.Type is
            Avm1InferredType.String or
            Avm1InferredType.Boolean or
            Avm1InferredType.Null or
            Avm1InferredType.Undefined;
    }

    private static bool IsNumeric(Avm1InferredType type) =>
        type is Avm1InferredType.Integer or Avm1InferredType.Number;

    private static bool ReachesMergeOrTerminates(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        BlockIndex merge,
        HashSet<BlockIndex> headers)
    {
        if (entry == merge)
            return true;

        var hasOutcome = false;
        var visited = new HashSet<BlockIndex>();
        var worklist = new Queue<BlockIndex>();
        worklist.Enqueue(entry);
        while (worklist.TryDequeue(out var block))
        {
            if (block == merge)
            {
                hasOutcome = true;
                continue;
            }

            if (!block.IsValid || headers.Contains(block))
                return false;
            if (!visited.Add(block))
                continue;

            var node = cfg[block];
            switch (node.Terminator)
            {
                case Avm1BlockTerminatorKind.Return:
                case Avm1BlockTerminatorKind.Throw:
                case Avm1BlockTerminatorKind.End:
                    hasOutcome = true;
                    break;
                case Avm1BlockTerminatorKind.FallThrough:
                case Avm1BlockTerminatorKind.Jump:
                    worklist.Enqueue(node.FirstSuccessor);
                    break;
                case Avm1BlockTerminatorKind.ConditionalBranch:
                    worklist.Enqueue(node.FirstSuccessor);
                    worklist.Enqueue(node.SecondSuccessor);
                    break;
                default:
                    return false;
            }
        }

        return hasOutcome;
    }

    private static bool HaveSameConstant(Avm1ValueFact left, Avm1ValueFact right)
    {
        if (left.ConstantKind is Avm1ConstantKind.Unknown || left.ConstantKind != right.ConstantKind)
            return false;

        return left.ConstantKind switch
        {
            Avm1ConstantKind.Undefined or Avm1ConstantKind.Null => true,
            Avm1ConstantKind.Boolean => left.BooleanValue == right.BooleanValue,
            Avm1ConstantKind.Integer => left.IntegerValue == right.IntegerValue,
            Avm1ConstantKind.Number => left.NumberValue.Equals(right.NumberValue),
            Avm1ConstantKind.String => string.Equals(left.StringValue, right.StringValue, StringComparison.Ordinal),
            _ => false
        };
    }

    private sealed class ValueGraph
    {
        private readonly Avm1ValueAnalysis _valueAnalysis;
        private readonly Avm1TacIr _tacIr;
        private readonly Dictionary<ValueIndex, Avm1TacInstruction> _definitions;
        private readonly Dictionary<IrIndex, Avm1RegisterAccess> _registerAccesses;
        private readonly Dictionary<(int Register, int Version), Avm1RegisterAccess> _registerWrites;
        private readonly Dictionary<(int Register, int Version), Avm1RegisterAccess[]> _registerReads;

        public ValueGraph(
            Avm1TacIr tacIr,
            Avm1RegisterSsa registerSsa,
            Avm1ValueAnalysis valueAnalysis)
        {
            _valueAnalysis = valueAnalysis;
            _tacIr = tacIr;
            _definitions = tacIr.Instructions
                .Where(instruction => instruction.Result.IsValid && DefinesValue(instruction.Op))
                .ToDictionary(instruction => instruction.Result);
            _registerAccesses = registerSsa.Accesses.ToDictionary(access => access.Instruction);
            _registerWrites = registerSsa.Accesses
                .Where(access => access.Kind is Avm1RegisterAccessKind.Write && access.Source.IsValid)
                .GroupBy(access => (access.Register, access.Version))
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single());
            _registerReads = registerSsa.Accesses
                .Where(access => access.Kind is Avm1RegisterAccessKind.Read)
                .GroupBy(access => (access.Register, access.Version))
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        public bool TryGetDefinition(ValueIndex value, out Avm1TacInstruction definition) =>
            _definitions.TryGetValue(value, out definition);

        private static bool DefinesValue(Avm1TacOp op) => op is
            Avm1TacOp.Phi or
            Avm1TacOp.LoadConstant or
            Avm1TacOp.LoadRegister or
            Avm1TacOp.Copy or
            Avm1TacOp.Unary or
            Avm1TacOp.Binary or
            Avm1TacOp.GetVariable or
            Avm1TacOp.GetMember or
            Avm1TacOp.Delete or
            Avm1TacOp.GetTime or
            Avm1TacOp.InitArray or
            Avm1TacOp.InitObject or
            Avm1TacOp.NewObject or
            Avm1TacOp.NewMethod or
            Avm1TacOp.CallFunction or
            Avm1TacOp.CallMethod or
            Avm1TacOp.FunctionLiteral or
            Avm1TacOp.Cast or
            Avm1TacOp.Enumerate;

        public bool IsStable(ValueIndex value) => IsStable(value, new HashSet<ValueIndex>());

        public bool AreEquivalentStable(ValueIndex left, ValueIndex right) =>
            IsStable(left) &&
            IsStable(right) &&
            AreEquivalent(left, right, depth: 0);

        public IrIndex[] GetTransparentDispatchStores(
            IReadOnlyList<ValueIndex> discriminators,
            IReadOnlySet<BlockIndex> headers,
            Avm1ControlFlowGraph cfg)
        {
            var candidateReads = new HashSet<IrIndex>();
            var candidateWrites = new Dictionary<(int Register, int Version), Avm1RegisterAccess>();
            foreach (var discriminator in discriminators)
            {
                var current = discriminator;
                var visited = new HashSet<ValueIndex>();
                while (current.IsValid && visited.Add(current) && TryGetDefinition(current, out var definition))
                {
                    if (definition.Op is Avm1TacOp.Copy)
                    {
                        current = definition.Operand0;
                        continue;
                    }

                    if (!TryResolveRegisterSource(current, out var source, out var read, out var write))
                        break;

                    candidateReads.Add(read.Instruction);
                    candidateWrites[(write.Register, write.Version)] = write;
                    current = source;
                }
            }

            if (candidateWrites.Count == 0)
                return [];

            var stores = new List<IrIndex>(candidateWrites.Count);
            foreach (var (key, write) in candidateWrites)
            {
                if (_tacIr[write.Instruction].Op is not Avm1TacOp.StoreRegister ||
                    !IsInHeaders(write.Action, headers, cfg) ||
                    !_registerReads.TryGetValue(key, out var reads) ||
                    reads.Length == 0 ||
                    reads.Any(read =>
                        !candidateReads.Contains(read.Instruction) ||
                        !IsInHeaders(read.Action, headers, cfg)))
                {
                    continue;
                }

                stores.Add(write.Instruction);
            }

            stores.Sort(static (left, right) => left.Value.CompareTo(right.Value));
            return stores.ToArray();
        }

        public ValueIndex ResolveTransparentDispatchSource(
            ValueIndex discriminator,
            IrIndex[] dispatchStores)
        {
            if (!discriminator.IsValid || dispatchStores.Length == 0)
                return discriminator;

            var current = discriminator;
            for (var depth = 0; depth < dispatchStores.Length; depth++)
            {
                if (!current.IsValid ||
                    !TryResolveRegisterSource(current, out var source, out _, out var write) ||
                    Array.IndexOf(dispatchStores, write.Instruction) < 0 ||
                    !TryGetDefinition(source, out var sourceDefinition) ||
                    sourceDefinition.Index.Value + 1 != write.Instruction.Value)
                {
                    break;
                }

                current = source;
            }

            return current;
        }

        private static bool IsInHeaders(
            ActionIndex action,
            IReadOnlySet<BlockIndex> headers,
            Avm1ControlFlowGraph cfg)
        {
            if (!action.IsValid)
                return false;

            foreach (var header in headers)
            {
                var block = cfg[header];
                if (action.Value >= block.StartAction.Value && action.Value < block.EndAction.Value)
                    return true;
            }

            return false;
        }

        private bool IsStable(ValueIndex value, HashSet<ValueIndex> visiting)
        {
            if (!value.IsValid || !visiting.Add(value))
                return false;

            var fact = _valueAnalysis[value];
            if (fact.ConstantKind is not Avm1ConstantKind.Unknown)
                return true;
            if (!TryGetDefinition(value, out var definition))
                return false;

            return definition.Op switch
            {
                Avm1TacOp.Copy => IsStable(definition.Operand0, visiting),
                Avm1TacOp.LoadRegister => true,
                Avm1TacOp.GetVariable => IsStable(definition.Operand0, visiting),
                Avm1TacOp.GetMember or Avm1TacOp.Cast =>
                    IsStable(definition.Operand0, visiting) && IsStable(definition.Operand1, visiting),
                _ => false
            };
        }

        private bool AreEquivalent(ValueIndex left, ValueIndex right, int depth)
        {
            if (left == right)
                return true;
            if (!left.IsValid || !right.IsValid || depth >= 16)
                return false;

            var leftFact = _valueAnalysis[left];
            var rightFact = _valueAnalysis[right];
            if (HaveSameConstant(leftFact, rightFact))
                return true;

            if (TryResolveRegisterSource(left, out var leftSource, out _, out _) &&
                AreEquivalent(leftSource, right, depth + 1))
            {
                return true;
            }

            if (TryResolveRegisterSource(right, out var rightSource, out _, out _) &&
                AreEquivalent(left, rightSource, depth + 1))
            {
                return true;
            }

            if (!TryGetDefinition(left, out var leftDefinition) ||
                !TryGetDefinition(right, out var rightDefinition) ||
                leftDefinition.Op != rightDefinition.Op)
            {
                return false;
            }

            if (leftDefinition.Op is Avm1TacOp.Copy)
                return AreEquivalent(leftDefinition.Operand0, rightDefinition.Operand0, depth + 1);

            if (leftDefinition.Op is Avm1TacOp.LoadRegister)
            {
                if (leftDefinition.IntOperand != rightDefinition.IntOperand ||
                    !_registerAccesses.TryGetValue(leftDefinition.Index, out var leftAccess) ||
                    !_registerAccesses.TryGetValue(rightDefinition.Index, out var rightAccess))
                {
                    return false;
                }

                return leftAccess.Version == rightAccess.Version;
            }

            if (leftDefinition.Op is Avm1TacOp.GetVariable)
                return AreEquivalent(leftDefinition.Operand0, rightDefinition.Operand0, depth + 1);

            if (leftDefinition.Op is Avm1TacOp.GetMember or Avm1TacOp.Cast)
            {
                return AreEquivalent(leftDefinition.Operand0, rightDefinition.Operand0, depth + 1) &&
                    AreEquivalent(leftDefinition.Operand1, rightDefinition.Operand1, depth + 1);
            }

            return false;
        }

        private bool TryResolveRegisterSource(
            ValueIndex value,
            out ValueIndex source,
            out Avm1RegisterAccess read,
            out Avm1RegisterAccess write)
        {
            source = ValueIndex.Invalid;
            read = default;
            write = default;
            if (!TryGetDefinition(value, out var definition) ||
                definition.Op is not Avm1TacOp.LoadRegister ||
                !_registerAccesses.TryGetValue(definition.Index, out read) ||
                read.Kind is not Avm1RegisterAccessKind.Read ||
                !_registerWrites.TryGetValue((read.Register, read.Version), out write) ||
                !write.Source.IsValid ||
                write.Source == value)
            {
                return false;
            }

            source = write.Source;
            return true;
        }
    }
}

public readonly record struct Avm1SwitchRegion(
    BlockIndex Header,
    BlockIndex Merge,
    ValueIndex Discriminator,
    IReadOnlyList<Avm1SwitchCaseRegion> Cases,
    BlockIndex DefaultEntry,
    IReadOnlyList<BlockIndex> Headers,
    IReadOnlyList<IrIndex> DispatchStores);

public readonly record struct Avm1SwitchCaseRegion(
    BlockIndex Header,
    ValueIndex Value,
    BlockIndex BodyEntry,
    BlockIndex BodyBoundary,
    Avm1SwitchCaseExitFlags ExitFlags);

[Flags]
public enum Avm1SwitchCaseExitFlags : byte
{
    None = 0,
    Grouped = 1 << 0,
    Break = 1 << 1,
    FallThrough = 1 << 2,
    Terminating = 1 << 3,
    Escaping = 1 << 4,
    ConditionalBreak = Break | FallThrough
}
