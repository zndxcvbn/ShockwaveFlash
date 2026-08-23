using System.Numerics;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal sealed class Avm1SourceInterferenceGraph
{
    private readonly SourceSymbolIndex[] _symbols;
    private readonly int[] _ordinalsBySymbol;
    private readonly ulong[] _neighbors;
    private readonly int _wordCount;

    public Avm1SourceInterferenceGraph(
        IReadOnlyList<SourceSymbolIndex> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _symbols = new SourceSymbolIndex[candidates.Count];
        var symbolCapacity = candidates.Count == 0
            ? 0
            : candidates.Max(candidate => candidate.Value) + 1;
        _ordinalsBySymbol = new int[symbolCapacity];
        Array.Fill(_ordinalsBySymbol, -1);
        _wordCount = GetWordCount(candidates.Count);
        _neighbors = new ulong[candidates.Count * _wordCount];
        for (var i = 0; i < candidates.Count; i++)
        {
            var symbol = candidates[i];
            if (!symbol.IsValid ||
                symbol.Value >= _ordinalsBySymbol.Length ||
                _ordinalsBySymbol[symbol.Value] >= 0)
            {
                throw new ArgumentException(
                    "Source-register candidates must contain unique valid symbols.",
                    nameof(candidates));
            }

            _symbols[i] = symbol;
            _ordinalsBySymbol[symbol.Value] = i;
        }
    }

    public int Count => _symbols.Length;

    public int WordCount => _wordCount;

    public SourceSymbolIndex GetSymbol(int ordinal) => _symbols[ordinal];

    public int GetOrdinal(SourceSymbolIndex symbol) =>
        symbol.IsValid && symbol.Value < _ordinalsBySymbol.Length
            ? _ordinalsBySymbol[symbol.Value]
            : -1;

    public bool Interferes(int left, int right) =>
        left != right && IsSet(GetNeighbors(left), right);

    public int GetDegree(int ordinal)
    {
        var degree = 0;
        foreach (var word in GetNeighbors(ordinal))
            degree += BitOperations.PopCount(word);
        return degree;
    }

    public void AddEdges(int ordinal, ReadOnlySpan<ulong> live)
    {
        for (var candidate = 0; candidate < Count; candidate++)
        {
            if (candidate != ordinal && IsSet(live, candidate))
                AddEdge(ordinal, candidate);
        }
    }

    public void AddClique(ReadOnlySpan<ulong> values)
    {
        for (var left = 0; left < Count; left++)
        {
            if (!IsSet(values, left))
                continue;

            for (var right = left + 1; right < Count; right++)
            {
                if (IsSet(values, right))
                    AddEdge(left, right);
            }
        }
    }

    public void AddCrossEdges(
        ReadOnlySpan<ulong> left,
        ReadOnlySpan<ulong> right)
    {
        for (var leftOrdinal = 0; leftOrdinal < Count; leftOrdinal++)
        {
            if (!IsSet(left, leftOrdinal))
                continue;

            for (var rightOrdinal = 0; rightOrdinal < Count; rightOrdinal++)
            {
                if (leftOrdinal != rightOrdinal && IsSet(right, rightOrdinal))
                    AddEdge(leftOrdinal, rightOrdinal);
            }
        }
    }

    public void AddInterferenceWithAll(SourceSymbolIndex symbol)
    {
        var ordinal = GetOrdinal(symbol);
        if (ordinal < 0)
            return;

        for (var candidate = 0; candidate < Count; candidate++)
        {
            if (candidate != ordinal)
                AddEdge(ordinal, candidate);
        }
    }

    private void AddEdge(int left, int right)
    {
        Set(GetNeighbors(left), right);
        Set(GetNeighbors(right), left);
    }

    private Span<ulong> GetNeighbors(int ordinal) =>
        _neighbors.AsSpan(ordinal * _wordCount, _wordCount);

    private static int GetWordCount(int count) => (count + 63) / 64;

    private static bool IsSet(ReadOnlySpan<ulong> bits, int ordinal) =>
        (bits[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;

    private static void Set(Span<ulong> bits, int ordinal) =>
        bits[ordinal >> 6] |= 1UL << (ordinal & 63);
}

internal static class Avm1SourceRegisterLivenessAnalysis
{
    private sealed record NodeResult(ulong[] Referenced);

    public static Avm1SourceInterferenceGraph Analyze(
        Avm1MirMethod mir,
        Avm1CompiledNestedCodeUnits nested,
        IReadOnlyList<SourceSymbolIndex> candidates,
        IReadOnlyList<SourceSymbolIndex> preservedLifetimeSymbols,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mir);
        ArgumentNullException.ThrowIfNull(nested);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(preservedLifetimeSymbols);

        var graph = new Avm1SourceInterferenceGraph(candidates);
        if (graph.Count == 0)
            return graph;

        AnalyzeNode(
            mir,
            nested.WithRegions.Values.Select(region => region.Artifact).ToArray(),
            nested.TryRegions.Values.Select(region => region.Artifact).ToArray(),
            graph,
            cancellationToken);
        foreach (var symbol in preservedLifetimeSymbols)
            graph.AddInterferenceWithAll(symbol);
        return graph;
    }

    private static NodeResult AnalyzeNode(
        Avm1MirMethod mir,
        IReadOnlyList<Avm1CompiledWithArtifact> withRegions,
        IReadOnlyList<Avm1CompiledTryArtifact> tryRegions,
        Avm1SourceInterferenceGraph graph,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var withReferences = new Dictionary<int, ulong[]>();
        foreach (var region in withRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = AnalyzeNode(
                region.Body.Mir,
                region.Body.NestedWithRegions,
                region.Body.NestedTryRegions,
                graph,
                cancellationToken);
            withReferences.Add(region.Plan.Site.Value, body.Referenced);
        }

        var tryReferences = new Dictionary<int, ulong[]>();
        foreach (var region in tryRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tryBody = AnalyzeNode(
                region.TryBody.Mir,
                region.TryBody.NestedWithRegions,
                region.TryBody.NestedTryRegions,
                graph,
                cancellationToken);
            var catchBody = region.CatchBody is null
                ? null
                : AnalyzeNode(
                    region.CatchBody.Mir,
                    region.CatchBody.NestedWithRegions,
                    region.CatchBody.NestedTryRegions,
                    graph,
                    cancellationToken);
            var finallyBody = region.FinallyBody is null
                ? null
                : AnalyzeNode(
                    region.FinallyBody.Mir,
                    region.FinallyBody.NestedWithRegions,
                    region.FinallyBody.NestedTryRegions,
                    graph,
                    cancellationToken);

            if (catchBody is not null)
                graph.AddCrossEdges(tryBody.Referenced, catchBody.Referenced);
            if (finallyBody is not null)
            {
                graph.AddCrossEdges(tryBody.Referenced, finallyBody.Referenced);
                if (catchBody is not null)
                {
                    graph.AddCrossEdges(
                        catchBody.Referenced,
                        finallyBody.Referenced);
                }
            }

            var referenced = (ulong[])tryBody.Referenced.Clone();
            if (catchBody is not null)
                Or(referenced, catchBody.Referenced);
            if (finallyBody is not null)
                Or(referenced, finallyBody.Referenced);
            tryReferences.Add(region.Plan.Site.Value, referenced);
        }

        var blockCount = mir.Blocks.Count;
        var wordCount = graph.WordCount;
        var uses = new ulong[blockCount * wordCount];
        var definitions = new ulong[blockCount * wordCount];
        var liveIn = new ulong[blockCount * wordCount];
        var liveOut = new ulong[blockCount * wordCount];
        var referencedByNode = new ulong[wordCount];
        if (blockCount == 0)
            return new NodeResult(referencedByNode);

        foreach (var block in mir.Blocks)
        {
            if (!block.IsReachable)
                continue;

            var blockUses = GetRow(uses, block.Index.Value, wordCount);
            var blockDefinitions = GetRow(
                definitions,
                block.Index.Value,
                wordCount);
            var end = block.Instructions.Start + block.Instructions.Count;
            for (var i = block.Instructions.Start; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instruction = mir.Instructions[i];
                var use = GetUseOrdinal(mir, instruction, graph);
                if (use >= 0)
                {
                    Set(referencedByNode, use);
                    if (!IsSet(blockDefinitions, use))
                        Set(blockUses, use);
                }

                var nestedReferences = GetNestedReferences(
                    instruction,
                    withReferences,
                    tryReferences);
                if (nestedReferences is not null)
                {
                    Or(referencedByNode, nestedReferences);
                    OrAndNot(blockUses, nestedReferences, blockDefinitions);
                }

                var definition = GetDefinitionOrdinal(mir, instruction, graph);
                if (definition >= 0)
                {
                    Set(referencedByNode, definition);
                    Set(blockDefinitions, definition);
                }
            }
        }

        ComputeLiveness(
            mir,
            uses,
            definitions,
            liveIn,
            liveOut,
            wordCount,
            cancellationToken);

        foreach (var block in mir.Blocks)
        {
            if (!block.IsReachable)
                continue;

            var live = GetRow(liveOut, block.Index.Value, wordCount).ToArray();
            for (var i = block.Instructions.Start + block.Instructions.Count - 1;
                i >= block.Instructions.Start;
                i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instruction = mir.Instructions[i];
                var nestedReferences = GetNestedReferences(
                    instruction,
                    withReferences,
                    tryReferences);
                if (nestedReferences is not null)
                    graph.AddCrossEdges(live, nestedReferences);

                var definition = GetDefinitionOrdinal(mir, instruction, graph);
                if (definition >= 0)
                {
                    graph.AddEdges(definition, live);
                    Clear(live, definition);
                }

                var use = GetUseOrdinal(mir, instruction, graph);
                if (use >= 0)
                    Set(live, use);
                if (nestedReferences is not null)
                    Or(live, nestedReferences);
            }
        }

        if (mir.EntryBlock.IsValid && mir.EntryBlock.Value < blockCount)
        {
            graph.AddClique(GetRow(
                liveIn,
                mir.EntryBlock.Value,
                wordCount));
        }
        return new NodeResult(referencedByNode);
    }

    private static void ComputeLiveness(
        Avm1MirMethod mir,
        ulong[] uses,
        ulong[] definitions,
        ulong[] liveIn,
        ulong[] liveOut,
        int wordCount,
        CancellationToken cancellationToken)
    {
        var nextOut = new ulong[wordCount];
        var nextIn = new ulong[wordCount];
        bool changed;
        do
        {
            changed = false;
            for (var layoutIndex = mir.BlockLayout.Count - 1;
                layoutIndex >= 0;
                layoutIndex--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = mir[mir.BlockLayout[layoutIndex]];
                if (!block.IsReachable)
                    continue;

                Array.Clear(nextOut);
                UnionSuccessorLiveIn(
                    mir,
                    block,
                    liveIn,
                    wordCount,
                    nextOut);
                CopyOrAndNot(
                    nextIn,
                    GetRow(uses, block.Index.Value, wordCount),
                    nextOut,
                    GetRow(definitions, block.Index.Value, wordCount));
                var blockLiveOut = GetRow(
                    liveOut,
                    block.Index.Value,
                    wordCount);
                if (!nextOut.AsSpan().SequenceEqual(blockLiveOut))
                {
                    nextOut.CopyTo(blockLiveOut);
                    changed = true;
                }
                var blockLiveIn = GetRow(
                    liveIn,
                    block.Index.Value,
                    wordCount);
                if (!nextIn.AsSpan().SequenceEqual(blockLiveIn))
                {
                    nextIn.CopyTo(blockLiveIn);
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static void UnionSuccessorLiveIn(
        Avm1MirMethod mir,
        Avm1MirBasicBlock block,
        ulong[] liveIn,
        int wordCount,
        ulong[] destination)
    {
        if (block.Instructions.Count == 0)
            return;

        var terminator = mir.Instructions[
            block.Instructions.Start + block.Instructions.Count - 1];
        switch (terminator.Kind)
        {
            case Avm1MirInstructionKind.Break:
            case Avm1MirInstructionKind.Continue:
            case Avm1MirInstructionKind.Branch:
            case Avm1MirInstructionKind.EndEnumeration:
                UnionTarget(terminator.Target, liveIn, wordCount, destination);
                break;

            case Avm1MirInstructionKind.BranchIfTrue:
            case Avm1MirInstructionKind.WaitForFrameImmediate:
            case Avm1MirInstructionKind.WaitForFrame:
            case Avm1MirInstructionKind.EnumerateNext:
                UnionTarget(terminator.Target, liveIn, wordCount, destination);
                UnionTarget(
                    terminator.AlternativeTarget,
                    liveIn,
                    wordCount,
                    destination);
                break;
        }
    }

    private static void UnionTarget(
        Avm1MirBlockIndex target,
        ulong[] liveIn,
        int wordCount,
        ulong[] destination)
    {
        var blockCount = wordCount == 0 ? 0 : liveIn.Length / wordCount;
        if (target.IsValid && target.Value < blockCount)
            Or(destination, GetRow(liveIn, target.Value, wordCount));
    }

    private static int GetUseOrdinal(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        Avm1SourceInterferenceGraph graph)
    {
        if (instruction.Kind is Avm1MirInstructionKind.LoadLValue &&
            instruction.LValue.IsValid &&
            instruction.LValue.Value < mir.LValues.Count)
        {
            return graph.GetOrdinal(mir[instruction.LValue].Symbol);
        }

        if (instruction.Kind is Avm1MirInstructionKind.Invoke &&
            instruction.CallSite.IsValid &&
            instruction.CallSite.Value < mir.CallSites.Count)
        {
            return graph.GetOrdinal(mir[instruction.CallSite].Symbol);
        }

        return -1;
    }

    private static int GetDefinitionOrdinal(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        Avm1SourceInterferenceGraph graph)
    {
        if (instruction.Kind is not (
                Avm1MirInstructionKind.DeclareLocal or
                Avm1MirInstructionKind.StoreLValue) ||
            !instruction.LValue.IsValid ||
            instruction.LValue.Value >= mir.LValues.Count)
        {
            return -1;
        }

        return graph.GetOrdinal(mir[instruction.LValue].Symbol);
    }

    private static ulong[]? GetNestedReferences(
        Avm1MirInstruction instruction,
        Dictionary<int, ulong[]> withReferences,
        Dictionary<int, ulong[]> tryReferences)
    {
        if (instruction.Kind is Avm1MirInstructionKind.With &&
            instruction.WithSite.IsValid &&
            withReferences.TryGetValue(
                instruction.WithSite.Value,
                out var withBody))
        {
            return withBody;
        }

        if (instruction.Kind is Avm1MirInstructionKind.Try &&
            instruction.TrySite.IsValid &&
            tryReferences.TryGetValue(
                instruction.TrySite.Value,
                out var tryBody))
        {
            return tryBody;
        }

        return null;
    }

    private static Span<ulong> GetRow(
        ulong[] matrix,
        int row,
        int columns) =>
        matrix.AsSpan(row * columns, columns);

    private static bool IsSet(ReadOnlySpan<ulong> bits, int ordinal) =>
        (bits[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;

    private static void Set(Span<ulong> bits, int ordinal) =>
        bits[ordinal >> 6] |= 1UL << (ordinal & 63);

    private static void Clear(Span<ulong> bits, int ordinal) =>
        bits[ordinal >> 6] &= ~(1UL << (ordinal & 63));

    private static void Or(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] |= source[i];
    }

    private static void OrAndNot(
        Span<ulong> destination,
        ReadOnlySpan<ulong> source,
        ReadOnlySpan<ulong> excluded)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] |= source[i] & ~excluded[i];
    }

    private static void CopyOrAndNot(
        Span<ulong> destination,
        ReadOnlySpan<ulong> first,
        ReadOnlySpan<ulong> second,
        ReadOnlySpan<ulong> excluded)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] = first[i] | (second[i] & ~excluded[i]);
    }
}
