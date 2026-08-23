using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal readonly record struct Avm1RegisterPlanningResult(
    int RequiredCompletionRegisters,
    SourceOriginIndex LimitingOrigin);

internal static class Avm1RegisterPlanningAnalysis
{
    public static Avm1RegisterPlanningResult Analyze(
        Avm1SourceMethod method,
        bool executesInDynamicScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        cancellationToken.ThrowIfCancellationRequested();

        return new Analyzer(method.Arena, cancellationToken).Analyze(
            method.Body,
            executesInDynamicScope);
    }

    private sealed class Analyzer(
        Avm1SourceArena arena,
        CancellationToken cancellationToken)
    {
        private int _maximumCompletionDepth;
        private SourceOriginIndex _limitingOrigin = SourceOriginIndex.Invalid;

        public Avm1RegisterPlanningResult Analyze(
            SourceStatementIndex body,
            bool executesInDynamicScope)
        {
            VisitStatement(
                body,
                new TraversalState(
                    ControlScopeDepth: 0,
                    CompletionRegisterDepth: 0,
                    DynamicScopeDepth: executesInDynamicScope ? 1 : 0));
            return new Avm1RegisterPlanningResult(
                _maximumCompletionDepth,
                _limitingOrigin);
        }

        private void VisitStatement(
            SourceStatementIndex index,
            TraversalState state)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!index.IsValid || index.Value >= arena.Statements.Count)
                return;

            var statement = arena[index];
            VisitInitializers(statement, state);
            switch (statement.Kind)
            {
                case Avm1SourceStatementKind.While:
                case Avm1SourceStatementKind.DoWhile:
                case Avm1SourceStatementKind.For:
                case Avm1SourceStatementKind.ForIn:
                case Avm1SourceStatementKind.Switch:
                    VisitChildren(
                        statement,
                        state with
                        {
                            ControlScopeDepth = state.ControlScopeDepth + 1
                        });
                    return;

                case Avm1SourceStatementKind.With:
                    VisitProtectedChildren(
                        statement,
                        state,
                        requiresRegister: state.ControlScopeDepth != 0,
                        entersDynamicScope: true);
                    return;

                case Avm1SourceStatementKind.Try:
                    VisitProtectedChildren(
                        statement,
                        state,
                        requiresRegister: state.ControlScopeDepth != 0 &&
                            state.DynamicScopeDepth != 0,
                        entersDynamicScope: false);
                    return;

                default:
                    VisitChildren(statement, state);
                    return;
            }
        }

        private void VisitProtectedChildren(
            Avm1SourceStatement statement,
            TraversalState state,
            bool requiresRegister,
            bool entersDynamicScope)
        {
            var protectedState = state with
            {
                CompletionRegisterDepth = state.CompletionRegisterDepth +
                    (requiresRegister ? 1 : 0),
                DynamicScopeDepth = state.DynamicScopeDepth +
                    (entersDynamicScope ? 1 : 0)
            };
            if (protectedState.CompletionRegisterDepth > _maximumCompletionDepth)
            {
                _maximumCompletionDepth = protectedState.CompletionRegisterDepth;
                _limitingOrigin = statement.Origin;
            }

            VisitChildren(statement, protectedState);
        }

        private void VisitInitializers(
            Avm1SourceStatement statement,
            TraversalState state)
        {
            for (var i = 0; i < statement.Initializers.Count; i++)
                VisitStatement(arena.GetInitializer(statement, i), state);
        }

        private void VisitChildren(
            Avm1SourceStatement statement,
            TraversalState state)
        {
            for (var i = 0; i < statement.Children.Count; i++)
                VisitStatement(arena.GetChild(statement, i), state);
        }
    }

    private readonly record struct TraversalState(
        int ControlScopeDepth,
        int CompletionRegisterDepth,
        int DynamicScopeDepth);
}
