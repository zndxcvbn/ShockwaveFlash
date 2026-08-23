using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal sealed class Avm1SourceRegisterCandidates
{
    private readonly SourceSymbolIndex[] _symbols;
    private readonly bool[] _invocationTargets;

    private Avm1SourceRegisterCandidates(
        SourceSymbolIndex[] symbols,
        bool[] invocationTargets,
        int symbolCapacity)
    {
        _symbols = symbols;
        _invocationTargets = invocationTargets;
        SymbolCapacity = symbolCapacity;
    }

    public int Count => _symbols.Length;

    public IReadOnlyList<SourceSymbolIndex> Symbols => _symbols;

    public int SymbolCapacity { get; }

    public static Avm1SourceRegisterCandidates Create(
        Avm1ClosureAnalysis analysis,
        SourceCodeUnitIndex codeUnit,
        IReadOnlyList<SourceSymbolIndex> parameters,
        bool includeInvocationTargets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(parameters);

        var arena = analysis.Method.Arena;
        if (!codeUnit.IsValid)
            return new([], [], arena.Symbols.Count);

        var seen = new bool[arena.Symbols.Count];
        var symbols = new List<SourceSymbolIndex>();
        var invocationTargets = new List<bool>();

        void TryAdd(SourceSymbolIndex symbol)
        {
            if (!symbol.IsValid ||
                symbol.Value >= seen.Length ||
                seen[symbol.Value] ||
                !IsEligible(
                    analysis,
                    codeUnit,
                    symbol,
                    includeInvocationTargets))
            {
                return;
            }

            seen[symbol.Value] = true;
            symbols.Add(symbol);
            invocationTargets.Add(IsInvocationTarget(analysis[symbol].Uses));
        }

        foreach (var parameter in parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAdd(parameter);
        }

        var owned = analysis[codeUnit];
        for (var i = 0; i < owned.OwnedSymbols.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAdd(analysis.GetOwnedSymbol(owned, i));
        }

        return new(
            symbols.ToArray(),
            invocationTargets.ToArray(),
            arena.Symbols.Count);
    }

    public bool HasSameInvocationSelection(
        Avm1SourceRegisterPlan left,
        Avm1SourceRegisterPlan right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        for (var i = 0; i < _symbols.Length; i++)
        {
            if (!_invocationTargets[i])
                continue;

            var leftAllocated = left.TryGetRegister(_symbols[i], out _);
            var rightAllocated = right.TryGetRegister(_symbols[i], out _);
            if (leftAllocated != rightAllocated)
                return false;
        }

        return true;
    }

    private static bool IsEligible(
        Avm1ClosureAnalysis analysis,
        SourceCodeUnitIndex codeUnit,
        SourceSymbolIndex symbol,
        bool includeInvocationTargets)
    {
        var facts = analysis[symbol];
        if (!facts.IsRegisterEligible ||
            facts.DeclarationCodeUnit != codeUnit ||
            facts.Uses is Avm1SourceExpressionUse.None ||
            facts.Uses.HasFlag(Avm1SourceExpressionUse.Delete) ||
            (!includeInvocationTargets && IsInvocationTarget(facts.Uses)))
        {
            return false;
        }

        return analysis.Method.Arena[symbol].Kind is
            Avm1SourceSymbolKind.Local or
            Avm1SourceSymbolKind.Parameter or
            Avm1SourceSymbolKind.Temporary or
            Avm1SourceSymbolKind.Catch;
    }

    private static bool IsInvocationTarget(Avm1SourceExpressionUse uses) =>
        (uses & (Avm1SourceExpressionUse.Invoke |
            Avm1SourceExpressionUse.Construct)) != 0;
}
