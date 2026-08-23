using System.Diagnostics.CodeAnalysis;

namespace ShockwaveFlash.Avm1.Source;

public sealed class Avm1SourceSemanticAnalysis
{
    private readonly Avm1SourceArenaBindings[] _arenaBindings;
    private readonly Dictionary<Avm1SourceArena, Avm1SourceArenaBindings> _bindingsByArena;

    private Avm1SourceSemanticAnalysis(
        Avm1SourceProgram program,
        Avm1SourceArenaBindings[] arenaBindings,
        Avm1SourceTypeAnalysis typeAnalysis,
        bool reachedFixedPoint,
        int iterationCount,
        int refinedBindingCount)
    {
        Program = program;
        _arenaBindings = arenaBindings;
        TypeAnalysis = typeAnalysis;
        ReachedFixedPoint = reachedFixedPoint;
        IterationCount = iterationCount;
        RefinedBindingCount = refinedBindingCount;
        _bindingsByArena = new Dictionary<Avm1SourceArena, Avm1SourceArenaBindings>(
            ReferenceEqualityComparer.Instance);
        foreach (var bindings in arenaBindings)
            _bindingsByArena.Add(bindings.Arena, bindings);
    }

    public Avm1SourceProgram Program { get; }

    public IReadOnlyList<Avm1SourceArenaBindings> ArenaBindings => _arenaBindings;

    public Avm1SourceTypeAnalysis TypeAnalysis { get; }

    public bool ReachedFixedPoint { get; }

    public bool IsComplete => ReachedFixedPoint &&
        _arenaBindings.All(bindings => bindings.ScopeAnalysis.IsComplete);

    public int IterationCount { get; }

    public int RefinedBindingCount { get; }

    public bool TryGetBindings(
        Avm1SourceArena arena,
        [NotNullWhen(true)] out Avm1SourceArenaBindings? bindings)
    {
        ArgumentNullException.ThrowIfNull(arena);
        return _bindingsByArena.TryGetValue(arena, out bindings);
    }

    internal static Avm1SourceSemanticAnalysis Build(
        Avm1SourceProgram program,
        Avm1SourceArenaBindings[] initialBindings)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(initialBindings);

        var bindings = (Avm1SourceArenaBindings[])initialBindings.Clone();
        var maximumIterations = bindings.Sum(arena =>
            arena.Bindings.Count(binding =>
                binding.Kind is Avm1SourceBindingKind.Dynamic)) + 1;
        var iterationCount = 0;
        var refinedBindingCount = 0;
        Avm1SourceTypeAnalysis? typeAnalysis = null;

        while (iterationCount < maximumIterations)
        {
            typeAnalysis = Avm1SourceTypeAnalysis.Build(program, bindings);
            iterationCount++;
            if (!typeAnalysis.ReachedFixedPoint)
            {
                return new Avm1SourceSemanticAnalysis(
                    program,
                    bindings,
                    typeAnalysis,
                    reachedFixedPoint: false,
                    iterationCount,
                    refinedBindingCount);
            }

            var refinement = RefineBindings(program, bindings, typeAnalysis);
            if (refinement.RefinedCount == 0)
            {
                return new Avm1SourceSemanticAnalysis(
                    program,
                    bindings,
                    typeAnalysis,
                    reachedFixedPoint: true,
                    iterationCount,
                    refinedBindingCount);
            }

            bindings = refinement.Bindings;
            refinedBindingCount += refinement.RefinedCount;
        }

        return new Avm1SourceSemanticAnalysis(
            program,
            bindings,
            typeAnalysis!,
            reachedFixedPoint: false,
            iterationCount,
            refinedBindingCount);
    }

    private static BindingRefinement RefineBindings(
        Avm1SourceProgram program,
        Avm1SourceArenaBindings[] current,
        Avm1SourceTypeAnalysis typeAnalysis)
    {
        var result = (Avm1SourceArenaBindings[])current.Clone();
        var refinedCount = 0;
        var compatibleMembers = new List<SourceProgramSymbolIndex>();

        for (var arenaIndex = 0; arenaIndex < current.Length; arenaIndex++)
        {
            var arenaBindings = current[arenaIndex];
            var arena = arenaBindings.Arena;
            Avm1SourceExpressionBinding[]? refinedBindings = null;
            List<SourceProgramSymbolIndex>? candidateSymbols = null;

            foreach (var expression in arena.Expressions)
            {
                var existing = arenaBindings[expression.Index];
                if (existing.Kind is not Avm1SourceBindingKind.Dynamic ||
                    expression.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                    !arenaBindings.ScopeAnalysis.GetScope(expression.Index).IsValid ||
                    arenaBindings.IsInDynamicScope(expression.Index) ||
                    !TryGetMemberName(arena, expression, out var memberName))
                {
                    continue;
                }

                var receiver = arena.GetChild(expression, 0);
                var receiverBinding = arenaBindings[receiver];
                if (receiverBinding.IsBound &&
                    program[receiverBinding.Symbol].Kind is
                        Avm1SourceProgramSymbolKind.Class or
                        Avm1SourceProgramSymbolKind.Interface)
                {
                    continue;
                }

                var receiverFact = typeAnalysis.GetExpressionFact(arena, receiver);
                if (!receiverFact.HasEvidence ||
                    !receiverFact.IsAnnotationSafe ||
                    program[receiverFact.Type] is not
                    {
                        Kind: Avm1SourceTypeKind.Nominal,
                        Symbol.IsValid: true
                    } receiverType ||
                    program[receiverType.Symbol].Kind is not (
                        Avm1SourceProgramSymbolKind.Class or
                        Avm1SourceProgramSymbolKind.Interface))
                {
                    continue;
                }

                compatibleMembers.Clear();
                program.CollectInstanceMemberCandidates(
                    receiverType.Symbol,
                    memberName,
                    arenaBindings.GetUse(expression.Index),
                    compatibleMembers);
                if (compatibleMembers.Count == 0)
                    continue;

                refinedBindings ??= arenaBindings.Bindings.ToArray();
                candidateSymbols ??= arenaBindings.CandidateSymbols.ToList();
                refinedBindings[expression.Index.Value] = compatibleMembers.Count switch
                {
                    1 => new Avm1SourceExpressionBinding(
                        Avm1SourceBindingKind.Bound,
                        compatibleMembers[0],
                        SourceProgramSymbolList.Empty),
                    _ => AddAmbiguousBinding(candidateSymbols, compatibleMembers)
                };
                refinedCount++;
            }

            if (refinedBindings is not null)
            {
                result[arenaIndex] = arenaBindings.WithBindings(
                    refinedBindings,
                    candidateSymbols!.ToArray());
            }
        }

        return new BindingRefinement(result, refinedCount);
    }

    private static Avm1SourceExpressionBinding AddAmbiguousBinding(
        List<SourceProgramSymbolIndex> candidateSymbols,
        List<SourceProgramSymbolIndex> candidates)
    {
        var start = candidateSymbols.Count;
        for (var i = 0; i < candidates.Count; i++)
            candidateSymbols.Add(candidates[i]);
        return new Avm1SourceExpressionBinding(
            Avm1SourceBindingKind.Ambiguous,
            SourceProgramSymbolIndex.Invalid,
            new SourceProgramSymbolList(start, candidates.Count));
    }

    private static bool TryGetMemberName(
        Avm1SourceArena arena,
        Avm1SourceExpression expression,
        [NotNullWhen(true)] out string? memberName)
    {
        memberName = null;
        if (expression.Flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember) ||
            expression.Children.Count != 2)
        {
            return false;
        }

        var nameExpression = arena[arena.GetChild(expression, 1)];
        if (nameExpression.Kind is not Avm1SourceExpressionKind.Literal ||
            !nameExpression.Literal.IsValid)
        {
            return false;
        }

        var literal = arena[nameExpression.Literal];
        if (literal.Kind is not Avm1SourceLiteralKind.String ||
            !literal.StringValue.IsValid)
        {
            return false;
        }

        memberName = arena[literal.StringValue];
        return true;
    }

    private readonly record struct BindingRefinement(
        Avm1SourceArenaBindings[] Bindings,
        int RefinedCount);
}
