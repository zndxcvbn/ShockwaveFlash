namespace ShockwaveFlash.Avm1.Source;

public enum Avm1SourceScopeKind : byte
{
    CodeUnit,
    Catch,
    With,
    Target
}

[Flags]
public enum Avm1SourceScopeFlags : byte
{
    None = 0,
    Dynamic = 1 << 0
}

public readonly record struct Avm1SourceCodeUnit(
    SourceCodeUnitIndex Index,
    SourceCodeUnitIndex Parent,
    SourceScopeIndex DeclarationScope,
    SourceScopeIndex RootScope,
    SourceFunctionIndex Function);

public readonly record struct Avm1SourceScope(
    SourceScopeIndex Index,
    Avm1SourceScopeKind Kind,
    SourceScopeIndex Parent,
    SourceCodeUnitIndex CodeUnit,
    SourceStatementIndex Owner,
    Avm1SourceScopeFlags Flags);

public sealed class Avm1SourceScopeAnalysis
{
    private readonly Avm1SourceCodeUnit[] _codeUnits;
    private readonly Avm1SourceScope[] _scopes;
    private readonly SourceScopeIndex[] _expressionScopes;
    private readonly SourceScopeIndex[] _statementScopes;
    private readonly SourceScopeIndex[] _symbolScopes;
    private readonly SourceCodeUnitIndex[] _functionCodeUnits;
    private readonly Avm1SourceExpressionUse[] _expressionUses;

    private Avm1SourceScopeAnalysis(
        Avm1SourceArena arena,
        Avm1SourceCodeUnit[] codeUnits,
        Avm1SourceScope[] scopes,
        SourceScopeIndex[] expressionScopes,
        SourceScopeIndex[] statementScopes,
        SourceScopeIndex[] symbolScopes,
        SourceCodeUnitIndex[] functionCodeUnits,
        Avm1SourceExpressionUse[] expressionUses,
        int conflictCount)
    {
        Arena = arena;
        _codeUnits = codeUnits;
        _scopes = scopes;
        _expressionScopes = expressionScopes;
        _statementScopes = statementScopes;
        _symbolScopes = symbolScopes;
        _functionCodeUnits = functionCodeUnits;
        _expressionUses = expressionUses;
        ConflictCount = conflictCount;
    }

    public Avm1SourceArena Arena { get; }

    public IReadOnlyList<Avm1SourceCodeUnit> CodeUnits => _codeUnits;

    public IReadOnlyList<Avm1SourceScope> Scopes => _scopes;

    public SourceCodeUnitIndex RootCodeUnit => _codeUnits[0].Index;

    public bool IsComplete => ConflictCount == 0;

    public int ConflictCount { get; }

    public Avm1SourceCodeUnit this[SourceCodeUnitIndex codeUnit] =>
        _codeUnits[codeUnit.Value];

    public Avm1SourceScope this[SourceScopeIndex scope] => _scopes[scope.Value];

    public SourceScopeIndex GetScope(SourceExpressionIndex expression) =>
        _expressionScopes[expression.Value];

    public SourceScopeIndex GetScope(SourceStatementIndex statement) =>
        _statementScopes[statement.Value];

    public SourceScopeIndex GetScope(SourceSymbolIndex symbol) =>
        _symbolScopes[symbol.Value];

    public SourceCodeUnitIndex GetCodeUnit(SourceExpressionIndex expression) =>
        GetCodeUnit(GetScope(expression));

    public SourceCodeUnitIndex GetCodeUnit(SourceStatementIndex statement) =>
        GetCodeUnit(GetScope(statement));

    public SourceCodeUnitIndex GetCodeUnit(SourceSymbolIndex symbol) =>
        GetCodeUnit(GetScope(symbol));

    public SourceCodeUnitIndex GetCodeUnit(SourceFunctionIndex function) =>
        _functionCodeUnits[function.Value];

    public Avm1SourceExpressionUse GetUse(SourceExpressionIndex expression) =>
        _expressionUses[expression.Value];

    public bool IsInDynamicScope(SourceExpressionIndex expression)
    {
        var scope = GetScope(expression);
        return scope.IsValid &&
            _scopes[scope.Value].Flags.HasFlag(Avm1SourceScopeFlags.Dynamic);
    }

    public bool IsRootCodeUnit(SourceExpressionIndex expression) =>
        GetCodeUnit(expression) == RootCodeUnit;

    public bool IsSymbolVisibleAt(
        SourceSymbolIndex symbol,
        SourceExpressionIndex expression)
    {
        if (!symbol.IsValid || !expression.IsValid)
            return false;

        var declaration = GetScope(symbol);
        var current = GetScope(expression);
        for (var depth = 0;
            declaration.IsValid && current.IsValid && depth < _scopes.Length;
            depth++)
        {
            if (current == declaration)
                return true;
            current = _scopes[current.Value].Parent;
        }
        return false;
    }

    public static Avm1SourceScopeAnalysis Analyze(Avm1SourceMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Build(
            method.Arena,
            method.Body,
            SourceExpressionIndex.Invalid,
            method.Parameters);
    }

    internal static Avm1SourceScopeAnalysis Build(
        Avm1SourceArena arena,
        SourceStatementIndex statementRoot,
        SourceExpressionIndex expressionRoot,
        IReadOnlyList<SourceSymbolIndex> rootParameters)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(rootParameters);
        return new Analyzer(
            arena,
            statementRoot,
            expressionRoot,
            rootParameters).Run();
    }

    private SourceCodeUnitIndex GetCodeUnit(SourceScopeIndex scope) =>
        scope.IsValid
            ? _scopes[scope.Value].CodeUnit
            : SourceCodeUnitIndex.Invalid;

    private sealed class Analyzer
    {
        private readonly Avm1SourceArena _arena;
        private readonly SourceStatementIndex _statementRoot;
        private readonly SourceExpressionIndex _expressionRoot;
        private readonly IReadOnlyList<SourceSymbolIndex> _rootParameters;
        private readonly List<Avm1SourceCodeUnit> _codeUnits = [];
        private readonly List<Avm1SourceScope> _scopes = [];
        private readonly SourceScopeIndex[] _expressionScopes;
        private readonly SourceScopeIndex[] _statementScopes;
        private readonly SourceScopeIndex[] _symbolScopes;
        private readonly SourceCodeUnitIndex[] _functionCodeUnits;
        private readonly SourceScopeIndex[] _ownedStatementScopes;
        private readonly Avm1SourceExpressionUse[] _expressionUses;
        private readonly bool[] _expressionConflicts;
        private readonly bool[] _statementConflicts;
        private readonly bool[] _symbolConflicts;
        private readonly bool[] _functionConflicts;
        private readonly SourceCodeUnitIndex[] _declarationExpressionOwners;
        private readonly SourceCodeUnitIndex[] _declarationStatementOwners;
        private int _conflictCount;

        public Analyzer(
            Avm1SourceArena arena,
            SourceStatementIndex statementRoot,
            SourceExpressionIndex expressionRoot,
            IReadOnlyList<SourceSymbolIndex> rootParameters)
        {
            _arena = arena;
            _statementRoot = statementRoot;
            _expressionRoot = expressionRoot;
            _rootParameters = rootParameters;
            _expressionScopes = CreateInvalidScopeArray(arena.Expressions.Count);
            _statementScopes = CreateInvalidScopeArray(arena.Statements.Count);
            _symbolScopes = CreateInvalidScopeArray(arena.Symbols.Count);
            _functionCodeUnits = CreateInvalidCodeUnitArray(arena.Functions.Count);
            _ownedStatementScopes = CreateInvalidScopeArray(arena.Statements.Count);
            _expressionUses = new Avm1SourceExpressionUse[arena.Expressions.Count];
            _expressionConflicts = new bool[arena.Expressions.Count];
            _statementConflicts = new bool[arena.Statements.Count];
            _symbolConflicts = new bool[arena.Symbols.Count];
            _functionConflicts = new bool[arena.Functions.Count];
            _declarationExpressionOwners = CreateInvalidCodeUnitArray(
                arena.Expressions.Count);
            _declarationStatementOwners = CreateInvalidCodeUnitArray(
                arena.Statements.Count);
        }

        public Avm1SourceScopeAnalysis Run()
        {
            var root = AddCodeUnit(
                SourceCodeUnitIndex.Invalid,
                SourceScopeIndex.Invalid,
                SourceFunctionIndex.Invalid,
                dynamicScope: false);
            var rootScope = _codeUnits[root.Value].RootScope;
            foreach (var parameter in _rootParameters)
                AssignSymbol(parameter, rootScope);

            PredeclareCodeUnit(root, rootScope, _statementRoot, _expressionRoot);
            VisitRoot(rootScope, _statementRoot, _expressionRoot);

            foreach (var function in _arena.Functions)
            {
                if (_functionCodeUnits[function.Index.Value].IsValid)
                    continue;
                MarkFunctionConflict(function.Index);
                VisitFunction(function.Index, rootScope);
            }

            AssignUnownedReferencedSymbols();
            InvalidateConflictingScopes(_expressionScopes, _expressionConflicts);
            InvalidateConflictingScopes(_statementScopes, _statementConflicts);
            InvalidateConflictingScopes(_symbolScopes, _symbolConflicts);
            for (var i = 0; i < _functionCodeUnits.Length; i++)
            {
                if (_functionConflicts[i])
                    _functionCodeUnits[i] = SourceCodeUnitIndex.Invalid;
            }

            return new Avm1SourceScopeAnalysis(
                _arena,
                _codeUnits.ToArray(),
                _scopes.ToArray(),
                _expressionScopes,
                _statementScopes,
                _symbolScopes,
                _functionCodeUnits,
                _expressionUses,
                _conflictCount);
        }

        private SourceCodeUnitIndex AddCodeUnit(
            SourceCodeUnitIndex parent,
            SourceScopeIndex declarationScope,
            SourceFunctionIndex function,
            bool dynamicScope)
        {
            var codeUnit = new SourceCodeUnitIndex(_codeUnits.Count);
            _codeUnits.Add(default);
            var rootScope = AddScope(
                Avm1SourceScopeKind.CodeUnit,
                declarationScope,
                codeUnit,
                SourceStatementIndex.Invalid,
                dynamicScope);
            _codeUnits[codeUnit.Value] = new Avm1SourceCodeUnit(
                codeUnit,
                parent,
                declarationScope,
                rootScope,
                function);
            return codeUnit;
        }

        private SourceScopeIndex AddScope(
            Avm1SourceScopeKind kind,
            SourceScopeIndex parent,
            SourceCodeUnitIndex codeUnit,
            SourceStatementIndex owner,
            bool dynamicScope)
        {
            var inheritedDynamic = parent.IsValid &&
                _scopes[parent.Value].Flags.HasFlag(Avm1SourceScopeFlags.Dynamic);
            var flags = dynamicScope || inheritedDynamic
                ? Avm1SourceScopeFlags.Dynamic
                : Avm1SourceScopeFlags.None;
            var index = new SourceScopeIndex(_scopes.Count);
            _scopes.Add(new Avm1SourceScope(
                index,
                kind,
                parent,
                codeUnit,
                owner,
                flags));
            return index;
        }

        private void PredeclareCodeUnit(
            SourceCodeUnitIndex codeUnit,
            SourceScopeIndex rootScope,
            SourceStatementIndex statementRoot,
            SourceExpressionIndex expressionRoot)
        {
            if (statementRoot.IsValid)
                PredeclareStatement(statementRoot, codeUnit, rootScope);
            if (expressionRoot.IsValid)
                PredeclareExpression(expressionRoot, codeUnit, rootScope);
        }

        private void PredeclareStatement(
            SourceStatementIndex index,
            SourceCodeUnitIndex codeUnit,
            SourceScopeIndex rootScope)
        {
            if (!AssignDeclarationOwner(
                    _declarationStatementOwners,
                    index.Value,
                    codeUnit))
            {
                return;
            }

            var statement = _arena[index];
            if (statement.Kind is
                    Avm1SourceStatementKind.VariableDeclaration or
                    Avm1SourceStatementKind.FunctionDeclaration &&
                statement.Symbol.IsValid)
            {
                AssignSymbol(statement.Symbol, rootScope);
            }
            if (statement.Kind is Avm1SourceStatementKind.ForIn &&
                statement.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey) &&
                TryGetReferencedSymbol(statement.Expression, out var keySymbol))
            {
                AssignSymbol(keySymbol, rootScope);
            }

            PredeclareExpressionIfValid(statement.Expression, codeUnit, rootScope);
            PredeclareExpressionIfValid(statement.SecondaryExpression, codeUnit, rootScope);
            for (var i = 0; i < statement.Expressions.Count; i++)
            {
                PredeclareExpression(
                    _arena.GetExpression(statement, i),
                    codeUnit,
                    rootScope);
            }
            for (var i = 0; i < statement.Initializers.Count; i++)
            {
                PredeclareStatement(
                    _arena.GetInitializer(statement, i),
                    codeUnit,
                    rootScope);
            }
            for (var i = 0; i < statement.Children.Count; i++)
                PredeclareStatement(_arena.GetChild(statement, i), codeUnit, rootScope);
        }

        private void PredeclareExpressionIfValid(
            SourceExpressionIndex index,
            SourceCodeUnitIndex codeUnit,
            SourceScopeIndex rootScope)
        {
            if (index.IsValid)
                PredeclareExpression(index, codeUnit, rootScope);
        }

        private void PredeclareExpression(
            SourceExpressionIndex index,
            SourceCodeUnitIndex codeUnit,
            SourceScopeIndex rootScope)
        {
            if (!AssignDeclarationOwner(
                    _declarationExpressionOwners,
                    index.Value,
                    codeUnit))
            {
                return;
            }

            var expression = _arena[index];
            for (var i = 0; i < expression.Children.Count; i++)
                PredeclareExpression(_arena.GetChild(expression, i), codeUnit, rootScope);
        }

        private bool AssignDeclarationOwner(
            SourceCodeUnitIndex[] owners,
            int index,
            SourceCodeUnitIndex codeUnit)
        {
            var existing = owners[index];
            if (!existing.IsValid)
            {
                owners[index] = codeUnit;
                return true;
            }
            if (existing != codeUnit)
                _conflictCount++;
            return false;
        }

        private void VisitRoot(
            SourceScopeIndex scope,
            SourceStatementIndex statementRoot,
            SourceExpressionIndex expressionRoot)
        {
            if (statementRoot.IsValid)
                VisitStatement(statementRoot, scope);
            if (expressionRoot.IsValid)
            {
                VisitExpression(
                    expressionRoot,
                    scope,
                    Avm1SourceExpressionUse.Read);
            }
        }

        private void VisitStatement(SourceStatementIndex index, SourceScopeIndex scope)
        {
            var statement = _arena[index];
            if (statement.Kind is Avm1SourceStatementKind.CatchClause)
            {
                var catchScope = GetOwnedScope(
                    statement,
                    scope,
                    Avm1SourceScopeKind.Catch,
                    dynamicScope: false);
                if (!AssignStatement(index, catchScope))
                    return;
                if (statement.Symbol.IsValid)
                    AssignSymbol(statement.Symbol, catchScope);
                VisitStatementContents(statement, catchScope);
                return;
            }

            if (!AssignStatement(index, scope))
                return;
            if (statement.Kind is
                Avm1SourceStatementKind.With or
                Avm1SourceStatementKind.TellTarget)
            {
                VisitStatementExpressions(statement, scope);
                var dynamicScope = GetOwnedScope(
                    statement,
                    scope,
                    statement.Kind is Avm1SourceStatementKind.With
                        ? Avm1SourceScopeKind.With
                        : Avm1SourceScopeKind.Target,
                    dynamicScope: true);
                for (var i = 0; i < statement.Initializers.Count; i++)
                    VisitStatement(_arena.GetInitializer(statement, i), scope);
                for (var i = 0; i < statement.Children.Count; i++)
                    VisitStatement(_arena.GetChild(statement, i), dynamicScope);
                return;
            }

            VisitStatementContents(statement, scope);
        }

        private void VisitStatementContents(
            Avm1SourceStatement statement,
            SourceScopeIndex scope)
        {
            VisitStatementExpressions(statement, scope);
            for (var i = 0; i < statement.Initializers.Count; i++)
                VisitStatement(_arena.GetInitializer(statement, i), scope);
            for (var i = 0; i < statement.Children.Count; i++)
                VisitStatement(_arena.GetChild(statement, i), scope);
        }

        private void VisitStatementExpressions(
            Avm1SourceStatement statement,
            SourceScopeIndex scope)
        {
            if (statement.Kind is Avm1SourceStatementKind.ForIn)
            {
                VisitExpressionIfValid(
                    statement.Expression,
                    scope,
                    Avm1SourceExpressionUse.Write);
                VisitExpressionIfValid(
                    statement.SecondaryExpression,
                    scope,
                    Avm1SourceExpressionUse.Read);
            }
            else
            {
                VisitExpressionIfValid(
                    statement.Expression,
                    scope,
                    Avm1SourceExpressionUse.Read);
                VisitExpressionIfValid(
                    statement.SecondaryExpression,
                    scope,
                    Avm1SourceExpressionUse.Read);
            }
            for (var i = 0; i < statement.Expressions.Count; i++)
            {
                VisitExpression(
                    _arena.GetExpression(statement, i),
                    scope,
                    Avm1SourceExpressionUse.Read);
            }
        }

        private SourceScopeIndex GetOwnedScope(
            Avm1SourceStatement statement,
            SourceScopeIndex parent,
            Avm1SourceScopeKind kind,
            bool dynamicScope)
        {
            var existing = _ownedStatementScopes[statement.Index.Value];
            if (existing.IsValid)
            {
                var scope = _scopes[existing.Value];
                if (scope.Parent != parent || scope.Kind != kind)
                    MarkStatementConflict(statement.Index);
                return existing;
            }

            var codeUnit = _scopes[parent.Value].CodeUnit;
            var created = AddScope(
                kind,
                parent,
                codeUnit,
                statement.Index,
                dynamicScope);
            _ownedStatementScopes[statement.Index.Value] = created;
            return created;
        }

        private void VisitExpressionIfValid(
            SourceExpressionIndex index,
            SourceScopeIndex scope,
            Avm1SourceExpressionUse use)
        {
            if (index.IsValid)
                VisitExpression(index, scope, use);
        }

        private void VisitExpression(
            SourceExpressionIndex index,
            SourceScopeIndex scope,
            Avm1SourceExpressionUse use)
        {
            _expressionUses[index.Value] |= use;
            if (!AssignExpression(index, scope))
                return;

            var expression = _arena[index];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                expression.Symbol.IsValid)
            {
                AssignReferenceSymbol(expression.Symbol, scope);
            }

            VisitExpressionChildren(expression, scope);
            if (expression.Kind is Avm1SourceExpressionKind.FunctionLiteral &&
                expression.Function.IsValid)
            {
                VisitFunction(expression.Function, scope);
            }
        }

        private void VisitExpressionChildren(
            Avm1SourceExpression expression,
            SourceScopeIndex scope)
        {
            switch (expression.Kind)
            {
                case Avm1SourceExpressionKind.Assignment:
                    VisitExpression(
                        _arena.GetChild(expression, 0),
                        scope,
                        expression.Operator is Avm1SourceOperator.Assign
                            ? Avm1SourceExpressionUse.Write
                            : Avm1SourceExpressionUse.ReadWrite);
                    VisitExpression(
                        _arena.GetChild(expression, 1),
                        scope,
                        Avm1SourceExpressionUse.Read);
                    return;

                case Avm1SourceExpressionKind.Unary
                    when expression.Operator is
                        Avm1SourceOperator.PrefixIncrement or
                        Avm1SourceOperator.PrefixDecrement or
                        Avm1SourceOperator.PostfixIncrement or
                        Avm1SourceOperator.PostfixDecrement:
                    VisitExpression(
                        _arena.GetChild(expression, 0),
                        scope,
                        Avm1SourceExpressionUse.ReadWrite);
                    return;

                case Avm1SourceExpressionKind.Delete:
                    VisitExpression(
                        _arena.GetChild(expression, 0),
                        scope,
                        Avm1SourceExpressionUse.Delete);
                    return;

                case Avm1SourceExpressionKind.Call:
                    VisitExpression(
                        _arena.GetChild(expression, 0),
                        scope,
                        Avm1SourceExpressionUse.Invoke);
                    VisitRemainingChildren(expression, 1, scope);
                    return;

                case Avm1SourceExpressionKind.New:
                    VisitExpression(
                        _arena.GetChild(expression, 0),
                        scope,
                        Avm1SourceExpressionUse.Construct);
                    VisitRemainingChildren(expression, 1, scope);
                    return;

                default:
                    VisitRemainingChildren(expression, 0, scope);
                    return;
            }
        }

        private void VisitRemainingChildren(
            Avm1SourceExpression expression,
            int firstChild,
            SourceScopeIndex scope)
        {
            for (var i = firstChild; i < expression.Children.Count; i++)
            {
                VisitExpression(
                    _arena.GetChild(expression, i),
                    scope,
                    Avm1SourceExpressionUse.Read);
            }
        }

        private void VisitFunction(
            SourceFunctionIndex functionIndex,
            SourceScopeIndex declarationScope)
        {
            var existing = _functionCodeUnits[functionIndex.Value];
            if (existing.IsValid)
            {
                if (_codeUnits[existing.Value].DeclarationScope != declarationScope)
                    MarkFunctionConflict(functionIndex);
                return;
            }

            var function = _arena[functionIndex];
            var parent = _scopes[declarationScope.Value].CodeUnit;
            var codeUnit = AddCodeUnit(
                parent,
                declarationScope,
                functionIndex,
                function.Flags.HasFlag(
                    Avm1SourceFunctionFlags.CapturesDynamicScope));
            _functionCodeUnits[functionIndex.Value] = codeUnit;
            var rootScope = _codeUnits[codeUnit.Value].RootScope;

            if (function.NameSymbol.IsValid)
            {
                var nameScope = function.Flags.HasFlag(
                    Avm1SourceFunctionFlags.Declaration)
                    ? _codeUnits[parent.Value].RootScope
                    : rootScope;
                AssignSymbol(function.NameSymbol, nameScope);
            }
            for (var i = 0; i < function.Parameters.Count; i++)
                AssignSymbol(_arena.GetParameter(function, i), rootScope);

            PredeclareCodeUnit(
                codeUnit,
                rootScope,
                function.Body,
                SourceExpressionIndex.Invalid);
            VisitStatement(function.Body, rootScope);
        }

        private void AssignReferenceSymbol(
            SourceSymbolIndex symbolIndex,
            SourceScopeIndex referenceScope)
        {
            var symbol = _arena[symbolIndex];
            var codeUnit = _scopes[referenceScope.Value].CodeUnit;
            var rootScope = _codeUnits[codeUnit.Value].RootScope;
            if (symbol.Kind is
                Avm1SourceSymbolKind.This or
                Avm1SourceSymbolKind.Super or
                Avm1SourceSymbolKind.Arguments)
            {
                AssignSymbol(symbolIndex, rootScope);
                return;
            }

            if (!_symbolScopes[symbolIndex.Value].IsValid)
                AssignSymbol(symbolIndex, rootScope);
        }

        private void AssignUnownedReferencedSymbols()
        {
            for (var i = 0; i < _arena.Expressions.Count; i++)
            {
                var expression = _arena[new SourceExpressionIndex(i)];
                if (expression.Kind is not Avm1SourceExpressionKind.SymbolReference ||
                    !expression.Symbol.IsValid ||
                    _symbolScopes[expression.Symbol.Value].IsValid ||
                    !_expressionScopes[i].IsValid)
                {
                    continue;
                }
                AssignReferenceSymbol(expression.Symbol, _expressionScopes[i]);
            }
        }

        private bool AssignExpression(
            SourceExpressionIndex expression,
            SourceScopeIndex scope) =>
            AssignScope(
                _expressionScopes,
                _expressionConflicts,
                expression.Value,
                scope);

        private bool AssignStatement(
            SourceStatementIndex statement,
            SourceScopeIndex scope) =>
            AssignScope(
                _statementScopes,
                _statementConflicts,
                statement.Value,
                scope);

        private void AssignSymbol(SourceSymbolIndex symbol, SourceScopeIndex scope) =>
            AssignScope(
                _symbolScopes,
                _symbolConflicts,
                symbol.Value,
                scope);

        private bool AssignScope(
            SourceScopeIndex[] assignments,
            bool[] conflicts,
            int index,
            SourceScopeIndex scope)
        {
            var existing = assignments[index];
            if (!existing.IsValid)
            {
                assignments[index] = scope;
                return true;
            }
            if (existing == scope)
                return false;
            if (!conflicts[index])
            {
                conflicts[index] = true;
                _conflictCount++;
            }
            return false;
        }

        private void MarkStatementConflict(SourceStatementIndex statement)
        {
            if (_statementConflicts[statement.Value])
                return;
            _statementConflicts[statement.Value] = true;
            _conflictCount++;
        }

        private void MarkFunctionConflict(SourceFunctionIndex function)
        {
            if (_functionConflicts[function.Value])
                return;
            _functionConflicts[function.Value] = true;
            _conflictCount++;
        }

        private bool TryGetReferencedSymbol(
            SourceExpressionIndex expressionIndex,
            out SourceSymbolIndex symbol)
        {
            if (expressionIndex.IsValid)
            {
                var expression = _arena[expressionIndex];
                if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                    expression.Symbol.IsValid)
                {
                    symbol = expression.Symbol;
                    return true;
                }
            }
            symbol = SourceSymbolIndex.Invalid;
            return false;
        }

        private static SourceScopeIndex[] CreateInvalidScopeArray(int length)
        {
            var result = new SourceScopeIndex[length];
            Array.Fill(result, SourceScopeIndex.Invalid);
            return result;
        }

        private static SourceCodeUnitIndex[] CreateInvalidCodeUnitArray(int length)
        {
            var result = new SourceCodeUnitIndex[length];
            Array.Fill(result, SourceCodeUnitIndex.Invalid);
            return result;
        }

        private static void InvalidateConflictingScopes(
            SourceScopeIndex[] scopes,
            bool[] conflicts)
        {
            for (var i = 0; i < scopes.Length; i++)
            {
                if (conflicts[i])
                    scopes[i] = SourceScopeIndex.Invalid;
            }
        }
    }
}
