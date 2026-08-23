using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;

namespace ShockwaveFlash.Avm1.Decompilation.Ast;

public static class Avm1AstBuilder
{
    private const int DefaultMaximumStructuredBlockCount = 768;
    private const int MaximumStructuredIfNestingDepth = 64;

    private readonly record struct ActiveLoop(
        BlockIndex ContinueTarget,
        BlockIndex Exit,
        BlockIndex ImplicitContinueBlock,
        BlockIndex ConditionBlock,
        LoopVariableKey? PromotedVariable,
        bool IsForIn);
    private enum ActiveControlScopeKind : byte
    {
        Loop,
        Switch
    }

    private readonly record struct ActiveControlScope(
        ActiveControlScopeKind Kind,
        BlockIndex Header,
        BlockIndex BreakTarget,
        BlockIndex ContinueTarget,
        BlockIndex ImplicitContinueBlock);
    private readonly record struct ActiveSwitch(
        BlockIndex Header,
        BlockIndex Merge,
        bool EmitConditionalBreak,
        bool EmitTerminalBreak,
        bool IsDefault);
    private readonly record struct StructuredWithRegion(
        BlockIndex Header,
        BlockIndex BodyEntry,
        BlockIndex Exit,
        ActionIndex EnterAction);
    private readonly record struct StructuredTryRegion(
        BlockIndex Header,
        BlockIndex TryBodyEntry,
        BlockIndex TryExit,
        BlockIndex CatchEnter,
        BlockIndex CatchBodyEntry,
        BlockIndex CatchExit,
        BlockIndex FinallyEnter,
        BlockIndex FinallyBodyEntry,
        BlockIndex FinallyExit,
        BlockIndex Continuation,
        BlockIndex LastBoundary,
        ActionIndex EnterAction)
    {
        public bool HasCatch => CatchEnter.IsValid;

        public bool HasFinally => FinallyEnter.IsValid;
    }
    private readonly record struct LoopVariableKey(Avm1AstNodeKind Kind, int Register, string? Name);
    private readonly record struct PostfixRegisterUpdate(
        int Register,
        int Version,
        ActionOpcode Opcode,
        IrIndex DuplicateLoad,
        IrIndex Unary,
        IrIndex Store,
        IrIndex Pop);

    private sealed class BuilderState(
        Avm1ControlFlowGraph cfg,
        Avm1InstructionTable instructions,
        Avm1RegisterSsa registerSsa,
        Avm1RegionAnalysis regions,
        Avm1SwitchAnalysis switches,
        Avm1SymbolTable symbolTable,
        Avm1SourceProjectionHints? projectionHints,
        Avm1ReachabilityAnalysis reachability)
    {
        public Avm1ControlFlowGraph ControlFlowGraph { get; } = cfg;
        public Avm1ReachabilityAnalysis Reachability { get; } = reachability;
        public HashSet<IrIndex> HiddenInstructions { get; } = [];
        public HashSet<BlockIndex> ConsumedLoopControlArmBlocks { get; } = [];
        public HashSet<BlockIndex> TransparentLoopContinueBlocks { get; } = [];
        public HashSet<BlockIndex> ActiveIfHeaders { get; } = [];
        public int IfNestingDepth { get; set; }
        public Stack<ActiveLoop> ActiveLoops { get; } = [];
        public Stack<ActiveSwitch> ActiveSwitches { get; } = [];
        public Stack<ActiveControlScope> ActiveControlScopes { get; } = [];
        public byte[] ReachabilityScratch { get; } = new byte[cfg.Count];
        public BlockIndex[] TraversalStack { get; } = new BlockIndex[cfg.Count];
        public Dictionary<IrIndex, Avm1RegisterAccess> SsaAccessByInstruction { get; } =
            registerSsa.Accesses.ToDictionary(a => a.Instruction);
        public Avm1SymbolTable SymbolTable { get; } = symbolTable;
        public Avm1SourceProjectionHints? ProjectionHints { get; } = projectionHints;
        public Dictionary<IrIndex, Avm1TacInstruction> ChainedMemberAssignments { get; } = [];
        public Dictionary<IrIndex, Avm1TacInstruction> ChainedRegisterAssignments { get; } = [];
        public Dictionary<BlockIndex, Avm1IfRegion> IfByHeader { get; } =
            regions.IfRegions.ToDictionary(region => region.Header);
        public Dictionary<BlockIndex, Avm1WhileRegion> WhileByHeader { get; } =
            regions.WhileRegions.ToDictionary(region => region.Header);
        public Dictionary<BlockIndex, Avm1DoWhileRegion> DoWhileByHeader { get; } =
            regions.DoWhileRegions.ToDictionary(region => region.Header);
        public Dictionary<BlockIndex, StructuredWithRegion> WithByHeader { get; } =
            CreateWithRegions(cfg, instructions);
        public Dictionary<BlockIndex, StructuredTryRegion> TryByHeader { get; } =
            CreateTryRegions(cfg, instructions);
        public Dictionary<BlockIndex, Avm1SwitchRegion> SwitchByHeader { get; } =
            switches.Regions.ToDictionary(region => region.Header);
    }

    public static Avm1AstArena Build(
        Avm1ControlFlowGraph cfg,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1RegionAnalysis regions,
        Avm1SwitchAnalysis switches) =>
        Build(
            cfg,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            regions,
            switches,
            new Avm1SymbolTable());

    public static Avm1AstArena Build(
        Avm1ControlFlowGraph cfg,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1RegionAnalysis regions,
        Avm1SwitchAnalysis switches,
        Avm1SymbolTable symbolTable,
        Avm1MethodDecompilationOptions options = default,
        Avm1ReachabilityAnalysis? reachability = null)
    {
        var builder = new Avm1AstArena.Builder();
        var topLevel = new List<AstIndex>();

        if (cfg.Count == 0)
        {
            var emptyRoot = builder.AddNode(Avm1AstNodeKind.Root, block: BlockIndex.Invalid, merge: BlockIndex.Invalid);
            return builder.ToArena(emptyRoot);
        }

        var maximumStructuredBlockCount = options.MaximumStructuredBlockCount > 0
            ? options.MaximumStructuredBlockCount
            : DefaultMaximumStructuredBlockCount;
        if (cfg.Count > maximumStructuredBlockCount)
        {
            var opaque = builder.AddNode(
                Avm1AstNodeKind.Opaque,
                block: new BlockIndex(0),
                startAction: new ActionIndex(0),
                endAction: new ActionIndex(instructions.Count));
            var opaqueRoot = builder.AddNode(
                Avm1AstNodeKind.Root,
                block: new BlockIndex(0),
                startAction: new ActionIndex(0),
                endAction: new ActionIndex(instructions.Count),
                children: [opaque]);
            return builder.ToArena(opaqueRoot);
        }

        var deadInstructions = Avm1DeadCodeElimination.Run(
            instructions,
            tacIr,
            registerSsa,
            valueAnalysis);
        foreach (var dispatchStore in switches.Regions.SelectMany(region => region.DispatchStores))
            deadInstructions.Add(dispatchStore);

        var inliner = new Avm1ExpressionInliner(
            instructions,
            tacIr,
            cfg,
            regions,
            valueAnalysis,
            deadInstructions);
        var state = new BuilderState(
            cfg,
            instructions,
            registerSsa,
            regions,
            switches,
            symbolTable,
            options.SourceProjectionHints,
            reachability ?? Avm1ReachabilityAnalysis.Build(
                cfg,
                Avm1FlowGraph.Build(instructions, cfg)));
        state.HiddenInstructions.UnionWith(
            switches.Regions.SelectMany(region => region.DispatchStores));
        foreach (var dispatchStoreIndex in switches.Regions
            .SelectMany(region => region.DispatchStores))
        {
            var nextIndex = dispatchStoreIndex.Value + 1;
            if ((uint)nextIndex >= (uint)tacIr.Count)
                continue;

            var dispatchStore = tacIr[dispatchStoreIndex];
            var next = tacIr[new IrIndex(nextIndex)];
            if (next.Op is Avm1TacOp.Pop &&
                next.Operand0 == dispatchStore.Operand0)
            {
                state.HiddenInstructions.Add(next.Index);
            }
        }

        foreach (var instruction in tacIr.Instructions)
        {
            if ((instruction.Flags & Avm1IrInstructionFlags.ContextProjection) != 0)
                state.HiddenInstructions.Add(instruction.Index);
        }

        foreach (var deadIdx in deadInstructions)
        {
            state.HiddenInstructions.Add(deadIdx);
        }

        HideImplicitSetterReturn(
            tacIr,
            valueAnalysis,
            inliner,
            state,
            options.SetterPropertyName);

        FindPostfixRegisterUpdates(tacIr, inliner, state);
        FindPostfixTransportedRegisterUpdates(tacIr, inliner, state);
        FindPostfixVariableExpressionUpdates(tacIr, valueAnalysis, inliner, state);
        FindPostfixMemberExpressionUpdates(tacIr, valueAnalysis, inliner, state);
        FindReturnRegisterTransports(tacIr, inliner, state);
        FindChainedMemberAssignments(tacIr, inliner, state);
        FindChainedRegisterMemberAssignments(tacIr, inliner, state);
        FindPseudoRegisterVariableAssignments(tacIr, valueAnalysis, inliner, state);
        FindDuplicatedVariableValueTransports(tacIr, instructions, inliner, state);
        FindRegisterValueTransports(tacIr, inliner, state);
        FindOrderedExpressionInlines(tacIr, inliner, state);
        FindCompoundVariableAssignmentReads(tacIr, valueAnalysis, inliner, state);

        var whileByHeader = state.WhileByHeader;
        var doWhileByHeader = state.DoWhileByHeader;
        var ifByHeader = state.IfByHeader;
        var switchByHeader = state.SwitchByHeader;
        var consumed = new HashSet<BlockIndex>();

        foreach (var block in cfg.Blocks)
        {
            if (!state.Reachability.IsReachable(block.Index) ||
                consumed.Contains(block.Index) ||
                state.ConsumedLoopControlArmBlocks.Contains(block.Index))
                continue;
            if (IsDeferredWhileBodyBlock(block.Index, state))
                continue;

            if (state.TryByHeader.TryGetValue(block.Index, out var tryRegion))
            {
                topLevel.Add(AddTry(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    tryRegion,
                    state));
                AddConsumedTryRegion(consumed, tryRegion);
                continue;
            }

            if (state.WithByHeader.TryGetValue(block.Index, out var withRegion))
            {
                topLevel.Add(AddWith(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    withRegion,
                    state));
                AddConsumedWithRegion(consumed, withRegion);
                continue;
            }

            if (doWhileByHeader.TryGetValue(block.Index, out var doWhileRegion))
            {
                topLevel.Add(AddDoWhile(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, cfg, doWhileRegion, state));
                foreach (var loopBlock in doWhileRegion.Blocks)
                    consumed.Add(loopBlock);

                continue;
            }

            if (whileByHeader.TryGetValue(block.Index, out var whileRegion))
            {
                var loopNode = AddWhile(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, cfg, whileRegion, state);
                RefreshTrailingPreheader(
                    topLevel,
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    state,
                    whileRegion);
                topLevel.Add(loopNode);
                foreach (var loopBlock in whileRegion.Blocks)
                    consumed.Add(loopBlock);

                continue;
            }

            if (switchByHeader.TryGetValue(block.Index, out var switchRegion))
            {
                topLevel.Add(AddSwitch(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    switchRegion,
                    state));
                AddConsumedSwitchRegion(consumed, cfg, switchRegion);
                continue;
            }

            if (ifByHeader.TryGetValue(block.Index, out var region))
            {
                topLevel.Add(AddIf(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, cfg, region, state));
                AddConsumedIfRegion(consumed, cfg, region);
                continue;
            }

            topLevel.Add(AddBasicBlock(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, block, state));
        }

        var root = builder.AddNode(
            Avm1AstNodeKind.Root,
            block: BlockIndex.Invalid,
            merge: BlockIndex.Invalid,
            children: topLevel);
        return builder.ToArena(root);
    }

    private static void HideImplicitSetterReturn(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        string? setterPropertyName)
    {
        if (string.IsNullOrEmpty(setterPropertyName))
            return;

        var getterName = "__get__" + setterPropertyName;
        for (var index = tacIr.Count - 1; index >= 0; index--)
        {
            var returnInstruction = tacIr.Instructions[index];
            if (returnInstruction.Op is not Avm1TacOp.Return)
                continue;

            if (!inliner.TryGetDefinition(returnInstruction.Operand0, out var getterCall) ||
                getterCall.Op is not Avm1TacOp.CallMethod ||
                getterCall.OperandCount != 0 ||
                getterCall.Index.Value + 1 != returnInstruction.Index.Value ||
                valueAnalysis[getterCall.Operand0].ConstantKind is not Avm1ConstantKind.String ||
                valueAnalysis[getterCall.Operand0].StringValue != getterName ||
                !TryGetSemanticDefinition(inliner, getterCall.Operand1, out var target) ||
                target.Op is not Avm1TacOp.LoadRegister ||
                !state.SsaAccessByInstruction.TryGetValue(target.Index, out var targetRead) ||
                targetRead.Kind is not Avm1RegisterAccessKind.Read ||
                !state.SymbolTable.TryGetRegisterSymbol(
                    targetRead.Register,
                    targetRead.Version,
                    out var targetSymbol) ||
                targetSymbol.Name != "this")
            {
                return;
            }

            state.HiddenInstructions.Add(returnInstruction.Index);
            return;
        }
    }

    private static void FindPostfixRegisterUpdates(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var instruction in tacIr.Instructions)
        {
            if (!instruction.Result.IsValid ||
                !TryGetPostfixRegisterUpdate(
                    tacIr,
                    inliner,
                    state.SsaAccessByInstruction,
                    instruction.Result,
                    out var update))
            {
                continue;
            }

            state.HiddenInstructions.Add(update.DuplicateLoad);
            state.HiddenInstructions.Add(update.Unary);
            state.HiddenInstructions.Add(update.Store);
            state.HiddenInstructions.Add(update.Pop);
            TryInlinePostfixMemberTarget(tacIr, inliner, instruction);
        }
    }

    private static void TryInlinePostfixMemberTarget(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        Avm1TacInstruction oldLoad)
    {
        var consumer = tacIr[new IrIndex(oldLoad.Index.Value + 5)];
        if (consumer.Op is not Avm1TacOp.GetMember ||
            consumer.Operand1 != oldLoad.Result ||
            !inliner.TryGetDefinition(consumer.Operand0, out var targetDefinition) ||
            !AreInSameBlock(inliner.ControlFlowGraph, targetDefinition, oldLoad))
        {
            return;
        }

        inliner.TryInlineSingleUseImmediatelyBefore(
            consumer.Operand0,
            oldLoad.Index);
    }

    private static void FindPostfixMemberExpressionUpdates(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        for (var index = 0; index + 1 < tacIr.Count; index++)
        {
            var assignment = tacIr.Instructions[index];
            var consumer = tacIr.Instructions[index + 1];
            if (assignment.Op is not Avm1TacOp.SetMember ||
                state.HiddenInstructions.Contains(assignment.Index) ||
                !TryGetSemanticDefinition(inliner, assignment.Operand2, out var update) ||
                update.Op is not Avm1TacOp.Unary ||
                update.Opcode is not (ActionOpcode.Increment or ActionOpcode.Decrement) ||
                !TryGetSemanticDefinition(inliner, update.Operand0, out var updateRead) ||
                updateRead.Op is not Avm1TacOp.GetMember)
            {
                continue;
            }

            if (TryRecord(consumer.Operand0) ||
                TryRecord(consumer.Operand1) ||
                (!Avm1TacEffectAnalysis.HasValueOperandSideTable(consumer.Op) &&
                    TryRecord(consumer.Operand2)))
            {
                state.HiddenInstructions.Add(assignment.Index);
            }

            bool TryRecord(ValueIndex oldValue)
            {
                if (!oldValue.IsValid ||
                    oldValue == updateRead.Result ||
                    inliner.GetUseCount(oldValue) != 1 ||
                    !inliner.TryGetDefinition(oldValue, out var oldRead) ||
                    oldRead.Op is not Avm1TacOp.GetMember ||
                    !inliner.UsesValue(consumer, oldValue) ||
                    !AreInSameBlock(
                        state.ControlFlowGraph,
                        oldRead,
                        updateRead,
                        assignment,
                        consumer) ||
                    HasMemberUpdateBarrier(tacIr, inliner, assignment, oldRead) ||
                    !AreEquivalentLValueValues(
                        valueAnalysis,
                        inliner,
                        state.SsaAccessByInstruction,
                        oldRead.Operand0,
                        assignment.Operand0) ||
                    !AreEquivalentLValueValues(
                        valueAnalysis,
                        inliner,
                        state.SsaAccessByInstruction,
                        oldRead.Operand1,
                        assignment.Operand1) ||
                    !inliner.TryInlineSingleUseForStructuredConsumer(oldValue))
                {
                    return false;
                }

                inliner.AddPostfixMemberUpdate(
                    oldValue,
                    assignment.Operand0,
                    assignment.Operand1,
                    update.Opcode);
                return true;
            }
        }
    }

    private static void FindPostfixVariableExpressionUpdates(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var oldLoad in tacIr.Instructions)
        {
            if (oldLoad.Op is not Avm1TacOp.LoadRegister ||
                !state.SsaAccessByInstruction.TryGetValue(oldLoad.Index, out var read) ||
                read.Kind is not Avm1RegisterAccessKind.Read ||
                !state.SymbolTable.TryGetRegisterSymbol(
                    read.Register,
                    read.Version,
                    out var symbol) ||
                !Avm1SymbolTable.IsGeneratedRegisterSymbol(symbol))
            {
                continue;
            }

            var matchingReads = state.SsaAccessByInstruction.Values
                .Where(access =>
                    access.Kind is Avm1RegisterAccessKind.Read &&
                    access.Register == read.Register &&
                    access.Version == read.Version)
                .ToArray();
            var matchingWrites = state.SsaAccessByInstruction.Values
                .Where(access =>
                    access.Kind is Avm1RegisterAccessKind.Write &&
                    access.Register == read.Register &&
                    access.Version == read.Version)
                .ToArray();
            if (matchingReads.Length is < 1 or > 2 || matchingWrites.Length != 1)
                continue;

            var store = tacIr[matchingWrites[0].Instruction];
            if (store.Op is not Avm1TacOp.StoreRegister ||
                store.Index.Value >= oldLoad.Index.Value ||
                !AreInSameBlock(state.ControlFlowGraph, store, oldLoad) ||
                !TryGetSemanticDefinition(inliner, store.Operand0, out var conversion))
            {
                continue;
            }

            var currentValue = store.Operand0;
            if (conversion.Op is Avm1TacOp.Unary &&
                conversion.Opcode is ActionOpcode.ToNumber)
            {
                currentValue = conversion.Operand0;
            }

            if (!TryGetSemanticDefinition(inliner, currentValue, out var currentRead) ||
                currentRead.Op is not Avm1TacOp.GetVariable)
            {
                continue;
            }

            Avm1TacInstruction assignment = default;
            Avm1TacInstruction update = default;
            Avm1TacInstruction updateRegisterRead = default;
            var ambiguous = false;
            for (var index = store.Index.Value + 1; index < oldLoad.Index.Value; index++)
            {
                var candidate = tacIr.Instructions[index];
                if (candidate.Op is not Avm1TacOp.SetVariable ||
                    !AreEquivalentLValueValues(
                        valueAnalysis,
                        inliner,
                        state.SsaAccessByInstruction,
                        currentRead.Operand0,
                        candidate.Operand0) ||
                    !TryGetSemanticDefinition(inliner, candidate.Operand1, out var candidateUpdate) ||
                    candidateUpdate.Op is not Avm1TacOp.Unary ||
                    candidateUpdate.Opcode is not (
                        ActionOpcode.Increment or ActionOpcode.Decrement))
                {
                    continue;
                }

                Avm1TacInstruction candidateUpdateRegisterRead = default;
                if (candidateUpdate.Operand0 != store.Operand0 &&
                    (!TryGetSemanticDefinition(
                            inliner,
                            candidateUpdate.Operand0,
                            out candidateUpdateRegisterRead) ||
                     candidateUpdateRegisterRead.Op is not Avm1TacOp.LoadRegister ||
                     !state.SsaAccessByInstruction.TryGetValue(
                         candidateUpdateRegisterRead.Index,
                         out var updateRead) ||
                     updateRead.Kind is not Avm1RegisterAccessKind.Read ||
                     updateRead.Register != read.Register ||
                     updateRead.Version != read.Version))
                {
                    continue;
                }

                if (assignment.Op is Avm1TacOp.SetVariable)
                {
                    ambiguous = true;
                    break;
                }

                assignment = candidate;
                update = candidateUpdate;
                updateRegisterRead = candidateUpdateRegisterRead;
            }

            if (ambiguous || assignment.Op is not Avm1TacOp.SetVariable)
                continue;

            var expectedReadCount = updateRegisterRead.Op is Avm1TacOp.LoadRegister
                ? 2
                : 1;
            if (matchingReads.Length != expectedReadCount)
                continue;

            var hasUnexpectedRoot = false;
            for (var index = store.Index.Value + 1; index < oldLoad.Index.Value; index++)
            {
                var candidate = tacIr.Instructions[index];
                if (candidate.Index == assignment.Index ||
                    candidate.Index == update.Index ||
                    candidate.Index == updateRegisterRead.Index ||
                    state.HiddenInstructions.Contains(candidate.Index) ||
                    !inliner.IsRootInstruction(candidate))
                {
                    continue;
                }

                hasUnexpectedRoot = true;
                break;
            }

            if (hasUnexpectedRoot)
                continue;

            inliner.TryInlineForStructuredConsumer(currentValue);
            inliner.TryInlineForStructuredConsumer(store.Operand0);
            inliner.TryInlineForStructuredConsumer(updateRegisterRead.Result);
            inliner.TryInlineForStructuredConsumer(update.Result);
            inliner.AddPostfixVariableUpdate(
                oldLoad.Result,
                currentValue,
                update.Opcode);
            state.HiddenInstructions.Add(store.Index);
            state.HiddenInstructions.Add(assignment.Index);
            if (store.Index.Value + 1 < tacIr.Count)
            {
                var cleanup = tacIr[new IrIndex(store.Index.Value + 1)];
                if (cleanup.Op is Avm1TacOp.Pop && cleanup.Operand0 == store.Operand0)
                    state.HiddenInstructions.Add(cleanup.Index);
            }
        }
    }

    private static void FindPostfixTransportedRegisterUpdates(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var oldLoad in tacIr.Instructions)
        {
            if (oldLoad.Op is not Avm1TacOp.LoadRegister ||
                !state.SsaAccessByInstruction.TryGetValue(oldLoad.Index, out var transportRead) ||
                transportRead.Kind is not Avm1RegisterAccessKind.Read ||
                !state.SymbolTable.TryGetRegisterSymbol(
                    transportRead.Register,
                    transportRead.Version,
                    out var transportSymbol) ||
                !Avm1SymbolTable.IsGeneratedRegisterSymbol(transportSymbol))
            {
                continue;
            }

            var transportReads = state.SsaAccessByInstruction.Values.Count(access =>
                access.Kind is Avm1RegisterAccessKind.Read &&
                access.Register == transportRead.Register &&
                access.Version == transportRead.Version);
            var transportWrites = state.SsaAccessByInstruction.Values
                .Where(access =>
                    access.Kind is Avm1RegisterAccessKind.Write &&
                    access.Register == transportRead.Register &&
                    access.Version == transportRead.Version)
                .ToArray();
            if (transportReads != 1 || transportWrites.Length != 1)
                continue;

            var transportStore = tacIr[transportWrites[0].Instruction];
            if (transportStore.Op is not Avm1TacOp.StoreRegister ||
                transportStore.IntOperand == 0 ||
                transportStore.Index.Value + 3 >= tacIr.Count ||
                transportStore.Index.Value >= oldLoad.Index.Value ||
                !TryGetSemanticDefinition(inliner, transportStore.Operand0, out var conversion))
            {
                continue;
            }

            var currentValue = transportStore.Operand0;
            if (conversion.Op is Avm1TacOp.Unary &&
                conversion.Opcode is ActionOpcode.ToNumber)
            {
                currentValue = conversion.Operand0;
            }

            if (!TryGetSemanticDefinition(inliner, currentValue, out var currentRead) ||
                currentRead.Op is not Avm1TacOp.LoadRegister ||
                !state.SsaAccessByInstruction.TryGetValue(currentRead.Index, out var targetRead) ||
                targetRead.Kind is not Avm1RegisterAccessKind.Read)
            {
                continue;
            }

            var update = tacIr[new IrIndex(transportStore.Index.Value + 1)];
            var targetStore = tacIr[new IrIndex(transportStore.Index.Value + 2)];
            var cleanup = tacIr[new IrIndex(transportStore.Index.Value + 3)];
            if (update.Op is not Avm1TacOp.Unary ||
                update.Opcode is not (ActionOpcode.Increment or ActionOpcode.Decrement) ||
                update.Operand0 != transportStore.Operand0 ||
                targetStore.Op is not Avm1TacOp.StoreRegister ||
                targetStore.Operand0 != update.Result ||
                targetStore.IntOperand != targetRead.Register ||
                targetStore.IntOperand == transportRead.Register ||
                cleanup.Op is not Avm1TacOp.Pop ||
                cleanup.Operand0 != update.Result ||
                !state.SsaAccessByInstruction.TryGetValue(targetStore.Index, out var targetWrite) ||
                targetWrite.Kind is not Avm1RegisterAccessKind.Write ||
                targetWrite.Register != targetRead.Register ||
                !AreInSameBlock(
                    state.ControlFlowGraph,
                    currentRead,
                    transportStore,
                    update,
                    targetStore,
                    cleanup,
                    oldLoad))
            {
                continue;
            }

            var hasUnexpectedRoot = false;
            for (var index = cleanup.Index.Value + 1; index < oldLoad.Index.Value; index++)
            {
                var candidate = tacIr.Instructions[index];
                if (state.HiddenInstructions.Contains(candidate.Index) ||
                    !inliner.IsRootInstruction(candidate))
                {
                    continue;
                }

                hasUnexpectedRoot = true;
                break;
            }

            if (hasUnexpectedRoot)
                continue;

            inliner.TryInlineForStructuredConsumer(currentValue);
            inliner.TryInlineForStructuredConsumer(transportStore.Operand0);
            inliner.TryInlineForStructuredConsumer(update.Result);
            inliner.AddPostfixVariableUpdate(
                oldLoad.Result,
                currentValue,
                update.Opcode);
            state.HiddenInstructions.Add(transportStore.Index);
            state.HiddenInstructions.Add(targetStore.Index);
            state.HiddenInstructions.Add(cleanup.Index);
        }
    }

    private static void FindReturnRegisterTransports(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not Avm1TacOp.Return ||
                !inliner.TryGetDefinition(consumer.Operand0, out var load) ||
                load.Op is not Avm1TacOp.LoadRegister ||
                load.IntOperand != 0 ||
                !state.SsaAccessByInstruction.TryGetValue(load.Index, out var read) ||
                read.Kind is not Avm1RegisterAccessKind.Read ||
                !state.ControlFlowGraph.TryGetBlockForAction(load.Action, out var blockIndex))
            {
                continue;
            }

            var block = state.ControlFlowGraph[blockIndex];
            for (var index = load.Index.Value - 1; index >= 0; index--)
            {
                var store = tacIr.Instructions[index];
                if (store.Action.Value < block.StartAction.Value)
                    break;
                if (store.Op is not Avm1TacOp.StoreRegister ||
                    store.IntOperand != load.IntOperand ||
                    !state.SsaAccessByInstruction.TryGetValue(store.Index, out var write) ||
                    write.Kind is not Avm1RegisterAccessKind.Write ||
                    write.Version != read.Version ||
                    !store.Operand0.IsValid ||
                    !HasOnlyRegisterCleanupBetween(tacIr, store, load))
                {
                    continue;
                }

                inliner.AddValueAlias(load.Result, store.Operand0);
                state.HiddenInstructions.Add(store.Index);
                if (store.Index.Value + 1 < tacIr.Count)
                {
                    var pop = tacIr.Instructions[store.Index.Value + 1];
                    if (pop.Op is Avm1TacOp.Pop && pop.Operand0 == store.Operand0)
                        state.HiddenInstructions.Add(pop.Index);
                }
                break;
            }
        }
    }

    private static bool HasOnlyRegisterCleanupBetween(
        Avm1TacIr tacIr,
        Avm1TacInstruction store,
        Avm1TacInstruction load)
    {
        for (var index = store.Index.Value + 1; index < load.Index.Value; index++)
        {
            if (tacIr.Instructions[index].Op is not (
                    Avm1TacOp.StoreRegister or
                    Avm1TacOp.Pop))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPostfixRegisterUpdate(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        ValueIndex oldValue,
        out PostfixRegisterUpdate update)
    {
        update = default;
        if (!inliner.TryGetDefinition(oldValue, out var oldLoad) ||
            oldLoad.Op is not Avm1TacOp.LoadRegister ||
            inliner.GetUseCount(oldValue) != 1 ||
            oldLoad.Index.Value + 5 >= tacIr.Count)
        {
            return false;
        }

        var duplicateLoad = tacIr[new IrIndex(oldLoad.Index.Value + 1)];
        var unary = tacIr[new IrIndex(oldLoad.Index.Value + 2)];
        var store = tacIr[new IrIndex(oldLoad.Index.Value + 3)];
        var pop = tacIr[new IrIndex(oldLoad.Index.Value + 4)];
        var consumer = tacIr[new IrIndex(oldLoad.Index.Value + 5)];
        if (duplicateLoad.Op is not Avm1TacOp.LoadRegister ||
            duplicateLoad.IntOperand != oldLoad.IntOperand ||
            inliner.GetUseCount(duplicateLoad.Result) != 1 ||
            unary.Op is not Avm1TacOp.Unary ||
            unary.Opcode is not (ActionOpcode.Increment or ActionOpcode.Decrement) ||
            unary.Operand0 != duplicateLoad.Result ||
            store.Op is not Avm1TacOp.StoreRegister ||
            store.IntOperand != oldLoad.IntOperand ||
            store.Operand0 != unary.Result ||
            pop.Op is not Avm1TacOp.Pop ||
            pop.Operand0 != unary.Result ||
            consumer.Op is Avm1TacOp.Pop or Avm1TacOp.StackOnly ||
            !inliner.UsesValue(consumer, oldValue) ||
            !AreInSameBlock(
                inliner.ControlFlowGraph,
                oldLoad,
                duplicateLoad,
                unary,
                store,
                pop,
                consumer) ||
            !ssaAccessByInstruction.TryGetValue(oldLoad.Index, out var oldRead) ||
            oldRead.Kind is not Avm1RegisterAccessKind.Read ||
            !ssaAccessByInstruction.TryGetValue(duplicateLoad.Index, out var duplicateRead) ||
            duplicateRead.Kind is not Avm1RegisterAccessKind.Read ||
            duplicateRead.Register != oldRead.Register ||
            duplicateRead.Version != oldRead.Version ||
            !ssaAccessByInstruction.TryGetValue(store.Index, out var write) ||
            write.Kind is not Avm1RegisterAccessKind.Write ||
            write.Register != oldRead.Register)
        {
            return false;
        }

        update = new PostfixRegisterUpdate(
            oldRead.Register,
            oldRead.Version,
            unary.Opcode,
            duplicateLoad.Index,
            unary.Index,
            store.Index,
            pop.Index);
        return true;
    }

    private static void FindChainedMemberAssignments(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        var writeByRegisterVersion = new Dictionary<(int Register, int Version), IrIndex>();
        var readCountByRegisterVersion = new Dictionary<(int Register, int Version), int>();
        foreach (var access in state.SsaAccessByInstruction.Values)
        {
            if (access.Kind is Avm1RegisterAccessKind.Write)
            {
                writeByRegisterVersion.TryAdd((access.Register, access.Version), access.Instruction);
            }
            else if (access.Kind is Avm1RegisterAccessKind.Read)
            {
                var key = (access.Register, access.Version);
                readCountByRegisterVersion[key] = readCountByRegisterVersion.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var outer in tacIr.Instructions)
        {
            if (outer.Op is not Avm1TacOp.SetMember ||
                !inliner.TryGetDefinition(outer.Operand2, out var load) ||
                load.Op is not Avm1TacOp.LoadRegister ||
                inliner.GetUseCount(load.Result) != 1 ||
                !state.SsaAccessByInstruction.TryGetValue(load.Index, out var read) ||
                read.Kind is not Avm1RegisterAccessKind.Read ||
                readCountByRegisterVersion.GetValueOrDefault((read.Register, read.Version)) != 1 ||
                !writeByRegisterVersion.TryGetValue((read.Register, read.Version), out var storeIndex))
            {
                continue;
            }

            var store = tacIr[storeIndex];
            if (store.Op is not Avm1TacOp.StoreRegister ||
                store.Index.Value + 1 >= tacIr.Count)
            {
                continue;
            }

            var inner = tacIr[new IrIndex(store.Index.Value + 1)];
            if (inner.Op is not Avm1TacOp.SetMember ||
                inner.Operand2 != store.Operand0 ||
                load.Index.Value != inner.Index.Value + 1 ||
                outer.Index.Value != load.Index.Value + 1 ||
                inliner.GetUseCount(inner.Operand0) != 1 ||
                inliner.GetUseCount(outer.Operand0) != 1 ||
                !AreInSameBlock(state.ControlFlowGraph, store, inner, load, outer))
            {
                continue;
            }

            if (inliner.GetUseCount(store.Operand0) == 2 &&
                inliner.TryGetDefinition(store.Operand0, out var sourceDefinition) &&
                sourceDefinition.Index.Value + 1 == store.Index.Value &&
                AreInSameBlock(state.ControlFlowGraph, sourceDefinition, store))
            {
                inliner.TryInlineExactUseCountForStructuredConsumer(
                    store.Operand0,
                    expectedUseCount: 2);
            }

            state.ChainedMemberAssignments[outer.Index] = inner;
            state.HiddenInstructions.Add(store.Index);
            state.HiddenInstructions.Add(inner.Index);
            state.HiddenInstructions.Add(load.Index);
            HideSingleUseDefinition(inner.Operand0, inliner, state);
            HideSingleUseDefinition(outer.Operand0, inliner, state);
        }
    }

    private static void FindOrderedMemberOperandInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not (
                    Avm1TacOp.GetMember or
                    Avm1TacOp.SetMember or
                    Avm1TacOp.Delete) ||
                consumer.Op is Avm1TacOp.Delete && !consumer.Operand1.IsValid)
                continue;

            // The member key is evaluated after the target. Admit it first so
            // embedding it no longer blocks the earlier target operand.
            TryInlineOrderedConsumerValue(
                tacIr,
                inliner,
                state,
                consumer.Operand1,
                consumer,
                allowStructuredConditionalSpan: consumer.Op is not Avm1TacOp.GetMember);
            TryInlineOrderedConsumerValue(
                tacIr,
                inliner,
                state,
                consumer.Operand0,
                consumer,
                allowStructuredConditionalSpan: consumer.Op is not Avm1TacOp.GetMember);
        }
    }

    private static void FindChainedRegisterMemberAssignments(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        for (var index = 0; index < tacIr.Count; index++)
        {
            var store = tacIr.Instructions[index];
            if (store.Op is not Avm1TacOp.StoreRegister ||
                !state.SsaAccessByInstruction.TryGetValue(store.Index, out var write) ||
                write.Kind is not Avm1RegisterAccessKind.Write ||
                !state.SymbolTable.TryGetRegisterSymbol(
                    write.Register,
                    write.Version,
                    out var symbol))
            {
                continue;
            }

            if (index + 1 >= tacIr.Count)
                continue;

            var outer = tacIr.Instructions[index + 1];
            if (outer.Op is not Avm1TacOp.SetMember ||
                state.HiddenInstructions.Contains(store.Index) ||
                state.HiddenInstructions.Contains(outer.Index) ||
                outer.Operand2 != store.Operand0 ||
                inliner.GetUseCount(store.Operand0) != 2 ||
                !AreInSameBlock(state.ControlFlowGraph, store, outer) ||
                !inliner.TryInlineExactUseCountForStructuredConsumer(
                    store.Operand0,
                    expectedUseCount: 2))
            {
                continue;
            }

            state.ChainedRegisterAssignments[outer.Index] = store;
            state.HiddenInstructions.Add(store.Index);
        }
    }

    private static void FindPseudoRegisterVariableAssignments(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        for (var index = 0; index + 2 < tacIr.Count; index++)
        {
            var store = tacIr.Instructions[index];
            var assignment = tacIr.Instructions[index + 1];
            var load = tacIr.Instructions[index + 2];
            if (store.Op is not Avm1TacOp.StoreRegister ||
                store.IntOperand != 0 ||
                assignment.Op is not Avm1TacOp.SetVariable ||
                assignment.Operand1 != store.Operand0 ||
                load.Op is not Avm1TacOp.LoadRegister ||
                load.IntOperand != store.IntOperand ||
                valueAnalysis[assignment.Operand0].ConstantKind is not Avm1ConstantKind.String ||
                valueAnalysis[assignment.Operand0].StringValue != "__reg0" ||
                inliner.GetUseCount(store.Operand0) != 2 ||
                inliner.GetUseCount(load.Result) != 1 ||
                !AreInSameBlock(state.ControlFlowGraph, store, assignment, load) ||
                !state.SsaAccessByInstruction.TryGetValue(store.Index, out var write) ||
                write.Kind is not Avm1RegisterAccessKind.Write ||
                !state.SsaAccessByInstruction.TryGetValue(load.Index, out var read) ||
                read.Kind is not Avm1RegisterAccessKind.Read ||
                read.Register != write.Register ||
                read.Version != write.Version ||
                !inliner.TryInlineExactUseCountForStructuredConsumer(
                    store.Operand0,
                    expectedUseCount: 2) ||
                !inliner.TryInlineSingleUseForStructuredConsumer(load.Result))
            {
                continue;
            }

            inliner.AddTransportAssignment(
                load.Result,
                assignment,
                store.Operand0);
            state.HiddenInstructions.Add(store.Index);
            state.HiddenInstructions.Add(assignment.Index);
            state.HiddenInstructions.Add(load.Index);
        }
    }

    private static void FindRegisterValueTransports(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        var definedSymbols = state.SymbolTable.Symbols
            .Where(symbol => symbol.IsDeclared)
            .ToHashSet();

        for (var index = 0; index < tacIr.Count; index++)
        {
            var store = tacIr.Instructions[index];
            if (store.Op is not Avm1TacOp.StoreRegister ||
                !state.SsaAccessByInstruction.TryGetValue(store.Index, out var write) ||
                write.Kind is not Avm1RegisterAccessKind.Write ||
                write.Register == 0 ||
                !state.SymbolTable.TryGetRegisterSymbol(
                    write.Register,
                    write.Version,
                    out var symbol))
            {
                continue;
            }

            var hasPriorDefinition = definedSymbols.Contains(symbol);
            if (!state.HiddenInstructions.Contains(store.Index))
                definedSymbols.Add(symbol);
            if (state.HiddenInstructions.Contains(store.Index) ||
                !store.Operand0.IsValid ||
                inliner.GetUseCount(store.Operand0) != 2 ||
                !inliner.TryGetDefinition(store.Operand0, out var definition) ||
                !inliner.RequiresTemporary(definition) ||
                !AreInSameBlock(state.ControlFlowGraph, definition, store) ||
                !TryFindRegisterTransportConsumer(tacIr, inliner, state, store, out var consumer) ||
                !AreInSameBlock(state.ControlFlowGraph, store, consumer) ||
                HasRegisterOverwriteBetween(tacIr, store, consumer))
            {
                continue;
            }

            // StoreRegister leaves the assigned value on the AVM1 stack. Move
            // it into the consumer only when that preserves left-to-right order.
            var isAssignmentExpressionPosition =
                consumer.Op is Avm1TacOp.Binary &&
                    consumer.Operand0 == store.Operand0 ||
                consumer.Op is Avm1TacOp.CallMethod &&
                    consumer.Operand1 == store.Operand0;
            var asAssignmentExpression = hasPriorDefinition &&
                isAssignmentExpressionPosition &&
                !HasVisibleRootBetween(tacIr, inliner, state, store, consumer);
            if (!inliner.TryInlineExactUseCountForStructuredConsumer(
                    store.Operand0,
                    expectedUseCount: 2))
            {
                continue;
            }

            inliner.AddRegisterValueTransport(
                store.Operand0,
                store,
                asAssignmentExpression);
            if (asAssignmentExpression)
                state.HiddenInstructions.Add(store.Index);
        }
    }

    private static bool TryFindRegisterTransportConsumer(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        Avm1TacInstruction store,
        out Avm1TacInstruction consumer)
    {
        consumer = default;
        for (var index = store.Index.Value + 1; index < tacIr.Count; index++)
        {
            var candidate = tacIr.Instructions[index];
            if (state.HiddenInstructions.Contains(candidate.Index) ||
                !inliner.UsesValue(candidate, store.Operand0))
            {
                continue;
            }

            if (candidate.Op is Avm1TacOp.Pop && index == store.Index.Value + 1)
                return false;

            consumer = candidate;
            return true;
        }

        return false;
    }

    private static bool HasRegisterOverwriteBetween(
        Avm1TacIr tacIr,
        Avm1TacInstruction store,
        Avm1TacInstruction consumer)
    {
        for (var index = store.Index.Value + 1; index < consumer.Index.Value; index++)
        {
            var candidate = tacIr.Instructions[index];
            if (candidate.Op is Avm1TacOp.StoreRegister &&
                candidate.IntOperand == store.IntOperand)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVisibleRootBetween(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        Avm1TacInstruction store,
        Avm1TacInstruction consumer)
    {
        for (var index = store.Index.Value + 1; index < consumer.Index.Value; index++)
        {
            var candidate = tacIr.Instructions[index];
            if (!state.HiddenInstructions.Contains(candidate.Index) &&
                inliner.IsRootInstruction(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static void FindOrderedBinaryOperandInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not Avm1TacOp.Binary)
                continue;

            // Process right-to-left so an inlined right operand no longer acts
            // as a root barrier for an earlier left operand.
            TryInlineOrderedConsumerValue(
                tacIr,
                inliner,
                state,
                consumer.Operand1,
                consumer,
                allowStructuredConditionalSpan: true);
            TryInlineOrderedConsumerValue(
                tacIr,
                inliner,
                state,
                consumer.Operand0,
                consumer,
                allowStructuredConditionalSpan: true);
        }
    }

    private static void FindDuplicatedVariableValueTransports(
        Avm1TacIr tacIr,
        Avm1InstructionTable instructions,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var assignment in tacIr.Instructions)
        {
            if (assignment.Op is not Avm1TacOp.SetVariable ||
                state.HiddenInstructions.Contains(assignment.Index) ||
                !inliner.TryGetDefinition(assignment.Operand1, out var duplicate) ||
                duplicate.Op is not Avm1TacOp.Copy ||
                !duplicate.Action.IsValid ||
                instructions[duplicate.Action].Action is not ActionPushDuplicate)
            {
                continue;
            }

            var transportedValue = duplicate.Operand0;
            if (!transportedValue.IsValid ||
                inliner.GetUseCount(transportedValue) != 2 ||
                inliner.GetUseCount(duplicate.Result) != 1 ||
                !inliner.TryGetDefinition(transportedValue, out var definition))
            {
                continue;
            }

            Avm1TacInstruction consumer = default;
            for (var index = assignment.Index.Value + 1; index < tacIr.Count; index++)
            {
                var candidate = tacIr.Instructions[index];
                if (state.HiddenInstructions.Contains(candidate.Index) ||
                    !inliner.UsesValue(candidate, transportedValue))
                {
                    continue;
                }

                consumer = candidate;
                break;
            }

            if (consumer.Op is not Avm1TacOp.Binary ||
                consumer.Operand0 != transportedValue ||
                !AreInSameBlock(
                    state.ControlFlowGraph,
                    definition,
                    duplicate,
                    assignment,
                    consumer) ||
                HasVisibleRootBetween(
                    tacIr,
                    inliner,
                    state,
                    assignment,
                    consumer) ||
                !inliner.TryInlineExactUseCountForStructuredConsumer(
                    transportedValue,
                    expectedUseCount: 2) ||
                !inliner.TryInlineSingleUseForStructuredConsumer(duplicate.Result))
            {
                continue;
            }

            inliner.AddTransportAssignment(transportedValue, assignment);
            state.HiddenInstructions.Add(assignment.Index);
        }
    }

    private static void FindOrderedExpressionInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        int inlinedBeforePass;
        do
        {
            inlinedBeforePass = inliner.InlinedValueCount;
            FindOrderedBinaryOperandInlines(tacIr, inliner, state);
            FindOrderedArrayLiteralOperandInlines(tacIr, inliner, state);
            FindOrderedObjectLiteralOperandInlines(tacIr, inliner, state);
            FindOrderedCallArgumentInlines(tacIr, inliner, state);
            FindOrderedCastOperandInlines(tacIr, inliner, state);
            FindOrderedMemberOperandInlines(tacIr, inliner, state);
        }
        while (inliner.InlinedValueCount != inlinedBeforePass);
    }

    private static void FindOrderedObjectLiteralOperandInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not Avm1TacOp.InitObject ||
                !consumer.Operand2.IsValid ||
                consumer.OperandCount <= 0)
            {
                continue;
            }

            var start = consumer.Operand2.Value;
            var availableSlots = Math.Max(0, tacIr.ValueOperands.Count - start);
            var pairCount = Math.Min(consumer.OperandCount, availableSlots / 2);
            for (var pair = 0; pair < pairCount; pair++)
            {
                var keyIndex = start + pair * 2;

                // InitObject records pairs in pop order, which is the reverse
                // of their evaluation order. Within each pair, the value is
                // evaluated after the key.
                TryInlineOrderedConsumerValue(
                    tacIr,
                    inliner,
                    state,
                    tacIr.ValueOperands[keyIndex + 1],
                    consumer,
                    allowStructuredConditionalSpan: true);
                TryInlineOrderedConsumerValue(
                    tacIr,
                    inliner,
                    state,
                    tacIr.ValueOperands[keyIndex],
                    consumer,
                    allowStructuredConditionalSpan: true);
            }
        }
    }

    private static void FindOrderedArrayLiteralOperandInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not Avm1TacOp.InitArray ||
                !consumer.Operand2.IsValid ||
                consumer.OperandCount <= 0)
            {
                continue;
            }

            var start = consumer.Operand2.Value;
            var end = Math.Min(
                start + consumer.OperandCount,
                tacIr.ValueOperands.Count);
            for (var i = start; i < end; i++)
            {
                // InitArray operands are recorded in source order, while their
                // AVM1 definitions normally appear in reverse evaluation order.
                TryInlineOrderedConsumerValue(
                    tacIr,
                    inliner,
                    state,
                    tacIr.ValueOperands[i],
                    consumer,
                    allowStructuredConditionalSpan: true);
            }
        }
    }

    private static void FindOrderedCallArgumentInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not (
                    Avm1TacOp.CallFunction or
                    Avm1TacOp.CallMethod or
                    Avm1TacOp.NewObject or
                    Avm1TacOp.NewMethod) ||
                !consumer.Operand2.IsValid ||
                consumer.OperandCount <= 0)
            {
                continue;
            }

            var start = consumer.Operand2.Value;
            var end = Math.Min(start + consumer.OperandCount, tacIr.ValueOperands.Count);
            for (var i = start; i < end; i++)
            {
                TryInlineOrderedConsumerValue(
                    tacIr,
                    inliner,
                    state,
                    tacIr.ValueOperands[i],
                    consumer,
                    allowStructuredConditionalSpan: true);
            }
        }
    }

    private static void FindOrderedCastOperandInlines(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var consumer in tacIr.Instructions)
        {
            if (consumer.Op is not Avm1TacOp.Cast)
                continue;

            // CastOp evaluates the cast function before its argument. Admit
            // operands backward so the later argument cannot block the type.
            TryInlineOrderedConsumerValue(
                tacIr,
                inliner,
                state,
                consumer.Operand1,
                consumer);
            TryInlineOrderedConsumerValue(
                tacIr,
                inliner,
                state,
                consumer.Operand0,
                consumer);
        }
    }

    private static bool TryInlineOrderedConsumerValue(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        ValueIndex value,
        Avm1TacInstruction consumer,
        bool allowStructuredConditionalSpan = false)
    {
        if (!value.IsValid ||
            inliner.IsInlined(value) ||
            inliner.GetUseCount(value) != 1 ||
            !inliner.TryGetDefinition(value, out var definition) ||
            definition.Index.Value >= consumer.Index.Value)
        {
            return false;
        }

        if (!AreInSameBlock(state.ControlFlowGraph, definition, consumer) &&
            (!allowStructuredConditionalSpan ||
                !IsStructuredConditionalConsumerSpan(
                    tacIr,
                    inliner,
                    state.ControlFlowGraph,
                    definition,
                    consumer)))
        {
            return false;
        }

        for (var i = definition.Index.Value + 1; i < consumer.Index.Value; i++)
        {
            var instruction = tacIr.Instructions[i];
            if (!state.HiddenInstructions.Contains(instruction.Index) &&
                inliner.IsRootInstruction(instruction))
            {
                return false;
            }
        }

        return inliner.TryInlineSingleUseForStructuredConsumer(value);
    }

    private static bool IsStructuredConditionalConsumerSpan(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1TacInstruction definition,
        Avm1TacInstruction consumer)
    {
        if (!cfg.TryGetBlockForAction(definition.Action, out var definitionBlock) ||
            !cfg.TryGetBlockForAction(consumer.Action, out var consumerBlock))
        {
            return false;
        }

        if (consumer.Op is Avm1TacOp.Binary)
            return Matches(consumer.Operand0) || Matches(consumer.Operand1);

        if (consumer.Op is Avm1TacOp.SetMember)
            return Matches(consumer.Operand2);

        if (consumer.Op is Avm1TacOp.Delete)
            return Matches(consumer.Operand1);

        if (consumer.Op is not (
                Avm1TacOp.InitArray or
                Avm1TacOp.InitObject or
                Avm1TacOp.CallFunction or
                Avm1TacOp.CallMethod or
                Avm1TacOp.NewObject or
                Avm1TacOp.NewMethod) ||
            !consumer.Operand2.IsValid ||
            consumer.OperandCount <= 0)
        {
            return false;
        }

        var start = consumer.Operand2.Value;
        var operandSlots = consumer.Op is Avm1TacOp.InitObject
            ? consumer.OperandCount * 2
            : consumer.OperandCount;
        var end = Math.Min(start + operandSlots, tacIr.ValueOperands.Count);
        for (var i = start; i < end; i++)
        {
            if (Matches(tacIr.ValueOperands[i]))
                return true;
        }

        return false;

        bool Matches(ValueIndex value) =>
            inliner.TryGetNestedStructuredConditionalBounds(
                value,
                out var header,
                out var merge) &&
            definitionBlock == header &&
            consumerBlock == merge;
    }

    private static bool AreInSameBlock(
        Avm1ControlFlowGraph cfg,
        Avm1TacInstruction first,
        Avm1TacInstruction second)
    {
        return cfg.TryGetBlockForAction(first.Action, out var block) &&
            cfg.TryGetBlockForAction(second.Action, out var secondBlock) &&
            secondBlock == block;
    }

    private static bool AreInSameBlock(
        Avm1ControlFlowGraph cfg,
        Avm1TacInstruction first,
        Avm1TacInstruction second,
        Avm1TacInstruction third)
    {
        return cfg.TryGetBlockForAction(first.Action, out var block) &&
            cfg.TryGetBlockForAction(second.Action, out var secondBlock) &&
            cfg.TryGetBlockForAction(third.Action, out var thirdBlock) &&
            secondBlock == block &&
            thirdBlock == block;
    }

    private static bool AreInSameBlock(
        Avm1ControlFlowGraph cfg,
        Avm1TacInstruction first,
        Avm1TacInstruction second,
        Avm1TacInstruction third,
        Avm1TacInstruction fourth)
    {
        return cfg.TryGetBlockForAction(first.Action, out var block) &&
            cfg.TryGetBlockForAction(second.Action, out var secondBlock) &&
            cfg.TryGetBlockForAction(third.Action, out var thirdBlock) &&
            cfg.TryGetBlockForAction(fourth.Action, out var fourthBlock) &&
            secondBlock == block &&
            thirdBlock == block &&
            fourthBlock == block;
    }

    private static bool AreInSameBlock(
        Avm1ControlFlowGraph cfg,
        Avm1TacInstruction first,
        Avm1TacInstruction second,
        Avm1TacInstruction third,
        Avm1TacInstruction fourth,
        Avm1TacInstruction fifth,
        Avm1TacInstruction sixth)
    {
        return cfg.TryGetBlockForAction(first.Action, out var block) &&
            cfg.TryGetBlockForAction(second.Action, out var secondBlock) &&
            cfg.TryGetBlockForAction(third.Action, out var thirdBlock) &&
            cfg.TryGetBlockForAction(fourth.Action, out var fourthBlock) &&
            cfg.TryGetBlockForAction(fifth.Action, out var fifthBlock) &&
            cfg.TryGetBlockForAction(sixth.Action, out var sixthBlock) &&
            secondBlock == block &&
            thirdBlock == block &&
            fourthBlock == block &&
            fifthBlock == block &&
            sixthBlock == block;
    }

    private static void HideSingleUseDefinition(
        ValueIndex value,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        if (inliner.GetUseCount(value) == 1 &&
            inliner.TryGetDefinition(value, out var definition))
        {
            state.HiddenInstructions.Add(definition.Index);
        }
    }

    private static Dictionary<BlockIndex, StructuredWithRegion> CreateWithRegions(
        Avm1ControlFlowGraph cfg,
        Avm1InstructionTable instructions)
    {
        var result = new Dictionary<BlockIndex, StructuredWithRegion>(instructions.WithRegions.Count);
        foreach (var region in instructions.WithRegions)
        {
            if (!cfg.TryGetBlockForAction(region.EnterAction, out var header) ||
                !cfg.TryGetBlockForAction(region.BodyStartAction, out var bodyEntry) ||
                !cfg.TryGetBlockForAction(region.ExitAction, out var exit))
            {
                continue;
            }

            result[header] = new StructuredWithRegion(
                header,
                bodyEntry,
                exit,
                region.EnterAction);
        }

        return result;
    }

    private static Dictionary<BlockIndex, StructuredTryRegion> CreateTryRegions(
        Avm1ControlFlowGraph cfg,
        Avm1InstructionTable instructions)
    {
        var result = new Dictionary<BlockIndex, StructuredTryRegion>(instructions.TryRegions.Count);
        foreach (var region in instructions.TryRegions)
        {
            if (!cfg.TryGetBlockForAction(region.EnterAction, out var header) ||
                !cfg.TryGetBlockForAction(region.TryBodyStartAction, out var tryBodyEntry) ||
                !cfg.TryGetBlockForAction(region.TryExitAction, out var tryExit))
            {
                continue;
            }

            var catchEnter = GetOptionalBlock(cfg, region.CatchEnterAction);
            var catchBodyEntry = GetOptionalBlock(cfg, region.CatchBodyStartAction);
            var catchExit = GetOptionalBlock(cfg, region.CatchExitAction);
            var finallyEnter = GetOptionalBlock(cfg, region.FinallyEnterAction);
            var finallyBodyEntry = GetOptionalBlock(cfg, region.FinallyBodyStartAction);
            var finallyExit = GetOptionalBlock(cfg, region.FinallyExitAction);
            var continuation = GetOptionalBlock(cfg, region.ContinuationAction);
            var lastBoundary = finallyExit.IsValid
                ? finallyExit
                : catchExit.IsValid
                    ? catchExit
                    : tryExit;

            result[header] = new StructuredTryRegion(
                header,
                tryBodyEntry,
                tryExit,
                catchEnter,
                catchBodyEntry,
                catchExit,
                finallyEnter,
                finallyBodyEntry,
                finallyExit,
                continuation,
                lastBoundary,
                region.EnterAction);
        }

        return result;
    }

    private static BlockIndex GetOptionalBlock(
        Avm1ControlFlowGraph cfg,
        ActionIndex action) =>
        action.IsValid && cfg.TryGetBlockForAction(action, out var block)
            ? block
            : BlockIndex.Invalid;

    private static void RefreshTrailingPreheader(
        List<AstIndex> topLevel,
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        BuilderState state,
        Avm1WhileRegion region)
    {
        if (topLevel.Count == 0 ||
            !TryGetLoopPreheader(cfg, region, out var expectedPreheader))
            return;

        var previousIndex = topLevel[^1];
        var previousNode = builder.GetNode(previousIndex);
        if (previousNode.Kind is not Avm1AstNodeKind.Block ||
            previousNode.Block != expectedPreheader.Index)
            return;

        if (!HasVisibleRootInstruction(tacIr, expectedPreheader, inliner, state))
        {
            topLevel.RemoveAt(topLevel.Count - 1);
            return;
        }

        topLevel[^1] = AddBasicBlock(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            expectedPreheader,
            state);
    }

    private static bool IsDeferredWhileBodyBlock(
        BlockIndex block,
        BuilderState state)
    {
        foreach (var region in state.WhileByHeader.Values)
        {
            if (block.Value >= region.Header.Value ||
                !region.Blocks.Contains(block) ||
                state.ActiveControlScopes.Any(scope =>
                    scope.Kind is ActiveControlScopeKind.Loop &&
                    scope.Header == region.Header))
            {
                continue;
            }

            return true;
        }
        return false;
    }

    private static bool TryGetLoopPreheader(
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region,
        out Avm1BasicBlock preheader)
    {
        var loopBlocks = region.Blocks.ToHashSet();
        loopBlocks.Add(region.Header);
        var found = BlockIndex.Invalid;
        foreach (var block in cfg.Blocks)
        {
            if (loopBlocks.Contains(block.Index) ||
                (block.FirstSuccessor != region.Header &&
                    block.SecondSuccessor != region.Header))
            {
                continue;
            }

            if (found.IsValid)
            {
                preheader = default;
                return false;
            }
            found = block.Index;
        }

        if (!found.IsValid)
        {
            preheader = default;
            return false;
        }

        preheader = cfg[found];
        return true;
    }

    private static bool TryGetLoopLatch(
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region,
        out Avm1BasicBlock latch)
    {
        var found = BlockIndex.Invalid;
        foreach (var blockIndex in region.Blocks)
        {
            var block = cfg[blockIndex];
            if (block.FirstSuccessor != region.Header &&
                block.SecondSuccessor != region.Header)
            {
                continue;
            }

            if (found.IsValid)
            {
                latch = default;
                return false;
            }
            found = blockIndex;
        }

        if (!found.IsValid)
        {
            latch = default;
            return false;
        }

        latch = cfg[found];
        return true;
    }

    private static bool HasVisibleRootInstruction(
        Avm1TacIr tacIr,
        Avm1BasicBlock block,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        for (var index = 0; index < tacIr.Count; index++)
        {
            var instruction = tacIr.Instructions[index];
            if (instruction.Action.Value < block.StartAction.Value ||
                instruction.Action.Value >= block.EndAction.Value)
            {
                continue;
            }

            if (state.HiddenInstructions.Contains(instruction.Index))
                continue;

            if (inliner.IsRootInstruction(instruction))
                return true;
        }

        return false;
    }

    private static bool HasVisibleRootBeforeMerge(
        Avm1TacIr tacIr,
        Avm1ControlFlowGraph cfg,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        BlockIndex start,
        BlockIndex merge)
    {
        var visited = state.ReachabilityScratch;
        var pending = state.TraversalStack;
        Array.Clear(visited);
        var pendingCount = 0;
        if (start.IsValid && start != merge)
        {
            visited[start.Value] = 1;
            pending[pendingCount++] = start;
        }

        while (pendingCount > 0)
        {
            var current = pending[--pendingCount];
            var block = cfg[current];
            if (HasVisibleRootInstruction(tacIr, block, inliner, state))
            {
                Array.Clear(visited);
                return true;
            }

            AddTraversalSuccessor(
                block.FirstSuccessor,
                merge,
                visited,
                pending,
                ref pendingCount);
            AddTraversalSuccessor(
                block.SecondSuccessor,
                merge,
                visited,
                pending,
                ref pendingCount);
        }

        Array.Clear(visited);
        return false;

        static void AddTraversalSuccessor(
            BlockIndex successor,
            BlockIndex merge,
            byte[] visited,
            BlockIndex[] pending,
            ref int pendingCount)
        {
            if (!successor.IsValid || successor == merge || visited[successor.Value] != 0)
                return;

            visited[successor.Value] = 1;
            pending[pendingCount++] = successor;
        }
    }

    private static AstIndex AddWhile(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region,
        BuilderState state)
    {
        var implicitContinueBlock = FindImplicitWhileContinueBlock(cfg, region);
        state.ActiveLoops.Push(new ActiveLoop(
            region.Header,
            region.Exit,
            implicitContinueBlock,
            region.ConditionBlock,
            PromotedVariable: null,
            IsForIn: false));
        state.ActiveControlScopes.Push(new ActiveControlScope(
            ActiveControlScopeKind.Loop,
            region.Header,
            region.Exit,
            region.Header,
            implicitContinueBlock));

        var header = cfg[region.Header];
        var conditionBlock = cfg[region.ConditionBlock];
        var branch = GetInstructions(tacIr, conditionBlock).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        AstIndex condExpr = AstIndex.Invalid;

        if (branch.Op is Avm1TacOp.BranchIf)
        {
            var bodyEntry = region.BodyEntry;
            bool invert = bodyEntry == conditionBlock.FirstSuccessor &&
                bodyEntry != conditionBlock.SecondSuccessor;
            if (!TryBuildRegisterShortCircuitCondition(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    region.Header,
                    region.ConditionBlock,
                    branch,
                    state,
                    out condExpr))
            {
                PrepareRegisterStagedExpression(
                    tacIr,
                    registerSsa,
                    inliner,
                    cfg,
                    state,
                    branch.Operand0);
                condExpr = BuildExpression(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    state.SsaAccessByInstruction,
                    branch.Operand0);
            }
            if (invert)
            {
                condExpr = InvertCondition(builder, condExpr);
            }
        }
        else
        {
            condExpr = builder.AddNode(Avm1AstNodeKind.Literal, startAction: ActionIndex.Invalid);
        }

        var hasSingleBlockCondition = region.ConditionBlock == region.Header;
        var forInKey = AstIndex.Invalid;
        var forInCollection = AstIndex.Invalid;
        var promoteToForIn = hasSingleBlockCondition && TryPrepareForInPromotion(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            cfg,
            region,
            branch,
            state,
            out forInKey,
            out forInCollection);

        IReadOnlyList<AstIndex> initializerStatements = [];
        IReadOnlyList<AstIndex> updateStatements = [];
        var promoteToFor = false;
        LoopVariableKey? promotedForVariable = null;
        if (!promoteToForIn)
        {
            if (hasSingleBlockCondition)
            {
                promoteToFor = TryPrepareForPromotion(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    region,
                    condExpr,
                    state,
                    out initializerStatements,
                    out updateStatements,
                    out var loopVariable);
                if (promoteToFor)
                    promotedForVariable = loopVariable;
            }
            else
            {
                promoteToFor = TryPrepareForShortCircuitPromotion(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    region,
                    condExpr,
                    state,
                    out var initializerStatement,
                    out var updateStatement);
                if (promoteToFor)
                {
                    initializerStatements = [initializerStatement];
                    updateStatements = [updateStatement];
                }
                if (promoteToFor &&
                    TryGetLoopVariable(builder, valueAnalysis, condExpr, out var loopVariable))
                {
                    promotedForVariable = loopVariable;
                }
            }
        }

        if ((promoteToForIn || promoteToFor) &&
            implicitContinueBlock.IsValid &&
            !HasVisibleRootInstruction(tacIr, cfg[implicitContinueBlock], inliner, state))
        {
            state.TransparentLoopContinueBlocks.Add(implicitContinueBlock);
        }

        if (promoteToForIn)
        {
            var activeLoop = state.ActiveLoops.Pop();
            state.ActiveLoops.Push(activeLoop with { IsForIn = true });
        }
        else if (promoteToFor && promotedForVariable is { } promotedVariable)
        {
            var activeLoop = state.ActiveLoops.Pop();
            state.ActiveLoops.Push(activeLoop with { PromotedVariable = promotedVariable });
        }

        var body = AddStructuredBlocks(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            cfg,
            region.BodyBlocks,
            state);

        var conditionPrelude = AstIndex.Invalid;
        if (!promoteToForIn &&
            !promoteToFor &&
            HasVisibleRootInstruction(tacIr, header, inliner, state))
        {
            var prelude = AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                header,
                state,
                suppressLoopControl: true);
            if (builder.GetNode(prelude).Children.Count > 0)
                conditionPrelude = prelude;
        }

        state.ActiveControlScopes.Pop();
        state.ActiveLoops.Pop();

        if (promoteToForIn)
        {
            var forInChildren = new List<AstIndex>(body.Count + 2) { forInKey, forInCollection };
            forInChildren.AddRange(body);

            return builder.AddNode(
                Avm1AstNodeKind.ForIn,
                region.Header,
                region.Exit,
                header.StartAction,
                header.EndAction,
                forInChildren);
        }

        if (promoteToFor)
        {
            var initializerClause = builder.AddNode(
                Avm1AstNodeKind.ForInitializerList,
                children: initializerStatements);
            var updateClause = builder.AddNode(
                Avm1AstNodeKind.ForUpdateList,
                children: updateStatements);
            var forChildren = new List<AstIndex>(body.Count + 3)
            {
                initializerClause,
                condExpr,
                updateClause
            };
            forChildren.AddRange(body);

            return builder.AddNode(
                Avm1AstNodeKind.For,
                region.Header,
                region.Exit,
                header.StartAction,
                conditionBlock.EndAction,
                forChildren);
        }

        if (conditionPrelude.IsValid)
        {
            var breakBlock = builder.AddNode(
                Avm1AstNodeKind.Block,
                block: region.ConditionBlock,
                children:
                [
                    builder.AddNode(
                        Avm1AstNodeKind.Break,
                        block: BlockIndex.Invalid)
                ]);
            var exitIf = builder.AddNode(
                Avm1AstNodeKind.If,
                block: region.ConditionBlock,
                merge: region.Exit,
                startAction: branch.Action,
                endAction: conditionBlock.EndAction,
                children: [InvertCondition(builder, condExpr), breakBlock]);
            var trueLiteral = builder.AddNode(
                Avm1AstNodeKind.Literal,
                startAction: ActionIndex.Invalid,
                intOperand: Avm1AstArena.SyntheticTrueLiteral);
            var fallbackChildren = new List<AstIndex>(body.Count + 3)
            {
                trueLiteral,
                conditionPrelude,
                exitIf
            };
            fallbackChildren.AddRange(body);

            return builder.AddNode(
                Avm1AstNodeKind.DoWhile,
                region.Header,
                region.Exit,
                header.StartAction,
                conditionBlock.EndAction,
                fallbackChildren);
        }

        var children = new List<AstIndex>(body.Count + 1) { condExpr };
        children.AddRange(body);

        return builder.AddNode(
            Avm1AstNodeKind.While,
            region.Header,
            region.Exit,
            header.StartAction,
            conditionBlock.EndAction,
            children);
    }

    private static BlockIndex FindImplicitWhileContinueBlock(
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region)
    {
        for (var index = region.BodyBlocks.Count - 1; index >= 0; index--)
        {
            var blockIndex = region.BodyBlocks[index];
            var block = cfg[blockIndex];
            if (block.FirstSuccessor == region.Header &&
                !block.SecondSuccessor.IsValid &&
                block.Terminator is Avm1BlockTerminatorKind.FallThrough or
                    Avm1BlockTerminatorKind.Jump)
            {
                return blockIndex;
            }
        }

        return region.BodyBlocks.Count > 0
            ? region.BodyBlocks[^1]
            : BlockIndex.Invalid;
    }

    private static bool TryBuildRegisterShortCircuitCondition(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        BlockIndex firstStageBlock,
        BlockIndex finalConditionBlock,
        Avm1TacInstruction finalBranch,
        BuilderState state,
        out AstIndex expression)
    {
        expression = AstIndex.Invalid;
        if (firstStageBlock == finalConditionBlock ||
            !TryGetRegisterRead(
                tacIr,
                state.SsaAccessByInstruction,
                finalBranch.Operand0,
                out var finalRead,
                out var finalReadInverted))
        {
            return false;
        }

        var stages = new List<(ValueIndex Left, ValueIndex Right, ActionOpcode Opcode)>();
        var current = firstStageBlock;
        var previousPhiVersion = -1;
        var visited = new HashSet<BlockIndex>();
        while (current != finalConditionBlock && visited.Add(current))
        {
            var header = cfg[current];
            if (!TryGetRegisterShortCircuitStage(
                    cfg,
                    registerSsa,
                    header,
                    finalRead.Register,
                    out var bridge,
                    out var continuation,
                    out var directPredecessor,
                    out var logicalOpcode))
            {
                return false;
            }

            var headerBranch = GetInstructions(tacIr, header)
                .LastOrDefault(instruction => instruction.Op is Avm1TacOp.BranchIf);
            if (headerBranch.Op is not Avm1TacOp.BranchIf ||
                !TryGetLastRegisterWrite(
                    registerSsa,
                    header,
                    finalRead.Register,
                    out var headerWrite) ||
                !TryGetLastRegisterWrite(
                    registerSsa,
                    cfg[bridge],
                    finalRead.Register,
                    out var bridgeWrite) ||
                !TryMatchValueThroughCopiesAndNot(
                    tacIr,
                    headerBranch.Operand0,
                    headerWrite.Source,
                    out var branchInverted) ||
                !TryGetRegisterPhiJoiningWrites(
                    registerSsa,
                    continuation,
                    finalRead.Register,
                    directPredecessor,
                    headerWrite.Version,
                    bridge,
                    bridgeWrite.Version,
                    out var phiVersion))
            {
                return false;
            }

            if (branchInverted)
            {
                logicalOpcode = logicalOpcode switch
                {
                    ActionOpcode.And => ActionOpcode.Or,
                    ActionOpcode.Or => ActionOpcode.And,
                    _ => logicalOpcode
                };
            }

            if (stages.Count > 0 &&
                (!TryGetRegisterRead(
                    tacIr,
                    state.SsaAccessByInstruction,
                    headerWrite.Source,
                    out var carriedRead,
                    out var carriedReadInverted) ||
                 carriedRead.Register != finalRead.Register ||
                 carriedRead.Version != previousPhiVersion ||
                 carriedReadInverted))
            {
                return false;
            }

            stages.Add((headerWrite.Source, bridgeWrite.Source, logicalOpcode));
            previousPhiVersion = phiVersion;
            current = continuation;
        }

        if (current != finalConditionBlock ||
            stages.Count == 0 ||
            previousPhiVersion != finalRead.Version)
        {
            return false;
        }

        PrepareRegisterStagedExpression(
            tacIr,
            registerSsa,
            inliner,
            cfg,
            state,
            stages[0].Left);
        expression = BuildDirectExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            stages[0].Left);
        foreach (var stage in stages)
        {
            PrepareRegisterStagedExpression(
                tacIr,
                registerSsa,
                inliner,
                cfg,
                state,
                stage.Right);
            var right = BuildDirectExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                stage.Right);
            expression = AddLogicalBinary(builder, stage.Opcode, expression, right);
        }

        if (finalReadInverted)
            expression = InvertCondition(builder, expression);

        return true;
    }

    private static bool TryGetRegisterShortCircuitStage(
        Avm1ControlFlowGraph cfg,
        Avm1RegisterSsa registerSsa,
        Avm1BasicBlock header,
        int register,
        out BlockIndex bridge,
        out BlockIndex continuation,
        out BlockIndex directPredecessor,
        out ActionOpcode logicalOpcode)
    {
        bridge = BlockIndex.Invalid;
        continuation = BlockIndex.Invalid;
        directPredecessor = BlockIndex.Invalid;
        logicalOpcode = default;

        if (TryGetSingleSuccessor(cfg, header.FirstSuccessor, out var firstTarget) &&
            firstTarget == header.SecondSuccessor)
        {
            bridge = header.FirstSuccessor;
            continuation = header.SecondSuccessor;
            directPredecessor = header.Index;
            logicalOpcode = ActionOpcode.Or;
            return true;
        }

        if (TryGetSingleSuccessor(cfg, header.SecondSuccessor, out var secondTarget) &&
            secondTarget == header.FirstSuccessor)
        {
            bridge = header.SecondSuccessor;
            continuation = header.FirstSuccessor;
            directPredecessor = header.Index;
            logicalOpcode = ActionOpcode.And;
            return true;
        }

        if (!TryGetSingleSuccessor(cfg, header.FirstSuccessor, out firstTarget) ||
            !TryGetSingleSuccessor(cfg, header.SecondSuccessor, out secondTarget) ||
            firstTarget != secondTarget)
        {
            return false;
        }

        continuation = firstTarget;
        if (TryGetLastRegisterWrite(
                registerSsa,
                cfg[header.SecondSuccessor],
                register,
                out _))
        {
            bridge = header.SecondSuccessor;
            directPredecessor = header.FirstSuccessor;
            logicalOpcode = ActionOpcode.And;
            return true;
        }

        if (TryGetLastRegisterWrite(
                registerSsa,
                cfg[header.FirstSuccessor],
                register,
                out _))
        {
            bridge = header.FirstSuccessor;
            directPredecessor = header.SecondSuccessor;
            logicalOpcode = ActionOpcode.Or;
            return true;
        }

        return false;
    }

    private static bool TryGetRegisterRead(
        Avm1TacIr tacIr,
        Dictionary<IrIndex, Avm1RegisterAccess> accesses,
        ValueIndex value,
        out Avm1RegisterAccess read,
        out bool inverted)
    {
        read = default;
        inverted = false;
        for (var depth = 0; depth < 8; depth++)
        {
            if (!TryFindDefinition(tacIr, value, out var definition))
                return false;

            if (definition.Op is Avm1TacOp.Copy)
            {
                value = definition.Operand0;
                continue;
            }

            if (definition.Op is Avm1TacOp.Unary &&
                definition.Opcode is ActionOpcode.Not)
            {
                inverted = !inverted;
                value = definition.Operand0;
                continue;
            }

            return definition.Op is Avm1TacOp.LoadRegister &&
                accesses.TryGetValue(definition.Index, out read) &&
                read.Kind is Avm1RegisterAccessKind.Read;
        }

        return false;
    }

    private static bool TryGetLastRegisterWrite(
        Avm1RegisterSsa registerSsa,
        Avm1BasicBlock block,
        int register,
        out Avm1RegisterAccess write)
    {
        write = default;
        var found = false;
        foreach (var access in registerSsa.Accesses)
        {
            if (access.Kind is not Avm1RegisterAccessKind.Write ||
                access.Register != register ||
                access.Action.Value < block.StartAction.Value ||
                access.Action.Value >= block.EndAction.Value)
            {
                continue;
            }

            write = access;
            found = true;
        }

        return found && write.Source.IsValid;
    }

    private static bool TryGetSingleSuccessor(
        Avm1ControlFlowGraph cfg,
        BlockIndex block,
        out BlockIndex successor)
    {
        successor = BlockIndex.Invalid;
        if (!block.IsValid)
            return false;

        var candidate = cfg[block];
        if (!candidate.FirstSuccessor.IsValid || candidate.SecondSuccessor.IsValid)
            return false;

        successor = candidate.FirstSuccessor;
        return true;
    }

    private static bool TryGetRegisterPhiJoiningWrites(
        Avm1RegisterSsa registerSsa,
        BlockIndex block,
        int register,
        BlockIndex firstPredecessor,
        int firstVersion,
        BlockIndex secondPredecessor,
        int secondVersion,
        out int phiVersion)
    {
        phiVersion = -1;
        foreach (var phi in registerSsa.PhiNodes)
        {
            if (phi.Block != block ||
                phi.Register != register)
            {
                continue;
            }

            var firstFound = false;
            var secondFound = false;
            var count = Math.Min(phi.Predecessors.Count, phi.IncomingVersions.Count);
            for (var index = 0; index < count; index++)
            {
                firstFound |= phi.Predecessors[index] == firstPredecessor &&
                    phi.IncomingVersions[index] == firstVersion;
                secondFound |= phi.Predecessors[index] == secondPredecessor &&
                    phi.IncomingVersions[index] == secondVersion;
            }

            if (!firstFound || !secondFound)
                continue;

            phiVersion = phi.Version;
            return true;
        }

        return false;
    }

    private static void PrepareRegisterStagedExpression(
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        BuilderState state,
        ValueIndex root)
    {
        var pending = new Stack<ValueIndex>();
        var visited = new HashSet<ValueIndex>();
        pending.Push(root);
        while (pending.TryPop(out var value))
        {
            if (!value.IsValid ||
                !visited.Add(value) ||
                !inliner.TryGetDefinition(value, out var definition))
            {
                continue;
            }

            inliner.TryInlineForStructuredConsumer(value);
            if (definition.Op is Avm1TacOp.LoadRegister &&
                !inliner.TryGetPostfixVariableUpdate(value, out _) &&
                state.SsaAccessByInstruction.TryGetValue(
                    definition.Index,
                    out var read) &&
                read.Kind is Avm1RegisterAccessKind.Read &&
                state.SymbolTable.TryGetRegisterSymbol(
                    read.Register,
                    read.Version,
                    out var symbol) &&
                Avm1SymbolTable.IsGeneratedRegisterSymbol(symbol) &&
                state.ProjectionHints?.PreservesSourceIdentity(read.Register) is not true &&
                HasSingleRegisterRead(registerSsa, read) &&
                TryGetSameBlockRegisterWrite(
                    registerSsa,
                    cfg,
                    read,
                    out var write) &&
                !IsRegisterSelfUpdate(inliner, write))
            {
                inliner.AddValueAlias(value, write.Source);
                state.HiddenInstructions.Add(write.Instruction);
                if (write.Instruction.Value + 1 < tacIr.Count)
                {
                    var cleanup = tacIr.Instructions[write.Instruction.Value + 1];
                    if (cleanup.Op is Avm1TacOp.Pop && cleanup.Operand0 == write.Source)
                        state.HiddenInstructions.Add(cleanup.Index);
                }
                pending.Push(write.Source);
                continue;
            }

            pending.Push(definition.Operand0);
            pending.Push(definition.Operand1);
            if (Avm1TacEffectAnalysis.HasValueOperandSideTable(definition.Op))
            {
                if (!definition.Operand2.IsValid || definition.OperandCount <= 0)
                    continue;

                var operandSlots = definition.Op is Avm1TacOp.InitObject
                    ? definition.OperandCount * 2
                    : definition.OperandCount;
                var end = Math.Min(
                    definition.Operand2.Value + operandSlots,
                    tacIr.ValueOperands.Count);
                for (var index = definition.Operand2.Value; index < end; index++)
                    pending.Push(tacIr.ValueOperands[index]);
            }
            else
            {
                pending.Push(definition.Operand2);
            }
        }
    }

    private static bool HasSingleRegisterRead(
        Avm1RegisterSsa registerSsa,
        Avm1RegisterAccess read)
    {
        var count = 0;
        foreach (var candidate in registerSsa.Accesses)
        {
            if (candidate.Kind is not Avm1RegisterAccessKind.Read ||
                candidate.Register != read.Register ||
                candidate.Version != read.Version)
            {
                continue;
            }

            if (++count > 1)
                return false;
        }

        return count == 1;
    }

    private static bool IsRegisterSelfUpdate(
        Avm1ExpressionInliner inliner,
        Avm1RegisterAccess write)
    {
        if (!TryGetSemanticDefinition(inliner, write.Source, out var value))
            return false;

        return value.Op switch
        {
            Avm1TacOp.Binary =>
                IsSameRegisterLoad(value.Operand0) ||
                IsSameRegisterLoad(value.Operand1),
            Avm1TacOp.Unary => IsSameRegisterLoad(value.Operand0),
            _ => false
        };

        bool IsSameRegisterLoad(ValueIndex operand) =>
            TryGetSemanticDefinition(inliner, operand, out var load) &&
            load.Op is Avm1TacOp.LoadRegister &&
            load.IntOperand == write.Register;
    }

    private static bool TryGetSameBlockRegisterWrite(
        Avm1RegisterSsa registerSsa,
        Avm1ControlFlowGraph cfg,
        Avm1RegisterAccess read,
        out Avm1RegisterAccess write)
    {
        write = default;
        if (!cfg.TryGetBlockForAction(read.Action, out var readBlock))
            return false;

        foreach (var candidate in registerSsa.Accesses)
        {
            if (candidate.Kind is not Avm1RegisterAccessKind.Write ||
                candidate.Register != read.Register ||
                candidate.Version != read.Version ||
                candidate.Instruction.Value >= read.Instruction.Value ||
                !candidate.Source.IsValid ||
                !cfg.TryGetBlockForAction(candidate.Action, out var writeBlock) ||
                writeBlock != readBlock)
            {
                continue;
            }

            write = candidate;
            return true;
        }

        return false;
    }

    private static bool TryMatchValueThroughCopiesAndNot(
        Avm1TacIr tacIr,
        ValueIndex left,
        ValueIndex right,
        out bool inverted)
    {
        inverted = false;
        for (var depth = 0; depth < 8; depth++)
        {
            if (left == right)
                return true;

            if (TryFindDefinition(tacIr, left, out var leftDefinition) &&
                leftDefinition.Op is Avm1TacOp.Copy)
            {
                left = leftDefinition.Operand0;
                continue;
            }

            if (TryFindDefinition(tacIr, left, out leftDefinition) &&
                leftDefinition.Op is Avm1TacOp.Unary &&
                leftDefinition.Opcode is ActionOpcode.Not)
            {
                inverted = !inverted;
                left = leftDefinition.Operand0;
                continue;
            }

            if (TryFindDefinition(tacIr, right, out var rightDefinition) &&
                rightDefinition.Op is Avm1TacOp.Copy)
            {
                right = rightDefinition.Operand0;
                continue;
            }


            if (TryFindDefinition(tacIr, right, out rightDefinition) &&
                rightDefinition.Op is Avm1TacOp.Unary &&
                rightDefinition.Opcode is ActionOpcode.Not)
            {
                inverted = !inverted;
                right = rightDefinition.Operand0;
                continue;
            }

            return false;
        }

        return left == right;
    }

    private static bool TryPrepareForInPromotion(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region,
        Avm1TacInstruction branch,
        BuilderState state,
        out AstIndex key,
        out AstIndex collection)
    {
        key = AstIndex.Invalid;
        collection = AstIndex.Invalid;

        if (branch.Op is not Avm1TacOp.BranchIf)
            return false;

        var header = cfg[region.Header];
        var headerInstructions = GetInstructions(tacIr, header).ToArray();
        var candidateBlocks = new List<Avm1BasicBlock>(2) { header };
        if (region.Header.Value > 0)
        {
            var predecessor = cfg[new BlockIndex(region.Header.Value - 1)];
            if (predecessor.FirstSuccessor == region.Header || predecessor.SecondSuccessor == region.Header)
                candidateBlocks.Add(predecessor);
        }

        foreach (var candidateBlock in candidateBlocks)
        {
            var enumerates = GetInstructions(tacIr, candidateBlock)
                .Where(i => i.Op is Avm1TacOp.Enumerate)
                .Reverse();

            foreach (var enumerate in enumerates)
            {
                var store = headerInstructions.FirstOrDefault(i =>
                    i.Op is Avm1TacOp.StoreRegister &&
                    DependsOnValue(tacIr, i.Operand0, enumerate.Result, depth: 0));
                if (store.Op is not Avm1TacOp.StoreRegister ||
                    !IsEnumerationSentinelCondition(tacIr, valueAnalysis, branch.Operand0, store.Operand0))
                {
                    continue;
                }

                var version = state.SsaAccessByInstruction.TryGetValue(store.Index, out var access)
                    ? access.Version
                    : 0;
                key = builder.AddNode(
                    Avm1AstNodeKind.Register,
                    block: new BlockIndex(store.IntOperand),
                    merge: new BlockIndex(version));

                collection = BuildExpression(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    state.SsaAccessByInstruction,
                    enumerate.Operand0);

                if (instructions[enumerate.Action].Action is Swf5.ActionEnumerate)
                    collection = builder.AddNode(Avm1AstNodeKind.Variable, children: [collection]);

                state.HiddenInstructions.Add(enumerate.Index);
                state.HiddenInstructions.Add(store.Index);
                if (TryFindDefinition(tacIr, store.Operand0, out var stackPhi) &&
                    stackPhi.Op is Avm1TacOp.Phi)
                {
                    state.HiddenInstructions.Add(stackPhi.Index);
                }
                return true;
            }
        }

        return false;
    }

    private static bool IsEnumerationSentinelCondition(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        ValueIndex condition,
        ValueIndex enumeratedValue,
        int depth = 0)
    {
        if (depth > 16 || !TryFindDefinition(tacIr, condition, out var definition))
            return false;

        if (definition.Op is Avm1TacOp.Copy or Avm1TacOp.Unary)
        {
            return IsEnumerationSentinelCondition(
                tacIr,
                valueAnalysis,
                definition.Operand0,
                enumeratedValue,
                depth + 1);
        }

        if (definition.Op is not Avm1TacOp.Binary ||
            definition.Opcode is not (ActionOpcode.Equals or ActionOpcode.Equals2 or ActionOpcode.StrictEquals))
        {
            return false;
        }

        return (DependsOnValue(tacIr, definition.Operand0, enumeratedValue, depth + 1) &&
                valueAnalysis[definition.Operand1].ConstantKind is Avm1ConstantKind.Null) ||
            (DependsOnValue(tacIr, definition.Operand1, enumeratedValue, depth + 1) &&
                valueAnalysis[definition.Operand0].ConstantKind is Avm1ConstantKind.Null);
    }

    private static bool TryGetForInEnumerationCleanupSuccessor(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1BasicBlock block,
        out BlockIndex continuation)
    {
        continuation = BlockIndex.Invalid;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch)
            return false;

        var firstIsSelf = block.FirstSuccessor == block.Index;
        var secondIsSelf = block.SecondSuccessor == block.Index;
        if (firstIsSelf == secondIsSelf)
            return false;

        var branch = GetInstructions(tacIr, block)
            .LastOrDefault(instruction => instruction.Op is Avm1TacOp.BranchIf);
        if (branch.Op is not Avm1TacOp.BranchIf ||
            !IsUnresolvedEnumerationCleanupCondition(
                tacIr,
                valueAnalysis,
                branch.Operand0))
        {
            return false;
        }

        foreach (var instruction in GetInstructions(tacIr, block))
        {
            if (instruction.Op is not (
                Avm1TacOp.NoOp or
                Avm1TacOp.LoadConstant or
                Avm1TacOp.Copy or
                Avm1TacOp.Phi or
                Avm1TacOp.Unary or
                Avm1TacOp.Binary or
                Avm1TacOp.BranchIf))
            {
                return false;
            }
        }

        continuation = firstIsSelf
            ? block.SecondSuccessor
            : block.FirstSuccessor;
        return true;
    }

    private static bool IsUnresolvedEnumerationCleanupCondition(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        ValueIndex condition,
        int depth = 0)
    {
        if (depth > 16 || !TryFindDefinition(tacIr, condition, out var definition))
            return false;

        if (definition.Op is Avm1TacOp.Copy or Avm1TacOp.Unary)
        {
            return IsUnresolvedEnumerationCleanupCondition(
                tacIr,
                valueAnalysis,
                definition.Operand0,
                depth + 1);
        }

        if (definition.Op is not Avm1TacOp.Binary ||
            definition.Opcode is not (
                ActionOpcode.Equals or
                ActionOpcode.Equals2 or
                ActionOpcode.StrictEquals))
        {
            return false;
        }

        return IsNullWithUnresolvedOperand(definition.Operand0, definition.Operand1) ||
            IsNullWithUnresolvedOperand(definition.Operand1, definition.Operand0);

        bool IsNullWithUnresolvedOperand(ValueIndex nullValue, ValueIndex stackValue) =>
            valueAnalysis[nullValue].ConstantKind is Avm1ConstantKind.Null &&
            stackValue.IsValid &&
            (!TryFindDefinition(tacIr, stackValue, out _) ||
             IsEnumerationStreamValue(tacIr, stackValue, depth + 1));
    }

    private static bool IsEnumerationStreamValue(
        Avm1TacIr tacIr,
        ValueIndex value,
        int depth)
    {
        if (depth > 16 || !TryFindDefinition(tacIr, value, out var definition))
            return false;

        return definition.Op switch
        {
            Avm1TacOp.Enumerate => true,
            Avm1TacOp.Copy =>
                IsEnumerationStreamValue(tacIr, definition.Operand0, depth + 1),
            Avm1TacOp.Phi => GetPhiOperands(tacIr, definition)
                .Any(operand => IsEnumerationStreamValue(tacIr, operand, depth + 1)),
            _ => false
        };
    }

    private static bool DependsOnValue(
        Avm1TacIr tacIr,
        ValueIndex value,
        ValueIndex target,
        int depth)
    {
        if (value == target)
            return true;

        if (depth > 16 || !TryFindDefinition(tacIr, value, out var definition))
            return false;

        return definition.Op switch
        {
            Avm1TacOp.Copy or Avm1TacOp.Unary =>
                DependsOnValue(tacIr, definition.Operand0, target, depth + 1),
            Avm1TacOp.Binary =>
                DependsOnValue(tacIr, definition.Operand0, target, depth + 1) ||
                DependsOnValue(tacIr, definition.Operand1, target, depth + 1),
            Avm1TacOp.Phi => GetPhiOperands(tacIr, definition)
                .Any(operand => DependsOnValue(tacIr, operand, target, depth + 1)),
            _ => false
        };
    }

    private static IEnumerable<ValueIndex> GetPhiOperands(
        Avm1TacIr tacIr,
        Avm1TacInstruction phi)
    {
        if (phi.Op is not Avm1TacOp.Phi || !phi.Operand2.IsValid || phi.OperandCount <= 0)
            yield break;

        var end = Math.Min(phi.Operand2.Value + phi.OperandCount, tacIr.ValueOperands.Count);
        for (var i = phi.Operand2.Value; i < end; i++)
            yield return tacIr.ValueOperands[i];
    }

    private static bool TryFindDefinition(
        Avm1TacIr tacIr,
        ValueIndex value,
        out Avm1TacInstruction definition)
    {
        for (var i = tacIr.Instructions.Count - 1; i >= 0; i--)
        {
            var candidate = tacIr.Instructions[i];
            if (candidate.Result != value || !DefinesValue(candidate.Op))
                continue;

            definition = candidate;
            return true;
        }

        definition = default;
        return false;
    }

    private static AstIndex AddDoWhile(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1DoWhileRegion region,
        BuilderState state)
    {
        var conditionBlock = cfg[region.ConditionBlock];
        var branch = GetInstructions(tacIr, conditionBlock).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        AstIndex condition;
        var recoveredShortCircuitCondition = false;
        var shortCircuitEntry = BlockIndex.Invalid;

        if (branch.Op is Avm1TacOp.BranchIf)
        {
            if (TryGetDoWhileShortCircuitEntry(cfg, region, out shortCircuitEntry) &&
                TryBuildRegisterShortCircuitCondition(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    shortCircuitEntry,
                    region.ConditionBlock,
                    branch,
                    state,
                    out condition))
            {
                recoveredShortCircuitCondition = true;
            }
            else
            {
                PrepareRegisterStagedExpression(
                    tacIr,
                    registerSsa,
                    inliner,
                    cfg,
                    state,
                    branch.Operand0);
                condition = BuildExpression(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    state.SsaAccessByInstruction,
                    branch.Operand0);
            }

            if (conditionBlock.FirstSuccessor == region.Header)
                condition = InvertCondition(builder, condition);
        }
        else
        {
            condition = builder.AddNode(Avm1AstNodeKind.Literal, startAction: ActionIndex.Invalid);
        }

        var implicitContinueBlock = FindImplicitDoWhileContinueBlock(cfg, region);

        state.ActiveLoops.Push(new ActiveLoop(
            region.ConditionBlock,
            region.Exit,
            implicitContinueBlock,
            region.ConditionBlock,
            PromotedVariable: null,
            IsForIn: false));
        state.ActiveControlScopes.Push(new ActiveControlScope(
            ActiveControlScopeKind.Loop,
            region.Header,
            region.Exit,
            region.ConditionBlock,
            implicitContinueBlock));

        var bodyBlocks = recoveredShortCircuitCondition
            ? region.Blocks
                .Where(block => !region.ConditionPrefixBlocks.Contains(block))
                .ToArray()
            : region.Blocks;
        var body = AddStructuredBlocks(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            cfg,
            bodyBlocks,
            state,
            recoveredShortCircuitCondition
                ? null
                : region.ConditionPrefixBlocks,
            structuralBoundary: recoveredShortCircuitCondition
                ? shortCircuitEntry
                : null);

        state.ActiveControlScopes.Pop();
        state.ActiveLoops.Pop();

        var children = new List<AstIndex>(body.Count + 1) { condition };
        children.AddRange(body);
        var header = cfg[region.Header];

        return builder.AddNode(
            Avm1AstNodeKind.DoWhile,
            region.Header,
            region.Exit,
            header.StartAction,
            conditionBlock.EndAction,
            children);
    }

    private static bool TryGetDoWhileShortCircuitEntry(
        Avm1ControlFlowGraph cfg,
        Avm1DoWhileRegion region,
        out BlockIndex entry)
    {
        entry = BlockIndex.Invalid;
        if (region.ConditionPrefixBlocks.Count == 0)
            return false;

        var conditionBlocks = region.ConditionPrefixBlocks.ToHashSet();
        conditionBlocks.Add(region.ConditionBlock);
        foreach (var candidate in region.ConditionPrefixBlocks)
        {
            if (cfg[candidate].Terminator is not
                    Avm1BlockTerminatorKind.ConditionalBranch ||
                !region.Blocks.Any(predecessor =>
                    !conditionBlocks.Contains(predecessor) &&
                    (cfg[predecessor].FirstSuccessor == candidate ||
                     cfg[predecessor].SecondSuccessor == candidate)))
            {
                continue;
            }

            if (entry.IsValid)
                return false;
            entry = candidate;
        }

        return entry.IsValid;
    }

    private static BlockIndex FindImplicitDoWhileContinueBlock(
        Avm1ControlFlowGraph cfg,
        Avm1DoWhileRegion region)
    {
        for (var index = region.Blocks.Count - 1; index >= 0; index--)
        {
            var blockIndex = region.Blocks[index];
            if (blockIndex == region.ConditionBlock ||
                region.ConditionPrefixBlocks.Contains(blockIndex))
            {
                continue;
            }

            var block = cfg[blockIndex];
            if (block.FirstSuccessor == region.ConditionBlock &&
                !block.SecondSuccessor.IsValid &&
                block.Terminator is Avm1BlockTerminatorKind.FallThrough or
                    Avm1BlockTerminatorKind.Jump)
            {
                return blockIndex;
            }
        }

        return region.ConditionBlock;
    }

    private static List<AstIndex> AddStructuredBlocks(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        IReadOnlyList<BlockIndex> blocks,
        BuilderState state,
        IReadOnlyList<BlockIndex>? structuralOnlyBlocks = null,
        BlockIndex? structuralBoundary = null)
    {
        var result = new List<AstIndex>(blocks.Count);
        var allowed = blocks
            .Where(state.Reachability.IsReachable)
            .ToHashSet();
        if (structuralBoundary is { IsValid: true } boundary)
            allowed.Add(boundary);
        var consumed = new HashSet<BlockIndex>();
        foreach (var block in blocks)
        {
            if (!state.Reachability.IsReachable(block) ||
                consumed.Contains(block) ||
                state.ConsumedLoopControlArmBlocks.Contains(block))
            {
                continue;
            }
            if (IsDeferredWhileBodyBlock(block, state))
                continue;

            if (structuralOnlyBlocks is not null && structuralOnlyBlocks.Contains(block))
            {
                result.Add(AddBasicBlock(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg[block],
                    state,
                    suppressLoopControl: true));
                continue;
            }

            if (state.TryByHeader.TryGetValue(block, out var tryRegion) &&
                CanStructureNestedTry(tryRegion, allowed))
            {
                result.Add(AddTry(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    tryRegion,
                    state));
                AddConsumedTryRegion(consumed, tryRegion);
                continue;
            }

            if (state.WithByHeader.TryGetValue(block, out var withRegion) &&
                allowed.Contains(withRegion.BodyEntry) &&
                allowed.Contains(withRegion.Exit))
            {
                result.Add(AddWith(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    withRegion,
                    state));
                AddConsumedWithRegion(consumed, withRegion);
                continue;
            }

            if (state.DoWhileByHeader.ContainsKey(block) &&
                TryGetForInEnumerationCleanupSuccessor(
                    tacIr,
                    valueAnalysis,
                    cfg[block],
                    out _))
            {
                consumed.Add(block);
                state.ConsumedLoopControlArmBlocks.Add(block);
                continue;
            }

            if (!IsActiveLoopHeader(block, state) &&
                state.DoWhileByHeader.TryGetValue(block, out var doWhileRegion) &&
                CanStructureNestedLoop(
                    doWhileRegion.Header,
                    doWhileRegion.Blocks,
                    doWhileRegion.Exit,
                    allowed))
            {
                result.Add(AddDoWhile(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    doWhileRegion,
                    state));
                AddConsumedNestedLoop(consumed, doWhileRegion.Header, doWhileRegion.Blocks);
                continue;
            }

            if (!IsActiveLoopHeader(block, state) &&
                state.WhileByHeader.TryGetValue(block, out var whileRegion) &&
                CanStructureNestedLoop(
                    whileRegion.Header,
                    whileRegion.Blocks,
                    whileRegion.Exit,
                    allowed))
            {
                var loopNode = AddWhile(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    whileRegion,
                    state);
                RefreshTrailingPreheader(
                    result,
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    state,
                    whileRegion);
                result.Add(loopNode);
                TryAppendBreakAfterNestedForInCleanup(
                    builder,
                    tacIr,
                    valueAnalysis,
                    cfg,
                    whileRegion,
                    loopNode,
                    allowed,
                    state,
                    result);
                AddConsumedNestedLoop(consumed, whileRegion.Header, whileRegion.Blocks);
                continue;
            }

            if (state.SwitchByHeader.TryGetValue(block, out var switchRegion) &&
                CanStructureNestedSwitch(switchRegion, allowed))
            {
                result.Add(AddSwitch(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    switchRegion,
                    state));
                AddConsumedSwitchRegion(consumed, cfg, switchRegion, allowed);
                continue;
            }

            if (state.IfByHeader.TryGetValue(block, out var terminalRegion) &&
                CanStructureNestedIf(
                    cfg,
                    terminalRegion,
                    allowed,
                    state,
                    out var externalTerminalBlocks) &&
                externalTerminalBlocks.Count > 0)
            {
                result.Add(AddIf(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    terminalRegion,
                    state));
                AddConsumedIfRegion(consumed, cfg, terminalRegion, allowed);
                foreach (var terminalBlock in externalTerminalBlocks)
                    state.ConsumedLoopControlArmBlocks.Add(terminalBlock);
                continue;
            }

            if (TryBuildConditionalLoopTerminalArm(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                cfg[block],
                allowed,
                state,
                out var loopTerminalIf))
            {
                result.Add(loopTerminalIf);
                continue;
            }

            if (TryBuildConditionalLoopBreakArm(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                cfg[block],
                state,
                out var loopBreakIf))
            {
                result.Add(loopBreakIf);
                continue;
            }

            if (state.IfByHeader.TryGetValue(block, out var latchMergedRegion) &&
                !state.SwitchByHeader.ContainsKey(block) &&
                latchMergedRegion.HasElse &&
                HasVisibleRootBeforeMerge(
                    tacIr,
                    cfg,
                    inliner,
                    state,
                    latchMergedRegion.FallThroughEntry,
                    latchMergedRegion.Merge) &&
                HasVisibleRootBeforeMerge(
                    tacIr,
                    cfg,
                    inliner,
                    state,
                    latchMergedRegion.BranchEntry,
                    latchMergedRegion.Merge) &&
                CanStructureNestedIf(
                    cfg,
                    latchMergedRegion,
                    allowed,
                    state,
                    out var latchMergedExternalTerminalBlocks))
            {
                result.Add(AddIf(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    latchMergedRegion,
                    state));
                AddConsumedIfRegion(consumed, cfg, latchMergedRegion, allowed);
                foreach (var terminalBlock in latchMergedExternalTerminalBlocks)
                    state.ConsumedLoopControlArmBlocks.Add(terminalBlock);
                continue;
            }

            if (TryBuildConditionalLoopContinueArm(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                cfg[block],
                state,
                out var loopContinueIf,
                out _))
            {
                result.Add(loopContinueIf);
                continue;
            }

            if (state.IfByHeader.TryGetValue(block, out var region) &&
                CanStructureNestedIf(
                    cfg,
                    region,
                    allowed,
                    state,
                    out var regularExternalTerminalBlocks))
            {
                result.Add(AddIf(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    region,
                    state));
                AddConsumedIfRegion(consumed, cfg, region, allowed);
                foreach (var terminalBlock in regularExternalTerminalBlocks)
                    state.ConsumedLoopControlArmBlocks.Add(terminalBlock);
                continue;
            }

            result.Add(AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg[block],
                state));
        }

        return result;
    }

    private static bool TryAppendBreakAfterNestedForInCleanup(
        Avm1AstArena.Builder builder,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion nestedLoop,
        AstIndex nestedLoopNode,
        HashSet<BlockIndex> allowed,
        BuilderState state,
        List<AstIndex> statements)
    {
        if (!IsStructuredNodeKind(builder, nestedLoopNode, Avm1AstNodeKind.ForIn) ||
            !nestedLoop.Exit.IsValid ||
            !state.ActiveLoops.TryPeek(out var outerLoop) ||
            !TryGetForInEnumerationCleanupSuccessor(
                tacIr,
                valueAnalysis,
                cfg[nestedLoop.Exit],
                out var continuation) ||
            continuation != outerLoop.Exit)
        {
            return false;
        }

        var labelTarget = OwnsInnermostBreak(
            state,
            ActiveControlScopeKind.Loop,
            outerLoop.Exit)
            ? BlockIndex.Invalid
            : outerLoop.ContinueTarget;
        statements.Add(builder.AddNode(Avm1AstNodeKind.Break, block: labelTarget));

        var cleanupTargets = new HashSet<BlockIndex> { nestedLoop.Exit };
        state.ConsumedLoopControlArmBlocks.Add(nestedLoop.Exit);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in allowed)
            {
                if (cleanupTargets.Contains(candidate) ||
                    nestedLoop.Blocks.Contains(candidate))
                {
                    continue;
                }

                var block = cfg[candidate];
                if (block.Terminator is not Avm1BlockTerminatorKind.Jump ||
                    block.EndAction.Value != block.StartAction.Value + 1 ||
                    !cleanupTargets.Contains(block.FirstSuccessor) ||
                    block.SecondSuccessor.IsValid)
                {
                    continue;
                }

                cleanupTargets.Add(candidate);
                state.ConsumedLoopControlArmBlocks.Add(candidate);
                changed = true;
            }
        }

        return true;
    }

    private static bool IsStructuredNodeKind(
        Avm1AstArena.Builder builder,
        AstIndex nodeIndex,
        Avm1AstNodeKind expectedKind)
    {
        var node = builder.GetNode(nodeIndex);
        if (node.Kind == expectedKind)
            return true;
        if (node.Kind is not Avm1AstNodeKind.Block || node.Children.Count == 0)
            return false;

        return IsStructuredNodeKind(
            builder,
            builder.GetChild(node, node.Children.Count - 1),
            expectedKind);
    }

    private static bool CanStructureNestedTry(
        StructuredTryRegion region,
        HashSet<BlockIndex> allowed) =>
        allowed.Contains(region.Header);

    private static bool CanStructureNestedLoop(
        BlockIndex header,
        IReadOnlyList<BlockIndex> blocks,
        BlockIndex exit,
        HashSet<BlockIndex> allowed)
    {
        if (!allowed.Contains(header) || (exit.IsValid && !allowed.Contains(exit)))
            return false;

        for (var i = 0; i < blocks.Count; i++)
            if (!allowed.Contains(blocks[i]))
                return false;
        return true;
    }

    private static bool IsActiveLoopHeader(BlockIndex block, BuilderState state) =>
        state.ActiveControlScopes.Any(scope =>
            scope.Kind is ActiveControlScopeKind.Loop && scope.Header == block);

    private static void AddConsumedNestedLoop(
        HashSet<BlockIndex> consumed,
        BlockIndex header,
        IReadOnlyList<BlockIndex> blocks)
    {
        consumed.Add(header);
        for (var i = 0; i < blocks.Count; i++)
            consumed.Add(blocks[i]);
    }

    private static bool CanStructureNestedSwitch(
        Avm1SwitchRegion region,
        HashSet<BlockIndex> allowed)
    {
        return allowed.Contains(region.Header) &&
            (!region.Merge.IsValid || allowed.Contains(region.Merge)) &&
            region.Cases.All(@case => allowed.Contains(@case.BodyEntry)) &&
            (region.DefaultEntry == region.Merge || allowed.Contains(region.DefaultEntry));
    }

    private static bool CanStructureNestedIf(
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region,
        HashSet<BlockIndex> allowed,
        BuilderState state,
        out List<BlockIndex> externalTerminalBlocks)
    {
        externalTerminalBlocks = [];
        if (!allowed.Contains(region.Header) ||
            TargetsActiveLoopControl(region, state))
        {
            return false;
        }

        if (!region.Merge.IsValid)
        {
            return region.HasElse &&
                TerminatesWithinAllowed(
                    cfg,
                    region.FallThroughEntry,
                    allowed) &&
                TerminatesWithinAllowed(
                    cfg,
                    region.BranchEntry,
                    allowed);
        }
        if (!allowed.Contains(region.Merge))
            return false;

        return CanIncludeNestedIfEntry(
                cfg,
                region.FallThroughEntry,
                region.Merge,
                allowed,
                externalTerminalBlocks) &&
            CanIncludeNestedIfEntry(
                cfg,
                region.BranchEntry,
                region.Merge,
                allowed,
                externalTerminalBlocks);
    }

    private static bool TerminatesWithinAllowed(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        HashSet<BlockIndex> allowed)
    {
        var states = new byte[cfg.Count];
        return Visit(entry);

        bool Visit(BlockIndex block)
        {
            if (!block.IsValid || !allowed.Contains(block))
                return false;

            ref var visitState = ref states[block.Value];
            if (visitState == 1)
                return false;
            if (visitState >= 2)
                return visitState == 2;

            visitState = 1;
            var node = cfg[block];
            var terminates = node.Terminator switch
            {
                Avm1BlockTerminatorKind.Return or
                    Avm1BlockTerminatorKind.Throw => true,
                Avm1BlockTerminatorKind.FallThrough or
                    Avm1BlockTerminatorKind.Jump => Visit(node.FirstSuccessor),
                Avm1BlockTerminatorKind.ConditionalBranch =>
                    Visit(node.FirstSuccessor) && Visit(node.SecondSuccessor),
                _ => false
            };
            visitState = terminates ? (byte)2 : (byte)3;
            return terminates;
        }
    }

    private static bool CanIncludeNestedIfEntry(
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        BlockIndex merge,
        HashSet<BlockIndex> allowed,
        List<BlockIndex> externalTerminalBlocks)
    {
        if (entry == merge || allowed.Contains(entry))
            return true;

        var terminalPath = new List<BlockIndex>();
        if (!TryCollectLinearTerminalPath(cfg, entry, allowed, terminalPath))
            return false;

        externalTerminalBlocks.AddRange(terminalPath);
        return true;
    }

    private static bool TryCollectLinearTerminalPath(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        HashSet<BlockIndex> forbidden,
        List<BlockIndex> path)
    {
        var visited = new HashSet<BlockIndex>();
        var current = start;
        while (current.IsValid && !forbidden.Contains(current) && visited.Add(current))
        {
            path.Add(current);
            var block = cfg[current];
            if (block.Terminator is Avm1BlockTerminatorKind.Return or
                Avm1BlockTerminatorKind.Throw)
            {
                return true;
            }

            if (block.Terminator is not (Avm1BlockTerminatorKind.FallThrough or
                Avm1BlockTerminatorKind.Jump))
            {
                return false;
            }

            current = block.FirstSuccessor;
        }

        return false;
    }

    private static bool TryBuildConditionalLoopTerminalArm(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        HashSet<BlockIndex> loopBlocks,
        BuilderState state,
        out AstIndex terminalIf)
    {
        terminalIf = AstIndex.Invalid;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            inliner.HasConditionalValueForHeader(block.Index) ||
            !state.ActiveLoops.TryPeek(out var loop) ||
            block.Index == loop.ConditionBlock ||
            !state.IfByHeader.TryGetValue(block.Index, out var region) ||
            region.HasElse ||
            !region.Merge.IsValid ||
            (region.Merge != loop.ImplicitContinueBlock &&
                region.Merge != loop.ContinueTarget))
        {
            return false;
        }

        var fallThroughIsMerge = region.FallThroughEntry == region.Merge;
        var branchIsMerge = region.BranchEntry == region.Merge;
        if (fallThroughIsMerge == branchIsMerge)
            return false;

        var terminalEntry = fallThroughIsMerge
            ? region.BranchEntry
            : region.FallThroughEntry;
        if (!terminalEntry.IsValid ||
            terminalEntry == loop.Exit ||
            loopBlocks.Contains(terminalEntry))
        {
            return false;
        }

        var semanticEntry = terminalEntry;
        if (loop.IsForIn &&
            TryGetForInEnumerationCleanupSuccessor(
                tacIr,
                valueAnalysis,
                cfg[terminalEntry],
                out var cleanupContinuation))
        {
            semanticEntry = cleanupContinuation;
        }

        if (!semanticEntry.IsValid ||
            semanticEntry == loop.Exit ||
            (loop.Exit.IsValid && CanReachBlock(cfg, semanticEntry, loop.Exit)) ||
            !AlwaysTerminatesBefore(cfg, semanticEntry, region.Merge))
        {
            return false;
        }

        terminalIf = AddIf(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            cfg,
            region,
            state);
        MarkLoopTerminalArmBlocksConsumed(cfg, terminalEntry, region.Merge, state);
        return true;
    }

    private static void MarkLoopTerminalArmBlocksConsumed(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex boundary,
        BuilderState state)
    {
        var visited = state.ReachabilityScratch;
        var pending = state.TraversalStack;
        var pendingCount = 0;
        Add(start);
        while (pendingCount > 0)
        {
            var block = pending[--pendingCount];
            state.ConsumedLoopControlArmBlocks.Add(block);
            var node = cfg[block];
            Add(node.FirstSuccessor);
            Add(node.SecondSuccessor);
        }

        Array.Clear(visited);

        void Add(BlockIndex block)
        {
            if (!block.IsValid || block == boundary || visited[block.Value] != 0)
                return;

            visited[block.Value] = 1;
            pending[pendingCount++] = block;
        }
    }

    private static bool TargetsActiveLoopControl(Avm1IfRegion region, BuilderState state)
    {
        return state.ActiveLoops.Any(loop =>
            region.FallThroughEntry == loop.ContinueTarget ||
            region.BranchEntry == loop.ContinueTarget ||
            region.FallThroughEntry == loop.Exit ||
            region.BranchEntry == loop.Exit);
    }

    private static bool TryPrepareForPromotion(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region,
        AstIndex condExpr,
        BuilderState state,
        out IReadOnlyList<AstIndex> initializerStatements,
        out IReadOnlyList<AstIndex> updateStatements,
        out LoopVariableKey variable)
    {
        initializerStatements = [];
        updateStatements = [];
        variable = default;

        if (!TryGetLoopPreheader(cfg, region, out var prevBlock) ||
            !TryGetLoopLatch(cfg, region, out var lastBlock))
            return false;
        Avm1TacInstruction initInst;
        Avm1TacInstruction updateInst;
        if (TryGetLoopVariable(builder, valueAnalysis, condExpr, out variable) &&
            FindLastAssignmentToVariable(
                tacIr,
                valueAnalysis,
                prevBlock,
                inliner,
                variable) is { } directInitializer &&
            FindLastAssignmentToVariable(
                tacIr,
                valueAnalysis,
                lastBlock,
                inliner,
                variable) is { } directUpdate)
        {
            initInst = directInitializer;
            updateInst = directUpdate;
        }
        else if (!TryFindPromotableLoopVariable(
                     builder,
                     valueAnalysis,
                     tacIr,
                     inliner,
                     condExpr,
                     prevBlock,
                     lastBlock,
                     out variable,
                     out initInst,
                     out updateInst))
        {
            return false;
        }

        if (!TryCollectForClauseInstructions(
                tacIr,
                valueAnalysis,
                inliner,
                prevBlock,
                lastBlock,
                state,
                initInst,
                updateInst,
                out var initializerInstructions,
                out var updateInstructions))
        {
            return false;
        }

        var projectedInitializers = new AstIndex[initializerInstructions.Count];
        for (var i = 0; i < projectedInitializers.Length; i++)
        {
            projectedInitializers[i] = BuildStatementFromInst(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                initializerInstructions[i]);
            if (!projectedInitializers[i].IsValid)
                return false;
        }

        var projectedUpdates = new AstIndex[updateInstructions.Count];
        for (var i = 0; i < projectedUpdates.Length; i++)
        {
            projectedUpdates[i] = BuildStatementFromInst(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                updateInstructions[i]);
            if (!projectedUpdates[i].IsValid)
                return false;
        }

        foreach (var instruction in initializerInstructions)
            state.HiddenInstructions.Add(instruction.Index);
        foreach (var instruction in updateInstructions)
            state.HiddenInstructions.Add(instruction.Index);

        initializerStatements = projectedInitializers;
        updateStatements = projectedUpdates;
        return true;
    }

    private static bool TryCollectForClauseInstructions(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock preheader,
        Avm1BasicBlock latch,
        BuilderState state,
        Avm1TacInstruction primaryInitializer,
        Avm1TacInstruction primaryUpdate,
        out IReadOnlyList<Avm1TacInstruction> initializerInstructions,
        out IReadOnlyList<Avm1TacInstruction> updateInstructions)
    {
        initializerInstructions = [];
        updateInstructions = [];

        var preheaderRoots = GetInstructions(tacIr, preheader)
            .Where(instruction =>
                !state.HiddenInstructions.Contains(instruction.Index) &&
                inliner.IsRootInstruction(instruction))
            .ToArray();
        var latchRoots = GetInstructions(tacIr, latch)
            .Where(instruction =>
                !state.HiddenInstructions.Contains(instruction.Index) &&
                inliner.IsRootInstruction(instruction))
            .ToArray();

        var primaryUpdatePosition = Array.FindIndex(
            latchRoots,
            instruction => instruction.Index == primaryUpdate.Index);
        if (primaryUpdatePosition < 0)
            return false;

        var updateRunStart = latchRoots.Length;
        for (var i = latchRoots.Length - 1; i >= 0; i--)
        {
            if (latchRoots[i].Op is not (Avm1TacOp.StoreRegister or Avm1TacOp.SetVariable) ||
                !TryGetAssignedLoopVariable(latchRoots[i], valueAnalysis, out _))
            {
                break;
            }
            updateRunStart = i;
        }
        if (primaryUpdatePosition < updateRunStart)
            return false;

        for (var candidateStart = updateRunStart;
             candidateStart <= primaryUpdatePosition;
             candidateStart++)
        {
            var candidateCount = latchRoots.Length - candidateStart;
            if (candidateCount == 0 || preheaderRoots.Length < candidateCount)
                continue;

            var candidateUpdates = latchRoots[candidateStart..];
            var candidateInitializers = preheaderRoots[^candidateCount..];
            if (!candidateUpdates.Any(instruction => instruction.Index == primaryUpdate.Index) ||
                !candidateInitializers.Any(instruction => instruction.Index == primaryInitializer.Index) ||
                candidateInitializers.Any(instruction => instruction.Op != primaryInitializer.Op))
            {
                continue;
            }

            if (!TryGetUniqueAssignedVariables(
                    candidateUpdates,
                    valueAnalysis,
                    out var updatedVariables) ||
                !TryGetUniqueAssignedVariables(
                    candidateInitializers,
                    valueAnalysis,
                    out var initializedVariables) ||
                !updatedVariables.SetEquals(initializedVariables))
            {
                continue;
            }

            initializerInstructions = candidateInitializers;
            updateInstructions = candidateUpdates;
            return true;
        }

        return TryCollectSingleForClauseInstructions(
            preheaderRoots,
            latchRoots,
            valueAnalysis,
            primaryInitializer,
            primaryUpdate,
            out initializerInstructions,
            out updateInstructions);
    }

    private static bool TryCollectSingleForClauseInstructions(
        Avm1TacInstruction[] preheaderRoots,
        Avm1TacInstruction[] latchRoots,
        Avm1ValueAnalysis valueAnalysis,
        Avm1TacInstruction primaryInitializer,
        Avm1TacInstruction primaryUpdate,
        out IReadOnlyList<Avm1TacInstruction> initializerInstructions,
        out IReadOnlyList<Avm1TacInstruction> updateInstructions)
    {
        initializerInstructions = [];
        updateInstructions = [];

        var initializerPosition = FindInstruction(preheaderRoots, primaryInitializer.Index);
        var updatePosition = FindInstruction(latchRoots, primaryUpdate.Index);
        if (initializerPosition < 0 || updatePosition < 0 ||
            !TryGetAssignedLoopVariable(primaryInitializer, valueAnalysis, out var primaryVariable))
        {
            return false;
        }

        for (var i = initializerPosition + 1; i < preheaderRoots.Length; i++)
        {
            var instruction = preheaderRoots[i];
            if (!TryGetAssignedLoopVariable(instruction, valueAnalysis, out var assignedVariable) ||
                assignedVariable == primaryVariable)
            {
                return false;
            }
        }

        initializerInstructions = [primaryInitializer];
        updateInstructions = [primaryUpdate];
        return true;
    }

    private static int FindInstruction(
        Avm1TacInstruction[] instructions,
        IrIndex index)
    {
        for (var i = 0; i < instructions.Length; i++)
        {
            if (instructions[i].Index == index)
                return i;
        }
        return -1;
    }

    private static bool TryGetUniqueAssignedVariables(
        IReadOnlyList<Avm1TacInstruction> instructions,
        Avm1ValueAnalysis valueAnalysis,
        out HashSet<LoopVariableKey> variables)
    {
        variables = [];
        foreach (var instruction in instructions)
        {
            if (!TryGetAssignedLoopVariable(instruction, valueAnalysis, out var variable) ||
                !variables.Add(variable))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetAssignedLoopVariable(
        Avm1TacInstruction instruction,
        Avm1ValueAnalysis valueAnalysis,
        out LoopVariableKey variable)
    {
        if (instruction.Op is Avm1TacOp.StoreRegister)
        {
            variable = new LoopVariableKey(
                Avm1AstNodeKind.Register,
                instruction.IntOperand,
                null);
            return true;
        }

        if (instruction.Op is Avm1TacOp.SetVariable or Avm1TacOp.DefineLocal)
        {
            var name = valueAnalysis[instruction.Operand0];
            if (name.ConstantKind is Avm1ConstantKind.String && name.StringValue is not null)
            {
                variable = new LoopVariableKey(
                    Avm1AstNodeKind.Variable,
                    -1,
                    name.StringValue);
                return true;
            }
        }

        variable = default;
        return false;
    }

    private static bool TryFindPromotableLoopVariable(
        Avm1AstArena.Builder builder,
        Avm1ValueAnalysis valueAnalysis,
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        AstIndex condition,
        Avm1BasicBlock preheader,
        Avm1BasicBlock latch,
        out LoopVariableKey variable,
        out Avm1TacInstruction initializer,
        out Avm1TacInstruction update)
    {
        var selectedVariable = default(LoopVariableKey);
        var selectedInitializer = default(Avm1TacInstruction);
        var selectedUpdate = default(Avm1TacInstruction);
        var found = false;

        var complete = Visit(condition, depth: 0);
        variable = selectedVariable;
        initializer = selectedInitializer;
        update = selectedUpdate;
        return complete && found;

        bool Visit(AstIndex expression, int depth)
        {
            if (!expression.IsValid || depth > 128)
                return false;

            if (TryGetDirectLoopVariable(builder, valueAnalysis, expression, out var candidate) &&
                !Consider(candidate))
            {
                return false;
            }

            var node = builder.GetNode(expression);
            for (var childIndex = 0; childIndex < node.Children.Count; childIndex++)
            {
                if (!Visit(builder.GetChild(node, childIndex), depth + 1))
                    return false;
            }

            return true;
        }

        bool Consider(LoopVariableKey candidate)
        {
            if (found && candidate == selectedVariable)
                return true;

            var candidateInitializer = FindTrailingAssignmentToVariable(
                tacIr,
                valueAnalysis,
                preheader,
                inliner,
                candidate);
            if (candidateInitializer is null)
                return true;

            var candidateUpdate = FindTrailingAssignmentToVariable(
                tacIr,
                valueAnalysis,
                latch,
                inliner,
                candidate);
            if (candidateUpdate is null)
                return true;

            if (found)
                return false;

            selectedVariable = candidate;
            selectedInitializer = candidateInitializer.Value;
            selectedUpdate = candidateUpdate.Value;
            found = true;
            return true;
        }
    }

    private static bool TryPrepareForShortCircuitPromotion(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1WhileRegion region,
        AstIndex condExpr,
        BuilderState state,
        out AstIndex initStmt,
        out AstIndex updateStmt)
    {
        initStmt = AstIndex.Invalid;
        updateStmt = AstIndex.Invalid;

        int headerIdx = region.Header.Value;
        if (headerIdx <= 0 || region.BodyBlocks.Count == 0)
            return false;

        var prevBlock = cfg[new BlockIndex(headerIdx - 1)];
        var lastBlock = cfg[region.BodyBlocks[^1]];
        return TryPrepareForShortCircuitPromotionFromCondition(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            prevBlock,
            lastBlock,
            condExpr,
            state,
            depth: 0,
            out initStmt,
            out updateStmt);
    }

    private static bool TryPrepareForShortCircuitPromotionFromCondition(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock preheader,
        Avm1BasicBlock latch,
        AstIndex condition,
        BuilderState state,
        int depth,
        out AstIndex initStmt,
        out AstIndex updateStmt)
    {
        initStmt = AstIndex.Invalid;
        updateStmt = AstIndex.Invalid;
        if (depth > 16 || !condition.IsValid)
            return false;

        var node = builder.GetNode(condition);
        if (node.Kind == Avm1AstNodeKind.Unary && node.StartAction.Value == 1) // logical not '!'
        {
            return TryPrepareForShortCircuitPromotionFromCondition(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                preheader,
                latch,
                builder.GetChild(node, 0),
                state,
                depth + 1,
                out initStmt,
                out updateStmt);
        }

        if (TryGetDirectLoopVariable(builder, valueAnalysis, condition, out var directVariable) &&
            TryPrepareForShortCircuitPromotionForVariable(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                preheader,
                latch,
                directVariable,
                state,
                out initStmt,
                out updateStmt))
        {
            return true;
        }

        if (node.Kind is not Avm1AstNodeKind.Binary)
            return false;

        var left = builder.GetChild(node, 0);
        var right = builder.GetChild(node, 1);
        var opcode = (ActionOpcode)node.StartAction.Value;
        if (opcode is ActionOpcode.And or ActionOpcode.Or)
        {
            return TryPrepareForShortCircuitPromotionFromCondition(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    preheader,
                    latch,
                    left,
                    state,
                    depth + 1,
                    out initStmt,
                    out updateStmt) ||
                TryPrepareForShortCircuitPromotionFromCondition(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    preheader,
                    latch,
                    right,
                    state,
                    depth + 1,
                    out initStmt,
                    out updateStmt);
        }

        return (TryGetDirectLoopVariable(builder, valueAnalysis, left, out var leftVariable) &&
                TryPrepareForShortCircuitPromotionForVariable(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    preheader,
                    latch,
                    leftVariable,
                    state,
                    out initStmt,
                    out updateStmt)) ||
            (TryGetDirectLoopVariable(builder, valueAnalysis, right, out var rightVariable) &&
                TryPrepareForShortCircuitPromotionForVariable(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    preheader,
                    latch,
                    rightVariable,
                    state,
                    out initStmt,
                    out updateStmt));
    }

    private static bool TryPrepareForShortCircuitPromotionForVariable(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock preheader,
        Avm1BasicBlock latch,
        LoopVariableKey variable,
        BuilderState state,
        out AstIndex initStmt,
        out AstIndex updateStmt)
    {
        initStmt = AstIndex.Invalid;
        updateStmt = AstIndex.Invalid;

        var initInst = FindTrailingAssignmentToVariable(
            tacIr,
            valueAnalysis,
            preheader,
            inliner,
            variable);
        if (initInst is null)
            return false;

        var updateInst = FindTrailingAssignmentToVariable(
            tacIr,
            valueAnalysis,
            latch,
            inliner,
            variable);
        if (updateInst is null)
            return false;

        initStmt = BuildStatementFromInst(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            initInst.Value);
        updateStmt = BuildStatementFromInst(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            updateInst.Value);
        if (!initStmt.IsValid || !updateStmt.IsValid)
            return false;

        state.HiddenInstructions.Add(initInst.Value.Index);
        state.HiddenInstructions.Add(updateInst.Value.Index);
        return true;
    }

    private static bool TryGetDirectLoopVariable(
        Avm1AstArena.Builder builder,
        Avm1ValueAnalysis valueAnalysis,
        AstIndex expression,
        out LoopVariableKey variable)
    {
        variable = default;
        var node = builder.GetNode(expression);
        if (node.Kind is Avm1AstNodeKind.Register)
        {
            variable = new LoopVariableKey(Avm1AstNodeKind.Register, node.Block.Value, null);
            return true;
        }

        if (node.Kind is Avm1AstNodeKind.Variable)
        {
            var nameIndex = builder.GetChild(node, 0);
            var nameNode = builder.GetNode(nameIndex);
            if (nameNode.Kind == Avm1AstNodeKind.Literal)
            {
                var nameFact = valueAnalysis[new ValueIndex(nameNode.StartAction.Value)];
                if (nameFact.ConstantKind is Avm1ConstantKind.String && nameFact.StringValue is not null)
                {
                    variable = new LoopVariableKey(Avm1AstNodeKind.Variable, -1, nameFact.StringValue);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetLoopVariable(
        Avm1AstArena.Builder builder,
        Avm1ValueAnalysis valueAnalysis,
        AstIndex condExpr,
        out LoopVariableKey variable)
    {
        variable = default;

        var node = builder.GetNode(condExpr);
        if (node.Kind == Avm1AstNodeKind.Unary && node.StartAction.Value == 1) // logical not '!'
        {
            return TryGetLoopVariable(builder, valueAnalysis, builder.GetChild(node, 0), out variable);
        }

        if (node.Kind != Avm1AstNodeKind.Binary)
            return false;

        var leftIndex = builder.GetChild(node, 0);
        var leftNode = builder.GetNode(leftIndex);

        if (leftNode.Kind == Avm1AstNodeKind.Register)
        {
            variable = new LoopVariableKey(Avm1AstNodeKind.Register, leftNode.Block.Value, null);
            return true;
        }

        if (leftNode.Kind == Avm1AstNodeKind.Variable)
        {
            var nameIndex = builder.GetChild(leftNode, 0);
            var nameNode = builder.GetNode(nameIndex);
            if (nameNode.Kind == Avm1AstNodeKind.Literal)
            {
                var nameFact = valueAnalysis[new ValueIndex(nameNode.StartAction.Value)];
                if (nameFact.ConstantKind is Avm1ConstantKind.String && nameFact.StringValue is not null)
                {
                    variable = new LoopVariableKey(Avm1AstNodeKind.Variable, -1, nameFact.StringValue);
                    return true;
                }
            }
        }

        return false;
    }

    private static Avm1TacInstruction? FindLastAssignmentToVariable(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1BasicBlock block,
        Avm1ExpressionInliner inliner,
        LoopVariableKey variable)
    {
        for (var index = tacIr.Count - 1; index >= 0; index--)
        {
            var inst = tacIr.Instructions[index];
            if (inst.Action.Value < block.StartAction.Value ||
                inst.Action.Value >= block.EndAction.Value)
            {
                continue;
            }

            if (!inliner.IsRootInstruction(inst))
                continue;

            if (TryGetAssignedLoopVariable(inst, valueAnalysis, out var assignedVariable) &&
                assignedVariable == variable)
                return inst;
        }
        return null;
    }

    private static Avm1TacInstruction? FindTrailingAssignmentToVariable(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1BasicBlock block,
        Avm1ExpressionInliner inliner,
        LoopVariableKey variable)
    {
        for (var index = tacIr.Count - 1; index >= 0; index--)
        {
            var inst = tacIr.Instructions[index];
            if (inst.Action.Value < block.StartAction.Value ||
                inst.Action.Value >= block.EndAction.Value)
            {
                continue;
            }

            if (!inliner.IsRootInstruction(inst))
                continue;

            if (TryGetAssignedLoopVariable(inst, valueAnalysis, out var assignedVariable) &&
                assignedVariable == variable)
                return inst;

            return null;
        }
        return null;
    }

    private static AstIndex BuildStatementFromInst(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction inst)
    {
        switch (inst.Op)
        {
            case Avm1TacOp.StoreRegister:
                return BuildRegisterAssignment(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    ssaAccessByInstruction,
                    inst);
            case Avm1TacOp.SetVariable:
                return BuildVariableAssignment(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    ssaAccessByInstruction,
                    inst);
            case Avm1TacOp.DefineLocal:
                return BuildVariableDeclaration(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    ssaAccessByInstruction,
                    inst);
            default:
                return AstIndex.Invalid;
        }
    }

    private static AstIndex BuildVariableDeclaration(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction instruction)
    {
        var name = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            instruction.Operand0);
        if (!instruction.Operand1.IsValid)
            return builder.AddNode(Avm1AstNodeKind.DeclareVariable, children: [name]);

        var value = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            instruction.Operand1);
        return builder.AddNode(Avm1AstNodeKind.DeclareVariable, children: [name, value]);
    }

    private static void FindCompoundVariableAssignmentReads(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        BuilderState state)
    {
        foreach (var instruction in tacIr.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.SetVariable ||
                !TryGetCompoundBinary(inliner, instruction.Operand1, out var binary) ||
                !TryGetSemanticDefinition(inliner, binary.Operand0, out var left) ||
                left.Op is not Avm1TacOp.GetVariable ||
                inliner.GetUseCount(left.Result) != 1 ||
                !AreEquivalentLValueValues(
                    valueAnalysis,
                    inliner,
                    state.SsaAccessByInstruction,
                    instruction.Operand0,
                    left.Operand0))
            {
                continue;
            }

            inliner.TryInlineForStructuredConsumer(left.Result);
        }
    }

    private static AstIndex BuildRegisterAssignment(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction instruction)
    {
        var kind = Avm1AstNodeKind.AssignRegister;
        var value = instruction.Operand0;
        var opcode = ActionOpcode.End;
        if (TryGetCompoundBinary(inliner, value, out var binary) &&
            TryGetSemanticDefinition(inliner, binary.Operand0, out var left) &&
            left.Op is Avm1TacOp.LoadRegister &&
            left.IntOperand == instruction.IntOperand)
        {
            kind = Avm1AstNodeKind.CompoundAssignRegister;
            value = binary.Operand1;
            opcode = binary.Opcode;
        }

        var isTransportSource = inliner.TryGetRegisterValueTransport(
            instruction.Operand0,
            out var transport) &&
            transport.Assignment.Index == instruction.Index;
        var valueExpression = isTransportSource
            ? BuildDirectExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                value)
            : BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                value);
        var version = ssaAccessByInstruction.TryGetValue(instruction.Index, out var access)
            ? access.Version
            : 0;

        return builder.AddNode(
            kind,
            block: new BlockIndex(instruction.IntOperand),
            merge: new BlockIndex(version),
            startAction: new ActionIndex((int)opcode),
            children: [valueExpression]);
    }

    private static AstIndex BuildVariableAssignment(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction instruction,
        ValueIndex? transportedValue = null)
    {
        var kind = Avm1AstNodeKind.AssignVariable;
        var value = transportedValue ?? instruction.Operand1;
        var opcode = ActionOpcode.End;
        if (TryGetCompoundBinary(inliner, value, out var binary) &&
            TryGetSemanticDefinition(inliner, binary.Operand0, out var left) &&
            left.Op is Avm1TacOp.GetVariable &&
            AreEquivalentLValueValues(
                valueAnalysis,
                inliner,
                ssaAccessByInstruction,
                instruction.Operand0,
                left.Operand0))
        {
            kind = Avm1AstNodeKind.CompoundAssignVariable;
            value = binary.Operand1;
            opcode = binary.Opcode;
        }

        var nameExpression = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            instruction.Operand0);
        var valueExpression = transportedValue.HasValue
            ? BuildDirectExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                value)
            : BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                value);

        return builder.AddNode(
            kind,
            startAction: new ActionIndex((int)opcode),
            children: [nameExpression, valueExpression]);
    }

    private static AstIndex BuildMemberAssignment(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        Avm1TacInstruction instruction,
        bool isNestedAssignment = false)
    {
        if (TryBuildMemberUpdate(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state,
            instruction,
            isValueConsumed: isNestedAssignment,
            out var memberUpdate))
        {
            return isNestedAssignment
                ? memberUpdate
                : builder.AddNode(
                    Avm1AstNodeKind.ExpressionStatement,
                    children: [memberUpdate]);
        }

        var nestedValue = AstIndex.Invalid;
        var hasNestedAssignment = state.ChainedMemberAssignments.TryGetValue(
            instruction.Index,
            out var nestedInstruction);
        if (hasNestedAssignment)
        {
            nestedValue = BuildMemberAssignment(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state,
                nestedInstruction,
                isNestedAssignment: true);
        }

        var nestedRegisterInstruction = default(Avm1TacInstruction);
        var hasNestedRegisterAssignment = !hasNestedAssignment &&
            state.ChainedRegisterAssignments.TryGetValue(
                instruction.Index,
                out nestedRegisterInstruction);
        if (hasNestedRegisterAssignment)
        {
            nestedValue = BuildRegisterAssignment(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                nestedRegisterInstruction);
        }

        var kind = Avm1AstNodeKind.AssignMember;
        var value = instruction.Operand2;
        var opcode = ActionOpcode.End;
        if (!nestedValue.IsValid &&
            TryGetCompoundBinary(inliner, value, out var binary) &&
            TryGetSemanticDefinition(inliner, binary.Operand0, out var left) &&
            left.Op is Avm1TacOp.GetMember &&
            AreEquivalentLValueValues(
                valueAnalysis,
                inliner,
                state.SsaAccessByInstruction,
                instruction.Operand0,
                left.Operand0) &&
            AreEquivalentLValueValues(
                valueAnalysis,
                inliner,
                state.SsaAccessByInstruction,
                instruction.Operand1,
                left.Operand1))
        {
            kind = Avm1AstNodeKind.CompoundAssignMember;
            value = binary.Operand1;
            opcode = binary.Opcode;
        }

        var targetExpression = isNestedAssignment || hasNestedAssignment || hasNestedRegisterAssignment
            ? BuildDirectExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                instruction.Operand0)
            : BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                instruction.Operand0);
        var memberExpression = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            instruction.Operand1);
        var valueExpression = nestedValue.IsValid
            ? nestedValue
            : BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                value);
        var nameFact = valueAnalysis[instruction.Operand1];
        var isComputed = nameFact.ConstantKind is not Avm1ConstantKind.String ||
            nameFact.StringValue is null ||
            !IsIdentifier(nameFact.StringValue);

        return builder.AddNode(
            kind,
            merge: new BlockIndex(isComputed ? 1 : 0),
            startAction: new ActionIndex((int)opcode),
            endAction: instruction.Action,
            children: [targetExpression, memberExpression, valueExpression]);
    }

    private static bool TryBuildMemberUpdate(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        Avm1TacInstruction assignment,
        bool isValueConsumed,
        out AstIndex memberUpdate)
    {
        memberUpdate = AstIndex.Invalid;
        if (!TryGetSemanticDefinition(inliner, assignment.Operand2, out var update) ||
            update.Op is not Avm1TacOp.Unary ||
            update.Opcode is not (ActionOpcode.Increment or ActionOpcode.Decrement) ||
            !TryGetSemanticDefinition(inliner, update.Operand0, out var read) ||
            read.Op is not Avm1TacOp.GetMember ||
            !AreInSameBlock(state.ControlFlowGraph, read, assignment) ||
            !AreInSameBlock(state.ControlFlowGraph, update, assignment) ||
            HasMemberUpdateBarrier(tacIr, inliner, assignment, read) ||
            !AreEquivalentLValueValues(
                valueAnalysis,
                inliner,
                state.SsaAccessByInstruction,
                assignment.Operand0,
                read.Operand0) ||
            !AreEquivalentLValueValues(
                valueAnalysis,
                inliner,
                state.SsaAccessByInstruction,
                assignment.Operand1,
                read.Operand1))
        {
            return false;
        }

        var targetValue = ResolvePurePoppedRegisterCapture(
            tacIr,
            valueAnalysis,
            inliner,
            state,
            assignment.Operand0);
        var memberValue = ResolvePurePoppedRegisterCapture(
            tacIr,
            valueAnalysis,
            inliner,
            state,
            assignment.Operand1);
        var targetExpression = BuildDirectExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            targetValue);
        var memberExpression = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            memberValue);
        var memberFact = valueAnalysis[memberValue];
        var isComputed = memberFact.ConstantKind is not Avm1ConstantKind.String ||
            memberFact.StringValue is null ||
            !IsIdentifier(memberFact.StringValue);
        var memberAccess = builder.AddNode(
            Avm1AstNodeKind.MemberAccess,
            merge: new BlockIndex(isComputed ? 1 : 0),
            children: [targetExpression, memberExpression]);
        // Flash erases the pre/post distinction when the expression result is discarded.
        memberUpdate = builder.AddNode(
            isValueConsumed ? Avm1AstNodeKind.Prefix : Avm1AstNodeKind.Postfix,
            startAction: new ActionIndex((int)update.Opcode),
            children: [memberAccess]);
        return true;
    }

    private static ValueIndex ResolvePurePoppedRegisterCapture(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        ValueIndex value)
    {
        if (!TryGetSemanticDefinition(inliner, value, out var load) ||
            load.Op is not Avm1TacOp.LoadRegister ||
            !state.SsaAccessByInstruction.TryGetValue(load.Index, out var read) ||
            read.Kind is not Avm1RegisterAccessKind.Read)
        {
            return value;
        }

        Avm1RegisterAccess write = default;
        var writeCount = 0;
        foreach (var access in state.SsaAccessByInstruction.Values)
        {
            if (access.Kind is not Avm1RegisterAccessKind.Write ||
                access.Register != read.Register ||
                access.Version != read.Version)
            {
                continue;
            }

            write = access;
            writeCount++;
            if (writeCount > 1)
                return value;
        }

        if (writeCount != 1)
            return value;

        var store = tacIr[write.Instruction];
        if (store.Op is not Avm1TacOp.StoreRegister ||
            store.Index.Value + 1 >= tacIr.Count ||
            store.Index.Value >= load.Index.Value ||
            !AreInSameBlock(state.ControlFlowGraph, store, load) ||
            !TryGetSemanticDefinition(inliner, store.Operand0, out var sourceDefinition) ||
            Avm1TacEffectAnalysis.GetEffects(sourceDefinition, valueAnalysis) is not
                Avm1TacEffect.None)
        {
            return value;
        }

        var cleanup = tacIr[new IrIndex(store.Index.Value + 1)];
        if (cleanup.Op is not Avm1TacOp.Pop ||
            cleanup.Operand0 != store.Operand0 ||
            !AreInSameBlock(state.ControlFlowGraph, store, cleanup))
        {
            return value;
        }

        inliner.TryInlineForStructuredConsumer(store.Operand0);
        state.HiddenInstructions.Add(store.Index);
        state.HiddenInstructions.Add(cleanup.Index);
        return store.Operand0;
    }

    private static bool HasMemberUpdateBarrier(
        Avm1TacIr tacIr,
        Avm1ExpressionInliner inliner,
        Avm1TacInstruction assignment,
        Avm1TacInstruction read)
    {
        var firstIndex = read.Index.Value;
        firstIndex = GetEarlierDefinitionIndex(inliner, assignment.Operand0, firstIndex);
        firstIndex = GetEarlierDefinitionIndex(inliner, assignment.Operand1, firstIndex);
        firstIndex = GetEarlierDefinitionIndex(inliner, read.Operand0, firstIndex);
        firstIndex = GetEarlierDefinitionIndex(inliner, read.Operand1, firstIndex);

        for (var i = firstIndex + 1; i < assignment.Index.Value; i++)
        {
            if (IsMemberUpdateBarrier(tacIr.Instructions[i].Op))
                return true;
        }

        return false;
    }

    private static int GetEarlierDefinitionIndex(
        Avm1ExpressionInliner inliner,
        ValueIndex value,
        int currentIndex) =>
        TryGetSemanticDefinition(inliner, value, out var definition)
            ? Math.Min(currentIndex, definition.Index.Value)
            : currentIndex;

    private static bool IsMemberUpdateBarrier(Avm1TacOp op) =>
        op is Avm1TacOp.SetVariable or Avm1TacOp.SetMember or
            Avm1TacOp.Delete or Avm1TacOp.DefineLocal or Avm1TacOp.Trace or
            Avm1TacOp.TimelineControl or Avm1TacOp.TimelineCall or
            Avm1TacOp.TimelineGoto or
            Avm1TacOp.GetUrl or
            Avm1TacOp.CallFunction or Avm1TacOp.CallMethod or
            Avm1TacOp.NewObject or Avm1TacOp.NewMethod or
            Avm1TacOp.WithEnter or Avm1TacOp.WithExit or
            Avm1TacOp.TryEnter or Avm1TacOp.TryExit or
            Avm1TacOp.CatchEnter or Avm1TacOp.CatchExit or
            Avm1TacOp.FinallyEnter or Avm1TacOp.FinallyExit or
            Avm1TacOp.Extends or Avm1TacOp.Implements or Avm1TacOp.Enumerate;

    private static bool TryGetCompoundBinary(
        Avm1ExpressionInliner inliner,
        ValueIndex value,
        out Avm1TacInstruction binary)
    {
        if (!TryGetSemanticDefinition(inliner, value, out binary) ||
            binary.Op is not Avm1TacOp.Binary ||
            !IsCompoundAssignmentOperator(binary.Opcode))
        {
            binary = default;
            return false;
        }

        return true;
    }

    private static bool TryGetSemanticDefinition(
        Avm1ExpressionInliner inliner,
        ValueIndex value,
        out Avm1TacInstruction definition)
    {
        for (var depth = 0; depth < 16 && inliner.TryGetDefinition(value, out definition); depth++)
        {
            if (definition.Op is not Avm1TacOp.Copy)
                return true;

            value = definition.Operand0;
        }

        definition = default;
        return false;
    }

    private static bool AreEquivalentLValueValues(
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        ValueIndex left,
        ValueIndex right,
        int depth = 0)
    {
        if (left == right)
            return true;
        if (!left.IsValid || !right.IsValid || depth >= 16)
            return false;

        var leftFact = valueAnalysis[left];
        var rightFact = valueAnalysis[right];
        if (HaveSameConstant(leftFact, rightFact))
            return true;

        if (!TryGetSemanticDefinition(inliner, left, out var leftDefinition) ||
            !TryGetSemanticDefinition(inliner, right, out var rightDefinition) ||
            leftDefinition.Op != rightDefinition.Op)
        {
            return false;
        }

        switch (leftDefinition.Op)
        {
            case Avm1TacOp.LoadRegister:
                if (leftDefinition.IntOperand != rightDefinition.IntOperand)
                    return false;

                var hasLeftAccess = ssaAccessByInstruction.TryGetValue(leftDefinition.Index, out var leftAccess);
                var hasRightAccess = ssaAccessByInstruction.TryGetValue(rightDefinition.Index, out var rightAccess);
                return hasLeftAccess == hasRightAccess &&
                    (!hasLeftAccess || leftAccess.Version == rightAccess.Version);

            case Avm1TacOp.GetVariable:
                return AreEquivalentLValueValues(
                    valueAnalysis,
                    inliner,
                    ssaAccessByInstruction,
                    leftDefinition.Operand0,
                    rightDefinition.Operand0,
                    depth + 1);

            case Avm1TacOp.GetMember:
            case Avm1TacOp.Cast:
                return AreEquivalentLValueValues(
                        valueAnalysis,
                        inliner,
                        ssaAccessByInstruction,
                        leftDefinition.Operand0,
                        rightDefinition.Operand0,
                        depth + 1) &&
                    AreEquivalentLValueValues(
                        valueAnalysis,
                        inliner,
                        ssaAccessByInstruction,
                        leftDefinition.Operand1,
                        rightDefinition.Operand1,
                        depth + 1);

            default:
                return false;
        }
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

    private static bool IsCompoundAssignmentOperator(ActionOpcode opcode) => opcode is
        ActionOpcode.Add or
        ActionOpcode.Add2 or
        ActionOpcode.StringAdd or
        ActionOpcode.Subtract or
        ActionOpcode.Multiply or
        ActionOpcode.Divide or
        ActionOpcode.Modulo or
        ActionOpcode.BitAnd or
        ActionOpcode.BitOr or
        ActionOpcode.BitXor or
        ActionOpcode.BitLShift or
        ActionOpcode.BitRShift or
        ActionOpcode.BitURShift;

    private static void PushActiveSwitchScope(BuilderState state, ActiveSwitch activeSwitch)
    {
        state.ActiveSwitches.Push(activeSwitch);
        state.ActiveControlScopes.Push(new ActiveControlScope(
            ActiveControlScopeKind.Switch,
            activeSwitch.Header,
            activeSwitch.Merge,
            BlockIndex.Invalid,
            BlockIndex.Invalid));
    }

    private static void PopActiveSwitchScope(BuilderState state)
    {
        state.ActiveControlScopes.Pop();
        state.ActiveSwitches.Pop();
    }

    private static AstIndex AddWith(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        StructuredWithRegion region,
        BuilderState state)
    {
        var header = cfg[region.Header];
        var enter = GetInstructions(tacIr, header)
            .LastOrDefault(instruction => instruction.Op is Avm1TacOp.WithEnter);
        if (enter.Op is not Avm1TacOp.WithEnter)
        {
            return AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                header,
                state);
        }

        var scope = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            enter.Operand0);
        var body = BuildIfChild(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state,
            cfg,
            region.BodyEntry,
            region.Exit);
        var withNode = builder.AddNode(
            Avm1AstNodeKind.With,
            block: region.Header,
            merge: region.Exit,
            startAction: region.EnterAction,
            endAction: cfg[region.Exit].EndAction,
            children: [scope, body]);

        var headerStatements = AddBasicBlock(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            header,
            state);
        var headerBlock = builder.GetNode(headerStatements);
        if (headerBlock.Children.Count == 0)
            return withNode;

        var children = new List<AstIndex>(headerBlock.Children.Count + 1);
        for (var i = 0; i < headerBlock.Children.Count; i++)
            children.Add(builder.GetChild(headerBlock, i));
        children.Add(withNode);
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: region.Header,
            startAction: header.StartAction,
            endAction: cfg[region.Exit].EndAction,
            children: children);
    }

    private static void AddConsumedWithRegion(
        HashSet<BlockIndex> consumed,
        StructuredWithRegion region)
    {
        for (var block = region.Header.Value; block <= region.Exit.Value; block++)
            consumed.Add(new BlockIndex(block));
    }

    private static AstIndex AddTry(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        StructuredTryRegion region,
        BuilderState state)
    {
        var children = new List<AstIndex>(3)
        {
            BuildTryBody(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                region.TryBodyEntry,
                region.TryExit,
                state)
        };

        if (region.HasCatch)
        {
            children.Add(BuildTryBody(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                region.CatchBodyEntry,
                region.CatchExit,
                state));
        }

        if (region.HasFinally)
        {
            children.Add(BuildTryBody(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                region.FinallyBodyEntry,
                region.FinallyExit,
                state));
        }

        return builder.AddNode(
            Avm1AstNodeKind.Try,
            block: region.Header,
            merge: region.Continuation,
            startAction: region.EnterAction,
            endAction: cfg[region.LastBoundary].EndAction,
            children: children);
    }

    private static AstIndex BuildTryBody(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        BlockIndex entry,
        BlockIndex exit,
        BuilderState state)
    {
        if (!entry.IsValid || entry == exit)
            return builder.AddNode(Avm1AstNodeKind.Block, block: entry, merge: exit);

        var blocks = new List<BlockIndex>(Math.Max(0, exit.Value - entry.Value));
        for (var block = entry.Value; block < exit.Value; block++)
            blocks.Add(new BlockIndex(block));

        var statements = AddStructuredBlocks(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            cfg,
            blocks,
            state,
            structuralBoundary: exit);
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: entry,
            merge: exit,
            startAction: cfg[entry].StartAction,
            endAction: cfg[exit].StartAction,
            children: statements);
    }

    private static void AddConsumedTryRegion(
        HashSet<BlockIndex> consumed,
        StructuredTryRegion region)
    {
        for (var block = region.Header.Value; block <= region.LastBoundary.Value; block++)
            consumed.Add(new BlockIndex(block));
    }

    private static AstIndex AddSwitch(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1SwitchRegion region,
        BuilderState state)
    {
        if (region.DispatchStores.Count > 0)
            inliner.TryInlineForStructuredConsumer(region.Discriminator);

        var children = new List<AstIndex>(1 + region.Cases.Count + 1)
        {
            BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                region.Discriminator)
        };

        foreach (var @case in region.Cases)
        {
            var value = BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                @case.Value);
            var caseChildren = new List<AstIndex>(2) { value };
            if (@case.ExitFlags is not Avm1SwitchCaseExitFlags.Grouped)
            {
                PushActiveSwitchScope(state, new ActiveSwitch(
                    region.Header,
                    region.Merge,
                    EmitConditionalBreak: true,
                    EmitTerminalBreak: true,
                    IsDefault: false));

                AstIndex body;
                try
                {
                    body = BuildIfChild(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        state,
                        cfg,
                        @case.BodyEntry,
                        @case.BodyBoundary);
                }
                finally
                {
                    PopActiveSwitchScope(state);
                }

                if (@case.ExitFlags is Avm1SwitchCaseExitFlags.Escaping &&
                    state.ActiveLoops.TryPeek(out var activeLoop) &&
                    @case.BodyBoundary == activeLoop.ImplicitContinueBlock &&
                    @case.BodyBoundary != activeLoop.ContinueTarget)
                {
                    body = AppendControlStatement(
                        builder,
                        body,
                        Avm1AstNodeKind.Continue,
                        @case.BodyEntry,
                        @case.BodyBoundary);
                }

                caseChildren.Add(body);
            }

            children.Add(builder.AddNode(
                Avm1AstNodeKind.SwitchCase,
                block: @case.Header,
                merge: @case.BodyBoundary,
                children: caseChildren));
        }

        if (region.DefaultEntry != region.Merge)
        {
            PushActiveSwitchScope(state, new ActiveSwitch(
                region.Header,
                region.Merge,
                EmitConditionalBreak: true,
                EmitTerminalBreak: !region.Merge.IsValid,
                IsDefault: true));
            AstIndex defaultBody;
            try
            {
                defaultBody = BuildIfChild(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    state,
                    cfg,
                    region.DefaultEntry,
                    region.Merge);
            }
            finally
            {
                PopActiveSwitchScope(state);
            }

            children.Add(builder.AddNode(
                Avm1AstNodeKind.SwitchDefault,
                merge: region.Merge,
                children: [defaultBody]));
        }

        var header = cfg[region.Header];
        var switchNode = builder.AddNode(
            Avm1AstNodeKind.Switch,
            block: region.Header,
            merge: region.Merge,
            startAction: header.StartAction,
            endAction: region.Merge.IsValid ? cfg[region.Merge].StartAction : ActionIndex.Invalid,
            children: children);

        PushActiveSwitchScope(state, new ActiveSwitch(
            region.Header,
            region.Merge,
            EmitConditionalBreak: false,
            EmitTerminalBreak: false,
            IsDefault: false));
        AstIndex headerStatements;
        try
        {
            headerStatements = AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                header,
                state);
        }
        finally
        {
            PopActiveSwitchScope(state);
        }

        var headerBlock = builder.GetNode(headerStatements);
        if (headerBlock.Children.Count == 0)
            return switchNode;

        var compositeChildren = new List<AstIndex>(headerBlock.Children.Count + 1);
        for (var i = 0; i < headerBlock.Children.Count; i++)
            compositeChildren.Add(builder.GetChild(headerBlock, i));
        compositeChildren.Add(switchNode);
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: region.Header,
            startAction: header.StartAction,
            endAction: header.EndAction,
            children: compositeChildren);
    }

    private static AstIndex AddIf(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region,
        BuilderState state)
    {
        if (!state.ActiveIfHeaders.Add(region.Header))
        {
            return AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg[region.Header],
                state);
        }

        try
        {
            if (state.IfNestingDepth >= MaximumStructuredIfNestingDepth)
                return AddOpaqueIfRegion(builder, instructions, cfg, region);

            state.IfNestingDepth++;
            try
            {
                return AddIfCore(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    region,
                    state);
            }
            finally
            {
                state.IfNestingDepth--;
            }
        }
        finally
        {
            state.ActiveIfHeaders.Remove(region.Header);
        }
    }

    private static AstIndex AddIfCore(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region,
        BuilderState state)
    {
        var header = cfg[region.Header];
        var frameWait = GetInstructions(tacIr, header)
            .LastOrDefault(instruction => instruction.Op is Avm1TacOp.FrameLoaded);
        if (frameWait.Op is Avm1TacOp.FrameLoaded &&
            !region.HasElse &&
            region.FallThroughEntry == header.FirstSuccessor)
        {
            return AddIfFrameLoaded(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                region,
                state,
                header,
                frameWait);
        }

        var trueSuccessor = header.SecondSuccessor;
        var falseSuccessor = header.FirstSuccessor;
        var thenEntry = region.FallThroughEntry;
        var elseEntry = region.BranchEntry;
        if (!region.HasElse &&
            (thenEntry == region.Merge || !thenEntry.IsValid) &&
            elseEntry.IsValid)
        {
            thenEntry = elseEntry;
        }

        var children = new List<AstIndex>();
        var branch = GetInstructions(tacIr, header).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        AstIndex condExpr = AstIndex.Invalid;

        if (branch.Op is Avm1TacOp.BranchIf)
        {
            condExpr = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state.SsaAccessByInstruction, branch.Operand0);
            bool invert = thenEntry == falseSuccessor && thenEntry != trueSuccessor;
            if (invert)
            {
                condExpr = InvertCondition(builder, condExpr);
            }
        }
        else
        {
            condExpr = builder.AddNode(Avm1AstNodeKind.Literal, startAction: ActionIndex.Invalid);
        }

        children.Add(condExpr); // Индекс 0: Условие

        var suppressDefaultBranchBreaks =
            state.ActiveSwitches.TryPeek(out var activeSwitch) &&
            activeSwitch.IsDefault &&
            activeSwitch.EmitTerminalBreak &&
            region.Merge == activeSwitch.Merge;
        if (suppressDefaultBranchBreaks)
            state.ActiveSwitches.Push(activeSwitch with { EmitTerminalBreak = false });

        AstIndex thenChild;
        var elseChild = AstIndex.Invalid;
        var hasElse = region.HasElse;
        try
        {
            // ИСПРАВЛЕНО: Всегда строим ветку 'Then' (Индекс 1), даже если она пуста
            thenChild = BuildIfChild(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state, cfg, thenEntry, region.Merge);
            children.Add(thenChild);

            // Ветка 'Else' (Индекс 2, опционально)
            if (hasElse)
            {
                elseChild = BuildIfChild(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state, cfg, elseEntry, region.Merge);
                children.Add(elseChild);
            }
        }
        finally
        {
            if (suppressDefaultBranchBreaks)
                state.ActiveSwitches.Pop();
        }

        if (hasElse && elseChild.IsValid)
        {
            var thenIsEmpty = builder.GetNode(thenChild).Children.Count == 0;
            var elseIsEmpty = builder.GetNode(elseChild).Children.Count == 0;
            if (thenIsEmpty != elseIsEmpty)
            {
                if (thenIsEmpty)
                {
                    condExpr = InvertCondition(builder, condExpr);
                    thenChild = elseChild;
                    children[0] = condExpr;
                    children[1] = thenChild;
                }

                children.RemoveAt(2);
                elseChild = AstIndex.Invalid;
                hasElse = false;
            }
        }

        var flattenedTail = AstIndex.Invalid;
        if (hasElse && region.Merge == BlockIndex.Invalid && elseChild.IsValid)
        {
            var thenIsTerminal = IsTerminalAstNode(builder, thenChild);
            var elseIsTerminal = IsTerminalAstNode(builder, elseChild);
            if (thenIsTerminal != elseIsTerminal)
            {
                if (thenIsTerminal)
                {
                    flattenedTail = elseChild;
                }
                else
                {
                    condExpr = InvertCondition(builder, condExpr);
                    flattenedTail = thenChild;
                    thenChild = elseChild;
                    children[0] = condExpr;
                    children[1] = thenChild;
                }

                children.RemoveAt(2);
                elseChild = AstIndex.Invalid;
                hasElse = false;
            }
        }

        var ifNode = builder.AddNode(
            Avm1AstNodeKind.If,
            region.Header,
            region.Merge,
            header.StartAction,
            header.EndAction,
            children);

        var structuredIf = ifNode;
        if (flattenedTail.IsValid)
        {
            var flattenedChildren = new List<AstIndex> { ifNode };
            var tailNode = builder.GetNode(flattenedTail);
            if (tailNode.Kind is Avm1AstNodeKind.Block)
            {
                for (var childIndex = 0; childIndex < tailNode.Children.Count; childIndex++)
                    flattenedChildren.Add(builder.GetChild(tailNode, childIndex));
            }
            else
            {
                flattenedChildren.Add(flattenedTail);
            }

            structuredIf = builder.AddNode(
                Avm1AstNodeKind.Block,
                block: region.Header,
                startAction: header.StartAction,
                endAction: tailNode.EndAction,
                children: flattenedChildren);
        }

        var suppressSwitchHeaderJump =
            state.ActiveSwitches.TryPeek(out var headerSwitch) &&
            headerSwitch.EmitConditionalBreak &&
            (region.Merge == headerSwitch.Merge ||
                IsSwitchExitSuccessor(
                    cfg,
                    region.Merge,
                    headerSwitch.Merge,
                    allowDirectVirtualExit: !headerSwitch.IsDefault));
        if (suppressSwitchHeaderJump)
            state.ActiveSwitches.Push(headerSwitch with { EmitConditionalBreak = false });

        AstIndex headerStatements;
        try
        {
            headerStatements = AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                header,
                state);
        }
        finally
        {
            if (suppressSwitchHeaderJump)
                state.ActiveSwitches.Pop();
        }
        var headerBlock = builder.GetNode(headerStatements);
        if (inliner.HasConditionalValueForHeader(region.Header))
            return headerStatements;

        if (headerBlock.Children.Count == 0)
            return structuredIf;

        var compositeChildren = new List<AstIndex>(headerBlock.Children.Count + 1);
        for (var i = 0; i < headerBlock.Children.Count; i++)
            compositeChildren.Add(builder.GetChild(headerBlock, i));
        compositeChildren.Add(structuredIf);

        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: region.Header,
            startAction: header.StartAction,
            endAction: header.EndAction,
            children: compositeChildren);
    }

    private static AstIndex AddIfFrameLoaded(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region,
        BuilderState state,
        Avm1BasicBlock header,
        Avm1TacInstruction frameWait)
    {
        var merge = header.SecondSuccessor.IsValid
            ? header.SecondSuccessor
            : region.Merge;
        var body = BuildIfChild(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state,
            cfg,
            header.FirstSuccessor,
            merge);
        var children = new List<AstIndex>(2);
        if (frameWait.Operand0.IsValid)
        {
            children.Add(BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state.SsaAccessByInstruction,
                frameWait.Operand0));
        }
        children.Add(body);

        var waitNode = builder.AddNode(
            Avm1AstNodeKind.IfFrameLoaded,
            block: region.Header,
            merge: merge,
            startAction: header.StartAction,
            endAction: header.EndAction,
            children: children,
            intOperand: frameWait.IntOperand);
        builder.SetOriginAction(waitNode, frameWait.Action);

        var headerStatements = AddBasicBlock(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            header,
            state);
        var headerBlock = builder.GetNode(headerStatements);
        if (headerBlock.Children.Count == 0)
            return waitNode;

        var compositeChildren = new List<AstIndex>(headerBlock.Children.Count + 1);
        for (var i = 0; i < headerBlock.Children.Count; i++)
            compositeChildren.Add(builder.GetChild(headerBlock, i));
        compositeChildren.Add(waitNode);
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: region.Header,
            startAction: header.StartAction,
            endAction: header.EndAction,
            children: compositeChildren);
    }

    private static bool IsTerminalAstNode(
        Avm1AstArena.Builder builder,
        AstIndex index,
        int depth = 0)
    {
        if (!index.IsValid || depth > 64)
            return false;

        var node = builder.GetNode(index);
        if (node.Kind is Avm1AstNodeKind.Return or Avm1AstNodeKind.Throw)
            return true;

        if (node.Kind is Avm1AstNodeKind.Block)
        {
            return node.Children.Count > 0 &&
                IsTerminalAstNode(
                    builder,
                    builder.GetChild(node, node.Children.Count - 1),
                    depth + 1);
        }

        if (node.Kind is Avm1AstNodeKind.If && node.Children.Count == 3)
        {
            return IsTerminalAstNode(builder, builder.GetChild(node, 1), depth + 1) &&
                IsTerminalAstNode(builder, builder.GetChild(node, 2), depth + 1);
        }

        return false;
    }

    private static AstIndex InvertCondition(Avm1AstArena.Builder builder, AstIndex condition)
    {
        if (condition.IsValid)
        {
            var node = builder.GetNode(condition);
            if (node.Kind is Avm1AstNodeKind.Unary && node.StartAction.Value == 1)
                return builder.GetChild(node, 0);

            if (node.Kind is Avm1AstNodeKind.Binary &&
                IsInvertibleComparison((ActionOpcode)node.StartAction.Value))
            {
                builder.UpdateNode(node with
                {
                    Merge = new BlockIndex(node.Merge.Value == 1 ? 0 : 1)
                });
                return condition;
            }
        }

        return builder.AddNode(Avm1AstNodeKind.Unary, startAction: new ActionIndex(1), children: [condition]);
    }

    private static bool IsInvertibleComparison(ActionOpcode opcode) => opcode is
        ActionOpcode.Equals or
        ActionOpcode.Equals2 or
        ActionOpcode.StringEquals or
        ActionOpcode.StrictEquals or
        ActionOpcode.Less or
        ActionOpcode.Less2 or
        ActionOpcode.StringLess or
        ActionOpcode.Greater or
        ActionOpcode.StringGreater;

    // ИСПРАВЛЕНО: Метод гарантирует создание пустого узла Block при отсутствии инструкций в ветке
    private static AstIndex BuildIfChild(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        Avm1ControlFlowGraph cfg,
        BlockIndex block,
        BlockIndex merge)
    {
        if (!block.IsValid || block == merge)
        {
            // Возвращаем пустой структурированный Block-узел для сохранения структуры индексов дочерних узлов If
            return builder.AddNode(Avm1AstNodeKind.Block, block: block, children: Array.Empty<AstIndex>());
        }

        var statements = new List<AstIndex>();
        var visited = new HashSet<BlockIndex>();
        var current = block;
        var endAction = cfg[block].EndAction;
        var trailingBasicBlock = BlockIndex.Invalid;
        var trailingBasicStatementStart = -1;
        while (current.IsValid && current != merge && visited.Add(current))
        {
            if (state.ActiveLoops.TryPeek(out var activeLoop) &&
                activeLoop.IsForIn &&
                TryGetForInEnumerationCleanupSuccessor(
                    tacIr,
                    valueAnalysis,
                    cfg[current],
                    out var cleanupContinuation))
            {
                state.ConsumedLoopControlArmBlocks.Add(current);
                endAction = cfg[current].EndAction;
                current = cleanupContinuation;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                continue;
            }

            if (state.TryByHeader.TryGetValue(current, out var nestedTry) &&
                (nestedTry.Continuation == merge ||
                    (nestedTry.Continuation.IsValid &&
                        CanReachBlock(cfg, nestedTry.Continuation, merge))))
            {
                var nested = AddTry(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    nestedTry,
                    state);
                AppendBlockStatements(builder, nested, statements);
                endAction = cfg[nestedTry.LastBoundary].EndAction;
                current = nestedTry.Continuation;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                continue;
            }

            if (state.WithByHeader.TryGetValue(current, out var nestedWith) &&
                (nestedWith.Exit == merge ||
                    CanReachBlock(cfg, cfg[nestedWith.Exit].FirstSuccessor, merge)))
            {
                var nested = AddWith(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    nestedWith,
                    state);
                AppendBlockStatements(builder, nested, statements);
                endAction = cfg[nestedWith.Exit].EndAction;
                current = nestedWith.Exit == merge
                    ? merge
                    : cfg[nestedWith.Exit].FirstSuccessor;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                continue;
            }

            if (state.DoWhileByHeader.TryGetValue(current, out var nestedDoWhile) &&
                CanStructureNestedPathLoop(
                    cfg,
                    nestedDoWhile.Header,
                    nestedDoWhile.Exit,
                    merge,
                    state))
            {
                var nested = AddDoWhile(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    nestedDoWhile,
                    state);
                AppendBlockStatements(builder, nested, statements);
                endAction = cfg[nestedDoWhile.ConditionBlock].EndAction;
                current = nestedDoWhile.Exit;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                if (TargetsActiveLoopBoundary(current, state))
                    break;
                continue;
            }

            if (state.WhileByHeader.TryGetValue(current, out var nestedWhile) &&
                CanStructureNestedPathLoop(
                    cfg,
                    nestedWhile.Header,
                    nestedWhile.Exit,
                    merge,
                    state))
            {
                var nested = AddWhile(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    nestedWhile,
                    state);
                RefreshTrailingPathPreheader(
                    statements,
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    state,
                    nestedWhile.Header,
                    trailingBasicBlock,
                    trailingBasicStatementStart);
                AppendBlockStatements(builder, nested, statements);
                endAction = nestedWhile.Blocks.Count > 0
                    ? cfg[nestedWhile.Blocks[^1]].EndAction
                    : cfg[nestedWhile.Header].EndAction;
                current = nestedWhile.Exit;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                if (TargetsActiveLoopBoundary(current, state))
                    break;
                continue;
            }

            if (state.SwitchByHeader.TryGetValue(current, out var nestedSwitch) &&
                (nestedSwitch.Merge == merge ||
                    (nestedSwitch.Merge.IsValid && CanReachBlock(cfg, nestedSwitch.Merge, merge))))
            {
                var nested = AddSwitch(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    nestedSwitch,
                    state);
                AppendBlockStatements(builder, nested, statements);
                endAction = nestedSwitch.Merge.IsValid
                    ? cfg[nestedSwitch.Merge].StartAction
                    : builder.GetNode(nested).EndAction;
                current = nestedSwitch.Merge;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                continue;
            }

            if (TryBuildConditionalLoopContinueArm(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                cfg,
                cfg[current],
                state,
                out var nestedLoopContinueIf,
                out var nestedLoopNormalEntry))
            {
                statements.Add(nestedLoopContinueIf);
                endAction = builder.GetNode(nestedLoopContinueIf).EndAction;
                current = nestedLoopNormalEntry;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                continue;
            }

            var preferDefaultSwitchBreak =
                state.ActiveSwitches.TryPeek(out var currentSwitch) &&
                currentSwitch.IsDefault &&
                TryGetConditionalSwitchContinuation(
                    cfg,
                    cfg[current],
                    inliner,
                    state,
                    out _,
                    out _);
            var useParentIfBoundary =
                state.IfByHeader.TryGetValue(current, out var parentBoundedRegion) &&
                !parentBoundedRegion.Merge.IsValid &&
                merge.IsValid &&
                AllPathsReachBoundaryOrTerminate(
                    cfg,
                    parentBoundedRegion.FallThroughEntry,
                    merge) &&
                AllPathsReachBoundaryOrTerminate(
                    cfg,
                    parentBoundedRegion.BranchEntry,
                    merge);
            if (!preferDefaultSwitchBreak &&
                !state.ActiveIfHeaders.Contains(current) &&
                state.IfByHeader.TryGetValue(current, out var nestedRegion) &&
                !TargetsActiveLoopControl(nestedRegion, state) &&
                (nestedRegion.Merge == merge ||
                    (nestedRegion.Merge.IsValid &&
                        (CanReachBlock(cfg, nestedRegion.Merge, merge) ||
                            AlwaysTerminatesBefore(cfg, nestedRegion.Merge, merge))) ||
                    useParentIfBoundary))
            {
                if (useParentIfBoundary)
                    nestedRegion = nestedRegion with { Merge = merge };

                var nested = AddIf(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    nestedRegion,
                    state);
                AppendBlockStatements(builder, nested, statements);
                MarkNestedTerminalBlocksConsumed(cfg, nestedRegion, state);
                endAction = nestedRegion.Merge.IsValid
                    ? cfg[nestedRegion.Merge].StartAction
                    : builder.GetNode(nested).EndAction;
                current = nestedRegion.Merge;
                trailingBasicBlock = BlockIndex.Invalid;
                trailingBasicStatementStart = -1;
                continue;
            }

            var basicBlock = cfg[current];
            trailingBasicBlock = current;
            trailingBasicStatementStart = statements.Count;
            var basic = AddBasicBlock(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                basicBlock,
                state);
            AppendBlockStatements(builder, basic, statements);
            endAction = basicBlock.EndAction;

            if (basicBlock.Terminator is Avm1BlockTerminatorKind.Return or
                Avm1BlockTerminatorKind.Throw or
                Avm1BlockTerminatorKind.End)
            {
                break;
            }

            if (basicBlock.Terminator is Avm1BlockTerminatorKind.ConditionalBranch)
            {
                if (TryGetConditionalLoopContinuation(
                    basicBlock,
                    inliner,
                    state,
                    out var loopContinuation))
                {
                    current = loopContinuation;
                    continue;
                }

                if (TryBuildConditionalLoopContinueArm(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    basicBlock,
                    state,
                    out var loopContinueIf,
                    out var normalEntry))
                {
                    statements.Add(loopContinueIf);
                    endAction = builder.GetNode(loopContinueIf).EndAction;
                    current = normalEntry;
                    trailingBasicBlock = BlockIndex.Invalid;
                    trailingBasicStatementStart = -1;
                    continue;
                }

                if (TryGetConditionalSwitchContinuation(
                    cfg,
                    basicBlock,
                    inliner,
                    state,
                    out var switchContinuation,
                    out _))
                {
                    current = switchContinuation;
                    continue;
                }

                if (TryBuildConditionalSwitchBreakArm(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    cfg,
                    basicBlock,
                    merge,
                    state,
                    out var switchBreakIf,
                    out switchContinuation))
                {
                    statements.Add(switchBreakIf);
                    endAction = builder.GetNode(switchBreakIf).EndAction;
                    current = switchContinuation;
                    continue;
                }

                break;
            }

            var next = basicBlock.FirstSuccessor;
            if (state.ActiveLoops.Any(loop => next == loop.ContinueTarget || next == loop.Exit))
                break;
            current = next;
        }

        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: block,
            merge: merge,
            startAction: cfg[block].StartAction,
            endAction: endAction,
            children: statements);
    }

    private static bool CanStructureNestedPathLoop(
        Avm1ControlFlowGraph cfg,
        BlockIndex header,
        BlockIndex exit,
        BlockIndex pathMerge,
        BuilderState state)
    {
        if (state.ActiveControlScopes.Any(scope =>
            scope.Kind is ActiveControlScopeKind.Loop && scope.Header == header))
        {
            return false;
        }

        if (exit == pathMerge)
            return true;
        if (!exit.IsValid)
            return !pathMerge.IsValid;
        return CanReachBlock(cfg, exit, pathMerge);
    }

    private static bool TargetsActiveLoopBoundary(BlockIndex block, BuilderState state) =>
        state.ActiveLoops.Any(loop => block == loop.ContinueTarget || block == loop.Exit);

    private static void RefreshTrailingPathPreheader(
        List<AstIndex> statements,
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        BuilderState state,
        BlockIndex header,
        BlockIndex trailingBasicBlock,
        int trailingBasicStatementStart)
    {
        if (!trailingBasicBlock.IsValid ||
            trailingBasicBlock.Value != header.Value - 1 ||
            trailingBasicStatementStart < 0 ||
            trailingBasicStatementStart > statements.Count)
        {
            return;
        }

        statements.RemoveRange(
            trailingBasicStatementStart,
            statements.Count - trailingBasicStatementStart);
        if (!HasVisibleRootInstruction(tacIr, cfg[trailingBasicBlock], inliner, state))
            return;

        var refreshed = AddBasicBlock(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            cfg[trailingBasicBlock],
            state);
        AppendBlockStatements(builder, refreshed, statements);
    }

    private static void AppendBlockStatements(
        Avm1AstArena.Builder builder,
        AstIndex nodeIndex,
        List<AstIndex> statements)
    {
        var node = builder.GetNode(nodeIndex);
        if (node.Kind is not Avm1AstNodeKind.Block)
        {
            statements.Add(nodeIndex);
            return;
        }

        for (var i = 0; i < node.Children.Count; i++)
            statements.Add(builder.GetChild(node, i));
    }

    private static bool CanReachBlock(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex target)
    {
        if (!start.IsValid)
            return false;
        if (start == target)
            return true;

        var visited = new HashSet<BlockIndex>();
        var worklist = new Queue<BlockIndex>();
        worklist.Enqueue(start);
        while (worklist.TryDequeue(out var block))
        {
            if (!block.IsValid || !visited.Add(block))
                continue;
            if (block == target)
                return true;

            var node = cfg[block];
            if (!target.IsValid &&
                (!node.FirstSuccessor.IsValid ||
                    (node.Terminator is Avm1BlockTerminatorKind.ConditionalBranch && !node.SecondSuccessor.IsValid)))
            {
                return true;
            }

            worklist.Enqueue(node.FirstSuccessor);
            worklist.Enqueue(node.SecondSuccessor);
        }

        return false;
    }

    private static bool AlwaysTerminatesBefore(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex boundary)
    {
        var memo = new Dictionary<BlockIndex, bool>();
        var visiting = new HashSet<BlockIndex>();
        return Visit(start);

        bool Visit(BlockIndex block)
        {
            if (!block.IsValid || block == boundary)
                return false;
            if (memo.TryGetValue(block, out var known))
                return known;
            if (!visiting.Add(block))
                return false;

            var node = cfg[block];
            var result = node.Terminator switch
            {
                Avm1BlockTerminatorKind.Return or Avm1BlockTerminatorKind.Throw => true,
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

    private static void MarkNestedTerminalBlocksConsumed(
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region,
        BuilderState state)
    {
        var pending = new Stack<BlockIndex>();
        var visited = new HashSet<BlockIndex>();
        pending.Push(region.FallThroughEntry);
        pending.Push(region.BranchEntry);
        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!block.IsValid || block == region.Merge || !visited.Add(block))
                continue;

            var node = cfg[block];
            if (node.Terminator is Avm1BlockTerminatorKind.Return or Avm1BlockTerminatorKind.Throw)
            {
                state.ConsumedLoopControlArmBlocks.Add(block);
                continue;
            }

            if (node.FirstSuccessor.IsValid)
                pending.Push(node.FirstSuccessor);
            if (node.SecondSuccessor.IsValid)
                pending.Push(node.SecondSuccessor);
        }
    }

    private static bool AllPathsReachBoundaryOrTerminate(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex boundary)
    {
        var states = new byte[cfg.Count];
        return Visit(start);

        bool Visit(BlockIndex block)
        {
            if (block == boundary)
                return true;
            if (!block.IsValid)
                return false;

            ref var state = ref states[block.Value];
            if (state == 1)
                return false;
            if (state >= 2)
                return state == 2;

            state = 1;
            var node = cfg[block];
            var result = node.Terminator switch
            {
                Avm1BlockTerminatorKind.Return or Avm1BlockTerminatorKind.Throw => true,
                Avm1BlockTerminatorKind.FallThrough or Avm1BlockTerminatorKind.Jump =>
                    Visit(node.FirstSuccessor),
                Avm1BlockTerminatorKind.ConditionalBranch =>
                    Visit(node.FirstSuccessor) && Visit(node.SecondSuccessor),
                _ => false
            };
            state = result ? (byte)2 : (byte)3;
            return result;
        }
    }

    private static void AddConsumedIfRegion(
        HashSet<BlockIndex> consumed,
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region,
        HashSet<BlockIndex>? allowed = null)
    {
        var worklist = new Queue<BlockIndex>();
        worklist.Enqueue(region.FallThroughEntry);
        worklist.Enqueue(region.BranchEntry);
        while (worklist.TryDequeue(out var block))
        {
            if (!block.IsValid || block == region.Merge ||
                (allowed is not null && !allowed.Contains(block)) ||
                !consumed.Add(block))
            {
                continue;
            }

            var node = cfg[block];
            worklist.Enqueue(node.FirstSuccessor);
            worklist.Enqueue(node.SecondSuccessor);
        }
    }

    private static void AddConsumedSwitchRegion(
        HashSet<BlockIndex> consumed,
        Avm1ControlFlowGraph cfg,
        Avm1SwitchRegion region,
        HashSet<BlockIndex>? allowed = null)
    {
        consumed.Add(region.Header);
        var header = cfg[region.Header];
        var worklist = new Queue<BlockIndex>();
        worklist.Enqueue(header.FirstSuccessor);
        worklist.Enqueue(header.SecondSuccessor);
        while (worklist.TryDequeue(out var block))
        {
            if (!block.IsValid || block == region.Merge ||
                (allowed is not null && !allowed.Contains(block)) ||
                !consumed.Add(block))
            {
                continue;
            }

            var node = cfg[block];
            worklist.Enqueue(node.FirstSuccessor);
            worklist.Enqueue(node.SecondSuccessor);
        }
    }

    private static AstIndex AddBasicBlock(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock block,
        BuilderState state,
        bool suppressLoopControl = false)
    {
        var statements = new List<AstIndex>();

        foreach (var instruction in GetInstructions(tacIr, block))
        {
            if (state.HiddenInstructions.Contains(instruction.Index))
                continue;

            if (instruction.Op is Avm1TacOp.Phi &&
                state.ActiveLoops.TryPeek(out var activeLoop) &&
                activeLoop.IsForIn &&
                IsEnumerationStreamValue(tacIr, instruction.Result, depth: 0))
            {
                continue;
            }

            if (!inliner.IsRootInstruction(instruction))
                continue;

            AstIndex stmtIndex = AstIndex.Invalid;
            switch (instruction.Op)
            {
                case Avm1TacOp.StoreRegister:
                    {
                        stmtIndex = BuildRegisterAssignment(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction);
                        break;
                    }
                case Avm1TacOp.SetVariable:
                    {
                        stmtIndex = BuildVariableAssignment(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction);
                        break;
                    }
                case Avm1TacOp.DefineLocal:
                    {
                        stmtIndex = BuildVariableDeclaration(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction);
                        break;
                    }
                case Avm1TacOp.SetMember:
                    {
                        stmtIndex = BuildMemberAssignment(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state,
                            instruction);
                        break;
                    }
                case Avm1TacOp.Trace:
                    {
                        var argument = BuildExpression(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction.Operand0);
                        var trace = builder.AddNode(
                            Avm1AstNodeKind.Intrinsic,
                            startAction: new ActionIndex((int)ActionOpcode.Trace),
                            children: [argument]);
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.ExpressionStatement,
                            children: [trace]);
                        break;
                    }
                case Avm1TacOp.TimelineControl:
                    {
                        var intrinsic = builder.AddNode(
                            Avm1AstNodeKind.Intrinsic,
                            startAction: new ActionIndex((int)instruction.Opcode));
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.ExpressionStatement,
                            children: [intrinsic]);
                        break;
                    }
                case Avm1TacOp.TimelineCall:
                    {
                        var frame = BuildExpression(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction.Operand0);
                        var intrinsic = builder.AddNode(
                            Avm1AstNodeKind.Intrinsic,
                            startAction: new ActionIndex((int)ActionOpcode.Call),
                            children: [frame]);
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.ExpressionStatement,
                            children: [intrinsic]);
                        break;
                    }
                case Avm1TacOp.TimelineGoto:
                    {
                        AstIndex[] arguments = instruction.Opcode is
                            ActionOpcode.GotoFrame2
                                ?
                                [
                                    BuildExpression(
                                        builder,
                                        instructions,
                                        tacIr,
                                        valueAnalysis,
                                        registerSsa,
                                        inliner,
                                        state.SsaAccessByInstruction,
                                        instruction.Operand0)
                                ]
                                : [];
                        var intrinsic = builder.AddNode(
                            Avm1AstNodeKind.Intrinsic,
                            startAction: new ActionIndex((int)instruction.Opcode),
                            intOperand: instruction.IntOperand,
                            children: arguments);
                        builder.SetOriginAction(intrinsic, instruction.Action);
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.ExpressionStatement,
                            children: [intrinsic]);
                        break;
                    }
                case Avm1TacOp.GetUrl:
                    {
                        AstIndex[] arguments;
                        if (instruction.Opcode is ActionOpcode.GetURL2)
                        {
                            arguments =
                            [
                                BuildExpression(
                                    builder,
                                    instructions,
                                    tacIr,
                                    valueAnalysis,
                                    registerSsa,
                                    inliner,
                                    state.SsaAccessByInstruction,
                                    instruction.Operand0),
                                BuildExpression(
                                    builder,
                                    instructions,
                                    tacIr,
                                    valueAnalysis,
                                    registerSsa,
                                    inliner,
                                    state.SsaAccessByInstruction,
                                    instruction.Operand1)
                            ];
                        }
                        else
                        {
                            arguments = [];
                        }

                        var intrinsic = builder.AddNode(
                            Avm1AstNodeKind.Intrinsic,
                            startAction: new ActionIndex((int)instruction.Opcode),
                            children: arguments,
                            intOperand: instruction.IntOperand);
                        builder.SetOriginAction(intrinsic, instruction.Action);
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.ExpressionStatement,
                            children: [intrinsic]);
                        break;
                    }
                case Avm1TacOp.HostIntrinsic:
                case Avm1TacOp.VariadicHostIntrinsic:
                    {
                        var intrinsic = BuildFromInstruction(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction);
                        stmtIndex = instruction.Result.IsValid &&
                            inliner.RequiresTemporary(instruction)
                                ? builder.AddNode(
                                    Avm1AstNodeKind.AssignTemp,
                                    startAction: new ActionIndex(
                                        instruction.Result.Value),
                                    children: [intrinsic])
                                : builder.AddNode(
                                    Avm1AstNodeKind.ExpressionStatement,
                                    children: [intrinsic]);
                        break;
                    }
                case Avm1TacOp.TargetControl:
                    {
                        var target = BuildExpression(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction.Operand0);
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.TargetControl,
                            startAction: new ActionIndex(
                                (int)instruction.Opcode),
                            children: [target]);
                        break;
                    }
                case Avm1TacOp.FunctionLiteral:
                    {
                        var isNamed = IsNamedFunctionDefinition(instructions, instruction);
                        if (!isNamed && !inliner.RequiresTemporary(instruction))
                            break;

                        var function = BuildFromInstruction(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            state.SsaAccessByInstruction,
                            instruction);
                        stmtIndex = isNamed
                            ? builder.AddNode(
                                Avm1AstNodeKind.ExpressionStatement,
                                children: [function])
                            : builder.AddNode(
                                Avm1AstNodeKind.AssignTemp,
                                startAction: new ActionIndex(instruction.Result.Value),
                                children: [function]);
                        break;
                    }
                case Avm1TacOp.Return:
                    {
                        if (valueAnalysis[instruction.Operand0].ConstantKind is Avm1ConstantKind.Undefined)
                        {
                            stmtIndex = builder.AddNode(Avm1AstNodeKind.Return);
                        }
                        else
                        {
                            var valExpr = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state.SsaAccessByInstruction, instruction.Operand0);
                            stmtIndex = builder.AddNode(
                                Avm1AstNodeKind.Return,
                                children: [valExpr]);
                        }
                        break;
                    }
                case Avm1TacOp.CallFunction:
                case Avm1TacOp.CallMethod:
                case Avm1TacOp.NewObject:
                case Avm1TacOp.NewMethod:
                case Avm1TacOp.Delete:
                    {
                        var callExpr = BuildFromInstruction(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state.SsaAccessByInstruction, instruction);
                        stmtIndex = inliner.RequiresTemporary(instruction)
                            ? builder.AddNode(
                                Avm1AstNodeKind.AssignTemp,
                                startAction: new ActionIndex(instruction.Result.Value),
                                children: [callExpr])
                            : builder.AddNode(
                                Avm1AstNodeKind.ExpressionStatement,
                                children: [callExpr]);
                        break;
                    }
                case Avm1TacOp.Throw:
                    {
                        var valExpr = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state.SsaAccessByInstruction, instruction.Operand0);
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.Throw,
                            children: [valExpr]);
                        break;
                    }
                case Avm1TacOp.Unknown:
                    {
                        stmtIndex = builder.AddNode(
                            Avm1AstNodeKind.Opaque,
                            startAction: instruction.Action,
                            endAction: new ActionIndex(instruction.Action.Value + 1));
                        break;
                    }
                default:
                    {
                        if (instruction.Result.IsValid && DefinesValue(instruction.Op))
                        {
                            var valueExpr = BuildFromInstruction(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state.SsaAccessByInstruction, instruction);
                            stmtIndex = Avm1TacEffectAnalysis.IsSourceStatementRoot(
                                    instruction,
                                    valueAnalysis) &&
                                !inliner.RequiresTemporary(instruction)
                                    ? builder.AddNode(
                                        Avm1AstNodeKind.ExpressionStatement,
                                        children: [valueExpr])
                                    : builder.AddNode(
                                        Avm1AstNodeKind.AssignTemp,
                                        startAction: new ActionIndex(instruction.Result.Value),
                                        children: [valueExpr]);
                        }

                        break;
                    }
            }

            if (stmtIndex.IsValid)
            {
                builder.SetOriginAction(stmtIndex, instruction.Action);
                statements.Add(stmtIndex);
            }
        }

        if (!suppressLoopControl)
        {
            AddConditionalLoopJump(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                block,
                state,
                statements);
        }

        AddConditionalSwitchJump(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            block,
            state,
            statements);

        var emittedSwitchBreak = AddTerminalSwitchBreak(
            builder,
            block,
            state,
            statements);

        // Анализ безусловных прыжков для вывода break и continue
        if (!suppressLoopControl &&
            !emittedSwitchBreak &&
            block.Terminator == Avm1BlockTerminatorKind.Jump &&
            state.ActiveLoops.Count > 0)
        {
            var targetBlockIdx = ResolveForInCleanupJumpTarget(
                tacIr,
                valueAnalysis,
                state.ControlFlowGraph,
                state,
                block.FirstSuccessor,
                out var cleanupBlock);
            if ((targetBlockIdx.IsValid || state.ActiveLoops.Any(loop => !loop.Exit.IsValid)) &&
                TryResolveLoopJump(
                    targetBlockIdx,
                    block,
                    state,
                    out var jumpKind,
                    out var labelTarget))
            {
                if (cleanupBlock.IsValid)
                    state.ConsumedLoopControlArmBlocks.Add(cleanupBlock);
                statements.Add(builder.AddNode(jumpKind, block: labelTarget));
            }
        }

        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: block.Index,
            startAction: block.StartAction,
            endAction: block.EndAction,
            children: statements);
    }

    private static bool OwnsInnermostBreak(
        BuilderState state,
        ActiveControlScopeKind kind,
        BlockIndex target)
    {
        return state.ActiveControlScopes.TryPeek(out var scope) &&
            scope.Kind == kind &&
            scope.BreakTarget == target;
    }

    private static bool AddTerminalSwitchBreak(
        Avm1AstArena.Builder builder,
        Avm1BasicBlock block,
        BuilderState state,
        List<AstIndex> statements)
    {
        if (!state.ActiveSwitches.TryPeek(out var activeSwitch) ||
            !activeSwitch.EmitTerminalBreak ||
            !OwnsInnermostBreak(state, ActiveControlScopeKind.Switch, activeSwitch.Merge) ||
            !ExitsSwitchAtBoundary(block, activeSwitch.Merge))
        {
            return false;
        }

        statements.Add(builder.AddNode(Avm1AstNodeKind.Break, block: BlockIndex.Invalid));
        return true;
    }

    private static bool ExitsSwitchAtBoundary(Avm1BasicBlock block, BlockIndex merge)
    {
        if (merge.IsValid)
        {
            return block.FirstSuccessor == merge &&
                block.Terminator is Avm1BlockTerminatorKind.FallThrough or
                    Avm1BlockTerminatorKind.Jump;
        }

        return block.Terminator is Avm1BlockTerminatorKind.Jump &&
            !block.FirstSuccessor.IsValid;
    }

    private static void AddConditionalSwitchJump(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock block,
        BuilderState state,
        List<AstIndex> statements)
    {
        if (!TryGetConditionalSwitchContinuation(
            state.ControlFlowGraph,
            block,
            inliner,
            state,
            out _,
            out var invertCondition))
        {
            return;
        }

        var branch = GetInstructions(tacIr, block).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        if (branch.Op is not Avm1TacOp.BranchIf)
            return;

        AddControlJumpIf(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state,
            block,
            branch,
            Avm1AstNodeKind.Break,
            BlockIndex.Invalid,
            invertCondition,
            statements);
    }

    private static bool TryBuildConditionalLoopContinueArm(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        BuilderState state,
        out AstIndex continueIf,
        out BlockIndex normalEntry)
    {
        continueIf = AstIndex.Invalid;
        normalEntry = BlockIndex.Invalid;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            inliner.HasConditionalValueForHeader(block.Index) ||
            !state.ActiveLoops.TryPeek(out var loop) ||
            block.Index == loop.ConditionBlock)
        {
            return false;
        }

        if (TargetsOuterLoopControl(block.FirstSuccessor) ||
            TargetsOuterLoopControl(block.SecondSuccessor))
        {
            return false;
        }

        var fallThroughPath = new List<BlockIndex>();
        var branchPath = new List<BlockIndex>();
        var fallThroughReachesLatch = TryCollectLoopContinuePath(
            cfg,
            block.FirstSuccessor,
            loop,
            state,
            fallThroughPath,
            out var fallThroughTarget);
        var branchReachesLatch = TryCollectLoopContinuePath(
            cfg,
            block.SecondSuccessor,
            loop,
            state,
            branchPath,
            out var branchTarget);
        var fallThroughContinues = fallThroughReachesLatch && IsExplicitLoopContinuePath(
            cfg,
            block.FirstSuccessor,
            fallThroughTarget,
            fallThroughPath);
        var branchContinues = branchReachesLatch && IsExplicitLoopContinuePath(
            cfg,
            block.SecondSuccessor,
            branchTarget,
            branchPath);
        if (fallThroughContinues == branchContinues)
            return false;

        var continueEntry = fallThroughContinues ? block.FirstSuccessor : block.SecondSuccessor;
        var continuePath = fallThroughContinues ? fallThroughPath : branchPath;
        var continueTarget = fallThroughContinues ? fallThroughTarget : branchTarget;
        normalEntry = fallThroughContinues ? block.SecondSuccessor : block.FirstSuccessor;
        var normalPath = fallThroughContinues ? branchPath : fallThroughPath;
        if (!normalEntry.IsValid || normalEntry == loop.Exit)
            return false;
        if (normalPath.Count > 0 &&
            cfg[normalPath[^1]].Terminator is Avm1BlockTerminatorKind.Return or
                Avm1BlockTerminatorKind.Throw)
        {
            return false;
        }

        var branch = GetInstructions(tacIr, block).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        if (branch.Op is not Avm1TacOp.BranchIf)
            return false;
        if (fallThroughReachesLatch &&
            branchReachesLatch &&
            CountVisibleRootInstructions(tacIr, cfg, inliner, state, normalPath, limit: 2) <= 1 &&
            !IsNullishComparisonCondition(tacIr, valueAnalysis, branch.Operand0))
        {
            return false;
        }

        if (loop.PromotedVariable is { } promotedVariable && normalPath.Any(pathBlock =>
                FindLastAssignmentToVariable(
                    tacIr,
                    valueAnalysis,
                    cfg[pathBlock],
                    inliner,
                    promotedVariable) is not null))
        {
            return false;
        }

        var condition = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            branch.Operand0);
        if (fallThroughContinues)
            condition = InvertCondition(builder, condition);

        var continueBody = BuildIfChild(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state,
            cfg,
            continueEntry,
            continueTarget);
        continueBody = AppendControlStatement(
            builder,
            continueBody,
            Avm1AstNodeKind.Continue,
            continueEntry,
            continueTarget);
        foreach (var pathBlock in continuePath)
            state.ConsumedLoopControlArmBlocks.Add(pathBlock);

        var ifNode = builder.AddNode(
            Avm1AstNodeKind.If,
            block: block.Index,
            merge: continueTarget,
            startAction: branch.Action,
            endAction: cfg[continueTarget].StartAction,
            children: [condition, continueBody]);
        continueIf = PrependBasicBlockStatements(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            block,
            state,
            ifNode,
            cfg[continueTarget].StartAction);
        return true;

        bool TargetsOuterLoopControl(BlockIndex target) =>
            TryResolveLoopJump(
                target,
                block,
                state,
                out _,
                out var labelTarget) &&
            labelTarget.IsValid;
    }

    private static bool TryCollectLoopContinuePath(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        ActiveLoop loop,
        BuilderState state,
        List<BlockIndex> path,
        out BlockIndex target)
    {
        target = loop.ImplicitContinueBlock;
        if (target.IsValid &&
            state.TransparentLoopContinueBlocks.Contains(target) &&
            TryCollectLinearPathToTarget(cfg, start, target, loop.Exit, path))
        {
            return true;
        }

        path.Clear();
        target = loop.ContinueTarget;
        return target.IsValid &&
            TryCollectLinearPathToTarget(cfg, start, target, loop.Exit, path);
    }

    private static bool IsExplicitLoopContinuePath(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex target,
        List<BlockIndex> path)
    {
        if (start == target)
            return true;
        if (path.Count == 0)
            return false;

        var terminal = cfg[path[^1]];
        return terminal.Terminator is Avm1BlockTerminatorKind.Jump &&
            terminal.FirstSuccessor == target;
    }

    private static int CountVisibleRootInstructions(
        Avm1TacIr tacIr,
        Avm1ControlFlowGraph cfg,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        List<BlockIndex> blocks,
        int limit)
    {
        var count = 0;
        foreach (var blockIndex in blocks)
        {
            var block = cfg[blockIndex];
            for (var index = 0; index < tacIr.Count; index++)
            {
                var instruction = tacIr.Instructions[index];
                if (instruction.Action.Value < block.StartAction.Value ||
                    instruction.Action.Value >= block.EndAction.Value ||
                    state.HiddenInstructions.Contains(instruction.Index) ||
                    !inliner.IsRootInstruction(instruction))
                {
                    continue;
                }

                count++;
                if (count >= limit)
                    return count;
            }
        }

        return count;
    }

    private static bool IsNullishComparisonCondition(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        ValueIndex value,
        int depth = 0)
    {
        if (depth > 16 || !TryFindDefinition(tacIr, value, out var definition))
            return false;

        if (definition.Op is Avm1TacOp.Copy or Avm1TacOp.Unary)
        {
            return IsNullishComparisonCondition(
                tacIr,
                valueAnalysis,
                definition.Operand0,
                depth + 1);
        }

        if (definition.Op is not Avm1TacOp.Binary)
            return false;
        if (definition.Opcode is ActionOpcode.And or ActionOpcode.Or)
        {
            return IsNullishComparisonCondition(
                    tacIr,
                    valueAnalysis,
                    definition.Operand0,
                    depth + 1) ||
                IsNullishComparisonCondition(
                    tacIr,
                    valueAnalysis,
                    definition.Operand1,
                    depth + 1);
        }

        if (definition.Opcode is not (ActionOpcode.Equals or
            ActionOpcode.Equals2 or
            ActionOpcode.StrictEquals))
        {
            return false;
        }

        return IsNullishConstant(valueAnalysis[definition.Operand0].ConstantKind) ||
            IsNullishConstant(valueAnalysis[definition.Operand1].ConstantKind);
    }

    private static bool IsNullishConstant(Avm1ConstantKind kind) =>
        kind is Avm1ConstantKind.Null or Avm1ConstantKind.Undefined;

    private static AstIndex AppendControlStatement(
        Avm1AstArena.Builder builder,
        AstIndex body,
        Avm1AstNodeKind controlKind,
        BlockIndex block,
        BlockIndex merge)
    {
        var bodyNode = builder.GetNode(body);
        if (EndsWithControlStatement(builder, body, controlKind))
            return body;

        var children = new List<AstIndex>(bodyNode.Children.Count + 1);
        if (bodyNode.Kind is Avm1AstNodeKind.Block)
        {
            for (var i = 0; i < bodyNode.Children.Count; i++)
                children.Add(builder.GetChild(bodyNode, i));
        }
        else
        {
            children.Add(body);
        }

        children.Add(builder.AddNode(controlKind, block: BlockIndex.Invalid));
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: block,
            merge: merge,
            startAction: bodyNode.StartAction,
            endAction: bodyNode.EndAction,
            children: children);
    }

    private static bool EndsWithControlStatement(
        Avm1AstArena.Builder builder,
        AstIndex index,
        Avm1AstNodeKind controlKind,
        int depth = 0)
    {
        if (!index.IsValid || depth > 64)
            return false;

        var node = builder.GetNode(index);
        if (node.Kind == controlKind)
            return true;
        return node.Kind is Avm1AstNodeKind.Block &&
            node.Children.Count > 0 &&
            EndsWithControlStatement(
                builder,
                builder.GetChild(node, node.Children.Count - 1),
                controlKind,
                depth + 1);
    }

    private static AstIndex PrependBasicBlockStatements(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock block,
        BuilderState state,
        AstIndex structuredNode,
        ActionIndex endAction)
    {
        var headerStatements = AddBasicBlock(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            block,
            state);
        var headerBlock = builder.GetNode(headerStatements);
        if (headerBlock.Children.Count == 0)
            return structuredNode;

        var compositeChildren = new List<AstIndex>(headerBlock.Children.Count + 1);
        for (var i = 0; i < headerBlock.Children.Count; i++)
            compositeChildren.Add(builder.GetChild(headerBlock, i));
        compositeChildren.Add(structuredNode);
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: block.Index,
            startAction: block.StartAction,
            endAction: endAction,
            children: compositeChildren);
    }

    private static bool TryBuildConditionalLoopBreakArm(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        BuilderState state,
        out AstIndex breakIf)
    {
        breakIf = AstIndex.Invalid;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            inliner.HasConditionalValueForHeader(block.Index) ||
            !state.ActiveLoops.TryPeek(out var loop) ||
            block.Index == loop.ConditionBlock ||
            !OwnsInnermostBreak(state, ActiveControlScopeKind.Loop, loop.Exit))
        {
            return false;
        }

        var allowsVirtualExit = IsTransparentVirtualLoopExit(cfg, loop.Exit);
        var fallThroughBreakPath = new List<BlockIndex>();
        var branchBreakPath = new List<BlockIndex>();
        var fallThroughBreaks =
            !block.FirstSuccessor.IsValid && allowsVirtualExit ||
            TryCollectLinearPathToTarget(
                cfg,
                block.FirstSuccessor,
                loop.Exit,
                loop.ContinueTarget,
                fallThroughBreakPath);
        var branchBreaks =
            !block.SecondSuccessor.IsValid && allowsVirtualExit ||
            TryCollectLinearPathToTarget(
                cfg,
                block.SecondSuccessor,
                loop.Exit,
                loop.ContinueTarget,
                branchBreakPath);
        if (fallThroughBreaks == branchBreaks)
            return false;

        var breakEntry = fallThroughBreaks ? block.FirstSuccessor : block.SecondSuccessor;
        var breakPath = fallThroughBreaks ? fallThroughBreakPath : branchBreakPath;
        var continuationEntry = fallThroughBreaks ? block.SecondSuccessor : block.FirstSuccessor;
        if (!continuationEntry.IsValid || continuationEntry == loop.Exit)
            return false;
        if (breakEntry.IsValid &&
            breakEntry != loop.Exit &&
            (breakPath.Count == 0 ||
                cfg[breakPath[^1]].Terminator is not Avm1BlockTerminatorKind.Jump ||
                cfg[breakPath[^1]].FirstSuccessor != loop.Exit))
        {
            return false;
        }

        var branch = GetInstructions(tacIr, block).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        if (branch.Op is not Avm1TacOp.BranchIf)
            return false;

        var condition = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            branch.Operand0);
        if (fallThroughBreaks)
            condition = InvertCondition(builder, condition);

        var breakBody = breakEntry.IsValid
            ? BuildIfChild(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state,
                cfg,
                breakEntry,
                loop.Exit)
            : builder.AddNode(
                Avm1AstNodeKind.Block,
                block: block.Index,
                startAction: block.EndAction,
                endAction: block.EndAction);
        breakBody = AppendControlStatement(
            builder,
            breakBody,
            Avm1AstNodeKind.Break,
            breakEntry,
            loop.Exit);
        state.ConsumedLoopControlArmBlocks.Add(block.Index);
        foreach (var pathBlock in breakPath)
            state.ConsumedLoopControlArmBlocks.Add(pathBlock);

        var loopExitAction = loop.Exit.IsValid
            ? cfg[loop.Exit].StartAction
            : new ActionIndex(instructions.Count);

        var ifNode = builder.AddNode(
            Avm1AstNodeKind.If,
            block: block.Index,
            merge: loop.Exit,
            startAction: branch.Action,
            endAction: loopExitAction,
            children: [condition, breakBody]);
        var headerStatements = AddBasicBlock(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            block,
            state,
            suppressLoopControl: true);
        var headerBlock = builder.GetNode(headerStatements);
        if (headerBlock.Children.Count == 0)
        {
            breakIf = ifNode;
            return true;
        }

        var compositeChildren = new List<AstIndex>(headerBlock.Children.Count + 1);
        for (var i = 0; i < headerBlock.Children.Count; i++)
            compositeChildren.Add(builder.GetChild(headerBlock, i));
        compositeChildren.Add(ifNode);
        breakIf = builder.AddNode(
            Avm1AstNodeKind.Block,
            block: block.Index,
            startAction: block.StartAction,
            endAction: loopExitAction,
            children: compositeChildren);
        return true;
    }

    private static bool IsTransparentVirtualLoopExit(
        Avm1ControlFlowGraph cfg,
        BlockIndex exit)
    {
        if (!exit.IsValid)
            return true;

        var block = cfg[exit];
        return block.Terminator is Avm1BlockTerminatorKind.Jump &&
            !block.FirstSuccessor.IsValid &&
            !block.SecondSuccessor.IsValid &&
            block.EndAction.Value == block.StartAction.Value + 1;
    }

    private static bool TryCollectLinearPathToTarget(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex target,
        BlockIndex forbidden,
        List<BlockIndex> path)
    {
        var visited = new HashSet<BlockIndex>();
        var current = start;
        while (current.IsValid && current != forbidden && visited.Add(current))
        {
            if (current == target)
                return true;

            path.Add(current);
            var block = cfg[current];
            if (block.Terminator is not (Avm1BlockTerminatorKind.FallThrough or
                Avm1BlockTerminatorKind.Jump))
            {
                return false;
            }

            current = block.FirstSuccessor;
        }

        return current == target;
    }

    private static bool TryBuildConditionalSwitchBreakArm(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        BlockIndex caseBoundary,
        BuilderState state,
        out AstIndex breakIf,
        out BlockIndex continuation)
    {
        breakIf = AstIndex.Invalid;
        continuation = BlockIndex.Invalid;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            inliner.HasConditionalValueForHeader(block.Index) ||
            !state.ActiveSwitches.TryPeek(out var activeSwitch) ||
            !activeSwitch.EmitConditionalBreak ||
            !OwnsInnermostBreak(state, ActiveControlScopeKind.Switch, activeSwitch.Merge))
        {
            return false;
        }

        var fallThroughBreaks = AlwaysReachesSwitchMerge(
            cfg,
            block.FirstSuccessor,
            activeSwitch.Merge,
            caseBoundary,
            state.ReachabilityScratch);
        var branchBreaks = AlwaysReachesSwitchMerge(
            cfg,
            block.SecondSuccessor,
            activeSwitch.Merge,
            caseBoundary,
            state.ReachabilityScratch);
        if (fallThroughBreaks == branchBreaks)
            return false;

        var breakEntry = fallThroughBreaks ? block.FirstSuccessor : block.SecondSuccessor;
        continuation = fallThroughBreaks ? block.SecondSuccessor : block.FirstSuccessor;
        if (!continuation.IsValid ||
            (caseBoundary.IsValid &&
                caseBoundary != activeSwitch.Merge &&
                !CanReachBlock(cfg, continuation, caseBoundary) &&
                !AlwaysTerminatesBefore(cfg, continuation, activeSwitch.Merge)))
        {
            return false;
        }

        var branch = GetInstructions(tacIr, block).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        if (branch.Op is not Avm1TacOp.BranchIf)
            return false;

        var condition = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            state.SsaAccessByInstruction,
            branch.Operand0);
        if (fallThroughBreaks)
            condition = InvertCondition(builder, condition);

        state.ActiveSwitches.Push(activeSwitch with { EmitTerminalBreak = true });
        AstIndex breakBody;
        try
        {
            breakBody = BuildIfChild(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                state,
                cfg,
                breakEntry,
                activeSwitch.Merge);
        }
        finally
        {
            state.ActiveSwitches.Pop();
        }

        breakIf = builder.AddNode(
            Avm1AstNodeKind.If,
            block: block.Index,
            merge: activeSwitch.Merge,
            startAction: branch.Action,
            endAction: activeSwitch.Merge.IsValid
                ? cfg[activeSwitch.Merge].StartAction
                : builder.GetNode(breakBody).EndAction,
            children: [condition, breakBody]);
        return true;
    }

    private static bool AlwaysReachesSwitchMerge(
        Avm1ControlFlowGraph cfg,
        BlockIndex start,
        BlockIndex switchMerge,
        BlockIndex caseBoundary,
        byte[] scratch)
    {
        Array.Clear(scratch);
        return Visit(start);

        bool Visit(BlockIndex block)
        {
            if (block == switchMerge)
                return true;
            if (!block.IsValid ||
                (caseBoundary.IsValid &&
                    caseBoundary != switchMerge &&
                    block == caseBoundary))
            {
                return false;
            }

            var state = scratch[block.Value];
            if (state is 2 or 3)
                return state == 3;
            if (state == 1)
                return false;

            scratch[block.Value] = 1;
            var node = cfg[block];
            var result = node.Terminator switch
            {
                Avm1BlockTerminatorKind.FallThrough or Avm1BlockTerminatorKind.Jump =>
                    Visit(node.FirstSuccessor),
                Avm1BlockTerminatorKind.ConditionalBranch =>
                    Visit(node.FirstSuccessor) && Visit(node.SecondSuccessor),
                _ => false
            };

            scratch[block.Value] = result ? (byte)3 : (byte)2;
            return result;
        }
    }

    private static bool TryGetConditionalSwitchContinuation(
        Avm1ControlFlowGraph cfg,
        Avm1BasicBlock block,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        out BlockIndex continuation,
        out bool invertCondition)
    {
        continuation = BlockIndex.Invalid;
        invertCondition = false;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            inliner.HasConditionalValueForHeader(block.Index) ||
            !state.ActiveSwitches.TryPeek(out var activeSwitch) ||
            !activeSwitch.EmitConditionalBreak ||
            !OwnsInnermostBreak(state, ActiveControlScopeKind.Switch, activeSwitch.Merge))
        {
            return false;
        }

        var allowDirectVirtualExit = !activeSwitch.IsDefault;
        var fallThroughExits = IsSwitchExitSuccessor(
            cfg,
            block.FirstSuccessor,
            activeSwitch.Merge,
            allowDirectVirtualExit);
        var branchExits = IsSwitchExitSuccessor(
            cfg,
            block.SecondSuccessor,
            activeSwitch.Merge,
            allowDirectVirtualExit);
        if (fallThroughExits == branchExits)
            return false;

        continuation = fallThroughExits ? block.SecondSuccessor : block.FirstSuccessor;
        invertCondition = fallThroughExits;
        return continuation.IsValid;
    }

    private static bool IsSwitchExitSuccessor(
        Avm1ControlFlowGraph cfg,
        BlockIndex successor,
        BlockIndex merge,
        bool allowDirectVirtualExit)
    {
        if (successor == merge && (merge.IsValid || allowDirectVirtualExit))
            return true;
        if (!successor.IsValid)
            return false;

        var block = cfg[successor];
        return block.Terminator is Avm1BlockTerminatorKind.Jump &&
            block.FirstSuccessor == merge &&
            block.EndAction.Value == block.StartAction.Value + 1;
    }

    private static void AddConditionalLoopJump(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Avm1BasicBlock block,
        BuilderState state,
        List<AstIndex> statements)
    {
        if (block.Terminator != Avm1BlockTerminatorKind.ConditionalBranch || state.ActiveLoops.Count == 0)
            return;

        var branch = GetInstructions(tacIr, block).LastOrDefault(i => i.Op is Avm1TacOp.BranchIf);
        if (branch.Op is not Avm1TacOp.BranchIf)
            return;

        var loop = state.ActiveLoops.Peek();
        if (block.Index == loop.ConditionBlock)
            return;

        var secondSuccessor = ResolveForInCleanupJumpTarget(
            tacIr,
            valueAnalysis,
            state.ControlFlowGraph,
            state,
            block.SecondSuccessor,
            out var secondCleanupBlock);
        if (TryResolveLoopJump(
            secondSuccessor,
            block,
            state,
            out var jumpKind,
            out var labelTarget))
        {
            if (secondCleanupBlock.IsValid)
                state.ConsumedLoopControlArmBlocks.Add(secondCleanupBlock);
            AddControlJumpIf(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state, block, branch, jumpKind, labelTarget, invertCondition: false, statements);
            return;
        }

        var firstSuccessor = ResolveForInCleanupJumpTarget(
            tacIr,
            valueAnalysis,
            state.ControlFlowGraph,
            state,
            block.FirstSuccessor,
            out var firstCleanupBlock);
        if (TryResolveLoopJump(
            firstSuccessor,
            block,
            state,
            out jumpKind,
            out labelTarget))
        {
            if (firstCleanupBlock.IsValid)
                state.ConsumedLoopControlArmBlocks.Add(firstCleanupBlock);
            AddControlJumpIf(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state, block, branch, jumpKind, labelTarget, invertCondition: true, statements);
        }
    }

    private static BlockIndex ResolveForInCleanupJumpTarget(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ControlFlowGraph cfg,
        BuilderState state,
        BlockIndex target,
        out BlockIndex cleanupBlock)
    {
        cleanupBlock = BlockIndex.Invalid;
        if (!target.IsValid ||
            !state.ActiveLoops.TryPeek(out var loop) ||
            !loop.IsForIn ||
            !TryGetForInEnumerationCleanupSuccessor(
                tacIr,
                valueAnalysis,
                cfg[target],
                out var continuation))
        {
            return target;
        }

        cleanupBlock = target;
        return continuation;
    }

    private static bool TryResolveLoopJump(
        BlockIndex target,
        Avm1BasicBlock block,
        BuilderState state,
        out Avm1AstNodeKind jumpKind,
        out BlockIndex labelTarget)
    {
        jumpKind = default;
        labelTarget = BlockIndex.Invalid;
        var isInnermostBreakable = true;
        var isNearestLoop = true;
        foreach (var scope in state.ActiveControlScopes)
        {
            if (scope.Kind is ActiveControlScopeKind.Loop)
            {
                if ((target == scope.ContinueTarget ||
                        target == scope.ImplicitContinueBlock &&
                        ExitsNestedCodeRegion(
                            state,
                            block.Index,
                            target)) &&
                    block.Index != scope.ImplicitContinueBlock)
                {
                    jumpKind = Avm1AstNodeKind.Continue;
                    labelTarget = isNearestLoop ? BlockIndex.Invalid : scope.Header;
                    return true;
                }

                if (target == scope.BreakTarget)
                {
                    jumpKind = Avm1AstNodeKind.Break;
                    labelTarget = isInnermostBreakable ? BlockIndex.Invalid : scope.Header;
                    return true;
                }

                isNearestLoop = false;
            }

            isInnermostBreakable = false;
        }

        return false;
    }

    private static bool ExitsNestedCodeRegion(
        BuilderState state,
        BlockIndex source,
        BlockIndex target)
    {
        if (!source.IsValid || !target.IsValid)
            return false;

        foreach (var region in state.TryByHeader.Values)
        {
            if (ExitsRange(source, target, region.TryBodyEntry, region.TryExit) ||
                ExitsRange(source, target, region.CatchBodyEntry, region.CatchExit) ||
                ExitsRange(source, target, region.FinallyBodyEntry, region.FinallyExit))
            {
                return true;
            }
        }

        foreach (var region in state.WithByHeader.Values)
        {
            if (ExitsRange(source, target, region.BodyEntry, region.Exit))
                return true;
        }

        return false;

        static bool ExitsRange(
            BlockIndex source,
            BlockIndex target,
            BlockIndex start,
            BlockIndex end) =>
            start.IsValid &&
            end.IsValid &&
            source.Value >= start.Value &&
            source.Value < end.Value &&
            (target.Value < start.Value || target.Value >= end.Value);
    }

    private static bool TryGetConditionalLoopContinuation(
        Avm1BasicBlock block,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        out BlockIndex continuation)
    {
        continuation = BlockIndex.Invalid;
        if (block.Terminator is not Avm1BlockTerminatorKind.ConditionalBranch ||
            inliner.HasConditionalValueForHeader(block.Index) ||
            state.ActiveLoops.Count == 0)
        {
            return false;
        }

        var fallThroughJumps = TryResolveLoopJump(
            block.FirstSuccessor,
            block,
            state,
            out _,
            out _);
        var branchJumps = TryResolveLoopJump(
            block.SecondSuccessor,
            block,
            state,
            out _,
            out _);
        if (fallThroughJumps == branchJumps)
            return false;

        continuation = fallThroughJumps ? block.SecondSuccessor : block.FirstSuccessor;
        return continuation.IsValid;
    }

    private static void AddControlJumpIf(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        BuilderState state,
        Avm1BasicBlock block,
        Avm1TacInstruction branch,
        Avm1AstNodeKind jumpKind,
        BlockIndex labelTarget,
        bool invertCondition,
        List<AstIndex> statements)
    {
        var condition = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, state.SsaAccessByInstruction, branch.Operand0);
        if (invertCondition)
            condition = InvertCondition(builder, condition);

        var jump = builder.AddNode(jumpKind, block: labelTarget);
        var jumpBlock = builder.AddNode(
            Avm1AstNodeKind.Block,
            block: block.Index,
            startAction: block.EndAction,
            endAction: block.EndAction,
            children: [jump]);

        var jumpIf = builder.AddNode(
            Avm1AstNodeKind.If,
            block: block.Index,
            startAction: branch.Action,
            endAction: block.EndAction,
            children: [condition, jumpBlock]);

        statements.Add(jumpIf);
    }

    private static AstIndex BuildExpression(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        ValueIndex value)
    {
        if (!value.IsValid)
        {
            return builder.AddNode(Avm1AstNodeKind.Literal, startAction: ActionIndex.Invalid);
        }

        if (!builder.TryEnterExpressionValue(value.Value))
        {
            return builder.AddNode(
                Avm1AstNodeKind.TempVar,
                startAction: new ActionIndex(value.Value));
        }

        try
        {
            return BuildExpressionCore(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                value);
        }
        finally
        {
            builder.ExitExpressionValue(value.Value);
        }
    }

    private static AstIndex AddOpaqueIfRegion(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1IfRegion region)
    {
        var header = cfg[region.Header];
        var endAction = region.Merge.IsValid
            ? cfg[region.Merge].StartAction
            : new ActionIndex(instructions.Count);
        if (!endAction.IsValid || endAction.Value <= header.StartAction.Value)
            endAction = new ActionIndex(instructions.Count);

        var opaque = builder.AddNode(
            Avm1AstNodeKind.Opaque,
            block: region.Header,
            merge: region.Merge,
            startAction: header.StartAction,
            endAction: endAction);
        return builder.AddNode(
            Avm1AstNodeKind.Block,
            block: region.Header,
            merge: region.Merge,
            startAction: header.StartAction,
            endAction: endAction,
            children: [opaque]);
    }

    private static AstIndex BuildExpressionCore(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        ValueIndex value)
    {

        if (inliner.TryGetValueAlias(value, out var alias))
        {
            return BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                alias);
        }

        if (inliner.TryGetPostfixMemberUpdate(value, out var memberPostfix))
        {
            var target = BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                memberPostfix.Target);
            var member = BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                memberPostfix.Member);
            var memberFact = valueAnalysis[memberPostfix.Member];
            var isComputed = memberFact.ConstantKind is not Avm1ConstantKind.String ||
                memberFact.StringValue is null ||
                !IsIdentifier(memberFact.StringValue);
            var memberAccess = builder.AddNode(
                Avm1AstNodeKind.MemberAccess,
                merge: new BlockIndex(isComputed ? 1 : 0),
                children: [target, member]);
            return builder.AddNode(
                Avm1AstNodeKind.Postfix,
                startAction: new ActionIndex((int)memberPostfix.Opcode),
                children: [memberAccess]);
        }

        if (inliner.TryGetPostfixVariableUpdate(value, out var variablePostfix))
        {
            var target = BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                variablePostfix.Target);
            return builder.AddNode(
                Avm1AstNodeKind.Postfix,
                startAction: new ActionIndex((int)variablePostfix.Opcode),
                children: [target]);
        }

        if (TryGetPostfixRegisterUpdate(
                tacIr,
                inliner,
                ssaAccessByInstruction,
                value,
                out var postfix))
        {
            var register = builder.AddNode(
                Avm1AstNodeKind.Register,
                block: new BlockIndex(postfix.Register),
                merge: new BlockIndex(postfix.Version));
            return builder.AddNode(
                Avm1AstNodeKind.Postfix,
                startAction: new ActionIndex((int)postfix.Opcode),
                children: [register]);
        }

        if (inliner.IsInlined(value) &&
            inliner.TryGetRegisterValueTransport(value, out var registerTransport))
        {
            if (registerTransport.AsAssignmentExpression)
            {
                return BuildRegisterAssignment(
                    builder,
                    instructions,
                    tacIr,
                    valueAnalysis,
                    registerSsa,
                    inliner,
                    ssaAccessByInstruction,
                    registerTransport.Assignment);
            }

            var version = ssaAccessByInstruction.TryGetValue(
                registerTransport.Assignment.Index,
                out var write)
                ? write.Version
                : 0;
            return builder.AddNode(
                Avm1AstNodeKind.Register,
                block: new BlockIndex(registerTransport.Assignment.IntOperand),
                merge: new BlockIndex(version));
        }

        if (inliner.IsInlined(value) &&
            inliner.TryGetTransportAssignment(value, out var transportAssignment))
        {
            return BuildVariableAssignment(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                transportAssignment.Instruction,
                transportAssignment.AssignedValue);
        }

        if (inliner.IsInlined(value) &&
            inliner.TryGetConditionalRecipeRoot(value, out var conditionalRecipeRoot))
        {
            return BuildConditionalRecipeExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                conditionalRecipeRoot);
        }

        if (inliner.IsInlined(value) &&
            inliner.TryGetConditionalValue(value, out var conditional))
        {
            return BuildConditionalExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                conditional);
        }

        if (inliner.TryGetDefinition(value, out var phi) &&
            phi.Op is Avm1TacOp.Phi &&
            TryGetPhiRegisterFallback(
                tacIr,
                valueAnalysis,
                inliner,
                ssaAccessByInstruction,
                phi,
                out var registerFallback))
        {
            return BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                registerFallback);
        }

        var fact = valueAnalysis[value];
        if (fact.ConstantKind != Avm1ConstantKind.Unknown)
        {
            return builder.AddNode(Avm1AstNodeKind.Literal, startAction: new ActionIndex(value.Value));
        }

        if (inliner.IsInlined(value) && inliner.TryGetDefinition(value, out var def))
        {
            return BuildFromInstruction(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, def);
        }

        if (inliner.TryGetDefinition(value, out var nonInlinedDef) && nonInlinedDef.Op == Avm1TacOp.LoadRegister)
        {
            int version = 0;
            if (ssaAccessByInstruction.TryGetValue(nonInlinedDef.Index, out var access))
                version = access.Version;

            return builder.AddNode(
                Avm1AstNodeKind.Register,
                block: new BlockIndex(nonInlinedDef.IntOperand),
                merge: new BlockIndex(version));
        }

        return builder.AddNode(Avm1AstNodeKind.TempVar, startAction: new ActionIndex(value.Value));
    }

    private static bool TryGetPhiRegisterFallback(
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction phi,
        out ValueIndex fallback)
    {
        fallback = ValueIndex.Invalid;
        var register = -1;
        var version = -1;
        foreach (var incoming in GetPhiOperands(tacIr, phi))
        {
            if (valueAnalysis[incoming].ConstantKind is Avm1ConstantKind.Undefined)
                continue;

            if (!TryGetSemanticDefinition(inliner, incoming, out var definition) ||
                definition.Op is not Avm1TacOp.LoadRegister ||
                !ssaAccessByInstruction.TryGetValue(definition.Index, out var access) ||
                access.Kind is not Avm1RegisterAccessKind.Read)
            {
                return false;
            }

            if (!fallback.IsValid)
            {
                fallback = incoming;
                register = access.Register;
                version = access.Version;
            }
            else if (access.Register != register || access.Version != version)
            {
                return false;
            }
        }

        return fallback.IsValid;
    }

    private static AstIndex BuildDirectExpression(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        ValueIndex value)
    {
        if (inliner.TryGetDefinition(value, out var definition) &&
            definition.Op is Avm1TacOp.LoadConstant or
                Avm1TacOp.LoadRegister or
                Avm1TacOp.Copy or
                Avm1TacOp.Unary or
                Avm1TacOp.HostIntrinsic or
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
                Avm1TacOp.Cast)
        {
            return BuildFromInstruction(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                definition);
        }

        return BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            value);
    }

    private static AstIndex BuildConditionalExpression(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1ConditionalValue conditional)
    {
        var condition = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            conditional.Condition);
        var whenTrue = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            conditional.WhenTrue);
        var whenFalse = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            conditional.WhenFalse);

        return SimplifyConditionalExpression(
            builder,
            valueAnalysis,
            conditional.Header,
            condition,
            whenTrue,
            whenFalse,
            conditional.WhenTrue,
            conditional.WhenFalse);
    }

    private static AstIndex BuildConditionalRecipeExpression(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        int recipeIndex)
    {
        var recipe = inliner.GetConditionalRecipeNode(recipeIndex);
        if (recipe.IsLeaf)
        {
            return BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                recipe.Value);
        }

        var condition = BuildExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            recipe.Condition);
        var whenTrue = BuildConditionalRecipeExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            recipe.WhenTrue);
        var whenFalse = BuildConditionalRecipeExpression(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            recipe.WhenFalse);
        var whenTrueRecipe = inliner.GetConditionalRecipeNode(recipe.WhenTrue);
        var whenFalseRecipe = inliner.GetConditionalRecipeNode(recipe.WhenFalse);
        return SimplifyConditionalExpression(
            builder,
            valueAnalysis,
            recipe.Header,
            condition,
            whenTrue,
            whenFalse,
            whenTrueRecipe.IsLeaf ? whenTrueRecipe.Value : ValueIndex.Invalid,
            whenFalseRecipe.IsLeaf ? whenFalseRecipe.Value : ValueIndex.Invalid);
    }

    private static AstIndex SimplifyConditionalExpression(
        Avm1AstArena.Builder builder,
        Avm1ValueAnalysis valueAnalysis,
        BlockIndex header,
        AstIndex condition,
        AstIndex whenTrue,
        AstIndex whenFalse,
        ValueIndex whenTrueValue,
        ValueIndex whenFalseValue)
    {
        condition = StripDoubleNegation(builder, condition);
        whenTrue = StripDoubleNegation(builder, whenTrue);
        whenFalse = StripDoubleNegation(builder, whenFalse);

        if (AreLogicalOpposites(builder, whenTrue, condition) ||
            IsBooleanConstant(valueAnalysis, whenTrueValue, expected: false))
        {
            return AddLogicalBinary(
                builder,
                ActionOpcode.And,
                InvertCondition(builder, condition),
                whenFalse);
        }

        if (AreEquivalentExpressions(builder, whenTrue, condition) ||
            IsBooleanConstant(valueAnalysis, whenTrueValue, expected: true))
        {
            return AddLogicalBinary(builder, ActionOpcode.Or, condition, whenFalse);
        }

        if (AreEquivalentExpressions(builder, whenFalse, condition) ||
            IsBooleanConstant(valueAnalysis, whenFalseValue, expected: false))
        {
            return AddLogicalBinary(builder, ActionOpcode.And, condition, whenTrue);
        }

        if (AreLogicalOpposites(builder, whenFalse, condition) ||
            IsBooleanConstant(valueAnalysis, whenFalseValue, expected: true))
        {
            return AddLogicalBinary(
                builder,
                ActionOpcode.Or,
                InvertCondition(builder, condition),
                whenTrue);
        }

        if (IsInvertedComparison(builder, condition))
        {
            condition = InvertCondition(builder, condition);
            (whenTrue, whenFalse) = (whenFalse, whenTrue);
        }

        return builder.AddNode(
            Avm1AstNodeKind.Conditional,
            block: header,
            children: [condition, whenTrue, whenFalse]);
    }

    private static bool IsInvertedComparison(Avm1AstArena.Builder builder, AstIndex expression)
    {
        if (!expression.IsValid)
            return false;

        var node = builder.GetNode(expression);
        return node.Kind is Avm1AstNodeKind.Binary &&
            node.Merge.Value == 1 &&
            IsInvertibleComparison((ActionOpcode)node.StartAction.Value);
    }

    private static AstIndex StripDoubleNegation(Avm1AstArena.Builder builder, AstIndex expression)
    {
        while (expression.IsValid)
        {
            var outer = builder.GetNode(expression);
            if (outer.Kind is not Avm1AstNodeKind.Unary || outer.StartAction.Value != 1)
                break;

            var innerIndex = builder.GetChild(outer, 0);
            var inner = builder.GetNode(innerIndex);
            if (inner.Kind is not Avm1AstNodeKind.Unary || inner.StartAction.Value != 1)
                break;

            expression = builder.GetChild(inner, 0);
        }

        return expression;
    }

    private static bool IsNegationOf(
        Avm1AstArena.Builder builder,
        AstIndex expression,
        AstIndex expectedOperand)
    {
        if (!expression.IsValid)
            return false;

        var node = builder.GetNode(expression);
        if (node.Kind is Avm1AstNodeKind.Unary && node.StartAction.Value == 1)
            return AreEquivalentExpressions(builder, builder.GetChild(node, 0), expectedOperand);

        if (node.Kind is not Avm1AstNodeKind.Binary || !expectedOperand.IsValid)
            return false;

        var expected = builder.GetNode(expectedOperand);
        return expected.Kind is Avm1AstNodeKind.Binary &&
            node.StartAction == expected.StartAction &&
            node.Merge.Value != expected.Merge.Value &&
            IsInvertibleComparison((ActionOpcode)node.StartAction.Value) &&
            AreEquivalentExpressions(builder, builder.GetChild(node, 0), builder.GetChild(expected, 0)) &&
            AreEquivalentExpressions(builder, builder.GetChild(node, 1), builder.GetChild(expected, 1));
    }

    private static bool AreLogicalOpposites(
        Avm1AstArena.Builder builder,
        AstIndex left,
        AstIndex right)
    {
        return IsNegationOf(builder, left, right) || IsNegationOf(builder, right, left);
    }

    private static bool AreEquivalentExpressions(
        Avm1AstArena.Builder builder,
        AstIndex left,
        AstIndex right)
    {
        if (left == right)
            return true;
        if (!left.IsValid || !right.IsValid)
            return false;

        var leftNode = builder.GetNode(left);
        var rightNode = builder.GetNode(right);
        if (leftNode.Kind != rightNode.Kind ||
            leftNode.Block != rightNode.Block ||
            leftNode.Merge != rightNode.Merge ||
            leftNode.StartAction != rightNode.StartAction ||
            leftNode.Children.Count != rightNode.Children.Count)
        {
            return false;
        }

        for (var i = 0; i < leftNode.Children.Count; i++)
        {
            if (!AreEquivalentExpressions(
                builder,
                builder.GetChild(leftNode, i),
                builder.GetChild(rightNode, i)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBooleanConstant(
        Avm1ValueAnalysis valueAnalysis,
        ValueIndex value,
        bool expected)
    {
        var fact = valueAnalysis[value];
        return fact.ConstantKind is Avm1ConstantKind.Boolean && fact.BooleanValue == expected;
    }

    private static AstIndex AddLogicalBinary(
        Avm1AstArena.Builder builder,
        ActionOpcode opcode,
        AstIndex left,
        AstIndex right)
    {
        return builder.AddNode(
            Avm1AstNodeKind.Binary,
            startAction: new ActionIndex((int)opcode),
            children: [left, right]);
    }

    private static AstIndex BuildFromInstruction(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction inst)
    {
        var result = BuildFromInstructionCore(
            builder,
            instructions,
            tacIr,
            valueAnalysis,
            registerSsa,
            inliner,
            ssaAccessByInstruction,
            inst);
        if (result.IsValid &&
            inst.Action.IsValid &&
            !builder.GetNode(result).OriginAction.IsValid)
        {
            builder.SetOriginAction(result, inst.Action);
        }

        return result;
    }

    private static AstIndex BuildFromInstructionCore(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction inst)
    {
        switch (inst.Op)
        {
            case Avm1TacOp.Phi:
                if (inliner.TryGetConditionalValue(inst.Result, out var conditional))
                {
                    return BuildConditionalExpression(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        ssaAccessByInstruction,
                        conditional);
                }

                return builder.AddNode(Avm1AstNodeKind.TempVar, startAction: new ActionIndex(inst.Result.Value));

            case Avm1TacOp.LoadConstant:
                return builder.AddNode(Avm1AstNodeKind.Literal, startAction: new ActionIndex(inst.Result.Value));

            case Avm1TacOp.LoadRegister:
                {
                    int version = 0;
                    if (ssaAccessByInstruction.TryGetValue(inst.Index, out var access))
                        version = access.Version;

                    return builder.AddNode(
                        Avm1AstNodeKind.Register,
                        block: new BlockIndex(inst.IntOperand),
                        merge: new BlockIndex(version));
                }

            case Avm1TacOp.Copy:
                return BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);

            case Avm1TacOp.Unary:
                {
                    var operand = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);
                    bool isNot = instructions[inst.Action].Action is Swf4.ActionNot;
                    if (inst.Opcode is ActionOpcode.TargetPath or
                        ActionOpcode.ToNumber or ActionOpcode.ToString or
                        ActionOpcode.ToInteger or ActionOpcode.RandomNumber or
                        ActionOpcode.StringLength or ActionOpcode.MBStringLength or
                        ActionOpcode.CharToAscii or ActionOpcode.AsciiToChar or
                        ActionOpcode.MBCharToAscii or ActionOpcode.MBAsciiToChar)
                    {
                        return builder.AddNode(
                            Avm1AstNodeKind.Intrinsic,
                            startAction: new ActionIndex((int)inst.Opcode),
                            children: [operand]);
                    }

                    return isNot
                        ? InvertCondition(builder, operand)
                        : builder.AddNode(
                            Avm1AstNodeKind.Unary,
                            startAction: new ActionIndex((int)inst.Opcode),
                            children: [operand]);
                }

            case Avm1TacOp.Intrinsic:
                {
                    var first = BuildExpression(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        ssaAccessByInstruction,
                        inst.Operand0);
                    var second = BuildExpression(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        ssaAccessByInstruction,
                        inst.Operand1);
                    var third = BuildExpression(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        ssaAccessByInstruction,
                        inst.Operand2);
                    return builder.AddNode(
                        Avm1AstNodeKind.Intrinsic,
                        startAction: new ActionIndex((int)inst.Opcode),
                        children: [first, second, third]);
                }

            case Avm1TacOp.HostIntrinsic:
                {
                    var children = new List<AstIndex>(inst.OperandCount);
                    if (inst.OperandCount >= 1)
                    {
                        children.Add(BuildExpression(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            ssaAccessByInstruction,
                            inst.Operand0));
                    }
                    if (inst.OperandCount >= 2)
                    {
                        children.Add(BuildExpression(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            ssaAccessByInstruction,
                            inst.Operand1));
                    }
                    if (inst.OperandCount >= 3)
                    {
                        children.Add(BuildExpression(
                            builder,
                            instructions,
                            tacIr,
                            valueAnalysis,
                            registerSsa,
                            inliner,
                            ssaAccessByInstruction,
                            inst.Operand2));
                    }
                    return builder.AddNode(
                        Avm1AstNodeKind.Intrinsic,
                        startAction: new ActionIndex((int)inst.Opcode),
                        children: children);
                }

            case Avm1TacOp.VariadicHostIntrinsic:
                {
                    var children = new List<AstIndex>(inst.OperandCount);
                    AddValueOperands(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        ssaAccessByInstruction,
                        inst,
                        children);
                    return builder.AddNode(
                        Avm1AstNodeKind.Intrinsic,
                        startAction: new ActionIndex((int)inst.Opcode),
                        children: children);
                }

            case Avm1TacOp.Binary:
                {
                    var left = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);
                    var right = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand1);
                    return builder.AddNode(
                        Avm1AstNodeKind.Binary,
                        startAction: new ActionIndex((int)inst.Opcode),
                        children: [left, right]);
                }

            case Avm1TacOp.GetVariable:
                {
                    var nameExpr = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);
                    return builder.AddNode(Avm1AstNodeKind.Variable, children: [nameExpr]);
                }

            case Avm1TacOp.GetMember:
                {
                    var target = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);
                    var member = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand1);

                    bool isComputed = true;
                    var nameFact = valueAnalysis[inst.Operand1];
                    if (nameFact.ConstantKind is Avm1ConstantKind.String && nameFact.StringValue is not null && IsIdentifier(nameFact.StringValue))
                    {
                        isComputed = false;
                    }

                    return builder.AddNode(
                        Avm1AstNodeKind.MemberAccess,
                        merge: new BlockIndex(isComputed ? 1 : 0),
                        children: [target, member]);
                }

            case Avm1TacOp.Delete:
                {
                    AstIndex deleted;
                    if (inst.Opcode is ActionOpcode.Delete)
                    {
                        var target = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);
                        var member = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand1);
                        var nameFact = valueAnalysis[inst.Operand1];
                        var isComputed = nameFact.ConstantKind is not Avm1ConstantKind.String ||
                            nameFact.StringValue is null ||
                            !IsIdentifier(nameFact.StringValue);
                        deleted = builder.AddNode(
                            Avm1AstNodeKind.MemberAccess,
                            merge: new BlockIndex(isComputed ? 1 : 0),
                            children: [target, member]);
                    }
                    else
                    {
                        var name = BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0);
                        deleted = builder.AddNode(Avm1AstNodeKind.Variable, children: [name]);
                    }

                    return builder.AddNode(Avm1AstNodeKind.Delete, children: [deleted]);
                }

            case Avm1TacOp.GetTime:
                return builder.AddNode(
                    Avm1AstNodeKind.Intrinsic,
                    startAction: new ActionIndex((int)ActionOpcode.GetTime));

            case Avm1TacOp.InitArray:
                {
                    var children = new List<AstIndex>(inst.OperandCount);
                    AddValueOperands(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst, children);
                    return builder.AddNode(Avm1AstNodeKind.ArrayLiteral, children: children);
                }

            case Avm1TacOp.InitObject:
                {
                    var children = new List<AstIndex>(inst.OperandCount * 2);
                    AddObjectValueOperands(
                        builder,
                        instructions,
                        tacIr,
                        valueAnalysis,
                        registerSsa,
                        inliner,
                        ssaAccessByInstruction,
                        inst,
                        children);
                    return builder.AddNode(Avm1AstNodeKind.ObjectLiteral, children: children);
                }

            case Avm1TacOp.NewObject:
                {
                    var children = new List<AstIndex>(inst.OperandCount + 1)
                    {
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0)
                    };
                    AddValueOperands(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst, children);
                    return builder.AddNode(Avm1AstNodeKind.NewObject, children: children);
                }

            case Avm1TacOp.NewMethod:
                {
                    var children = new List<AstIndex>(inst.OperandCount + 2)
                    {
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand1),
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0)
                    };
                    AddValueOperands(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst, children);
                    return builder.AddNode(Avm1AstNodeKind.NewMethod, children: children);
                }

            case Avm1TacOp.CallFunction:
                {
                    var children = new List<AstIndex>(inst.OperandCount + 1)
                    {
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0)
                    };
                    AddValueOperands(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst, children);
                    return builder.AddNode(Avm1AstNodeKind.CallFunction, children: children);
                }

            case Avm1TacOp.CallMethod:
                {
                    var children = new List<AstIndex>(inst.OperandCount + 2)
                    {
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand1),
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0)
                    };
                    AddValueOperands(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst, children);
                    return builder.AddNode(Avm1AstNodeKind.CallMethod, children: children);
                }

            case Avm1TacOp.FunctionLiteral:
                return builder.AddNode(
                    Avm1AstNodeKind.FunctionLiteral,
                    startAction: inst.Action);

            case Avm1TacOp.Cast:
                return builder.AddNode(
                    Avm1AstNodeKind.CallFunction,
                    children:
                    [
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand0),
                        BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, inst.Operand1)
                    ]);

            default:
                return builder.AddNode(Avm1AstNodeKind.TempVar, startAction: new ActionIndex(inst.Result.Value));
        }
    }

    private static void AddValueOperands(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction inst,
        List<AstIndex> children)
    {
        if (!inst.Operand2.IsValid || inst.OperandCount == 0)
            return;

        var start = inst.Operand2.Value;
        var operandSlots = inst.Op is Avm1TacOp.InitObject ? inst.OperandCount * 2 : inst.OperandCount;
        var end = Math.Min(start + operandSlots, tacIr.ValueOperands.Count);
        for (var i = start; i < end; i++)
            children.Add(BuildExpression(builder, instructions, tacIr, valueAnalysis, registerSsa, inliner, ssaAccessByInstruction, tacIr.ValueOperands[i]));
    }

    private static void AddObjectValueOperands(
        Avm1AstArena.Builder builder,
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1ExpressionInliner inliner,
        Dictionary<IrIndex, Avm1RegisterAccess> ssaAccessByInstruction,
        Avm1TacInstruction inst,
        List<AstIndex> children)
    {
        if (!inst.Operand2.IsValid || inst.OperandCount == 0)
            return;

        var start = inst.Operand2.Value;
        var availableSlots = Math.Max(0, tacIr.ValueOperands.Count - start);
        var pairCount = Math.Min(inst.OperandCount, availableSlots / 2);
        for (var pair = pairCount - 1; pair >= 0; pair--)
        {
            var keyIndex = start + pair * 2;
            children.Add(BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                tacIr.ValueOperands[keyIndex]));
            children.Add(BuildExpression(
                builder,
                instructions,
                tacIr,
                valueAnalysis,
                registerSsa,
                inliner,
                ssaAccessByInstruction,
                tacIr.ValueOperands[keyIndex + 1]));
        }
    }

    private static IEnumerable<Avm1TacInstruction> GetInstructions(Avm1TacIr tacIr, Avm1BasicBlock block)
    {
        foreach (var instruction in tacIr.Instructions)
        {
            if (instruction.Action.Value < block.StartAction.Value || instruction.Action.Value >= block.EndAction.Value)
                continue;

            yield return instruction;
        }
    }

    private static bool IsNamedFunctionDefinition(
        Avm1InstructionTable instructions,
        Avm1TacInstruction instruction)
    {
        if (instruction.Op is not Avm1TacOp.FunctionLiteral ||
            !instruction.Action.IsValid)
        {
            return false;
        }

        return instructions[instruction.Action].Action switch
        {
            ActionDefineFunction function => function.Name.Length > 0,
            ActionDefineFunction2 function => function.Name.Length > 0,
            _ => false
        };
    }

    private static bool DefinesValue(Avm1TacOp op) =>
        Avm1TacEffectAnalysis.DefinesValue(op);

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] != '_' && value[0] != '$' && !char.IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
            if (value[i] != '_' && value[i] != '$' && !char.IsAsciiLetterOrDigit(value[i]))
                return false;
        return true;
    }
}
