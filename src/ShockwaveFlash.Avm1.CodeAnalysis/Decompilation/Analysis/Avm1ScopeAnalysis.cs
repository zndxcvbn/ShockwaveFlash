using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ScopeAnalysis
{
    public Avm1SymbolTable SymbolTable { get; } = new();

    public static Avm1ScopeAnalysis Run(
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ValueOriginAnalysis valueOrigins,
        FunctionContext? ctx = null)
    {
        var analysis = new Avm1ScopeAnalysis();
        analysis.BuildSymbols(tacIr, registerSsa, valueAnalysis, valueOrigins, ctx);
        return analysis;
    }

    private void BuildSymbols(
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ValueAnalysis valueAnalysis,
        Avm1ValueOriginAnalysis valueOrigins,
        FunctionContext? ctx)
    {
        var preloadNames = new Dictionary<int, string>();
        if (ctx is not null)
        {
            var reg = 1;
            if (ctx.PreloadThis)
                preloadNames[reg++] = "this";
            if (ctx.PreloadArguments)
                preloadNames[reg++] = "arguments";
            if (ctx.PreloadSuper)
                preloadNames[reg++] = "super";
            if (ctx.PreloadRoot)
                preloadNames[reg++] = "_root";
            if (ctx.PreloadParent)
                preloadNames[reg++] = "_parent";
            if (ctx.PreloadGlobal)
                preloadNames[reg++] = "_global";

            foreach (var p in ctx.Parameters)
            {
                if (p.Register > 0 && p.Name.Length > 0)
                    preloadNames[p.Register] = p.Name;
                SymbolTable.ReserveName(p.Name);
            }
        }

        foreach (var instruction in tacIr.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.DefineLocal)
                continue;

            var name = valueAnalysis[instruction.Operand0];
            if (name.ConstantKind is Avm1ConstantKind.String &&
                name.StringValue is { Length: > 0 } localName)
            {
                SymbolTable.ReserveName(localName);
            }
        }

        var registerKeys = new SortedSet<(int Register, int Version)>();
        foreach (var access in registerSsa.Accesses)
            registerKeys.Add((access.Register, access.Version));
        foreach (var phi in registerSsa.PhiNodes)
        {
            registerKeys.Add((phi.Register, phi.Version));
            foreach (var incomingVersion in phi.IncomingVersions)
                registerKeys.Add((phi.Register, incomingVersion));
        }

        var writeAccesses = registerSsa.Accesses
            .Where(a => a.Kind == Avm1RegisterAccessKind.Write)
            .ToDictionary(a => (a.Register, a.Version));
        var livePhiNodes = FindLivePhiNodes(registerSsa);

        var versionGroups = new RegisterVersionGroups();
        foreach (var key in registerKeys)
            versionGroups.Add(key);
        foreach (var phi in livePhiNodes)
        {
            var result = (phi.Register, phi.Version);
            foreach (var incomingVersion in phi.IncomingVersions)
                versionGroups.Union(result, (phi.Register, incomingVersion));
        }

        CoalesceReadModifyWrites(tacIr, registerSsa, versionGroups);
        CoalesceDeclarationSeeds(
            registerSsa,
            livePhiNodes,
            valueAnalysis,
            writeAccesses,
            versionGroups,
            registerKeys);

        var groups = registerKeys
            .GroupBy(versionGroups.Find)
            .OrderBy(group => group.Key.Register)
            .ThenBy(group => group.Key.Version)
            .ToArray();
        var accessedGroupKeys = registerSsa.Accesses
            .Select(access => versionGroups.Find((
                access.Register,
                access.Version)))
            .ToHashSet();
        var readGroupKeys = registerSsa.Accesses
            .Where(access => access.Kind is Avm1RegisterAccessKind.Read)
            .Select(access => versionGroups.Find((
                access.Register,
                access.Version)))
            .ToHashSet();
        var canonicalGroupKeys = new HashSet<(int Register, int Version)>();
        foreach (var registerGroups in groups.GroupBy(group => group.Key.Register))
        {
            var readGroups = registerGroups
                .Where(group => readGroupKeys.Contains(group.Key))
                .ToArray();
            if (readGroups.Length == 1)
            {
                canonicalGroupKeys.Add(readGroups[0].Key);
                continue;
            }

            if (readGroups.Length == 0)
            {
                var accessedGroups = registerGroups
                    .Where(group => accessedGroupKeys.Contains(group.Key))
                    .ToArray();
                if (accessedGroups.Length == 1)
                    canonicalGroupKeys.Add(accessedGroups[0].Key);
            }
        }

        foreach (var group in groups)
        {
            var keys = group.OrderBy(key => key.Version).ToArray();
            var type = Avm1InferredType.Unknown;
            string? preloadName = null;
            string? suggestedName = null;
            var isDeclared = false;

            foreach (var key in keys)
            {
                if (key.Version == 0 && preloadNames.TryGetValue(key.Register, out var knownName))
                {
                    preloadName = knownName;
                    isDeclared = true;
                    continue;
                }

                if (!writeAccesses.TryGetValue(key, out var write))
                    continue;

                var inst = tacIr[write.Instruction];
                var writtenValue = write.Source.IsValid
                    ? write.Source
                    : write.Value;
                var fact = valueAnalysis[writtenValue];
                if (type is Avm1InferredType.Unknown && fact.Type is not Avm1InferredType.Unknown)
                    type = fact.Type;

                if (inst.Op is Avm1TacOp.CatchEnter)
                    suggestedName ??= SuggestCatchName(valueOrigins[writtenValue]);
                suggestedName ??= SuggestNameFromDef(inst, tacIr, valueAnalysis);
            }

            var representative = keys[0];
            var symbol = SymbolTable.GetOrCreateRegisterSymbol(
                representative.Register,
                representative.Version,
                type);
            if (preloadName is not null)
            {
                symbol.Name = preloadName;
            }
            else
            {
                var preferredName = suggestedName ?? (canonicalGroupKeys.Contains(group.Key)
                    ? $"_loc{representative.Register.ToString(System.Globalization.CultureInfo.InvariantCulture)}_"
                    : symbol.Name);
                SymbolTable.SetPreferredName(symbol, preferredName);
            }
            symbol.IsDeclared = isDeclared;

            foreach (var key in keys)
                SymbolTable.AliasRegisterSymbol(key.Register, key.Version, symbol);
        }
    }

    private static Avm1RegisterPhiNode[] FindLivePhiNodes(
        Avm1RegisterSsa registerSsa)
    {
        var liveVersions = registerSsa.Accesses
            .Where(access => access.Kind is Avm1RegisterAccessKind.Read)
            .Select(access => (access.Register, access.Version))
            .ToHashSet();
        var livePhiResults = new HashSet<(int Register, int Version)>();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var phi in registerSsa.PhiNodes)
            {
                var result = (phi.Register, phi.Version);
                if (!liveVersions.Contains(result) || !livePhiResults.Add(result))
                    continue;

                changed = true;
                foreach (var incomingVersion in phi.IncomingVersions)
                    liveVersions.Add((phi.Register, incomingVersion));
            }
        }

        return registerSsa.PhiNodes
            .Where(phi => livePhiResults.Contains((phi.Register, phi.Version)))
            .ToArray();
    }

    private static void CoalesceDeclarationSeeds(
        Avm1RegisterSsa registerSsa,
        IReadOnlyList<Avm1RegisterPhiNode> livePhiNodes,
        Avm1ValueAnalysis valueAnalysis,
        Dictionary<(int Register, int Version), Avm1RegisterAccess> writeAccesses,
        RegisterVersionGroups versionGroups,
        IEnumerable<(int Register, int Version)> registerKeys)
    {
        var readKeys = registerSsa.Accesses
            .Where(access => access.Kind == Avm1RegisterAccessKind.Read)
            .Select(access => (access.Register, access.Version))
            .ToHashSet();
        var phiResults = livePhiNodes
            .Select(phi => (phi.Register, phi.Version))
            .ToHashSet();

        foreach (var registerGroup in registerKeys.GroupBy(key => key.Register))
        {
            CoalesceDeadPhiDeclarationSeedInputs(registerGroup);
            var groups = registerGroup
                .GroupBy(versionGroups.Find)
                .Select(group => group.OrderBy(key => key.Version).ToArray())
                .ToArray();
            if (groups.Length != 2)
                continue;

            for (var seedGroupIndex = 0; seedGroupIndex < groups.Length; seedGroupIndex++)
            {
                var seedGroup = groups[seedGroupIndex];
                if (seedGroup.Length != 1)
                    continue;

                var seed = seedGroup[0];
                if (readKeys.Contains(seed) || !writeAccesses.TryGetValue(seed, out var seedWrite))
                    continue;

                var seedValue = seedWrite.Source.IsValid ? seedWrite.Source : seedWrite.Value;
                if (!seedValue.IsValid || valueAnalysis[seedValue].ConstantKind != Avm1ConstantKind.Undefined)
                    continue;

                var valueGroup = groups[1 - seedGroupIndex];
                if (!valueGroup.Any(phiResults.Contains))
                    continue;

                var valueWritesFollowSeed = valueGroup
                    .Where(writeAccesses.ContainsKey)
                    .Select(key => writeAccesses[key])
                    .All(write => write.Instruction.Value > seedWrite.Instruction.Value);
                if (!valueWritesFollowSeed)
                    continue;

                versionGroups.Union(seed, valueGroup[0]);
                break;
            }
        }

        void CoalesceDeadPhiDeclarationSeedInputs(
            IEnumerable<(int Register, int Version)> registerGroup)
        {
            var keys = registerGroup.ToArray();
            var register = keys[0].Register;
            foreach (var seed in keys)
            {
                if (readKeys.Contains(seed) ||
                    !writeAccesses.TryGetValue(seed, out var seedWrite))
                {
                    continue;
                }

                var seedValue = seedWrite.Source.IsValid
                    ? seedWrite.Source
                    : seedWrite.Value;
                if (!seedValue.IsValid ||
                    valueAnalysis[seedValue].ConstantKind is not
                        Avm1ConstantKind.Undefined)
                {
                    continue;
                }

                foreach (var phi in registerSsa.PhiNodes)
                {
                    if (phi.Register != register ||
                        phiResults.Contains((phi.Register, phi.Version)) ||
                        !phi.IncomingVersions.Contains(seed.Version))
                    {
                        continue;
                    }

                    var incoming = phi.IncomingVersions
                        .Where(version => version != seed.Version)
                        .Distinct()
                        .Select(version => (Register: register, Version: version))
                        .ToArray();
                    if (incoming.Length == 0 || incoming.Any(key =>
                        !writeAccesses.TryGetValue(key, out var write) ||
                        write.Instruction.Value <= seedWrite.Instruction.Value))
                    {
                        continue;
                    }

                    foreach (var key in incoming)
                        versionGroups.Union(seed, key);
                }
            }
        }
    }

    private static void CoalesceReadModifyWrites(
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        RegisterVersionGroups versionGroups)
    {
        var definitions = tacIr.Instructions
            .Where(instruction =>
                instruction.Result.IsValid &&
                instruction.Op is not Avm1TacOp.StoreRegister)
            .GroupBy(instruction => instruction.Result)
            .ToDictionary(group => group.Key, group => group.First());
        var accesses = registerSsa.Accesses.ToDictionary(access => access.Instruction);

        foreach (var write in registerSsa.Accesses)
        {
            if (write.Kind != Avm1RegisterAccessKind.Write ||
                !write.Source.IsValid ||
                !TryFindRegisterReadVersion(
                    write.Source,
                    write.Register,
                    definitions,
                    accesses,
                    depth: 0,
                    out var sourceVersion))
            {
                continue;
            }

            versionGroups.Union(
                (write.Register, write.Version),
                (write.Register, sourceVersion));
        }
    }

    internal static IReadOnlyList<Avm1RegisterSymbolCoalescing>
        FindNonInterferingMaterializedRegisterVersions(
        Avm1TacIr tacIr,
        Avm1ControlFlowGraph controlFlowGraph,
        Avm1RegionAnalysis regionAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1SymbolTable symbolTable,
        Func<Avm1RegisterAccess, bool> isMaterialized)
    {
        var result = new List<Avm1RegisterSymbolCoalescing>();
        var dependencies = BuildRegisterValueDependencies(tacIr, registerSsa);
        var materializedSymbols = registerSsa.Accesses
            .Where(isMaterialized)
            .Select(access => symbolTable.TryGetRegisterSymbol(
                access.Register,
                access.Version,
                out var symbol)
                    ? symbol
                    : null)
            .Where(symbol => symbol is not null &&
                Avm1SymbolTable.IsGeneratedRegisterSymbol(symbol))
            .Cast<Avm1Symbol>()
            .ToHashSet();
        var interference = BuildRegisterSymbolInterference(
            tacIr,
            controlFlowGraph,
            regionAnalysis,
            registerSsa,
            symbolTable,
            materializedSymbols,
            dependencies);
        var intervalBySymbol = new Dictionary<Avm1Symbol, RegisterLiveInterval>();

        foreach (var access in registerSsa.Accesses)
        {
            if (!symbolTable.TryGetRegisterSymbol(
                    access.Register,
                    access.Version,
                    out var symbol))
            {
                continue;
            }

            var interval = intervalBySymbol.GetValueOrDefault(
                symbol,
                new RegisterLiveInterval(symbol, int.MaxValue, int.MinValue));

            var instruction = access.Instruction.Value;
            var start = access.Kind is Avm1RegisterAccessKind.Write
                ? Math.Min(interval.Start, instruction)
                : interval.Start;
            if (access.Version == 0)
                start = Math.Min(start, 0);

            intervalBySymbol[symbol] = interval with
            {
                Start = start,
                End = Math.Max(interval.End, instruction)
            };
        }

        foreach (var instruction in tacIr.Instructions)
        {
            foreach (var operand in GetConsumedValues(tacIr, instruction))
            {
                if (!dependencies.TryGetValue(operand, out var registerDependencies))
                    continue;

                foreach (var dependency in registerDependencies)
                {
                    if (!symbolTable.TryGetRegisterSymbol(
                            dependency.Register,
                            dependency.Version,
                            out var symbol) ||
                        !intervalBySymbol.TryGetValue(symbol, out var interval))
                    {
                        continue;
                    }

                    intervalBySymbol[symbol] = interval with
                    {
                        End = Math.Max(interval.End, instruction.Index.Value)
                    };
                }
            }
        }

        var intervals = intervalBySymbol.Values
            .Where(interval => materializedSymbols.Contains(interval.Symbol))
            .Select(interval => interval with
            {
                Start = interval.Start == int.MaxValue ? 0 : interval.Start,
                End = interval.End == int.MinValue
                    ? (interval.Start == int.MaxValue ? 0 : interval.Start)
                    : interval.End
            })
            .GroupBy(interval => interval.Symbol.Register);

        foreach (var registerIntervals in intervals)
        {
            var colors = new List<List<RegisterLiveInterval>>();
            foreach (var interval in registerIntervals
                .OrderBy(interval => interval.Start)
                .ThenBy(interval => interval.End)
                .ThenBy(interval => interval.Symbol.Version))
            {
                var color = colors.FirstOrDefault(existing =>
                    existing.All(other => !interference.Contains(
                        SymbolPair.Create(interval.Symbol, other.Symbol))));
                if (color is null)
                {
                    colors.Add([interval]);
                    continue;
                }

                result.Add(new Avm1RegisterSymbolCoalescing(
                    color[0].Symbol,
                    interval.Symbol));
                color.Add(interval);
            }
        }

        return result;
    }

    private static HashSet<SymbolPair> BuildRegisterSymbolInterference(
        Avm1TacIr tacIr,
        Avm1ControlFlowGraph controlFlowGraph,
        Avm1RegionAnalysis regionAnalysis,
        Avm1RegisterSsa registerSsa,
        Avm1SymbolTable symbolTable,
        HashSet<Avm1Symbol> materializedSymbols,
        IReadOnlyDictionary<ValueIndex, HashSet<(int Register, int Version)>> dependencies)
    {
        var interference = new HashSet<SymbolPair>();
        if (materializedSymbols.Count < 2)
            return interference;
        if (controlFlowGraph.Count == 0)
        {
            var symbols = materializedSymbols.ToArray();
            for (var left = 0; left < symbols.Length; left++)
                for (var right = left + 1; right < symbols.Length; right++)
                    interference.Add(SymbolPair.Create(symbols[left], symbols[right]));
            return interference;
        }

        var accesses = registerSsa.Accesses.ToDictionary(access => access.Instruction);
        var instructionsByBlock = new List<Avm1TacInstruction>[controlFlowGraph.Count];
        var usesByBlock = new HashSet<Avm1Symbol>[controlFlowGraph.Count];
        var definitionsByBlock = new HashSet<Avm1Symbol>[controlFlowGraph.Count];
        for (var block = 0; block < controlFlowGraph.Count; block++)
        {
            instructionsByBlock[block] = [];
            usesByBlock[block] = [];
            definitionsByBlock[block] = [];
        }

        foreach (var instruction in tacIr.Instructions)
        {
            if (!instruction.Action.IsValid ||
                !controlFlowGraph.TryGetBlockForAction(instruction.Action, out var block))
            {
                continue;
            }

            instructionsByBlock[block.Value].Add(instruction);
            foreach (var symbol in GetUsedRegisterSymbols(
                tacIr,
                instruction,
                symbolTable,
                materializedSymbols,
                dependencies))
            {
                if (!definitionsByBlock[block.Value].Contains(symbol))
                    usesByBlock[block.Value].Add(symbol);
            }

            if (TryGetDefinedRegisterSymbol(
                instruction,
                accesses,
                symbolTable,
                materializedSymbols,
                out var defined))
            {
                definitionsByBlock[block.Value].Add(defined);
            }
        }

        AddConditionalRegionInterference(
            tacIr,
            controlFlowGraph,
            regionAnalysis,
            symbolTable,
            materializedSymbols,
            dependencies,
            instructionsByBlock,
            definitionsByBlock,
            interference);

        var liveIn = new HashSet<Avm1Symbol>[controlFlowGraph.Count];
        var liveOut = new HashSet<Avm1Symbol>[controlFlowGraph.Count];
        for (var block = 0; block < controlFlowGraph.Count; block++)
        {
            liveIn[block] = [];
            liveOut[block] = [];
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var blockIndex = controlFlowGraph.Count - 1; blockIndex >= 0; blockIndex--)
            {
                var nextOut = new HashSet<Avm1Symbol>();
                foreach (var successor in GetSuccessors(
                    controlFlowGraph,
                    new BlockIndex(blockIndex)))
                {
                    nextOut.UnionWith(liveIn[successor.Value]);
                }

                var nextIn = new HashSet<Avm1Symbol>(nextOut);
                nextIn.ExceptWith(definitionsByBlock[blockIndex]);
                nextIn.UnionWith(usesByBlock[blockIndex]);
                if (!liveOut[blockIndex].SetEquals(nextOut))
                {
                    liveOut[blockIndex] = nextOut;
                    changed = true;
                }
                if (!liveIn[blockIndex].SetEquals(nextIn))
                {
                    liveIn[blockIndex] = nextIn;
                    changed = true;
                }
            }
        }

        for (var blockIndex = 0; blockIndex < controlFlowGraph.Count; blockIndex++)
        {
            var live = new HashSet<Avm1Symbol>(liveOut[blockIndex]);
            var blockInstructions = instructionsByBlock[blockIndex];
            for (var instructionIndex = blockInstructions.Count - 1;
                instructionIndex >= 0;
                instructionIndex--)
            {
                var instruction = blockInstructions[instructionIndex];
                if (TryGetDefinedRegisterSymbol(
                    instruction,
                    accesses,
                    symbolTable,
                    materializedSymbols,
                    out var defined))
                {
                    foreach (var other in live)
                    {
                        if (!ReferenceEquals(defined, other))
                            interference.Add(SymbolPair.Create(defined, other));
                    }
                    live.Remove(defined);
                }

                live.UnionWith(GetUsedRegisterSymbols(
                    tacIr,
                    instruction,
                    symbolTable,
                    materializedSymbols,
                    dependencies));
            }
        }

        return interference;
    }

    private static void AddConditionalRegionInterference(
        Avm1TacIr tacIr,
        Avm1ControlFlowGraph controlFlowGraph,
        Avm1RegionAnalysis regionAnalysis,
        Avm1SymbolTable symbolTable,
        HashSet<Avm1Symbol> materializedSymbols,
        IReadOnlyDictionary<ValueIndex, HashSet<(int Register, int Version)>> dependencies,
        List<Avm1TacInstruction>[] instructionsByBlock,
        HashSet<Avm1Symbol>[] definitionsByBlock,
        HashSet<SymbolPair> interference)
    {
        foreach (var region in regionAnalysis.IfRegions)
        {
            if (!region.Header.IsValid ||
                region.Header.Value >= instructionsByBlock.Length)
            {
                continue;
            }

            var branch = instructionsByBlock[region.Header.Value]
                .LastOrDefault(instruction => instruction.Op is Avm1TacOp.BranchIf);
            if (branch.Op is not Avm1TacOp.BranchIf)
                continue;

            var conditionSymbols = GetUsedRegisterSymbols(
                    tacIr,
                    branch,
                    symbolTable,
                    materializedSymbols,
                    dependencies)
                .ToArray();
            if (conditionSymbols.Length == 0)
                continue;

            var armBlocks = CollectBlocksBeforeMerge(
                controlFlowGraph,
                region.FallThroughEntry,
                region.Merge);
            armBlocks.UnionWith(CollectBlocksBeforeMerge(
                controlFlowGraph,
                region.BranchEntry,
                region.Merge));
            foreach (var block in armBlocks)
            {
                foreach (var defined in definitionsByBlock[block.Value])
                {
                    foreach (var condition in conditionSymbols)
                    {
                        if (condition.Register == defined.Register &&
                            !ReferenceEquals(condition, defined))
                        {
                            interference.Add(SymbolPair.Create(condition, defined));
                        }
                    }
                }
            }
        }
    }

    private static HashSet<BlockIndex> CollectBlocksBeforeMerge(
        Avm1ControlFlowGraph controlFlowGraph,
        BlockIndex entry,
        BlockIndex merge)
    {
        var result = new HashSet<BlockIndex>();
        if (!entry.IsValid || entry == merge)
            return result;

        var pending = new Stack<BlockIndex>();
        pending.Push(entry);
        while (pending.TryPop(out var block))
        {
            if (!block.IsValid || block == merge || !result.Add(block))
                continue;
            foreach (var successor in GetSuccessors(controlFlowGraph, block))
            {
                if (successor != merge && !result.Contains(successor))
                    pending.Push(successor);
            }
        }
        return result;
    }

    private static IEnumerable<Avm1Symbol> GetUsedRegisterSymbols(
        Avm1TacIr tacIr,
        Avm1TacInstruction instruction,
        Avm1SymbolTable symbolTable,
        HashSet<Avm1Symbol> materializedSymbols,
        IReadOnlyDictionary<ValueIndex, HashSet<(int Register, int Version)>> dependencies)
    {
        var yielded = new HashSet<Avm1Symbol>();
        foreach (var operand in GetConsumedValues(tacIr, instruction))
        {
            if (!dependencies.TryGetValue(operand, out var registerDependencies))
                continue;
            foreach (var dependency in registerDependencies)
            {
                if (symbolTable.TryGetRegisterSymbol(
                        dependency.Register,
                        dependency.Version,
                        out var symbol) &&
                    materializedSymbols.Contains(symbol) &&
                    yielded.Add(symbol))
                {
                    yield return symbol;
                }
            }
        }
    }

    private static bool TryGetDefinedRegisterSymbol(
        Avm1TacInstruction instruction,
        Dictionary<IrIndex, Avm1RegisterAccess> accesses,
        Avm1SymbolTable symbolTable,
        HashSet<Avm1Symbol> materializedSymbols,
        out Avm1Symbol symbol)
    {
        symbol = null!;
        return accesses.TryGetValue(instruction.Index, out var access) &&
            access.Kind is Avm1RegisterAccessKind.Write &&
            symbolTable.TryGetRegisterSymbol(
                access.Register,
                access.Version,
                out symbol) &&
            materializedSymbols.Contains(symbol);
    }

    private static List<BlockIndex> GetSuccessors(
        Avm1ControlFlowGraph controlFlowGraph,
        BlockIndex blockIndex)
    {
        var block = controlFlowGraph[blockIndex];
        var result = new List<BlockIndex>(4);
        var yielded = new HashSet<BlockIndex>();
        if (block.FirstSuccessor.IsValid && yielded.Add(block.FirstSuccessor))
            result.Add(block.FirstSuccessor);
        if (block.SecondSuccessor.IsValid && yielded.Add(block.SecondSuccessor))
            result.Add(block.SecondSuccessor);
        if (block.ExceptionSuccessor.IsValid && yielded.Add(block.ExceptionSuccessor))
            result.Add(block.ExceptionSuccessor);
        foreach (var completion in controlFlowGraph.GetCompletionEdges(blockIndex))
        {
            if (completion.Target.IsValid && yielded.Add(completion.Target))
                result.Add(completion.Target);
        }
        return result;
    }

    private static Dictionary<ValueIndex, HashSet<(int Register, int Version)>>
        BuildRegisterValueDependencies(
            Avm1TacIr tacIr,
            Avm1RegisterSsa registerSsa)
    {
        var dependencies = new Dictionary<
            ValueIndex,
            HashSet<(int Register, int Version)>>();
        var accessByInstruction = registerSsa.Accesses
            .Where(access => access.Kind is Avm1RegisterAccessKind.Read)
            .ToDictionary(access => access.Instruction);

        foreach (var instruction in tacIr.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.LoadRegister ||
                !instruction.Result.IsValid ||
                !accessByInstruction.TryGetValue(instruction.Index, out var access))
            {
                continue;
            }

            dependencies[instruction.Result] = [(access.Register, access.Version)];
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var instruction in tacIr.Instructions)
            {
                if (!instruction.Result.IsValid ||
                    instruction.Op is not (Avm1TacOp.Copy or Avm1TacOp.Phi))
                {
                    continue;
                }

                if (!dependencies.TryGetValue(instruction.Result, out var resultDependencies))
                {
                    resultDependencies = [];
                    dependencies.Add(instruction.Result, resultDependencies);
                }

                foreach (var operand in GetConsumedValues(tacIr, instruction))
                {
                    if (!dependencies.TryGetValue(operand, out var operandDependencies))
                        continue;

                    foreach (var dependency in operandDependencies)
                        changed |= resultDependencies.Add(dependency);
                }
            }
        }

        return dependencies;
    }

    private static IEnumerable<ValueIndex> GetConsumedValues(
        Avm1TacIr tacIr,
        Avm1TacInstruction instruction)
    {
        if (instruction.Op is Avm1TacOp.StackOnly)
            yield break;

        if (instruction.Op is not (Avm1TacOp.InitArray or Avm1TacOp.InitObject) &&
            instruction.Operand0.IsValid)
        {
            yield return instruction.Operand0;
        }

        if (instruction.Op is not (Avm1TacOp.CallFunction or Avm1TacOp.NewObject) &&
            instruction.Operand1.IsValid)
        {
            yield return instruction.Operand1;
        }

        if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(instruction.Op) &&
            instruction.Operand2.IsValid)
        {
            yield return instruction.Operand2;
        }

        if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(instruction.Op) ||
            !instruction.Operand2.IsValid ||
            instruction.OperandCount <= 0)
        {
            yield break;
        }

        var start = instruction.Operand2.Value;
        var operandSlots = instruction.Op is Avm1TacOp.InitObject
            ? instruction.OperandCount * 2
            : instruction.OperandCount;
        var end = Math.Min(start + operandSlots, tacIr.ValueOperands.Count);
        for (var index = start; index < end; index++)
            yield return tacIr.ValueOperands[index];
    }

    private readonly record struct RegisterLiveInterval(
        Avm1Symbol Symbol,
        int Start,
        int End);

    private readonly record struct SymbolPair(int Left, int Right)
    {
        public static SymbolPair Create(Avm1Symbol left, Avm1Symbol right) =>
            left.Id <= right.Id
                ? new SymbolPair(left.Id, right.Id)
                : new SymbolPair(right.Id, left.Id);
    }

    internal readonly record struct Avm1RegisterSymbolCoalescing(
        Avm1Symbol Representative,
        Avm1Symbol Merged);

    private static bool TryFindRegisterReadVersion(
        ValueIndex value,
        int register,
        IReadOnlyDictionary<ValueIndex, Avm1TacInstruction> definitions,
        IReadOnlyDictionary<IrIndex, Avm1RegisterAccess> accesses,
        int depth,
        out int version)
    {
        version = 0;
        if (depth >= 16 || !definitions.TryGetValue(value, out var definition))
            return false;

        if (definition.Op is Avm1TacOp.LoadRegister &&
            definition.IntOperand == register &&
            accesses.TryGetValue(definition.Index, out var read) &&
            read.Kind == Avm1RegisterAccessKind.Read)
        {
            version = read.Version;
            return true;
        }

        if (definition.Op is Avm1TacOp.Copy or Avm1TacOp.Unary or Avm1TacOp.Cast)
        {
            return TryFindRegisterReadVersion(
                definition.Operand0,
                register,
                definitions,
                accesses,
                depth + 1,
                out version);
        }

        if (definition.Op is Avm1TacOp.Binary)
        {
            return TryFindRegisterReadVersion(
                    definition.Operand0,
                    register,
                    definitions,
                    accesses,
                    depth + 1,
                    out version) ||
                TryFindRegisterReadVersion(
                    definition.Operand1,
                    register,
                    definitions,
                    accesses,
                    depth + 1,
                    out version);
        }

        return false;
    }

    private static string? SuggestNameFromDef(
        Avm1TacInstruction inst,
        Avm1TacIr tacIr,
        Avm1ValueAnalysis valueAnalysis)
    {
        if (inst.Op == Avm1TacOp.SetVariable || inst.Op == Avm1TacOp.SetMember)
            return null;

        if (inst.Operand0.IsValid)
        {
            var fact = valueAnalysis[inst.Operand0];
            if (fact.ConstantKind == Avm1ConstantKind.String && fact.StringValue is not null)
            {
                string cleanStr = fact.StringValue.Trim('"');
                if (IsIdentifier(cleanStr))
                    return ToLocalCamelCase(cleanStr);
            }
        }

        return null;
    }

    private static string? SuggestCatchName(Avm1ValueOrigin origin)
    {
        if (origin.Kind is not Avm1ValueOriginKind.ConstructedObject ||
            origin.Name is null)
        {
            return null;
        }

        var simpleName = origin.Name.Split('.').LastOrDefault();
        if (simpleName is null || !IsIdentifier(simpleName))
            return null;

        return simpleName.EndsWith("Error", StringComparison.Ordinal) ||
            simpleName.EndsWith("Exception", StringComparison.Ordinal)
                ? "error"
                : ToLocalCamelCase(simpleName);
    }

    private static string ToLocalCamelCase(string name)
    {
        if (name.Length == 0) return "v";
        while (name.StartsWith('_') && name.Length > 1)
            name = name[1..];
        if (name.Length == 0) return "v";

        if (char.IsUpper(name[0]))
        {
            if (name.Length > 1 && char.IsUpper(name[1]))
                return name.ToLowerInvariant();
            return char.ToLowerInvariant(name[0]) + name[1..];
        }
        return name;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] != '_' && value[0] != '$' && !char.IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
            if (value[i] != '_' && value[i] != '$' && !char.IsAsciiLetterOrDigit(value[i]))
                return false;
        return true;
    }

    private sealed class RegisterVersionGroups
    {
        private readonly Dictionary<(int Register, int Version), (int Register, int Version)> _parents = [];

        public void Add((int Register, int Version) key)
        {
            _parents.TryAdd(key, key);
        }

        public (int Register, int Version) Find((int Register, int Version) key)
        {
            Add(key);
            var parent = _parents[key];
            if (parent == key)
                return key;

            var root = Find(parent);
            _parents[key] = root;
            return root;
        }

        public void Union(
            (int Register, int Version) left,
            (int Register, int Version) right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return;

            var representative = leftRoot.Version <= rightRoot.Version ? leftRoot : rightRoot;
            var merged = representative == leftRoot ? rightRoot : leftRoot;
            _parents[merged] = representative;
        }
    }
}
