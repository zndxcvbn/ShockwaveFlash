using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;

namespace ShockwaveFlash.Avm1.Decompilation.Ast;

public sealed class Avm1ExpressionInliner
{
    private readonly Avm1InstructionTable _instructions;
    private readonly Avm1TacIr _tacIr;
    private readonly Avm1ControlFlowGraph _cfg;
    private readonly Avm1ValueAnalysis _valueAnalysis;
    private readonly int[] _useCounts;
    private readonly int[] _firstUseIndices;
    private readonly int[] _definitionIndices;
    private readonly bool[] _inlinedValues;
    private readonly bool[] _structurallyConsumedValues;
    private readonly Dictionary<ValueIndex, Avm1ConditionalValue> _conditionalValues = [];
    private readonly Dictionary<ValueIndex, Avm1TransportAssignment> _transportAssignments = [];
    private readonly Dictionary<ValueIndex, Avm1RegisterValueTransport> _registerValueTransports = [];
    private readonly Dictionary<ValueIndex, Avm1PostfixMemberUpdate> _postfixMemberUpdates = [];
    private readonly Dictionary<ValueIndex, Avm1PostfixVariableUpdate> _postfixVariableUpdates = [];
    private readonly Dictionary<ValueIndex, ValueIndex> _valueAliases = [];
    private readonly Dictionary<ValueIndex, int> _conditionalRecipeRoots = [];
    private readonly List<Avm1ConditionalRecipeNode> _conditionalRecipeNodes = [];
    private readonly HashSet<BlockIndex> _conditionalHeaders = [];
    private readonly bool[] _conditionalComponents;
    private readonly IReadOnlySet<IrIndex> _ignoredInstructions;
    private int _inlinedValueCount;

    public Avm1ExpressionInliner(
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ControlFlowGraph cfg,
        Avm1RegionAnalysis regions,
        Avm1ValueAnalysis valueAnalysis,
        IReadOnlySet<IrIndex>? ignoredInstructions = null)
    {
        _instructions = instructions;
        _tacIr = tacIr;
        _cfg = cfg;
        _valueAnalysis = valueAnalysis;
        _ignoredInstructions = ignoredInstructions ?? new HashSet<IrIndex>();
        var valueCount = valueAnalysis.Facts.Count;
        _useCounts = new int[valueCount];
        _firstUseIndices = new int[valueCount];
        _definitionIndices = new int[valueCount];
        _inlinedValues = new bool[valueCount];
        _structurallyConsumedValues = new bool[valueCount];
        _conditionalComponents = new bool[valueCount];
        Array.Fill(_firstUseIndices, -1);
        Array.Fill(_definitionIndices, -1);
        IndexInstructions();
        BuildConditionalValues(cfg, regions);
        BuildNestedConditionalValues(cfg, regions);
        AnalyzeInlining();
    }

    public bool IsRootInstruction(Avm1TacInstruction inst)
    {
        if ((inst.Flags & Avm1IrInstructionFlags.ContextProjection) != 0)
            return false;

        if (IsMarked(_structurallyConsumedValues, inst.Result))
            return false;

        if (IsNamedFunctionDefinition(inst) &&
            !IsConsumedByNonPop(inst.Result, inst.Index.Value))
        {
            return true;
        }

        if (IsEffectfulValue(inst) &&
            inst.Result.IsValid &&
            IsInlined(inst.Result) &&
            IsConsumedByNonPop(inst.Result, inst.Index.Value))
        {
            return false;
        }

        if (Avm1TacEffectAnalysis.IsSourceStatementRoot(inst, _valueAnalysis))
            return true;

        if (inst.Result.IsValid && Avm1TacEffectAnalysis.DefinesValue(inst.Op))
        {
            return !IsInlined(inst.Result);
        }

        return false;
    }

    private bool IsConsumedByNonPop(ValueIndex value, int defIdx)
    {
        var useIdx = FindFirstUseIndex(value, defIdx);
        return useIdx >= 0 && _tacIr.Instructions[useIdx].Op is not Avm1TacOp.Pop;
    }

    private bool IsNamedFunctionDefinition(Avm1TacInstruction instruction)
    {
        if (instruction.Op is not Avm1TacOp.FunctionLiteral ||
            !instruction.Action.IsValid)
        {
            return false;
        }

        return _instructions[instruction.Action].Action switch
        {
            ActionDefineFunction function => function.Name.Length > 0,
            ActionDefineFunction2 function => function.Name.Length > 0,
            _ => false
        };
    }

    public bool IsInlined(ValueIndex val) => IsMarked(_inlinedValues, val);

    internal int InlinedValueCount => _inlinedValueCount;

    public int GetUseCount(ValueIndex val) =>
        IsValueInRange(val) ? _useCounts[val.Value] : 0;

    public bool RequiresTemporary(Avm1TacInstruction instruction) =>
        instruction.Result.IsValid &&
        !IsInlined(instruction.Result) &&
        IsConsumedByNonPop(instruction.Result, instruction.Index.Value);

    public bool TryGetDefinition(ValueIndex val, out Avm1TacInstruction def)
    {
        if (IsValueInRange(val) && _definitionIndices[val.Value] >= 0)
        {
            def = _tacIr.Instructions[_definitionIndices[val.Value]];
            return true;
        }

        def = default;
        return false;
    }

    private bool HasDefinition(ValueIndex value) =>
        IsValueInRange(value) && _definitionIndices[value.Value] >= 0;

    private bool IsValueInRange(ValueIndex value) =>
        value.IsValid && (uint)value.Value < (uint)_definitionIndices.Length;

    private static bool IsMarked(bool[] values, ValueIndex value) =>
        value.IsValid &&
        (uint)value.Value < (uint)values.Length &&
        values[value.Value];

    private static void Mark(bool[] values, ValueIndex value)
    {
        if (value.IsValid && (uint)value.Value < (uint)values.Length)
            values[value.Value] = true;
    }

    private void MarkInlined(ValueIndex value)
    {
        if (!IsValueInRange(value) || _inlinedValues[value.Value])
            return;

        _inlinedValues[value.Value] = true;
        _inlinedValueCount++;
    }

    private void MarkConditionalComponents(IEnumerable<ValueIndex> values)
    {
        foreach (var value in values)
            Mark(_conditionalComponents, value);
    }

    public bool TryGetConditionalValue(ValueIndex value, out Avm1ConditionalValue conditional) =>
        _conditionalValues.TryGetValue(value, out conditional);

    internal void AddTransportAssignment(
        ValueIndex transportedValue,
        Avm1TacInstruction assignment,
        ValueIndex assignedValue) =>
        _transportAssignments[transportedValue] = new Avm1TransportAssignment(
            assignment,
            assignedValue);

    internal void AddTransportAssignment(
        ValueIndex transportedValue,
        Avm1TacInstruction assignment) =>
        AddTransportAssignment(transportedValue, assignment, transportedValue);

    internal bool TryGetTransportAssignment(
        ValueIndex transportedValue,
        out Avm1TransportAssignment assignment) =>
        _transportAssignments.TryGetValue(transportedValue, out assignment);

    internal void AddRegisterValueTransport(
        ValueIndex transportedValue,
        Avm1TacInstruction assignment,
        bool asAssignmentExpression) =>
        _registerValueTransports[transportedValue] = new Avm1RegisterValueTransport(
            assignment,
            asAssignmentExpression);

    internal bool TryGetRegisterValueTransport(
        ValueIndex transportedValue,
        out Avm1RegisterValueTransport transport) =>
        _registerValueTransports.TryGetValue(transportedValue, out transport);

    internal void AddPostfixMemberUpdate(
        ValueIndex oldValue,
        ValueIndex target,
        ValueIndex member,
        ActionOpcode opcode) =>
        _postfixMemberUpdates[oldValue] = new Avm1PostfixMemberUpdate(
            target,
            member,
            opcode);

    internal bool TryGetPostfixMemberUpdate(
        ValueIndex oldValue,
        out Avm1PostfixMemberUpdate update) =>
        _postfixMemberUpdates.TryGetValue(oldValue, out update);

    internal void AddPostfixVariableUpdate(
        ValueIndex oldValue,
        ValueIndex target,
        ActionOpcode opcode) =>
        _postfixVariableUpdates[oldValue] = new Avm1PostfixVariableUpdate(
            target,
            opcode);

    internal bool TryGetPostfixVariableUpdate(
        ValueIndex oldValue,
        out Avm1PostfixVariableUpdate update) =>
        _postfixVariableUpdates.TryGetValue(oldValue, out update);

    internal void AddValueAlias(ValueIndex value, ValueIndex source) =>
        _valueAliases[value] = source;

    internal bool TryGetValueAlias(ValueIndex value, out ValueIndex source) =>
        _valueAliases.TryGetValue(value, out source);

    internal bool TryGetConditionalRecipeRoot(ValueIndex value, out int root) =>
        _conditionalRecipeRoots.TryGetValue(value, out root);

    internal Avm1ConditionalRecipeNode GetConditionalRecipeNode(int index) =>
        _conditionalRecipeNodes[index];

    internal bool TryGetStructuredConditionalBounds(
        ValueIndex value,
        out BlockIndex header,
        out BlockIndex merge)
    {
        return TryResolve(value, depth: 0, out header, out merge);

        bool TryResolve(
            ValueIndex candidate,
            int depth,
            out BlockIndex resolvedHeader,
            out BlockIndex resolvedMerge)
        {
            resolvedHeader = BlockIndex.Invalid;
            resolvedMerge = BlockIndex.Invalid;
            if (depth >= 16)
                return false;

            var current = candidate;
            for (; depth < 16 && current.IsValid; depth++)
            {
                if (_conditionalRecipeRoots.TryGetValue(current, out var recipeRoot))
                {
                    var recipe = _conditionalRecipeNodes[recipeRoot];
                    resolvedHeader = recipe.Header;
                    if (!TryGetPhiMerge(current, out resolvedMerge) || !resolvedHeader.IsValid)
                        return false;

                    ExtendThroughCondition(recipe.Condition, depth + 1, ref resolvedHeader);
                    return true;
                }

                if (_conditionalValues.TryGetValue(current, out var conditional))
                {
                    resolvedHeader = conditional.Header;
                    if (!TryGetPhiMerge(current, out resolvedMerge) || !resolvedHeader.IsValid)
                        return false;

                    ExtendThroughCondition(conditional.Condition, depth + 1, ref resolvedHeader);
                    return true;
                }

                if (!TryGetDefinition(current, out var definition) ||
                    definition.Op is not (Avm1TacOp.Copy or Avm1TacOp.Unary))
                {
                    break;
                }

                current = definition.Operand0;
            }

            return false;
        }

        void ExtendThroughCondition(
            ValueIndex condition,
            int depth,
            ref BlockIndex currentHeader)
        {
            if (TryResolve(condition, depth, out var nestedHeader, out var nestedMerge) &&
                nestedMerge == currentHeader)
            {
                currentHeader = nestedHeader;
            }
        }

        bool TryGetPhiMerge(ValueIndex phiValue, out BlockIndex phiMerge)
        {
            phiMerge = BlockIndex.Invalid;
            if (!TryGetDefinition(phiValue, out var definition) ||
                definition.Op is not Avm1TacOp.Phi ||
                definition.IntOperand < 0)
            {
                return false;
            }

            phiMerge = new BlockIndex(definition.IntOperand);
            return true;
        }
    }

    internal bool TryGetNestedStructuredConditionalBounds(
        ValueIndex value,
        out BlockIndex header,
        out BlockIndex merge)
    {
        return TryResolve(value, depth: 0, out header, out merge);

        bool TryResolve(
            ValueIndex candidate,
            int depth,
            out BlockIndex resolvedHeader,
            out BlockIndex resolvedMerge)
        {
            resolvedHeader = BlockIndex.Invalid;
            resolvedMerge = BlockIndex.Invalid;
            if (!candidate.IsValid || depth >= 32)
                return false;

            if (TryGetStructuredConditionalBounds(
                    candidate,
                    out resolvedHeader,
                    out resolvedMerge))
            {
                return true;
            }

            if (!TryGetDefinition(candidate, out var definition))
                return false;

            if (definition.Op is not (Avm1TacOp.InitArray or Avm1TacOp.InitObject) &&
                TryResolve(
                    definition.Operand0,
                    depth + 1,
                    out resolvedHeader,
                    out resolvedMerge))
            {
                return true;
            }

            if (definition.Op is not (Avm1TacOp.CallFunction or Avm1TacOp.NewObject) &&
                TryResolve(
                    definition.Operand1,
                    depth + 1,
                    out resolvedHeader,
                    out resolvedMerge))
            {
                return true;
            }

            if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(definition.Op) &&
                TryResolve(
                    definition.Operand2,
                    depth + 1,
                    out resolvedHeader,
                    out resolvedMerge))
            {
                return true;
            }

            foreach (var operand in GetValueOperands(definition))
            {
                if (TryResolve(
                        operand,
                        depth + 1,
                        out resolvedHeader,
                        out resolvedMerge))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool HasConditionalValueForHeader(BlockIndex header) => _conditionalHeaders.Contains(header);

    internal Avm1ControlFlowGraph ControlFlowGraph => _cfg;

    internal bool TryInlineSingleUseImmediatelyBefore(
        ValueIndex value,
        IrIndex nextInstruction)
    {
        if (GetUseCount(value) != 1 ||
            !TryGetDefinition(value, out var definition) ||
            definition.Index.Value + 1 != nextInstruction.Value)
        {
            return false;
        }

        MarkInlined(value);
        return true;
    }

    internal bool TryInlineSingleUseForStructuredConsumer(ValueIndex value) =>
        TryInlineExactUseCountForStructuredConsumer(value, expectedUseCount: 1);

    internal bool TryInlineForStructuredConsumer(ValueIndex value)
    {
        if (!HasDefinition(value))
            return false;

        MarkInlined(value);
        Mark(_structurallyConsumedValues, value);
        return true;
    }

    internal bool TryInlineExactUseCountForStructuredConsumer(
        ValueIndex value,
        int expectedUseCount)
    {
        if (GetUseCount(value) != expectedUseCount ||
            !HasDefinition(value))
        {
            return false;
        }

        MarkInlined(value);
        return true;
    }

    private void BuildConditionalValues(Avm1ControlFlowGraph cfg, Avm1RegionAnalysis regions)
    {
        foreach (var phi in _tacIr.PhiNodes)
        {
            if (phi.State is not Avm1FlowState.Normal)
                continue;

            foreach (var region in regions.IfRegions
                .Where(candidate => candidate.Merge == phi.Block)
                .OrderByDescending(candidate => candidate.Header.Value))
            {
                var header = cfg[region.Header];
                var branch = _tacIr.Instructions.LastOrDefault(instruction =>
                    instruction.Op is Avm1TacOp.BranchIf &&
                    instruction.Action.Value >= header.StartAction.Value &&
                    instruction.Action.Value < header.EndAction.Value);
                if (branch.Op is not Avm1TacOp.BranchIf || !branch.Operand0.IsValid)
                    continue;

                if (!RegionCoversAllIncomingValues(cfg, phi, header, region.Merge) ||
                    !TryGetArmValue(cfg, phi, header.FirstSuccessor, region.Merge, region.Header, out var whenFalse) ||
                    !TryGetArmValue(cfg, phi, header.SecondSuccessor, region.Merge, region.Header, out var whenTrue))
                {
                    continue;
                }

                var components = new HashSet<ValueIndex>
                {
                    phi.Result,
                    branch.Operand0,
                    whenTrue,
                    whenFalse
                };
                if (!HasOnlyConditionalExpressionEffects(cfg, region, components))
                    continue;

                _conditionalValues[phi.Result] = new Avm1ConditionalValue(
                    phi.Result,
                    region.Header,
                    branch.Operand0,
                    whenTrue,
                    whenFalse);
                _conditionalHeaders.Add(region.Header);
                MarkConditionalComponents(components);
                break;
            }
        }
    }

    private void BuildNestedConditionalValues(
        Avm1ControlFlowGraph cfg,
        Avm1RegionAnalysis regions)
    {
        foreach (var phi in _tacIr.PhiNodes)
        {
            var incomingCount = Math.Min(phi.Predecessors.Count, phi.IncomingValues.Count);
            if (phi.State is not Avm1FlowState.Normal || incomingCount < 3)
                continue;

            foreach (var region in regions.IfRegions
                .Where(candidate => candidate.Merge == phi.Block)
                .OrderBy(candidate => candidate.Header.Value))
            {
                var expected = new HashSet<int>(Enumerable.Range(0, incomingCount));
                var headers = new HashSet<BlockIndex>();
                var components = new HashSet<ValueIndex>();
                var checkpoint = _conditionalRecipeNodes.Count;
                if (!TryBuildConditionalRecipe(
                        cfg,
                        regions,
                        phi,
                        region,
                        expected,
                        headers,
                        components,
                        out var root) ||
                    !HasOnlyConditionalExpressionEffects(
                        cfg,
                        region,
                        components))
                {
                    _conditionalRecipeNodes.RemoveRange(
                        checkpoint,
                        _conditionalRecipeNodes.Count - checkpoint);
                    continue;
                }

                _conditionalRecipeRoots[phi.Result] = root;
                _conditionalHeaders.UnionWith(headers);
                MarkConditionalComponents(components);
                Mark(_conditionalComponents, phi.Result);
                break;
            }
        }
    }

    private bool HasOnlyConditionalExpressionEffects(
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion outerRegion,
        HashSet<ValueIndex> components)
    {
        var dependencies = new HashSet<ValueIndex>();
        var pendingValues = new Stack<ValueIndex>(components.Where(value => value.IsValid));
        while (pendingValues.TryPop(out var value))
        {
            if (!value.IsValid ||
                !dependencies.Add(value) ||
                !TryGetDefinition(value, out var definition))
            {
                continue;
            }

            if (_conditionalValues.TryGetValue(value, out var conditional))
            {
                pendingValues.Push(conditional.Condition);
                pendingValues.Push(conditional.WhenTrue);
                pendingValues.Push(conditional.WhenFalse);
            }

            if (_conditionalRecipeRoots.TryGetValue(value, out var recipeRoot))
                AddRecipeDependencies(recipeRoot);

            pendingValues.Push(definition.Operand0);
            pendingValues.Push(definition.Operand1);
            pendingValues.Push(definition.Operand2);
            foreach (var operand in GetValueOperands(definition))
                pendingValues.Push(operand);
        }

        var pendingBlocks = new Queue<BlockIndex>();
        var visitedBlocks = new HashSet<BlockIndex>();
        var outerHeader = cfg[outerRegion.Header];
        pendingBlocks.Enqueue(outerHeader.FirstSuccessor);
        pendingBlocks.Enqueue(outerHeader.SecondSuccessor);
        while (pendingBlocks.TryDequeue(out var blockIndex))
        {
            if (!blockIndex.IsValid ||
                blockIndex == outerRegion.Merge ||
                !visitedBlocks.Add(blockIndex))
            {
                continue;
            }

            var block = cfg[blockIndex];
            foreach (var instruction in _tacIr.Instructions)
            {
                if (instruction.Action.Value < block.StartAction.Value ||
                    instruction.Action.Value >= block.EndAction.Value ||
                    (!Avm1TacEffectAnalysis.IsSourceStatementRoot(
                            instruction,
                            _valueAnalysis) &&
                        !Avm1TacEffectAnalysis.ReadsMutableState(
                            instruction,
                            _valueAnalysis)))
                {
                    continue;
                }

                if (!instruction.Result.IsValid || !dependencies.Contains(instruction.Result))
                    return false;
            }

            pendingBlocks.Enqueue(block.FirstSuccessor);
            pendingBlocks.Enqueue(block.SecondSuccessor);
        }

        return true;

        void AddRecipeDependencies(int recipeIndex)
        {
            if ((uint)recipeIndex >= (uint)_conditionalRecipeNodes.Count)
                return;

            var recipe = _conditionalRecipeNodes[recipeIndex];
            pendingValues.Push(recipe.Value);
            pendingValues.Push(recipe.Condition);
            if (!recipe.IsLeaf)
            {
                AddRecipeDependencies(recipe.WhenTrue);
                AddRecipeDependencies(recipe.WhenFalse);
            }
        }
    }

    private bool TryBuildConditionalRecipe(
        Avm1ControlFlowGraph cfg,
        Avm1RegionAnalysis regions,
        Avm1StackPhiNode phi,
        Avm1IfRegion region,
        HashSet<int> expected,
        HashSet<BlockIndex> headers,
        HashSet<ValueIndex> components,
        out int root)
    {
        root = -1;
        var header = cfg[region.Header];
        var branch = _tacIr.Instructions.LastOrDefault(instruction =>
            instruction.Op is Avm1TacOp.BranchIf &&
            instruction.Action.Value >= header.StartAction.Value &&
            instruction.Action.Value < header.EndAction.Value);
        if (branch.Op is not Avm1TacOp.BranchIf || !branch.Operand0.IsValid)
            return false;

        var whenFalseInputs = GetArmIncomingIndices(
            cfg,
            phi,
            header.FirstSuccessor,
            region.Merge,
            region.Header,
            expected);
        var whenTrueInputs = GetArmIncomingIndices(
            cfg,
            phi,
            header.SecondSuccessor,
            region.Merge,
            region.Header,
            expected);
        if (whenFalseInputs.Count == 0 || whenTrueInputs.Count == 0 ||
            whenFalseInputs.Overlaps(whenTrueInputs))
        {
            return false;
        }

        var covered = new HashSet<int>(whenFalseInputs);
        covered.UnionWith(whenTrueInputs);
        if (!covered.SetEquals(expected))
            return false;

        if (!TryBuildConditionalArmRecipe(
                cfg,
                regions,
                phi,
                header.SecondSuccessor,
                region.Merge,
                whenTrueInputs,
                headers,
                components,
                out var whenTrue) ||
            !TryBuildConditionalArmRecipe(
                cfg,
                regions,
                phi,
                header.FirstSuccessor,
                region.Merge,
                whenFalseInputs,
                headers,
                components,
                out var whenFalse))
        {
            return false;
        }

        components.Add(branch.Operand0);
        headers.Add(region.Header);
        root = _conditionalRecipeNodes.Count;
        _conditionalRecipeNodes.Add(new Avm1ConditionalRecipeNode(
            ValueIndex.Invalid,
            branch.Operand0,
            whenTrue,
            whenFalse,
            region.Header));
        return true;
    }

    private bool TryBuildConditionalArmRecipe(
        Avm1ControlFlowGraph cfg,
        Avm1RegionAnalysis regions,
        Avm1StackPhiNode phi,
        BlockIndex entry,
        BlockIndex merge,
        HashSet<int> incomingIndices,
        HashSet<BlockIndex> headers,
        HashSet<ValueIndex> components,
        out int root)
    {
        root = -1;
        var first = phi.IncomingValues[incomingIndices.First()];
        if (incomingIndices.All(index => phi.IncomingValues[index] == first))
        {
            components.Add(first);
            root = _conditionalRecipeNodes.Count;
            _conditionalRecipeNodes.Add(new Avm1ConditionalRecipeNode(
                first,
                ValueIndex.Invalid,
                -1,
                -1,
                BlockIndex.Invalid));
            return true;
        }

        if (!entry.IsValid || entry == merge)
            return false;

        foreach (var nested in regions.IfRegions.Where(candidate =>
            candidate.Merge == merge &&
            (candidate.Header == entry || CanReach(cfg, entry, candidate.Header, merge))))
        {
            var nestedHeaders = new HashSet<BlockIndex>();
            var nestedComponents = new HashSet<ValueIndex>();
            var checkpoint = _conditionalRecipeNodes.Count;
            if (TryBuildConditionalRecipe(
                    cfg,
                    regions,
                    phi,
                    nested,
                    incomingIndices,
                    nestedHeaders,
                    nestedComponents,
                    out root))
            {
                headers.UnionWith(nestedHeaders);
                components.UnionWith(nestedComponents);
                return true;
            }

            _conditionalRecipeNodes.RemoveRange(
                checkpoint,
                _conditionalRecipeNodes.Count - checkpoint);
        }

        return false;
    }

    private static HashSet<int> GetArmIncomingIndices(
        Avm1ControlFlowGraph cfg,
        Avm1StackPhiNode phi,
        BlockIndex entry,
        BlockIndex merge,
        BlockIndex header,
        HashSet<int> expected)
    {
        var result = new HashSet<int>();
        var count = Math.Min(phi.Predecessors.Count, phi.IncomingValues.Count);
        for (var index = 0; index < count; index++)
        {
            if (expected.Contains(index) &&
                BelongsToArm(cfg, phi.Predecessors[index], entry, merge, header))
            {
                result.Add(index);
            }
        }

        return result;
    }

    private static bool RegionCoversAllIncomingValues(
        Avm1ControlFlowGraph cfg,
        Avm1StackPhiNode phi,
        Avm1BasicBlock header,
        BlockIndex merge)
    {
        var count = Math.Min(phi.Predecessors.Count, phi.IncomingValues.Count);
        for (var index = 0; index < count; index++)
        {
            var predecessor = phi.Predecessors[index];
            var belongsToFallThrough = BelongsToArm(
                cfg,
                predecessor,
                header.FirstSuccessor,
                merge,
                header.Index);
            var belongsToBranch = BelongsToArm(
                cfg,
                predecessor,
                header.SecondSuccessor,
                merge,
                header.Index);
            if (belongsToFallThrough == belongsToBranch)
                return false;
        }

        return count > 0;
    }

    private static bool TryGetArmValue(
        Avm1ControlFlowGraph cfg,
        Avm1StackPhiNode phi,
        BlockIndex entry,
        BlockIndex merge,
        BlockIndex header,
        out ValueIndex value)
    {
        value = ValueIndex.Invalid;
        var found = false;
        var count = Math.Min(phi.Predecessors.Count, phi.IncomingValues.Count);
        for (var i = 0; i < count; i++)
        {
            var predecessor = phi.Predecessors[i];
            var belongsToArm = BelongsToArm(cfg, predecessor, entry, merge, header);
            if (!belongsToArm)
                continue;

            var incoming = phi.IncomingValues[i];
            if (found && value != incoming)
                return false;

            value = incoming;
            found = true;
        }

        return found;
    }

    private static bool BelongsToArm(
        Avm1ControlFlowGraph cfg,
        BlockIndex predecessor,
        BlockIndex entry,
        BlockIndex merge,
        BlockIndex header) =>
        entry == merge
            ? predecessor == header
            : predecessor.IsValid && CanReach(cfg, entry, predecessor, merge);

    private static bool CanReach(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        BlockIndex target,
        BlockIndex merge)
    {
        if (!entry.IsValid || !target.IsValid || entry == merge)
            return false;

        var visited = new HashSet<BlockIndex>();
        var worklist = new Queue<BlockIndex>();
        worklist.Enqueue(entry);
        while (worklist.TryDequeue(out var block))
        {
            if (!block.IsValid || block == merge || !visited.Add(block))
                continue;

            if (block == target)
                return true;

            var node = cfg[block];
            worklist.Enqueue(node.FirstSuccessor);
            worklist.Enqueue(node.SecondSuccessor);
        }

        return false;
    }

    private void IndexInstructions()
    {
        for (int i = 0; i < _tacIr.Instructions.Count; i++)
        {
            var inst = _tacIr.Instructions[i];
            if (_ignoredInstructions.Contains(inst.Index))
                continue;

            if (inst.Result.IsValid && Avm1TacEffectAnalysis.DefinesValue(inst.Op))
                _definitionIndices[inst.Result.Value] = i;

            CountInstructionUses(i, inst);
        }
    }

    private void AnalyzeInlining()
    {
        for (var value = 0; value < _definitionIndices.Length; value++)
        {
            var defIdx = _definitionIndices[value];
            if (defIdx < 0)
                continue;

            var val = new ValueIndex(value);
            var defInst = _tacIr.Instructions[defIdx];
            int useIdx = FindFirstUseIndex(val, defIdx);

            if (IsSafeToInline(val, defInst, defIdx, useIdx))
                MarkInlined(val);
        }
    }

    private int FindFirstUseIndex(ValueIndex val, int defIdx)
    {
        var firstUseIndex = IsValueInRange(val)
            ? _firstUseIndices[val.Value]
            : -1;
        if (firstUseIndex > defIdx)
        {
            return firstUseIndex;
        }

        // Valid TAC defines values before their uses. Keep a conservative
        // fallback for malformed or synthetic IR that violates that invariant.
        for (int i = defIdx + 1; i < _tacIr.Instructions.Count; i++)
        {
            var inst = _tacIr.Instructions[i];
            if (_ignoredInstructions.Contains(inst.Index))
                continue;
            if (UsesValue(inst, val))
                return i;
        }
        return -1;
    }

    private void CountInstructionUses(int instructionIndex, Avm1TacInstruction inst)
    {
        if (inst.Op is Avm1TacOp.StackOnly)
            return;

        if (IsStoreRegisterDiscardPop(instructionIndex, inst))
            return;

        if (inst.Op is not (Avm1TacOp.InitArray or Avm1TacOp.InitObject))
            CountUse(inst.Operand0, instructionIndex);

        if (inst.Op is not (Avm1TacOp.CallFunction or Avm1TacOp.NewObject))
            CountUse(inst.Operand1, instructionIndex);

        if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(inst.Op))
            CountUse(inst.Operand2, instructionIndex);

        foreach (var argument in GetValueOperands(inst))
            CountUse(argument, instructionIndex);
    }

    internal bool UsesValue(Avm1TacInstruction inst, ValueIndex value)
    {
        if (inst.Op is Avm1TacOp.StackOnly)
            return false;

        if (inst.Op is not (Avm1TacOp.InitArray or Avm1TacOp.InitObject) && inst.Operand0 == value)
            return true;

        if (inst.Op is not (Avm1TacOp.CallFunction or Avm1TacOp.NewObject) && inst.Operand1 == value)
            return true;

        if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(inst.Op) &&
            inst.Operand2 == value)
            return true;

        return GetValueOperands(inst).Contains(value);
    }

    private IEnumerable<ValueIndex> GetValueOperands(Avm1TacInstruction inst)
    {
        if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(inst.Op) ||
            !inst.Operand2.IsValid ||
            inst.OperandCount == 0)
        {
            yield break;
        }

        var start = inst.Operand2.Value;
        var operandSlots = inst.Op is Avm1TacOp.InitObject ? inst.OperandCount * 2 : inst.OperandCount;
        var end = Math.Min(start + operandSlots, _tacIr.ValueOperands.Count);
        for (var i = start; i < end; i++)
            yield return _tacIr.ValueOperands[i];
    }

    private bool IsStoreRegisterDiscardPop(int instructionIndex, Avm1TacInstruction inst)
    {
        if (inst.Op is not Avm1TacOp.Pop ||
            !inst.Operand0.IsValid ||
            instructionIndex <= 0)
        {
            return false;
        }

        var previous = _tacIr.Instructions[instructionIndex - 1];
        return previous.Op is Avm1TacOp.StoreRegister &&
            previous.Operand0 == inst.Operand0;
    }

    private void CountUse(ValueIndex value, int instructionIndex)
    {
        if (!IsValueInRange(value))
            return;

        _useCounts[value.Value]++;
        if (_firstUseIndices[value.Value] < 0)
            _firstUseIndices[value.Value] = instructionIndex;
    }

    private bool IsSafeToInline(ValueIndex val, Avm1TacInstruction defInst, int defIdx, int useIdx)
    {
        if (useIdx == -1)
            return false;

        if ((defInst.Flags & Avm1IrInstructionFlags.ContextProjection) != 0)
            return true;

        if (defInst.Op == Avm1TacOp.LoadConstant)
            return true;

        if (IsMarked(_conditionalComponents, val))
            return true;

        if (GetUseCount(val) != 1)
            return false;

        if (useIdx == defIdx + 1)
            return true;

        var readsState = Avm1TacEffectAnalysis.ReadsMutableState(
            defInst,
            _valueAnalysis);
        if (readsState)
        {
            for (int i = defIdx + 1; i < useIdx; i++)
            {
                var inst = _tacIr.Instructions[i];
                if (Avm1TacEffectAnalysis.IsOrderingBarrier(
                        inst,
                        _valueAnalysis))
                    return false;
            }
        }

        return true;
    }

    private bool IsEffectfulValue(in Avm1TacInstruction instruction) =>
        instruction.Result.IsValid &&
        Avm1TacEffectAnalysis.DefinesValue(instruction.Op) &&
        Avm1TacEffectAnalysis.IsSourceStatementRoot(
            instruction,
            _valueAnalysis);
}

public readonly record struct Avm1ConditionalValue(
    ValueIndex Result,
    BlockIndex Header,
    ValueIndex Condition,
    ValueIndex WhenTrue,
    ValueIndex WhenFalse);

internal readonly record struct Avm1ConditionalRecipeNode(
    ValueIndex Value,
    ValueIndex Condition,
    int WhenTrue,
    int WhenFalse,
    BlockIndex Header)
{
    public bool IsLeaf => Value.IsValid;
}

internal readonly record struct Avm1RegisterValueTransport(
    Avm1TacInstruction Assignment,
    bool AsAssignmentExpression);

internal readonly record struct Avm1TransportAssignment(
    Avm1TacInstruction Instruction,
    ValueIndex AssignedValue);

internal readonly record struct Avm1PostfixMemberUpdate(
    ValueIndex Target,
    ValueIndex Member,
    ActionOpcode Opcode);

internal readonly record struct Avm1PostfixVariableUpdate(
    ValueIndex Target,
    ActionOpcode Opcode);
