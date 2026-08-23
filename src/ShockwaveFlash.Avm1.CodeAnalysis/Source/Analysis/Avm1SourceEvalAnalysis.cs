namespace ShockwaveFlash.Avm1.Source;

public static class Avm1SourceEvalAnalysis
{
    public static bool IsEvalExpression(
        Avm1SourceArena arena,
        SourceExpressionIndex index)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!index.IsValid || index.Value >= arena.Expressions.Count)
            return false;

        var expression = arena[index];
        return expression.Kind is Avm1SourceExpressionKind.ComputedDynamicName ||
            IsDirectEvalCall(arena, expression);
    }

    public static bool IsDirectEvalCall(
        Avm1SourceArena arena,
        Avm1SourceExpression expression)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (expression.Kind is not Avm1SourceExpressionKind.Call ||
            expression.Children.Count == 0)
        {
            return false;
        }

        return IsDirectEvalTarget(arena, arena.GetChild(expression, 0));
    }

    public static bool IsDirectEvalTarget(
        Avm1SourceArena arena,
        SourceExpressionIndex index)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!index.IsValid || index.Value >= arena.Expressions.Count)
            return false;

        var target = arena[index];
        if (target.Kind is
            Avm1SourceExpressionKind.DynamicName or
            Avm1SourceExpressionKind.QualifiedName)
        {
            return target.Name.IsValid &&
                target.Name.Value < arena.Strings.Count &&
                IsEvalName(arena[target.Name]);
        }

        if (target.Kind is not Avm1SourceExpressionKind.SymbolReference ||
            !target.Symbol.IsValid ||
            target.Symbol.Value >= arena.Symbols.Count)
        {
            return false;
        }

        var symbol = arena[target.Symbol];
        return symbol.Kind is Avm1SourceSymbolKind.Global &&
            symbol.Name.IsValid &&
            symbol.Name.Value < arena.Strings.Count &&
            IsEvalName(arena[symbol.Name]);
    }

    public static bool IsEvalName(string name) =>
        name is "eval" or "_global.eval";
}
