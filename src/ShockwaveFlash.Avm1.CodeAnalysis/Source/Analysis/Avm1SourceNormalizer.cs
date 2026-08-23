namespace ShockwaveFlash.Avm1.Source;

internal readonly record struct Avm1SourceNormalizationDiagnostic(
    string Code,
    string Path,
    string Message);

internal sealed class Avm1SourceNormalizationResult
{
    public Avm1SourceNormalizationResult(
        Avm1SourceMethod method,
        int rewriteCount,
        Avm1SourceNormalizationDiagnostic[] diagnostics)
    {
        Method = method;
        RewriteCount = rewriteCount;
        Diagnostics = diagnostics;
    }

    public Avm1SourceMethod Method { get; }

    public int RewriteCount { get; }

    public IReadOnlyList<Avm1SourceNormalizationDiagnostic> Diagnostics { get; }
}

internal static class Avm1SourceNormalizer
{
    public static Avm1SourceNormalizationResult Normalize(
        Avm1SourceMethod method,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Avm1SourceNormalizationDiagnostic>();
        for (var i = 0; i < method.Diagnostics.Count; i++)
        {
            var diagnostic = method.Diagnostics[i];
            if (diagnostic.Severity is not Avm1SourceDiagnosticSeverity.Error)
                continue;

            diagnostics.Add(new Avm1SourceNormalizationDiagnostic(
                "AVM1EQ001",
                $".diagnostics[{i}]",
                $"Source HIR error {diagnostic.Code}: {diagnostic.Message}"));
        }
        if (diagnostics.Count != 0)
            return new Avm1SourceNormalizationResult(method, 0, diagnostics.ToArray());

        var current = method;
        var rewriteCount = 0;
        var maximumPasses = Math.Max(
            1,
            method.Arena.Statements.Count + method.Arena.Expressions.Count + 1);
        for (var passIndex = 0; passIndex < maximumPasses; passIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pass = new NormalizationPass(current, cancellationToken);
            var normalized = pass.Run();
            rewriteCount += pass.RewriteCount;
            if (pass.Diagnostics.Count != 0)
            {
                return new Avm1SourceNormalizationResult(
                    normalized,
                    rewriteCount,
                    pass.Diagnostics.ToArray());
            }
            if (!pass.Changed)
            {
                return new Avm1SourceNormalizationResult(
                    normalized,
                    rewriteCount,
                    []);
            }
            current = normalized;
        }

        diagnostics.Add(new Avm1SourceNormalizationDiagnostic(
            "AVM1EQ004",
            ".body",
            "Source HIR normalization did not reach a fixed point."));
        return new Avm1SourceNormalizationResult(
            current,
            rewriteCount,
            diagnostics.ToArray());
    }

    private sealed class NormalizationPass
    {
        private const int MaximumDepth = 2048;
        private readonly Avm1SourceMethod _method;
        private readonly Avm1SourceArena _source;
        private readonly CancellationToken _cancellationToken;
        private readonly Avm1SourceArena.Builder _builder = new();
        private readonly Dictionary<SourceSymbolIndex, SourceSymbolIndex> _symbols = [];
        private readonly Dictionary<SourceLabelIndex, SourceLabelIndex> _labels = [];
        private readonly Dictionary<SourceFunctionIndex, SourceFunctionIndex> _functions = [];
        private readonly HashSet<SourceFunctionIndex> _activeFunctions = [];
        private readonly Dictionary<Avm1SourceSymbolKind, int> _symbolOrdinals = [];
        private readonly List<CompletionTransport> _activeCompletionTransports = [];
        private readonly List<Avm1SourceStatementKind> _activeSequenceOwners = [];
        private readonly List<Avm1SourceNormalizationDiagnostic> _diagnostics = [];
        private readonly HashSet<SourceLabelIndex> _elidedLabels;
        private readonly SourceSymbolIndex[] _hoistedSwitchSymbols;
        private readonly HashSet<SourceSymbolIndex> _unusedGeneratedDeclarations;
        private readonly HashSet<SourceSymbolIndex> _branchSplitDeclarations;
        private readonly SourceTypeIndex _unknownType;

        private sealed record CompletionTransport(
            SourceSymbolIndex Symbol,
            IReadOnlyDictionary<int, CompletionRewrite> Rewrites);

        private readonly record struct CompletionRewrite(
            Avm1SourceStatementKind Kind,
            SourceLabelIndex Label);

        private readonly record struct LabelScope(
            SourceStatementIndex Statement,
            Avm1SourceStatementKind Kind,
            SourceLabelIndex Label);

        public NormalizationPass(
            Avm1SourceMethod method,
            CancellationToken cancellationToken)
        {
            _method = method;
            _source = method.Arena;
            _cancellationToken = cancellationToken;
            _unknownType = _builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
            _elidedLabels = FindElidableLabels();
            _hoistedSwitchSymbols = FindHoistedSwitchSymbols();
            _unusedGeneratedDeclarations = FindUnusedGeneratedDeclarations();
            _branchSplitDeclarations = FindBranchSplitDeclarations();
        }

        public bool Changed { get; private set; }

        public int RewriteCount { get; private set; }

        public List<Avm1SourceNormalizationDiagnostic> Diagnostics => _diagnostics;

        public Avm1SourceMethod Run()
        {
            var parameters = new SourceSymbolIndex[_method.Parameters.Count];
            for (var i = 0; i < parameters.Length; i++)
            {
                parameters[i] = CloneSymbol(
                    _method.Parameters[i],
                    $".parameters[{i}]");
            }

            var body = CloneStatement(_method.Body, ".body", depth: 0);
            if (_hoistedSwitchSymbols.Length > 0)
            {
                var clonedBody = _builder.GetStatement(body);
                if (clonedBody.Kind is Avm1SourceStatementKind.Block)
                {
                    var children = new List<SourceStatementIndex>(
                        _hoistedSwitchSymbols.Length + clonedBody.Children.Count);
                    foreach (var symbol in _hoistedSwitchSymbols)
                    {
                        children.Add(_builder.AddStatement(
                            Avm1SourceStatementKind.VariableDeclaration,
                            symbol: CloneSymbol(symbol, ".body.hoistedSwitchSymbol")));
                    }
                    for (var i = 0; i < clonedBody.Children.Count; i++)
                        children.Add(_builder.GetStatementChild(clonedBody, i));
                    body = _builder.AddStatement(
                        Avm1SourceStatementKind.Block,
                        children: children);
                    Changed = true;
                    RewriteCount++;
                }
            }
            return new Avm1SourceMethod(
                _builder.ToArena(),
                body,
                parameters,
                []);
        }

        private SourceStatementIndex CloneStatement(
            SourceStatementIndex index,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions = null,
            bool removeTerminalSwitchBreak = false)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetStatement(index, path, depth, out var statement))
                return _builder.AddStatement(Avm1SourceStatementKind.Block);
            if (!IsSupported(statement.Kind))
            {
                AddIncomplete(
                    "AVM1EQ002",
                    path,
                    $"Statement kind {statement.Kind} is outside the normalized-HIR subset.");
                return _builder.AddStatement(Avm1SourceStatementKind.Block);
            }
            if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                _unusedGeneratedDeclarations.Contains(statement.Symbol))
            {
                Changed = true;
                RewriteCount++;
                if (statement.Expression.IsValid &&
                    !IsPureStable(statement.Expression))
                {
                    return _builder.AddStatement(
                        Avm1SourceStatementKind.Expression,
                        expression: CloneExpression(
                            statement.Expression,
                            path + ".expression",
                            depth + 1,
                            substitutions));
                }
                return _builder.AddStatement(Avm1SourceStatementKind.Block);
            }
            if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                _branchSplitDeclarations.Contains(statement.Symbol))
            {
                Changed = true;
                RewriteCount++;
                if (!statement.Expression.IsValid)
                    return _builder.AddStatement(Avm1SourceStatementKind.Block);

                var target = _builder.AddExpression(
                    Avm1SourceExpressionKind.SymbolReference,
                    symbol: CloneSymbol(statement.Symbol, path + ".symbol"));
                var value = CloneExpression(
                    statement.Expression,
                    path + ".expression",
                    depth + 1,
                    substitutions);
                var assignment = _builder.AddExpression(
                    Avm1SourceExpressionKind.Assignment,
                    Avm1SourceOperator.Assign,
                    children: [target, value]);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: assignment);
            }
            if (statement.Kind is Avm1SourceStatementKind.Expression &&
                IsRedundantSelfAssignment(statement.Expression))
            {
                Changed = true;
                RewriteCount++;
                return _builder.AddStatement(Avm1SourceStatementKind.Block);
            }
            if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                _hoistedSwitchSymbols.Contains(statement.Symbol))
            {
                Changed = true;
                RewriteCount++;
                if (!statement.Expression.IsValid)
                    return _builder.AddStatement(Avm1SourceStatementKind.Block);

                var target = _builder.AddExpression(
                    Avm1SourceExpressionKind.SymbolReference,
                    symbol: CloneSymbol(statement.Symbol, path + ".symbol"));
                var value = CloneExpression(
                    statement.Expression,
                    path + ".expression",
                    depth + 1,
                    substitutions);
                var assignment = _builder.AddExpression(
                    Avm1SourceExpressionKind.Assignment,
                    Avm1SourceOperator.Assign,
                    children: [target, value]);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: assignment);
            }
            if (TryCloneCompletionWrite(statement, path, out var completion))
                return completion;
            if (TryCloneCanonicalTerminalIf(
                statement,
                path,
                depth,
                substitutions,
                out var terminalIf))
            {
                Changed = true;
                RewriteCount++;
                return terminalIf;
            }
            if (TryCloneCanonicalComparisonIf(
                statement,
                path,
                depth,
                substitutions,
                out var comparisonIf))
            {
                Changed = true;
                RewriteCount++;
                return comparisonIf;
            }
            if (TryCloneInvertedIf(
                statement,
                path,
                depth,
                substitutions,
                out var invertedIf))
            {
                Changed = true;
                RewriteCount++;
                return invertedIf;
            }

            var expression = CloneOptionalExpression(
                statement.Expression,
                path + ".expression",
                depth + 1,
                substitutions,
                discardResult: statement.Kind is Avm1SourceStatementKind.Expression);
            var secondaryExpression = CloneOptionalExpression(
                statement.SecondaryExpression,
                path + ".secondaryExpression",
                depth + 1,
                substitutions);
            var symbol = statement.Symbol.IsValid
                ? CloneSymbol(statement.Symbol, path + ".symbol")
                : SourceSymbolIndex.Invalid;
            var label = CloneOptionalLabel(statement.Label, path + ".label");
            var name = CloneOptionalName(statement.Name, path + ".name");
            var children = removeTerminalSwitchBreak &&
                statement.Kind is Avm1SourceStatementKind.SwitchCase or
                    Avm1SourceStatementKind.SwitchDefault
                ? CloneTerminalSwitchClauseChildren(statement, path, depth + 1)
                : CloneStatementChildren(statement, path, depth + 1);
            var initializers = CloneStatementList(
                statement.Initializers,
                path + ".initializers",
                depth + 1,
                normalizeSequence: true);
            var expressions = CloneExpressionList(
                statement.Expressions,
                path + ".expressions",
                depth + 1,
                substitutions,
                discardResults: statement.Kind is Avm1SourceStatementKind.For);
            return _builder.AddStatement(
                statement.Kind,
                expression: expression,
                secondaryExpression: secondaryExpression,
                symbol: symbol,
                name: name,
                label: label,
                flags: statement.Flags,
                children: children,
                initializers: initializers,
                expressions: expressions);
        }

        private SourceSymbolIndex[] FindHoistedSwitchSymbols()
        {
            var result = new List<SourceSymbolIndex>();
            var visited = new HashSet<SourceStatementIndex>();
            Visit(_method.Body, SourceStatementIndex.Invalid);
            return result.ToArray();

            void Visit(
                SourceStatementIndex index,
                SourceStatementIndex switchClause)
            {
                if (!index.IsValid ||
                    index.Value >= _source.Statements.Count ||
                    !visited.Add(index))
                {
                    return;
                }

                var statement = _source[index];
                if (switchClause.IsValid &&
                    statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                    IsHoistableLocalSymbol(statement.Symbol) &&
                    !IsSymbolConfinedToStatement(
                        statement.Symbol,
                        switchClause) &&
                    !result.Contains(statement.Symbol))
                {
                    result.Add(statement.Symbol);
                }

                for (var i = 0; i < statement.Initializers.Count; i++)
                {
                    Visit(
                        _source.GetInitializer(statement, i),
                        switchClause);
                }
                for (var i = 0; i < statement.Children.Count; i++)
                {
                    var child = _source.GetChild(statement, i);
                    Visit(
                        child,
                        statement.Kind is Avm1SourceStatementKind.Switch
                            ? child
                            : switchClause);
                }
            }
        }

        private bool IsSymbolConfinedToStatement(
            SourceSymbolIndex symbol,
            SourceStatementIndex statement)
        {
            var allUses = new SymbolUses();
            CountStatementUses(_method.Body, symbol, ref allUses, depth: 0);
            foreach (var function in _source.Functions)
                CountStatementUses(function.Body, symbol, ref allUses, depth: 0);

            var statementUses = new SymbolUses();
            CountStatementUses(statement, symbol, ref statementUses, depth: 0);
            return allUses.Declarations == statementUses.Declarations &&
                allUses.Reads == statementUses.Reads &&
                allUses.Writes == statementUses.Writes;
        }

        private HashSet<SourceSymbolIndex> FindUnusedGeneratedDeclarations()
        {
            var candidates = _source.Statements
                .Where(statement =>
                    statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                    IsGeneratedTransportSymbol(statement.Symbol))
                .Select(statement => statement.Symbol)
                .ToHashSet();
            if (candidates.Count == 0)
                return candidates;

            foreach (var symbol in candidates.ToArray())
            {
                var uses = new SymbolUses();
                CountStatementUses(_method.Body, symbol, ref uses, depth: 0);
                foreach (var function in _source.Functions)
                    CountStatementUses(function.Body, symbol, ref uses, depth: 0);
                if (uses.Reads != 0 || uses.Writes != 0)
                    candidates.Remove(symbol);
            }
            return candidates;
        }

        private HashSet<SourceSymbolIndex> FindBranchSplitDeclarations()
        {
            var result = new HashSet<SourceSymbolIndex>();
            var visited = new HashSet<SourceStatementIndex>();
            Visit(_method.Body);
            foreach (var function in _source.Functions)
                Visit(function.Body);
            return result;

            void Visit(SourceStatementIndex index)
            {
                if (!index.IsValid ||
                    index.Value >= _source.Statements.Count ||
                    !visited.Add(index))
                {
                    return;
                }

                var statement = _source[index];
                if (statement.Kind is Avm1SourceStatementKind.If &&
                    statement.Children.Count == 2)
                {
                    var whenTrue = _source.GetChild(statement, 0);
                    var whenFalse = _source.GetChild(statement, 1);
                    var trueDeclarations = CollectDeclarations(whenTrue);
                    var falseDeclarations = CollectDeclarations(whenFalse);
                    foreach (var symbol in trueDeclarations)
                    {
                        if (falseDeclarations.Contains(symbol) &&
                            IsAuthoredLocalSymbol(symbol))
                        {
                            result.Add(symbol);
                        }
                    }
                    AddSplitDeclarations(trueDeclarations, whenFalse);
                    AddSplitDeclarations(falseDeclarations, whenTrue);
                }

                for (var i = 0; i < statement.Initializers.Count; i++)
                    Visit(_source.GetInitializer(statement, i));
                for (var i = 0; i < statement.Children.Count; i++)
                    Visit(_source.GetChild(statement, i));
            }

            void AddSplitDeclarations(
                HashSet<SourceSymbolIndex> declared,
                SourceStatementIndex assignmentBranch)
            {
                foreach (var symbol in declared)
                {
                    if (!IsAuthoredLocalSymbol(symbol))
                        continue;

                    var uses = new SymbolUses();
                    CountStatementUses(
                        assignmentBranch,
                        symbol,
                        ref uses,
                        depth: 0);
                    if (uses.Writes != 0)
                        result.Add(symbol);
                }
            }

            HashSet<SourceSymbolIndex> CollectDeclarations(
                SourceStatementIndex root)
            {
                var declared = new HashSet<SourceSymbolIndex>();
                Collect(root, depth: 0);
                return declared;

                void Collect(SourceStatementIndex index, int depth)
                {
                    if (depth > MaximumDepth ||
                        !index.IsValid ||
                        index.Value >= _source.Statements.Count)
                    {
                        return;
                    }

                    var statement = _source[index];
                    if (statement.Kind is
                            Avm1SourceStatementKind.VariableDeclaration &&
                        statement.Symbol.IsValid)
                    {
                        declared.Add(statement.Symbol);
                    }
                    for (var i = 0; i < statement.Initializers.Count; i++)
                        Collect(_source.GetInitializer(statement, i), depth + 1);
                    for (var i = 0; i < statement.Children.Count; i++)
                        Collect(_source.GetChild(statement, i), depth + 1);
                }
            }
        }

        private bool IsRedundantSelfAssignment(SourceExpressionIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;

            var expression = _source[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Assignment ||
                expression.Operator is not Avm1SourceOperator.Assign ||
                expression.Children.Count != 2)
            {
                return false;
            }

            var target = _source[_source.GetChild(expression, 0)];
            var value = _source[_source.GetChild(expression, 1)];
            return target.Kind is Avm1SourceExpressionKind.SymbolReference &&
                value.Kind is Avm1SourceExpressionKind.SymbolReference &&
                target.Symbol.IsValid &&
                target.Symbol == value.Symbol;
        }

        private bool IsHoistableLocalSymbol(SourceSymbolIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Symbols.Count)
                return false;
            var symbol = _source[index];
            if (symbol.Kind is not (
                Avm1SourceSymbolKind.Local or
                Avm1SourceSymbolKind.Temporary))
            {
                return false;
            }
            return !symbol.Name.IsValid ||
                symbol.Name.Value >= _source.Strings.Count ||
                !IsCompletionTransportName(_source[symbol.Name]);
        }

        private bool TryCloneCanonicalTerminalIf(
            Avm1SourceStatement statement,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceStatementIndex result)
        {
            result = SourceStatementIndex.Invalid;
            if (statement.Kind is not Avm1SourceStatementKind.If ||
                statement.Children.Count != 2)
            {
                return false;
            }

            var whenTrue = _source.GetChild(statement, 0);
            var whenFalse = _source.GetChild(statement, 1);
            var trueRank = GetTerminalRank(whenTrue);
            var falseRank = GetTerminalRank(whenFalse);
            if (trueRank < 0 ||
                falseRank < 0 ||
                trueRank < falseRank ||
                trueRank == falseRank &&
                GetStatementComplexity(whenTrue) >=
                    GetStatementComplexity(whenFalse))
            {
                return false;
            }

            result = _builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: CloneNegatedCondition(
                    statement.Expression,
                    path + ".expression",
                    depth + 1,
                    substitutions),
                secondaryExpression: CloneOptionalExpression(
                    statement.SecondaryExpression,
                    path + ".secondaryExpression",
                    depth + 1,
                    substitutions),
                symbol: statement.Symbol.IsValid
                    ? CloneSymbol(statement.Symbol, path + ".symbol")
                    : SourceSymbolIndex.Invalid,
                name: CloneOptionalName(statement.Name, path + ".name"),
                label: CloneOptionalLabel(statement.Label, path + ".label"),
                flags: statement.Flags,
                children:
                [
                    CloneStatement(
                        whenFalse,
                        path + ".children[1]",
                        depth + 1,
                        substitutions),
                    CloneStatement(
                        whenTrue,
                        path + ".children[0]",
                        depth + 1,
                        substitutions)
                ],
                initializers: CloneStatementList(
                    statement.Initializers,
                    path + ".initializers",
                    depth + 1,
                    normalizeSequence: true),
                expressions: CloneExpressionList(
                    statement.Expressions,
                    path + ".expressions",
                    depth + 1,
                    substitutions));
            return true;
        }

        private bool TryCloneCanonicalComparisonIf(
            Avm1SourceStatement statement,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceStatementIndex result)
        {
            result = SourceStatementIndex.Invalid;
            if (statement.Kind is not Avm1SourceStatementKind.If ||
                statement.Children.Count != 2 ||
                !statement.Expression.IsValid ||
                statement.Expression.Value >= _source.Expressions.Count ||
                GetTerminalRank(_source.GetChild(statement, 0)) >= 0 ||
                GetTerminalRank(_source.GetChild(statement, 1)) >= 0)
            {
                return false;
            }

            var condition = _source[statement.Expression];
            if (condition.Kind is not Avm1SourceExpressionKind.Binary ||
                condition.Children.Count != 2 ||
                !TryGetNegatedBinaryOperator(
                    condition.Operator,
                    out var negatedOperator) ||
                (int)negatedOperator >= (int)condition.Operator)
            {
                return false;
            }

            result = _builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: CloneNegatedCondition(
                    statement.Expression,
                    path + ".expression",
                    depth + 1,
                    substitutions),
                secondaryExpression: CloneOptionalExpression(
                    statement.SecondaryExpression,
                    path + ".secondaryExpression",
                    depth + 1,
                    substitutions),
                symbol: statement.Symbol.IsValid
                    ? CloneSymbol(statement.Symbol, path + ".symbol")
                    : SourceSymbolIndex.Invalid,
                name: CloneOptionalName(statement.Name, path + ".name"),
                label: CloneOptionalLabel(statement.Label, path + ".label"),
                flags: statement.Flags,
                children:
                [
                    CloneStatement(
                        _source.GetChild(statement, 1),
                        path + ".children[1]",
                        depth + 1,
                        substitutions),
                    CloneStatement(
                        _source.GetChild(statement, 0),
                        path + ".children[0]",
                        depth + 1,
                        substitutions)
                ],
                initializers: CloneStatementList(
                    statement.Initializers,
                    path + ".initializers",
                    depth + 1,
                    normalizeSequence: true),
                expressions: CloneExpressionList(
                    statement.Expressions,
                    path + ".expressions",
                    depth + 1,
                    substitutions));
            return true;
        }

        private SourceExpressionIndex CloneNegatedCondition(
            SourceExpressionIndex index,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions)
            => NegateClonedExpression(CloneExpression(
                index,
                path,
                depth + 1,
                substitutions));

        private SourceExpressionIndex NegateClonedExpression(SourceExpressionIndex index)
            => NegateClonedExpression(index, out _);

        private SourceExpressionIndex NegateClonedExpression(
            SourceExpressionIndex index,
            out bool simplified)
        {
            simplified = false;
            var expression = _builder.GetExpression(index);
            if (expression.Kind is Avm1SourceExpressionKind.Unary &&
                expression.Operator is Avm1SourceOperator.LogicalNot &&
                expression.Children.Count == 1)
            {
                simplified = true;
                return _builder.GetExpressionChild(expression, 0);
            }

            if (expression.Kind is Avm1SourceExpressionKind.Binary &&
                expression.Children.Count == 2 &&
                TryGetNegatedBinaryOperator(expression.Operator, out var negatedOperator))
            {
                simplified = true;
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    negatedOperator,
                    children:
                    [
                        _builder.GetExpressionChild(expression, 0),
                        _builder.GetExpressionChild(expression, 1)
                    ]);
            }

            return _builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                Avm1SourceOperator.LogicalNot,
                children: [index]);
        }

        private int GetTerminalRank(SourceStatementIndex index, int depth = 0)
        {
            if (!index.IsValid ||
                index.Value >= _source.Statements.Count ||
                depth > MaximumDepth)
            {
                return -1;
            }

            var statement = _source[index];
            if (statement.Kind is Avm1SourceStatementKind.Block)
            {
                return statement.Children.Count == 0
                    ? -1
                    : GetTerminalRank(
                        _source.GetChild(
                            statement,
                            statement.Children.Count - 1),
                        depth + 1);
            }

            return statement.Kind switch
            {
                Avm1SourceStatementKind.Throw => 0,
                Avm1SourceStatementKind.Return => 1,
                Avm1SourceStatementKind.Continue => 2,
                Avm1SourceStatementKind.Break => 3,
                _ => -1
            };
        }

        private int GetStatementComplexity(
            SourceStatementIndex index,
            int depth = 0)
        {
            if (!index.IsValid ||
                index.Value >= _source.Statements.Count ||
                depth > MaximumDepth)
            {
                return 0;
            }

            var statement = _source[index];
            var baseComplexity = statement.Kind is
                Avm1SourceStatementKind.Block or
                Avm1SourceStatementKind.SwitchCase or
                Avm1SourceStatementKind.SwitchDefault
                    ? 0L
                    : 1L;
            var result = (int)Math.Min(
                int.MaxValue,
                baseComplexity +
                (statement.Expression.IsValid ? 1 : 0) +
                (statement.SecondaryExpression.IsValid ? 1 : 0) +
                statement.Expressions.Count);
            for (var i = 0; i < statement.Initializers.Count; i++)
            {
                result = (int)Math.Min(
                    int.MaxValue,
                    (long)result + GetStatementComplexity(
                        _source.GetInitializer(statement, i),
                        depth + 1));
            }
            for (var i = 0; i < statement.Children.Count; i++)
            {
                result = (int)Math.Min(
                    int.MaxValue,
                    (long)result + GetStatementComplexity(
                        _source.GetChild(statement, i),
                        depth + 1));
            }
            return result;
        }

        private static bool TryGetNegatedBinaryOperator(
            Avm1SourceOperator @operator,
            out Avm1SourceOperator negated)
        {
            negated = @operator switch
            {
                Avm1SourceOperator.Equal => Avm1SourceOperator.NotEqual,
                Avm1SourceOperator.NotEqual => Avm1SourceOperator.Equal,
                Avm1SourceOperator.StrictEqual =>
                    Avm1SourceOperator.StrictNotEqual,
                Avm1SourceOperator.StrictNotEqual =>
                    Avm1SourceOperator.StrictEqual,
                Avm1SourceOperator.Less =>
                    Avm1SourceOperator.GreaterOrEqual,
                Avm1SourceOperator.LessOrEqual =>
                    Avm1SourceOperator.Greater,
                Avm1SourceOperator.Greater =>
                    Avm1SourceOperator.LessOrEqual,
                Avm1SourceOperator.GreaterOrEqual =>
                    Avm1SourceOperator.Less,
                _ => Avm1SourceOperator.None
            };
            return negated is not Avm1SourceOperator.None;
        }

        private bool TryCloneInvertedIf(
            Avm1SourceStatement statement,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceStatementIndex result)
        {
            result = SourceStatementIndex.Invalid;
            if (statement.Kind is not Avm1SourceStatementKind.If ||
                statement.Children.Count != 2 ||
                !TryGetLogicalNotOperand(statement.Expression, out var condition))
            {
                return false;
            }

            var trueRank = GetTerminalRank(_source.GetChild(statement, 0));
            var falseRank = GetTerminalRank(_source.GetChild(statement, 1));
            if (trueRank >= 0 && falseRank >= 0 && trueRank < falseRank)
                return false;

            result = _builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: CloneExpression(
                    condition,
                    path + ".expression.children[0]",
                    depth + 1,
                    substitutions),
                secondaryExpression: CloneOptionalExpression(
                    statement.SecondaryExpression,
                    path + ".secondaryExpression",
                    depth + 1,
                    substitutions),
                symbol: statement.Symbol.IsValid
                    ? CloneSymbol(statement.Symbol, path + ".symbol")
                    : SourceSymbolIndex.Invalid,
                name: CloneOptionalName(statement.Name, path + ".name"),
                label: CloneOptionalLabel(statement.Label, path + ".label"),
                flags: statement.Flags,
                children:
                [
                    CloneStatement(
                        _source.GetChild(statement, 1),
                        path + ".children[1]",
                        depth + 1,
                        substitutions),
                    CloneStatement(
                        _source.GetChild(statement, 0),
                        path + ".children[0]",
                        depth + 1,
                        substitutions)
                ],
                initializers: CloneStatementList(
                    statement.Initializers,
                    path + ".initializers",
                    depth + 1,
                    normalizeSequence: true),
                expressions: CloneExpressionList(
                    statement.Expressions,
                    path + ".expressions",
                    depth + 1,
                    substitutions));
            return true;
        }

        private SourceStatementIndex[] CloneStatementChildren(
            Avm1SourceStatement statement,
            string path,
            int depth)
        {
            if (statement.Kind is Avm1SourceStatementKind.Switch &&
                TryGetStatementList(statement.Children, path + ".children", out var clauses))
            {
                var result = new SourceStatementIndex[clauses.Length];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = CloneStatement(
                        clauses[i],
                        $"{path}.children[{i}]",
                        depth,
                        removeTerminalSwitchBreak: i == result.Length - 1);
                }
                return result;
            }

            var normalizeSequence = statement.Kind is
                Avm1SourceStatementKind.Block or
                Avm1SourceStatementKind.While or
                Avm1SourceStatementKind.DoWhile or
                Avm1SourceStatementKind.For or
                Avm1SourceStatementKind.ForIn or
                Avm1SourceStatementKind.SwitchCase or
                Avm1SourceStatementKind.SwitchDefault;
            if (!normalizeSequence)
            {
                return CloneStatementList(
                    statement.Children,
                    path + ".children",
                    depth,
                    normalizeSequence: false);
            }

            _activeSequenceOwners.Add(statement.Kind);
            try
            {
                return CloneStatementList(
                    statement.Children,
                    path + ".children",
                    depth,
                    normalizeSequence: true);
            }
            finally
            {
                _activeSequenceOwners.RemoveAt(_activeSequenceOwners.Count - 1);
            }
        }

        private SourceStatementIndex[] CloneTerminalSwitchClauseChildren(
            Avm1SourceStatement statement,
            string path,
            int depth)
        {
            if (!TryGetStatementList(statement.Children, path + ".children", out var children))
                return [];
            if (children.Length > 0 &&
                TryGetStatement(
                    children[^1],
                    $"{path}.children[{children.Length - 1}]",
                    depth,
                    out var terminal) &&
                terminal.Kind is Avm1SourceStatementKind.Break &&
                !terminal.Label.IsValid)
            {
                Changed = true;
                RewriteCount++;
                children = children[..^1];
            }
            _activeSequenceOwners.Add(statement.Kind);
            try
            {
                return NormalizeSequence(children, path + ".children", depth);
            }
            finally
            {
                _activeSequenceOwners.RemoveAt(_activeSequenceOwners.Count - 1);
            }
        }

        private SourceStatementIndex[] CloneStatementList(
            SourceStatementList list,
            string path,
            int depth,
            bool normalizeSequence)
        {
            if (!TryGetStatementList(list, path, out var source))
                return [];
            if (normalizeSequence)
                return NormalizeSequence(source, path, depth);

            var result = new SourceStatementIndex[source.Length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = CloneStatement(
                    source[i],
                    $"{path}[{i}]",
                    depth);
            }
            return result;
        }

        private SourceStatementIndex[] NormalizeSequence(
            SourceStatementIndex[] source,
            string path,
            int depth)
        {
            if (_activeSequenceOwners.Count == 1 &&
                _activeSequenceOwners[0] is Avm1SourceStatementKind.Block)
            {
                source = HoistCodeUnitFunctionDeclarations(source);
            }

            var result = new List<SourceStatementIndex>(source.Length);
            for (var i = 0; i < source.Length;)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (i == source.Length - 1 &&
                    TryRewriteTrailingLoopConditional(
                        source[i],
                        $"{path}[{i}]",
                        depth,
                        out var guard,
                        out var guardedBody))
                {
                    result.Add(guard);
                    AppendStatementOrBlockChildren(result, guardedBody);
                    i++;
                    Changed = true;
                    RewriteCount++;
                    continue;
                }
                if (TryRewriteCompletionTransport(
                        source,
                        i,
                        path,
                        depth,
                        out var assignmentResult,
                        out var assignmentConsumed) ||
                    TryElideDuplicatedReceiverMaterialization(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteRegisterCompletionTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteForInKeyTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryInlineForInitializerTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryInlineSeparatedStableTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryCollapseTransportAliases(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewritePrefixTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewritePostfixTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteReversedObjectTransports(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryInlineObjectLiteralValueTransports(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewritePureAliasTransports(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteReverseEvaluationTransports(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteAssignmentResult(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteAssignmentChainTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteReturnTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteConditionalLogicalTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteAssignedLogicalTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteAssignedLogicalAliasTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteSplitLogicalTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteLogicalAliasChain(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteLogicalAliasTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteGuardedLogicalTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewritePhiTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryRewriteBranchPhiTransport(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryInlineTransportAssignments(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed) ||
                    TryInlineTransportDeclarations(
                        source,
                        i,
                        path,
                        depth,
                        out assignmentResult,
                        out assignmentConsumed))
                {
                    result.Add(assignmentResult);
                    i += assignmentConsumed;
                    Changed = true;
                    RewriteCount++;
                    continue;
                }

                if (TryGetStatement(source[i], $"{path}[{i}]", depth, out var statement) &&
                    statement.Kind is Avm1SourceStatementKind.Block)
                {
                    if (TryGetStatementList(
                        statement.Children,
                        $"{path}[{i}].children",
                        out var nested))
                    {
                        for (var nestedIndex = 0;
                            nestedIndex < nested.Length;
                            nestedIndex++)
                        {
                            result.Add(CloneStatement(
                                nested[nestedIndex],
                                $"{path}[{i}].children[{nestedIndex}]",
                                depth + 1));
                        }
                        Changed = true;
                        RewriteCount++;
                    }
                    i++;
                    continue;
                }

                var cloned = CloneStatement(source[i], $"{path}[{i}]", depth);
                if (!TryAppendTerminalIf(result, cloned))
                    result.Add(cloned);
                i++;
            }

            result.RemoveAll(index =>
            {
                var statement = _builder.GetStatement(index);
                return statement.Kind is Avm1SourceStatementKind.Block &&
                    statement.Children.Count == 0;
            });
            RewriteTerminalSwitchDefaults(result);
            RewriteTerminalGuardTails(result);
            return result.ToArray();
        }

        private bool TryElideDuplicatedReceiverMaterialization(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 1 >= statements.Length ||
                !TryGetStatement(
                    statements[start],
                    $"{path}[{start}]",
                    depth,
                    out var declaration) ||
                declaration.Kind is not Avm1SourceStatementKind.VariableDeclaration ||
                !_unusedGeneratedDeclarations.Contains(declaration.Symbol) ||
                !declaration.Expression.IsValid)
            {
                return false;
            }

            for (var consumerIndex = start + 1;
                consumerIndex < statements.Length;
                consumerIndex++)
            {
                if (TryGetAssignmentStatement(
                        statements[consumerIndex],
                        out var target,
                        out _) &&
                    target.IsValid &&
                    target.Value < _source.Expressions.Count)
                {
                    var member = _source[target];
                    if (member.Kind is Avm1SourceExpressionKind.MemberAccess &&
                        member.Children.Count == 2 &&
                        HaveSameRecoveryRange(
                            declaration.Expression,
                            _source.GetChild(member, 0)))
                    {
                        if (consumerIndex == start + 1)
                        {
                            result = CloneStatement(
                                statements[consumerIndex],
                                $"{path}[{consumerIndex}]",
                                depth);
                            consumed = 2;
                        }
                        else
                        {
                            result = _builder.AddStatement(
                                Avm1SourceStatementKind.Block);
                            consumed = 1;
                        }
                        return true;
                    }
                }

                if (!TryGetGeneratedDeclaration(
                        statements[consumerIndex],
                        out _,
                        out _))
                {
                    return false;
                }
            }

            return false;
        }

        private bool HaveSameRecoveryRange(
            SourceExpressionIndex left,
            SourceExpressionIndex right)
        {
            if (!left.IsValid ||
                left.Value >= _source.Expressions.Count ||
                !right.IsValid ||
                right.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var leftOriginIndex = _source[left].Origin;
            var rightOriginIndex = _source[right].Origin;
            if (!leftOriginIndex.IsValid ||
                leftOriginIndex.Value >= _source.Origins.Count ||
                !rightOriginIndex.IsValid ||
                rightOriginIndex.Value >= _source.Origins.Count)
            {
                return false;
            }

            var leftOrigin = _source[leftOriginIndex];
            var rightOrigin = _source[rightOriginIndex];
            return leftOrigin.RecoveryUnit == rightOrigin.RecoveryUnit &&
                leftOrigin.StartOffset >= 0 &&
                leftOrigin.EndOffset > leftOrigin.StartOffset &&
                leftOrigin.StartOffset == rightOrigin.StartOffset &&
                leftOrigin.EndOffset == rightOrigin.EndOffset;
        }

        private void RewriteTerminalSwitchDefaults(
            List<SourceStatementIndex> statements)
        {
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = _builder.GetStatement(statements[i]);
                if (statement.Kind is not Avm1SourceStatementKind.Switch ||
                    statement.Children.Count < 2)
                {
                    continue;
                }

                var defaultClause = _builder.GetStatementChild(
                    statement,
                    statement.Children.Count - 1);
                var defaultStatement = _builder.GetStatement(defaultClause);
                if (defaultStatement.Kind is not
                        Avm1SourceStatementKind.SwitchDefault ||
                    defaultStatement.Children.Count == 0)
                {
                    continue;
                }

                var canMoveDefault = true;
                for (var childIndex = 0;
                    childIndex < statement.Children.Count - 1;
                    childIndex++)
                {
                    var clause = _builder.GetStatementChild(
                        statement,
                        childIndex);
                    if (_builder.GetStatement(clause).Kind is not
                            Avm1SourceStatementKind.SwitchCase ||
                        !BypassesFollowingSequence(clause))
                    {
                        canMoveDefault = false;
                        break;
                    }
                }
                if (!canMoveDefault)
                    continue;

                var clauses = new SourceStatementIndex[
                    statement.Children.Count - 1];
                for (var childIndex = 0;
                    childIndex < clauses.Length;
                    childIndex++)
                {
                    clauses[childIndex] = _builder.GetStatementChild(
                        statement,
                        childIndex);
                }

                statements[i] = _builder.AddStatement(
                    Avm1SourceStatementKind.Switch,
                    expression: statement.Expression,
                    secondaryExpression: statement.SecondaryExpression,
                    symbol: statement.Symbol,
                    name: statement.Name,
                    label: statement.Label,
                    flags: statement.Flags,
                    children: clauses,
                    initializers: GetBuilderInitializers(statement),
                    expressions: GetBuilderExpressions(statement));

                var defaultChildren = new SourceStatementIndex[
                    defaultStatement.Children.Count];
                for (var childIndex = 0;
                    childIndex < defaultChildren.Length;
                    childIndex++)
                {
                    defaultChildren[childIndex] = _builder.GetStatementChild(
                        defaultStatement,
                        childIndex);
                }
                statements.InsertRange(i + 1, defaultChildren);
                i += defaultChildren.Length;
                Changed = true;
                RewriteCount++;
            }
        }

        private void RewriteTerminalGuardTails(
            List<SourceStatementIndex> statements)
        {
            for (var guardIndex = statements.Count - 2;
                guardIndex >= 0;
                guardIndex--)
            {
                var conditional = _builder.GetStatement(statements[guardIndex]);
                if (conditional.Kind is not Avm1SourceStatementKind.If ||
                    conditional.Children.Count != 1 ||
                    conditional.Label.IsValid ||
                    !conditional.Expression.IsValid)
                {
                    continue;
                }

                var guardedBody = _builder.GetStatementChild(conditional, 0);
                var guardedRank = GetBuilderTerminalRank(guardedBody);
                var tailRank = GetTerminalSequenceRank(
                    statements,
                    guardIndex + 1);
                if (guardedRank < 0 || tailRank < 0)
                    continue;

                var guardedComplexity = GetBuilderStatementComplexity(
                    guardedBody);
                var tailComplexity = GetBuilderSequenceComplexity(
                    statements,
                    guardIndex + 1);
                if (guardedRank < tailRank ||
                    guardedRank == tailRank &&
                    guardedComplexity >= tailComplexity)
                {
                    continue;
                }

                var tail = _builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: statements.Skip(guardIndex + 1).ToArray());
                var canonicalGuard = _builder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: NegateClonedExpression(
                        conditional.Expression),
                    secondaryExpression: conditional.SecondaryExpression,
                    symbol: conditional.Symbol,
                    name: conditional.Name,
                    label: conditional.Label,
                    flags: conditional.Flags,
                    children: [tail],
                    initializers: GetBuilderInitializers(conditional),
                    expressions: GetBuilderExpressions(conditional));

                statements.RemoveRange(
                    guardIndex,
                    statements.Count - guardIndex);
                statements.Add(canonicalGuard);
                AppendStatementOrBlockChildren(statements, guardedBody);
                Changed = true;
                RewriteCount++;
                return;
            }
        }

        private bool BypassesFollowingSequence(
            SourceStatementIndex index,
            int depth = 0)
        {
            if (!index.IsValid || depth > MaximumDepth)
                return false;

            var statement = _builder.GetStatement(index);
            if (statement.Kind is
                Avm1SourceStatementKind.Return or
                Avm1SourceStatementKind.Throw or
                Avm1SourceStatementKind.Continue)
            {
                return true;
            }

            if (statement.Kind is
                Avm1SourceStatementKind.Block or
                Avm1SourceStatementKind.SwitchCase or
                Avm1SourceStatementKind.SwitchDefault)
            {
                return statement.Children.Count > 0 &&
                    BypassesFollowingSequence(
                        _builder.GetStatementChild(
                            statement,
                            statement.Children.Count - 1),
                        depth + 1);
            }

            return statement.Kind is Avm1SourceStatementKind.If &&
                statement.Children.Count == 2 &&
                BypassesFollowingSequence(
                    _builder.GetStatementChild(statement, 0),
                    depth + 1) &&
                BypassesFollowingSequence(
                    _builder.GetStatementChild(statement, 1),
                    depth + 1);
        }

        private int GetTerminalSequenceRank(
            List<SourceStatementIndex> statements,
            int start)
            => start < statements.Count
                ? GetBuilderTerminalRank(statements[^1])
                : -1;

        private int GetBuilderTerminalRank(
            SourceStatementIndex index,
            int depth = 0)
        {
            if (!index.IsValid || depth > MaximumDepth)
                return -1;

            var statement = _builder.GetStatement(index);
            if (statement.Kind is
                Avm1SourceStatementKind.Block or
                Avm1SourceStatementKind.SwitchCase or
                Avm1SourceStatementKind.SwitchDefault)
            {
                return statement.Children.Count == 0
                    ? -1
                    : GetBuilderTerminalRank(
                        _builder.GetStatementChild(
                            statement,
                            statement.Children.Count - 1),
                        depth + 1);
            }

            if (statement.Kind is Avm1SourceStatementKind.If &&
                statement.Children.Count == 2)
            {
                var trueRank = GetBuilderTerminalRank(
                    _builder.GetStatementChild(statement, 0),
                    depth + 1);
                var falseRank = GetBuilderTerminalRank(
                    _builder.GetStatementChild(statement, 1),
                    depth + 1);
                return trueRank < 0 || falseRank < 0
                    ? -1
                    : Math.Min(trueRank, falseRank);
            }

            if (statement.Kind is Avm1SourceStatementKind.Switch)
            {
                var hasDefault = false;
                var rank = int.MaxValue;
                for (var i = 0; i < statement.Children.Count; i++)
                {
                    var clause = _builder.GetStatementChild(statement, i);
                    var clauseStatement = _builder.GetStatement(clause);
                    hasDefault |= clauseStatement.Kind is
                        Avm1SourceStatementKind.SwitchDefault;
                    var clauseRank = GetBuilderTerminalRank(
                        clause,
                        depth + 1);
                    if (clauseRank < 0)
                        return -1;
                    rank = Math.Min(rank, clauseRank);
                }
                return hasDefault && rank != int.MaxValue ? rank : -1;
            }

            return statement.Kind switch
            {
                Avm1SourceStatementKind.Throw => 0,
                Avm1SourceStatementKind.Return => 1,
                Avm1SourceStatementKind.Continue => 2,
                Avm1SourceStatementKind.Break => 3,
                _ => -1
            };
        }

        private int GetBuilderSequenceComplexity(
            List<SourceStatementIndex> statements,
            int start)
        {
            var result = 0;
            for (var i = start; i < statements.Count; i++)
            {
                result = (int)Math.Min(
                    int.MaxValue,
                    (long)result +
                    GetBuilderStatementComplexity(statements[i]));
            }
            return result;
        }

        private int GetBuilderStatementComplexity(
            SourceStatementIndex index,
            int depth = 0)
        {
            if (!index.IsValid || depth > MaximumDepth)
                return 0;

            var statement = _builder.GetStatement(index);
            var baseComplexity = statement.Kind is
                Avm1SourceStatementKind.Block or
                Avm1SourceStatementKind.SwitchCase or
                Avm1SourceStatementKind.SwitchDefault
                    ? 0L
                    : 1L;
            var result = (int)Math.Min(
                int.MaxValue,
                baseComplexity +
                (statement.Expression.IsValid ? 1 : 0) +
                (statement.SecondaryExpression.IsValid ? 1 : 0) +
                statement.Expressions.Count);
            for (var i = 0; i < statement.Initializers.Count; i++)
            {
                result = (int)Math.Min(
                    int.MaxValue,
                    (long)result + GetBuilderStatementComplexity(
                        _builder.GetStatementInitializer(statement, i),
                        depth + 1));
            }
            for (var i = 0; i < statement.Children.Count; i++)
            {
                result = (int)Math.Min(
                    int.MaxValue,
                    (long)result + GetBuilderStatementComplexity(
                        _builder.GetStatementChild(statement, i),
                        depth + 1));
            }
            return result;
        }

        private SourceStatementIndex[] GetBuilderInitializers(
            Avm1SourceStatement statement)
        {
            var result = new SourceStatementIndex[statement.Initializers.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = _builder.GetStatementInitializer(statement, i);
            return result;
        }

        private SourceExpressionIndex[] GetBuilderExpressions(
            Avm1SourceStatement statement)
        {
            var result = new SourceExpressionIndex[statement.Expressions.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = _builder.GetStatementExpression(statement, i);
            return result;
        }

        private bool TryRewriteTrailingLoopConditional(
            SourceStatementIndex index,
            string path,
            int depth,
            out SourceStatementIndex guard,
            out SourceStatementIndex body)
        {
            guard = SourceStatementIndex.Invalid;
            body = SourceStatementIndex.Invalid;
            if (_activeSequenceOwners.Count == 0 ||
                _activeSequenceOwners[^1] is not (
                    Avm1SourceStatementKind.While or
                    Avm1SourceStatementKind.DoWhile or
                    Avm1SourceStatementKind.For or
                    Avm1SourceStatementKind.ForIn) ||
                !TryGetStatement(index, path, depth, out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                conditional.Children.Count is < 1 or > 2 ||
                conditional.Label.IsValid ||
                !conditional.Expression.IsValid ||
                (TryGetCompletionDispatchSymbol(index, out var dispatchSymbol) &&
                    IsCompletionTransportSymbol(dispatchSymbol)))
            {
                return false;
            }

            var sourceBody = _source.GetChild(conditional, 0);
            if (IsEmptyStatement(sourceBody))
                return false;

            if (conditional.Children.Count == 2)
            {
                var sourceTail = _source.GetChild(conditional, 1);
                if (IsEmptyStatement(sourceTail))
                    return false;

                var guardedBody = CloneStatement(
                    sourceBody,
                    path + ".children[0]",
                    depth + 1);
                if (!IsTerminalStatement(guardedBody))
                {
                    var statements = new List<SourceStatementIndex>();
                    AppendStatementOrBlockChildren(statements, guardedBody);
                    statements.Add(_builder.AddStatement(
                        Avm1SourceStatementKind.Continue));
                    guardedBody = _builder.AddStatement(
                        Avm1SourceStatementKind.Block,
                        children: statements);
                }

                guard = _builder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: CloneExpression(
                        conditional.Expression,
                        path + ".expression",
                        depth + 1,
                        substitutions: null),
                    children: [guardedBody]);
                body = CloneStatement(
                    sourceTail,
                    path + ".children[1]",
                    depth + 1);
                return true;
            }

            var continuation = _builder.AddStatement(
                Avm1SourceStatementKind.Continue);
            var continuationBlock = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: [continuation]);
            guard = _builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: CloneNegatedCondition(
                    conditional.Expression,
                    path + ".expression",
                    depth + 1,
                    substitutions: null),
                children: [continuationBlock]);
            body = CloneStatement(
                sourceBody,
                path + ".children[0]",
                depth + 1);
            return true;
        }

        private SourceStatementIndex[] HoistCodeUnitFunctionDeclarations(
            SourceStatementIndex[] source)
        {
            var sawNonFunction = false;
            var requiresHoist = false;
            foreach (var index in source)
            {
                var isFunction = index.IsValid &&
                    index.Value < _source.Statements.Count &&
                    _source[index].Kind is
                        Avm1SourceStatementKind.FunctionDeclaration;
                if (isFunction)
                    requiresHoist |= sawNonFunction;
                else
                    sawNonFunction = true;
            }
            if (!requiresHoist)
                return source;

            Changed = true;
            RewriteCount++;
            return
            [
                .. source.Where(index =>
                    index.IsValid &&
                    index.Value < _source.Statements.Count &&
                    _source[index].Kind is
                        Avm1SourceStatementKind.FunctionDeclaration),
                .. source.Where(index =>
                    !index.IsValid ||
                    index.Value >= _source.Statements.Count ||
                    _source[index].Kind is not
                        Avm1SourceStatementKind.FunctionDeclaration)
            ];
        }

        private bool TryAppendTerminalIf(
            List<SourceStatementIndex> destination,
            SourceStatementIndex index)
        {
            var conditional = _builder.GetStatement(index);
            if (conditional.Kind is not Avm1SourceStatementKind.If ||
                conditional.Children.Count != 2)
            {
                return false;
            }

            var whenTrue = _builder.GetStatementChild(conditional, 0);
            var whenFalse = _builder.GetStatementChild(conditional, 1);
            var trueTerminates = IsTerminalStatement(whenTrue);
            var falseTerminates = IsTerminalStatement(whenFalse);
            if (!trueTerminates && !falseTerminates)
                return false;

            var terminal = trueTerminates ? whenTrue : whenFalse;
            var tail = trueTerminates ? whenFalse : whenTrue;
            var condition = conditional.Expression;
            if (!trueTerminates)
                condition = NegateClonedExpression(condition);

            destination.Add(_builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: condition,
                secondaryExpression: conditional.SecondaryExpression,
                symbol: conditional.Symbol,
                name: conditional.Name,
                label: conditional.Label,
                flags: conditional.Flags,
                children: [terminal]));
            AppendStatementOrBlockChildren(destination, tail);
            Changed = true;
            RewriteCount++;
            return true;
        }

        private bool IsTerminalStatement(SourceStatementIndex index, int depth = 0)
        {
            if (!index.IsValid || depth > MaximumDepth)
                return false;

            var statement = _builder.GetStatement(index);
            if (statement.Kind is
                Avm1SourceStatementKind.Return or
                Avm1SourceStatementKind.Throw or
                Avm1SourceStatementKind.Break or
                Avm1SourceStatementKind.Continue)
            {
                return true;
            }

            if (statement.Kind is Avm1SourceStatementKind.Block)
            {
                return statement.Children.Count > 0 &&
                    IsTerminalStatement(
                        _builder.GetStatementChild(
                            statement,
                            statement.Children.Count - 1),
                        depth + 1);
            }

            return statement.Kind is Avm1SourceStatementKind.If &&
                statement.Children.Count == 2 &&
                IsTerminalStatement(
                    _builder.GetStatementChild(statement, 0),
                    depth + 1) &&
                IsTerminalStatement(
                    _builder.GetStatementChild(statement, 1),
                    depth + 1);
        }

        private void AppendStatementOrBlockChildren(
            List<SourceStatementIndex> destination,
            SourceStatementIndex index)
        {
            var statement = _builder.GetStatement(index);
            if (statement.Kind is not Avm1SourceStatementKind.Block)
            {
                destination.Add(index);
                return;
            }

            for (var i = 0; i < statement.Children.Count; i++)
                destination.Add(_builder.GetStatementChild(statement, i));
        }

        private bool TryRewriteCompletionTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var symbol,
                    out var initializer) ||
                !IsGeneratedTransportSymbol(symbol) ||
                !TryGetIntegerLiteral(initializer, out var initialToken) ||
                initialToken != 0 ||
                !TryGetStatement(
                    statements[start + 1],
                    $"{path}[{start + 1}]",
                    depth,
                    out var protectedStatement) ||
                protectedStatement.Kind is not (
                    Avm1SourceStatementKind.Try or
                    Avm1SourceStatementKind.With))
            {
                return false;
            }

            var rewrites = new Dictionary<int, CompletionRewrite>();
            var dispatchEnd = start + 2;
            var normalTail = SourceStatementIndex.Invalid;
            while (dispatchEnd < statements.Length &&
                TryCollectCompletionDispatch(
                    statements[dispatchEnd],
                    symbol,
                    rewrites))
            {
                dispatchEnd++;
            }
            if (rewrites.Count == 0 &&
                dispatchEnd < statements.Length &&
                TryGetInverseSwitchCompletionDispatch(
                    statements[dispatchEnd],
                    symbol,
                    out var inverseToken,
                    out normalTail))
            {
                rewrites.Add(
                    inverseToken,
                    new CompletionRewrite(
                        Avm1SourceStatementKind.Break,
                        SourceLabelIndex.Invalid));
                dispatchEnd++;
            }
            var dispatchCount = rewrites.Count;
            if (rewrites.Count == 0 ||
                !TryAddImplicitLoopContinueRewrite(
                    protectedStatement.Index,
                    symbol,
                    rewrites) ||
                !TryValidateCompletionWrites(
                    protectedStatement.Index,
                    symbol,
                    rewrites,
                    out var writeCount) ||
                writeCount == 0 ||
                CountSymbolOccurrences(symbol) !=
                    1 + writeCount + dispatchCount)
            {
                return false;
            }

            _activeCompletionTransports.Add(new CompletionTransport(symbol, rewrites));
            try
            {
                var protectedResult = CloneStatement(
                    protectedStatement.Index,
                    $"{path}[{start + 1}]",
                    depth);
                if (!normalTail.IsValid)
                {
                    result = protectedResult;
                }
                else
                {
                    result = _builder.AddStatement(
                        Avm1SourceStatementKind.Block,
                        children:
                        [
                            protectedResult,
                            CloneStatement(
                                normalTail,
                                $"{path}[{dispatchEnd - 1}].children[0]",
                                depth)
                        ]);
                }
            }
            finally
            {
                _activeCompletionTransports.RemoveAt(
                    _activeCompletionTransports.Count - 1);
            }

            consumed = dispatchEnd - start;
            return true;
        }

        private bool TryGetInverseSwitchCompletionDispatch(
            SourceStatementIndex index,
            SourceSymbolIndex symbol,
            out int token,
            out SourceStatementIndex normalTail)
        {
            token = 0;
            normalTail = SourceStatementIndex.Invalid;
            if (_activeSequenceOwners.Count == 0 ||
                _activeSequenceOwners[^1] is not
                    Avm1SourceStatementKind.SwitchCase ||
                !index.IsValid ||
                index.Value >= _source.Statements.Count ||
                _source[index] is not
                {
                    Kind: Avm1SourceStatementKind.If,
                    Children.Count: 1
                } conditional ||
                !TryGetCompletionTokenComparison(
                    conditional.Expression,
                    symbol,
                    out token,
                    out var isEqual) ||
                isEqual ||
                token <= 0 ||
                (token & 1) != 0)
            {
                return false;
            }

            normalTail = _source.GetChild(conditional, 0);
            return EndsWithUnlabeledBreak(normalTail);
        }

        private bool EndsWithUnlabeledBreak(
            SourceStatementIndex index,
            int depth = 0)
        {
            if (!index.IsValid ||
                index.Value >= _source.Statements.Count ||
                depth > MaximumDepth)
            {
                return false;
            }

            var statement = _source[index];
            if (statement.Kind is Avm1SourceStatementKind.Break)
                return !statement.Label.IsValid;
            return statement.Kind is Avm1SourceStatementKind.Block &&
                statement.Children.Count > 0 &&
                EndsWithUnlabeledBreak(
                    _source.GetChild(statement, statement.Children.Count - 1),
                    depth + 1);
        }

        private bool TryRewriteRegisterCompletionTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 1 >= statements.Length ||
                !TryGetStatement(
                    statements[start],
                    $"{path}[{start}]",
                    depth,
                    out var protectedStatement) ||
                protectedStatement.Kind is not (
                    Avm1SourceStatementKind.Try or
                    Avm1SourceStatementKind.With) ||
                !TryGetCompletionDispatchSymbol(
                    statements[start + 1],
                    out var symbol) ||
                !IsGeneratedTransportSymbol(symbol) ||
                IsCompletionTransportSymbol(symbol))
            {
                return false;
            }

            var rewrites = new Dictionary<int, CompletionRewrite>();
            var dispatchEnd = start + 1;
            while (dispatchEnd < statements.Length &&
                TryCollectCompletionDispatch(
                    statements[dispatchEnd],
                    symbol,
                    rewrites))
            {
                dispatchEnd++;
            }
            var dispatchCount = rewrites.Count;
            if (rewrites.Count == 0 ||
                !TryAddImplicitLoopContinueRewrite(
                    protectedStatement.Index,
                    symbol,
                    rewrites) ||
                !TryValidateCompletionWrites(
                    protectedStatement.Index,
                    symbol,
                    rewrites,
                    out var writeCount) ||
                writeCount == 0 ||
                CountSymbolOccurrences(symbol) != writeCount + dispatchCount)
            {
                return false;
            }

            _activeCompletionTransports.Add(new CompletionTransport(symbol, rewrites));
            try
            {
                result = CloneStatement(
                    protectedStatement.Index,
                    $"{path}[{start}]",
                    depth);
            }
            finally
            {
                _activeCompletionTransports.RemoveAt(
                    _activeCompletionTransports.Count - 1);
            }

            consumed = dispatchEnd - start;
            return true;
        }

        private bool TryCloneCompletionWrite(
            Avm1SourceStatement statement,
            string path,
            out SourceStatementIndex result)
        {
            result = SourceStatementIndex.Invalid;
            if (_activeCompletionTransports.Count == 0 ||
                !TryGetCompletionWrite(
                    statement.Index,
                    out var symbol,
                    out var token))
            {
                return false;
            }

            for (var i = _activeCompletionTransports.Count - 1; i >= 0; i--)
            {
                var transport = _activeCompletionTransports[i];
                if (transport.Symbol != symbol)
                    continue;
                if (!transport.Rewrites.TryGetValue(token, out var rewrite))
                {
                    AddIncomplete(
                        "AVM1EQ005",
                        path,
                        $"Completion transport token {token} has no dispatcher route.");
                    return false;
                }

                result = _builder.AddStatement(
                    rewrite.Kind,
                    label: CloneOptionalLabel(rewrite.Label, path + ".label"));
                Changed = true;
                RewriteCount++;
                return true;
            }
            return false;
        }

        private bool TryAddImplicitLoopContinueRewrite(
            SourceStatementIndex protectedStatement,
            SourceSymbolIndex symbol,
            Dictionary<int, CompletionRewrite> rewrites)
        {
            var tokens = new HashSet<int>();
            Collect(protectedStatement);
            var missingTokens = tokens
                .Where(token => !rewrites.ContainsKey(token))
                .ToArray();
            if (missingTokens.Length == 0)
                return true;

            if (missingTokens.Length != 1 ||
                (missingTokens[0] & 1) == 0 ||
                _activeSequenceOwners.Count == 0 ||
                _activeSequenceOwners[^1] is not (
                    Avm1SourceStatementKind.While or
                    Avm1SourceStatementKind.DoWhile or
                    Avm1SourceStatementKind.For or
                    Avm1SourceStatementKind.ForIn))
            {
                return false;
            }

            rewrites.Add(
                missingTokens[0],
                new CompletionRewrite(
                    Avm1SourceStatementKind.Continue,
                    SourceLabelIndex.Invalid));
            return true;

            void Collect(SourceStatementIndex index)
            {
                if (!index.IsValid || index.Value >= _source.Statements.Count)
                    return;

                var statement = _source[index];
                if (TryGetCompletionWrite(
                    index,
                    out var writeSymbol,
                    out var token) &&
                    writeSymbol == symbol)
                {
                    tokens.Add(token);
                    return;
                }

                for (var i = 0; i < statement.Initializers.Count; i++)
                    Collect(_source.GetInitializer(statement, i));
                for (var i = 0; i < statement.Children.Count; i++)
                    Collect(_source.GetChild(statement, i));
            }
        }

        private bool TryCollectCompletionDispatch(
            SourceStatementIndex index,
            SourceSymbolIndex symbol,
            Dictionary<int, CompletionRewrite> rewrites)
        {
            var collected = new Dictionary<int, CompletionRewrite>();
            if (!Visit(index) ||
                collected.Count == 0 ||
                collected.Any(route => rewrites.ContainsKey(route.Key)))
            {
                return false;
            }

            foreach (var route in collected)
                rewrites.Add(route.Key, route.Value);
            return true;

            bool Visit(SourceStatementIndex statementIndex)
            {
                if (!TryUnwrapSingleStatement(statementIndex, out var conditional) ||
                    conditional.Kind is not Avm1SourceStatementKind.If ||
                    conditional.Children.Count is < 1 or > 2 ||
                    !TryGetCompletionTokenComparison(
                        conditional.Expression,
                        symbol,
                        out var token,
                        out var isEqual) ||
                    token <= 0)
                {
                    return false;
                }

                var routeChild = isEqual ? 0 : 1;
                var remainderChild = isEqual ? 1 : 0;
                if (routeChild >= conditional.Children.Count ||
                    !TryResolveCompletionRewrite(
                        _source.GetChild(conditional, routeChild),
                        token,
                        out var rewrite) ||
                    !collected.TryAdd(token, rewrite))
                {
                    return false;
                }

                return conditional.Children.Count == 1 ||
                    Visit(_source.GetChild(conditional, remainderChild));
            }
        }

        private bool TryGetCompletionTokenComparison(
            SourceExpressionIndex index,
            SourceSymbolIndex symbol,
            out int token,
            out bool isEqual)
        {
            token = 0;
            isEqual = false;
            if (!index.IsValid ||
                index.Value >= _source.Expressions.Count ||
                _source[index] is not
                {
                    Kind: Avm1SourceExpressionKind.Binary,
                    Operator: Avm1SourceOperator.StrictEqual or
                        Avm1SourceOperator.StrictNotEqual,
                    Children.Count: 2
                } comparison)
            {
                return false;
            }

            isEqual = comparison.Operator is Avm1SourceOperator.StrictEqual;
            var left = _source.GetChild(comparison, 0);
            var right = _source.GetChild(comparison, 1);
            return IsSymbolReference(left, symbol) &&
                    TryGetIntegerLiteral(right, out token) ||
                IsSymbolReference(right, symbol) &&
                    TryGetIntegerLiteral(left, out token);
        }

        private bool TryGetCompletionDispatchSymbol(
            SourceStatementIndex index,
            out SourceSymbolIndex symbol)
        {
            symbol = SourceSymbolIndex.Invalid;
            if (!TryUnwrapSingleStatement(index, out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                !conditional.Expression.IsValid ||
                conditional.Expression.Value >= _source.Expressions.Count ||
                _source[conditional.Expression] is not
                {
                    Kind: Avm1SourceExpressionKind.Binary,
                    Operator: Avm1SourceOperator.StrictEqual or
                        Avm1SourceOperator.StrictNotEqual,
                    Children.Count: 2
                } comparison)
            {
                return false;
            }

            var left = _source.GetChild(comparison, 0);
            var right = _source.GetChild(comparison, 1);
            return TryGetSymbolReference(left, out symbol) &&
                    TryGetIntegerLiteral(right, out _) ||
                TryGetSymbolReference(right, out symbol) &&
                    TryGetIntegerLiteral(left, out _);
        }

        private bool TryResolveCompletionRewrite(
            SourceStatementIndex index,
            int token,
            out CompletionRewrite rewrite)
        {
            rewrite = default;
            if (!TryUnwrapSingleStatement(index, out var completion))
                return false;

            if (completion.Kind is
                Avm1SourceStatementKind.Break or
                Avm1SourceStatementKind.Continue)
            {
                rewrite = new CompletionRewrite(completion.Kind, completion.Label);
            }
            else if (!TryGetCompletionWrite(
                    completion.Index,
                    out var outerSymbol,
                    out var outerToken) ||
                !TryResolveActiveCompletionRewrite(
                    outerSymbol,
                    outerToken,
                    out rewrite))
            {
                return false;
            }

            return ((token & 1) != 0) ==
                (rewrite.Kind is Avm1SourceStatementKind.Continue);
        }

        private bool TryResolveActiveCompletionRewrite(
            SourceSymbolIndex symbol,
            int token,
            out CompletionRewrite rewrite)
        {
            for (var i = _activeCompletionTransports.Count - 1; i >= 0; i--)
            {
                var transport = _activeCompletionTransports[i];
                if (transport.Symbol == symbol &&
                    transport.Rewrites.TryGetValue(token, out rewrite))
                {
                    return true;
                }
            }

            rewrite = default;
            return false;
        }

        private bool TryValidateCompletionWrites(
            SourceStatementIndex index,
            SourceSymbolIndex symbol,
            IReadOnlyDictionary<int, CompletionRewrite> rewrites,
            out int writeCount)
        {
            var count = 0;
            var isValid = Visit(index);
            writeCount = count;
            return isValid;

            bool Visit(SourceStatementIndex statementIndex)
            {
                if (!statementIndex.IsValid ||
                    statementIndex.Value >= _source.Statements.Count)
                {
                    return false;
                }

                var statement = _source[statementIndex];
                if (TryGetCompletionWrite(
                    statementIndex,
                    out var writeSymbol,
                    out var token) &&
                    writeSymbol == symbol)
                {
                    if (!rewrites.ContainsKey(token))
                        return false;
                    count++;
                    return true;
                }

                if (statement.Symbol == symbol ||
                    ContainsSymbolReference(statement.Expression, symbol) ||
                    ContainsSymbolReference(statement.SecondaryExpression, symbol))
                {
                    return false;
                }
                for (var i = 0; i < statement.Expressions.Count; i++)
                {
                    if (ContainsSymbolReference(
                            _source.GetExpression(statement, i),
                            symbol))
                    {
                        return false;
                    }
                }
                for (var i = 0; i < statement.Initializers.Count; i++)
                {
                    if (!Visit(_source.GetInitializer(statement, i)))
                        return false;
                }
                for (var i = 0; i < statement.Children.Count; i++)
                {
                    if (!Visit(_source.GetChild(statement, i)))
                        return false;
                }
                return true;
            }
        }

        private bool TryGetCompletionWrite(
            SourceStatementIndex index,
            out SourceSymbolIndex symbol,
            out int token)
        {
            symbol = SourceSymbolIndex.Invalid;
            token = 0;
            if (index.IsValid &&
                index.Value < _source.Statements.Count &&
                _source[index] is
                {
                    Kind: Avm1SourceStatementKind.VariableDeclaration,
                    Symbol: var declarationSymbol,
                    Expression.IsValid: true
                } declaration &&
                TryGetIntegerLiteral(declaration.Expression, out token) &&
                token > 0)
            {
                symbol = declarationSymbol;
                return symbol.IsValid;
            }

            return TryGetAssignmentStatement(index, out var target, out var value) &&
                TryGetSymbolReference(target, out symbol) &&
                TryGetIntegerLiteral(value, out token) &&
                token > 0;
        }

        private bool ContainsSymbolReference(
            SourceExpressionIndex index,
            SourceSymbolIndex symbol)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                expression.Symbol == symbol)
            {
                return true;
            }
            for (var i = 0; i < expression.Children.Count; i++)
            {
                if (ContainsSymbolReference(_source.GetChild(expression, i), symbol))
                    return true;
            }
            return false;
        }

        private int CountSymbolOccurrences(SourceSymbolIndex symbol) =>
            _source.Expressions.Count(expression =>
                expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                expression.Symbol == symbol) +
            _source.Statements.Count(statement => statement.Symbol == symbol);

        private bool IsCompletionTransportSymbol(SourceSymbolIndex index)
        {
            if (!IsGeneratedTransportSymbol(index))
                return false;
            var symbol = _source[index];
            if (!symbol.Name.IsValid || symbol.Name.Value >= _source.Strings.Count)
                return false;
            return IsCompletionTransportName(_source[symbol.Name]);
        }

        private static bool IsCompletionTransportName(string value)
        {
            const string prefix = "__avm1_completion";
            var name = value.AsSpan();
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var suffix = name[prefix.Length..];
            if (suffix.IsEmpty)
                return false;
            while (!suffix.IsEmpty)
            {
                if (suffix[0] != '_')
                    return false;
                suffix = suffix[1..];
                var digitCount = 0;
                while (digitCount < suffix.Length &&
                    char.IsAsciiDigit(suffix[digitCount]))
                {
                    digitCount++;
                }
                if (digitCount == 0)
                    return false;
                suffix = suffix[digitCount..];
            }
            return true;
        }

        private bool TryGetIntegerLiteral(
            SourceExpressionIndex index,
            out int value)
        {
            value = 0;
            if (!TryGetNumericLiteral(index, out var numeric) ||
                !double.IsFinite(numeric) ||
                numeric != Math.Truncate(numeric) ||
                numeric < int.MinValue ||
                numeric > int.MaxValue)
            {
                return false;
            }
            value = (int)numeric;
            return true;
        }

        private bool TryRewriteForInKeyTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 1 >= statements.Length ||
                !TryGetStatement(statements[start], $"{path}[{start}]", depth, out var declaration) ||
                declaration.Kind is not Avm1SourceStatementKind.VariableDeclaration ||
                declaration.Expression.IsValid ||
                !declaration.Symbol.IsValid ||
                IsGeneratedTransportSymbol(declaration.Symbol) ||
                !TryGetStatement(statements[start + 1], $"{path}[{start + 1}]", depth, out var loop) ||
                loop.Kind is not Avm1SourceStatementKind.ForIn ||
                !loop.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey) ||
                !TryGetSymbolReference(loop.Expression, out var transport) ||
                !IsGeneratedTransportSymbol(transport) ||
                !TryGetStatementList(
                    loop.Children,
                    $"{path}[{start + 1}].children",
                    out var body) ||
                body.Length == 0 ||
                !TryGetAssignmentStatement(body[0], out var target, out var value) ||
                !IsSymbolReference(target, declaration.Symbol) ||
                !IsSymbolReference(value, transport))
            {
                return false;
            }

            var transportUses = CountUses(transport, statements, start);
            if (transportUses is not { Declarations: 0, Reads: 2, Writes: 0 })
                return false;

            var key = _builder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: CloneSymbol(declaration.Symbol, $"{path}[{start}].symbol"));
            result = _builder.AddStatement(
                Avm1SourceStatementKind.ForIn,
                expression: key,
                secondaryExpression: CloneOptionalExpression(
                    loop.SecondaryExpression,
                    $"{path}[{start + 1}].secondaryExpression",
                    depth + 1,
                    substitutions: null),
                label: CloneOptionalLabel(
                    loop.Label,
                    $"{path}[{start + 1}].label"),
                flags: loop.Flags,
                children: NormalizeSequence(
                    body[1..],
                    $"{path}[{start + 1}].children",
                    depth + 1));
            consumed = 2;
            return true;
        }

        private bool TryInlineSeparatedStableTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (!TryGetGeneratedDeclaration(
                    statements[start],
                    out var symbol,
                    out var initializer) ||
                !initializer.IsValid ||
                !IsPureStable(initializer) ||
                CountUses(symbol, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 })
            {
                return false;
            }

            var consumerIndex = -1;
            for (var i = start + 1; i < statements.Length; i++)
            {
                var uses = new SymbolUses();
                CountStatementUses(statements[i], symbol, ref uses, depth: 0);
                if (uses is { Declarations: 0, Reads: 0, Writes: 0 })
                    continue;
                if (uses is not { Declarations: 0, Reads: 1, Writes: 0 } ||
                    !IsLinearConsumer(statements[i]) ||
                    !CanMoveTransportIntoConsumer(symbol, statements[i]))
                {
                    return false;
                }
                consumerIndex = i;
                break;
            }
            if (consumerIndex <= start + 1)
                return false;

            for (var i = start + 1; i <= consumerIndex; i++)
            {
                if (ContainsDirectEval(statements[i]))
                    return false;
            }

            if (TryGetSymbolReference(initializer, out var sourceSymbol))
            {
                if (IsCapturedSymbol(sourceSymbol))
                    return false;
                for (var i = start + 1; i <= consumerIndex; i++)
                {
                    var sourceUses = new SymbolUses();
                    CountStatementUses(
                        statements[i],
                        sourceSymbol,
                        ref sourceUses,
                        depth: 0);
                    if (sourceUses.Writes != 0)
                        return false;
                }
            }

            var rewritten = new SourceStatementIndex[consumerIndex - start];
            for (var i = start + 1; i < consumerIndex; i++)
            {
                rewritten[i - start - 1] = CloneStatement(
                    statements[i],
                    $"{path}[{i}]",
                    depth);
            }
            rewritten[^1] = CloneStatement(
                statements[consumerIndex],
                $"{path}[{consumerIndex}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [symbol] = CloneExpression(
                        initializer,
                        $"{path}[{start}].expression",
                        depth + 1)
                });
            result = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: rewritten);
            consumed = consumerIndex - start + 1;
            return true;
        }

        private bool TryInlineForInitializerTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 1 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var symbol,
                    out var initializer) ||
                !initializer.IsValid ||
                !TryGetStatement(
                    statements[start + 1],
                    $"{path}[{start + 1}]",
                    depth,
                    out var loop) ||
                loop.Kind is not Avm1SourceStatementKind.For ||
                !TryGetStatementList(
                    loop.Initializers,
                    $"{path}[{start + 1}].initializers",
                    out var loopInitializers) ||
                loopInitializers.Length == 0 ||
                !CanMoveTransportIntoConsumer(symbol, loopInitializers[0]) ||
                CountUses(symbol, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 } ||
                ContainsDirectEval(statements[start]) ||
                ContainsDirectEval(loopInitializers[0]))
            {
                return false;
            }

            var replacement = CloneExpression(
                initializer,
                $"{path}[{start}].expression",
                depth + 1);
            var substitutions = new Dictionary<
                SourceSymbolIndex,
                SourceExpressionIndex>
            {
                [symbol] = replacement
            };
            var clonedInitializers = new SourceStatementIndex[
                loopInitializers.Length];
            for (var i = 0; i < loopInitializers.Length; i++)
            {
                clonedInitializers[i] = CloneStatement(
                    loopInitializers[i],
                    $"{path}[{start + 1}].initializers[{i}]",
                    depth + 1,
                    substitutions);
            }

            result = _builder.AddStatement(
                Avm1SourceStatementKind.For,
                expression: CloneOptionalExpression(
                    loop.Expression,
                    $"{path}[{start + 1}].expression",
                    depth + 1,
                    substitutions),
                secondaryExpression: CloneOptionalExpression(
                    loop.SecondaryExpression,
                    $"{path}[{start + 1}].secondaryExpression",
                    depth + 1,
                    substitutions),
                symbol: loop.Symbol.IsValid
                    ? CloneSymbol(
                        loop.Symbol,
                        $"{path}[{start + 1}].symbol")
                    : SourceSymbolIndex.Invalid,
                name: CloneOptionalName(
                    loop.Name,
                    $"{path}[{start + 1}].name"),
                label: CloneOptionalLabel(
                    loop.Label,
                    $"{path}[{start + 1}].label"),
                flags: loop.Flags,
                children: CloneStatementChildren(
                    loop,
                    $"{path}[{start + 1}]",
                    depth + 1),
                initializers: clonedInitializers,
                expressions: CloneExpressionList(
                    loop.Expressions,
                    $"{path}[{start + 1}].expressions",
                    depth + 1,
                    substitutions));
            consumed = 2;
            return true;
        }

        private bool TryCollapseTransportAliases(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var declarationCount = 0;
            while (start + declarationCount < statements.Length &&
                TryGetGeneratedDeclaration(
                    statements[start + declarationCount],
                    out _,
                    out var initializer) &&
                initializer.IsValid)
            {
                declarationCount++;
            }
            var consumerIndex = start + declarationCount;
            if (declarationCount < 2 ||
                consumerIndex >= statements.Length ||
                !IsLinearConsumer(statements[consumerIndex]))
            {
                return false;
            }

            var declared = new HashSet<SourceSymbolIndex>();
            var aliasCount = 0;
            for (var i = 0; i < declarationCount; i++)
            {
                TryGetGeneratedDeclaration(
                    statements[start + i],
                    out var symbol,
                    out var initializer);
                if (TryGetSymbolReference(initializer, out var source) &&
                    declared.Contains(source) &&
                    IsAliasConsumedByRewrite(symbol, source))
                {
                    aliasCount++;
                }
                declared.Add(symbol);
            }
            if (aliasCount == 0)
                return false;

            declared.Clear();
            var substitutions = new Dictionary<SourceSymbolIndex, SourceExpressionIndex>();
            var rewritten = new List<SourceStatementIndex>(declarationCount - aliasCount + 1);
            for (var i = 0; i < declarationCount; i++)
            {
                TryGetGeneratedDeclaration(
                    statements[start + i],
                    out var symbol,
                    out var initializer);
                if (TryGetSymbolReference(initializer, out var source) &&
                    declared.Contains(source) &&
                    IsAliasConsumedByRewrite(symbol, source))
                {
                    if (!substitutions.TryGetValue(source, out var replacement))
                    {
                        replacement = _builder.AddExpression(
                            Avm1SourceExpressionKind.SymbolReference,
                            symbol: CloneSymbol(
                                source,
                                $"{path}[{start + i}].expression.symbol"));
                    }
                    substitutions.Add(symbol, replacement);
                }
                else
                {
                    rewritten.Add(CloneStatement(
                        statements[start + i],
                        $"{path}[{start + i}]",
                        depth,
                        substitutions));
                }
                declared.Add(symbol);
            }
            rewritten.Add(CloneStatement(
                statements[consumerIndex],
                $"{path}[{consumerIndex}]",
                depth,
                substitutions));
            result = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: rewritten);
            consumed = declarationCount + 1;
            return true;

            bool IsAliasConsumedByRewrite(
                SourceSymbolIndex symbol,
                SourceSymbolIndex source)
            {
                if (CountUses(symbol, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 })
                {
                    return false;
                }

                var covered = new SymbolUses();
                for (var index = start; index <= consumerIndex; index++)
                {
                    CountStatementUses(
                        statements[index],
                        symbol,
                        ref covered,
                        depth: 0);
                }
                if (covered is not
                    { Declarations: 1, Reads: 1, Writes: 0 })
                {
                    return false;
                }

                var sourceUses = new SymbolUses();
                for (var index = start; index <= consumerIndex; index++)
                {
                    CountStatementUses(
                        statements[index],
                        source,
                        ref sourceUses,
                        depth: 0);
                }
                return sourceUses.Writes == 0;
            }
        }

        private bool TryRewriteAssignmentResult(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 < statements.Length &&
                TryGetGeneratedDeclaration(
                    statements[start],
                    out var symbol,
                    out var initializer) &&
                initializer.IsValid &&
                TryGetAssignmentStatement(
                    statements[start + 1],
                    out var target,
                    out var value) &&
                IsSymbolReference(value, symbol) &&
                TryGetReturnExpression(statements[start + 2], out var returned) &&
                IsSymbolReference(returned, symbol) &&
                IsStableAssignmentTarget(target))
            {
                var uses = CountUses(symbol, statements, start);
                if (uses is { Declarations: 1, Reads: 2, Writes: 0 })
                {
                    result = AddAssignmentReturn(
                        target,
                        initializer,
                        $"{path}[{start}]",
                        depth);
                    consumed = 3;
                    return true;
                }
            }

            if (start + 2 < statements.Length &&
                TryGetGeneratedDeclaration(
                    statements[start],
                    out var propagatedSymbol,
                    out var propagatedInitializer) &&
                propagatedInitializer.IsValid &&
                IsPureStable(propagatedInitializer) &&
                TryGetAssignmentStatement(
                    statements[start + 1],
                    out var propagatedTarget,
                    out var propagatedValue) &&
                IsStableAssignmentTarget(propagatedTarget) &&
                ExpressionsEqual(
                    propagatedInitializer,
                    propagatedValue,
                    depth + 1) &&
                TryGetReturnExpression(
                    statements[start + 2],
                    out var propagatedReturn) &&
                IsSymbolReference(propagatedReturn, propagatedSymbol) &&
                CountUses(propagatedSymbol, statements, start) is
                { Declarations: 1, Reads: 1, Writes: 0 })
            {
                result = AddAssignmentReturn(
                    propagatedTarget,
                    propagatedInitializer,
                    $"{path}[{start}]",
                    depth);
                consumed = 3;
                return true;
            }

            if (start + 1 >= statements.Length ||
                !TryGetAssignmentStatement(
                    statements[start],
                    out var directTarget,
                    out var directValue) ||
                !TryGetReturnExpression(statements[start + 1], out var directReturn) ||
                !IsAssignmentTarget(directTarget) ||
                !IsPureStable(directValue) ||
                !ExpressionsEqual(directValue, directReturn, depth + 1))
            {
                return false;
            }

            result = AddAssignmentReturn(
                directTarget,
                directValue,
                $"{path}[{start}]",
                depth);
            consumed = 2;
            return true;
        }

        private bool TryRewriteReturnTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 1 >= statements.Length ||
                !TryGetInlineableDeclaration(
                    statements[start],
                    out var symbol,
                    out var initializer) ||
                !initializer.IsValid ||
                IsCapturedSymbol(symbol) ||
                ContainsDirectEval(initializer, depth: 0) ||
                !TryGetReturnExpression(statements[start + 1], out var returned) ||
                !IsSymbolReference(returned, symbol) ||
                CountUses(symbol, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 })
            {
                return false;
            }

            result = _builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: CloneExpression(
                    initializer,
                    path + $"[{start}].expression",
                    depth + 1));
            consumed = 2;
            return true;
        }

        private bool TryRewriteAssignmentChainTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var transport,
                    out var initializer) ||
                !initializer.IsValid ||
                !TryGetAssignmentStatement(
                    statements[start + 1],
                    out var innerTarget,
                    out var innerValue) ||
                !TryGetAssignmentStatement(
                    statements[start + 2],
                    out var outerTarget,
                    out var outerValue) ||
                !IsSymbolReference(innerValue, transport) ||
                !IsSymbolReference(outerValue, transport) ||
                !IsStableAssignmentTarget(innerTarget) ||
                !IsStableAssignmentTarget(outerTarget) ||
                CountUses(transport, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 0 } ||
                ContainsDirectEval(statements[start]) ||
                ContainsDirectEval(statements[start + 1]) ||
                ContainsDirectEval(statements[start + 2]))
            {
                return false;
            }

            var innerAssignment = _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children:
                [
                    CloneExpression(
                        innerTarget,
                        $"{path}[{start + 1}].target",
                        depth + 1),
                    CloneExpression(
                        initializer,
                        $"{path}[{start}].expression",
                        depth + 1)
                ]);
            var outerAssignment = _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children:
                [
                    CloneExpression(
                        outerTarget,
                        $"{path}[{start + 2}].target",
                        depth + 1),
                    innerAssignment
                ]);
            result = _builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: outerAssignment);
            consumed = 3;
            return true;
        }

        private bool TryRewritePrefixTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var transport,
                    out var steppedValue) ||
                !steppedValue.IsValid ||
                !TryGetExpression(
                    steppedValue,
                    $"{path}[{start}].expression",
                    depth + 1,
                    out var step) ||
                step.Kind is not Avm1SourceExpressionKind.Binary ||
                step.Operator is not (
                    Avm1SourceOperator.Add or
                    Avm1SourceOperator.Subtract) ||
                step.Children.Count != 2 ||
                !TryGetNumericLiteral(_source.GetChild(step, 1), out var amount) ||
                amount != 1 ||
                !statements[start + 1].IsValid ||
                statements[start + 1].Value >= _source.Statements.Count)
            {
                return false;
            }

            var updateStatement = _source[statements[start + 1]];
            if (updateStatement.Kind is not Avm1SourceStatementKind.Expression ||
                !updateStatement.Expression.IsValid ||
                updateStatement.Expression.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var update = _source[updateStatement.Expression];
            var expectedUpdate = step.Operator is Avm1SourceOperator.Add
                ? Avm1SourceOperator.AddAssign
                : Avm1SourceOperator.SubtractAssign;
            if (update.Kind is not Avm1SourceExpressionKind.Assignment ||
                update.Operator != expectedUpdate ||
                update.Children.Count != 2 ||
                !TryGetNumericLiteral(_source.GetChild(update, 1), out amount) ||
                amount != 1 ||
                !ExpressionsEqual(
                    _source.GetChild(step, 0),
                    _source.GetChild(update, 0),
                    depth + 1) ||
                CountUses(transport, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 } ||
                ContainsDirectEval(statements[start]) ||
                ContainsDirectEval(statements[start + 1]) ||
                ContainsDirectEval(statements[start + 2]))
            {
                return false;
            }

            var canMoveIntoConsumer = CanMoveTransportIntoConsumer(
                transport,
                statements[start + 2]);
            if (!canMoveIntoConsumer &&
                TryGetAssignmentStatement(
                    statements[start + 2],
                    out var outerTarget,
                    out var outerValue))
            {
                canMoveIntoConsumer = IsSymbolReference(outerValue, transport) &&
                    IsStableAssignmentTarget(outerTarget);
            }
            if (!canMoveIntoConsumer)
                return false;

            var prefix = _builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                step.Operator is Avm1SourceOperator.Add
                    ? Avm1SourceOperator.PrefixIncrement
                    : Avm1SourceOperator.PrefixDecrement,
                children:
                [
                    CloneExpression(
                        _source.GetChild(update, 0),
                        $"{path}[{start + 1}].target",
                        depth + 1)
                ]);
            result = CloneStatement(
                statements[start + 2],
                $"{path}[{start + 2}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [transport] = prefix
                });
            consumed = 3;
            return true;
        }

        private bool TryRewritePostfixTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var transport,
                    out var converted) ||
                !converted.IsValid ||
                !TryGetExpression(
                    converted,
                    $"{path}[{start}].expression",
                    depth + 1,
                    out var conversion) ||
                conversion.Kind is not Avm1SourceExpressionKind.IntrinsicCall ||
                conversion.Children.Count != 1 ||
                !conversion.Name.IsValid ||
                conversion.Name.Value >= _source.Strings.Count ||
                !string.Equals(
                    _source[conversion.Name],
                    "Number",
                    StringComparison.Ordinal) ||
                !TryGetAssignmentStatement(
                    statements[start + 1],
                    out var assignmentTarget,
                    out var assignmentValue) ||
                !TryGetExpression(
                    assignmentValue,
                    $"{path}[{start + 1}].value",
                    depth + 1,
                    out var step) ||
                step.Kind is not Avm1SourceExpressionKind.Binary ||
                step.Operator is not (
                    Avm1SourceOperator.Add or
                    Avm1SourceOperator.Subtract) ||
                step.Children.Count != 2 ||
                !IsSymbolReference(_source.GetChild(step, 0), transport) ||
                !TryGetNumericLiteral(_source.GetChild(step, 1), out var amount) ||
                amount != 1 ||
                !ExpressionsEqual(
                    assignmentTarget,
                    _source.GetChild(conversion, 0),
                    depth + 1) ||
                CountUses(transport, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 0 })
            {
                return false;
            }

            var consumerUses = new SymbolUses();
            CountStatementUses(
                statements[start + 2],
                transport,
                ref consumerUses,
                depth: 0);
            if (consumerUses is not { Declarations: 0, Reads: 1, Writes: 0 } ||
                !CanMoveTransportIntoConsumer(
                    transport,
                    statements[start + 2]))
            {
                return false;
            }

            var update = _builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                step.Operator is Avm1SourceOperator.Add
                    ? Avm1SourceOperator.PostfixIncrement
                    : Avm1SourceOperator.PostfixDecrement,
                children:
                [
                    CloneExpression(
                        assignmentTarget,
                        $"{path}[{start + 1}].target",
                        depth + 1)
                ]);
            result = CloneStatement(
                statements[start + 2],
                $"{path}[{start + 2}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [transport] = update
                });
            consumed = 3;
            return true;
        }

        private bool TryRewriteReversedObjectTransports(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var symbols = new List<SourceSymbolIndex>();
            var initializers = new List<SourceExpressionIndex>();
            var consumerIndex = start;
            while (consumerIndex < statements.Length &&
                TryGetGeneratedDeclaration(
                    statements[consumerIndex],
                    out var symbol,
                    out var initializer) &&
                initializer.IsValid)
            {
                symbols.Add(symbol);
                initializers.Add(initializer);
                consumerIndex++;
            }
            if (symbols.Count < 2 ||
                consumerIndex >= statements.Length ||
                !TryGetStatement(
                    statements[consumerIndex],
                    $"{path}[{consumerIndex}]",
                    depth,
                    out var consumer) ||
                !IsLinearConsumer(statements[consumerIndex]) ||
                !consumer.Expression.IsValid ||
                !TryGetExpression(
                    consumer.Expression,
                    $"{path}[{consumerIndex}].expression",
                    depth + 1,
                    out var literal) ||
                literal.Kind is not Avm1SourceExpressionKind.ObjectLiteral ||
                literal.Children.Count != symbols.Count * 2)
            {
                return false;
            }

            var symbolSet = symbols.ToHashSet();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (CountUses(symbols[i], statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 } ||
                    ContainsAnySymbol(initializers[i], symbolSet))
                {
                    return false;
                }

                var pair = symbols.Count - 1 - i;
                var key = _source.GetChild(literal, pair * 2);
                var value = _source.GetChild(literal, pair * 2 + 1);
                if (!TryGetStaticObjectKey(key, out var keyText) ||
                    !keys.Add(keyText) ||
                    !IsSymbolReference(value, symbols[i]))
                {
                    return false;
                }
            }

            var substitutions = new Dictionary<SourceSymbolIndex, SourceExpressionIndex>();
            for (var i = 0; i < symbols.Count; i++)
            {
                substitutions.Add(
                    symbols[i],
                    CloneExpression(
                        initializers[i],
                        $"{path}[{start + i}].expression",
                        depth + 1));
            }

            var children = new SourceExpressionIndex[literal.Children.Count];
            for (var sourcePair = symbols.Count - 1; sourcePair >= 0; sourcePair--)
            {
                var targetPair = symbols.Count - 1 - sourcePair;
                children[targetPair * 2] = CloneExpression(
                    _source.GetChild(literal, sourcePair * 2),
                    $"{path}[{consumerIndex}].expression.children[{sourcePair * 2}]",
                    depth + 1,
                    substitutions);
                children[targetPair * 2 + 1] = CloneExpression(
                    _source.GetChild(literal, sourcePair * 2 + 1),
                    $"{path}[{consumerIndex}].expression.children[{sourcePair * 2 + 1}]",
                    depth + 1,
                    substitutions);
            }
            var normalizedLiteral = _builder.AddExpression(
                Avm1SourceExpressionKind.ObjectLiteral,
                children: children);
            result = _builder.AddStatement(
                consumer.Kind,
                expression: normalizedLiteral,
                secondaryExpression: CloneOptionalExpression(
                    consumer.SecondaryExpression,
                    $"{path}[{consumerIndex}].secondaryExpression",
                    depth + 1,
                    substitutions: null),
                symbol: consumer.Symbol.IsValid
                    ? CloneSymbol(
                        consumer.Symbol,
                        $"{path}[{consumerIndex}].symbol")
                    : SourceSymbolIndex.Invalid,
                name: CloneOptionalName(
                    consumer.Name,
                    $"{path}[{consumerIndex}].name"),
                label: CloneOptionalLabel(
                    consumer.Label,
                    $"{path}[{consumerIndex}].label"),
                flags: consumer.Flags,
                children: CloneStatementChildren(
                    consumer,
                    $"{path}[{consumerIndex}]",
                    depth + 1),
                initializers: CloneStatementList(
                    consumer.Initializers,
                    $"{path}[{consumerIndex}].initializers",
                    depth + 1,
                    normalizeSequence: true),
                expressions: CloneExpressionList(
                    consumer.Expressions,
                    $"{path}[{consumerIndex}].expressions",
                    depth + 1,
                    substitutions: null));
            consumed = consumerIndex - start + 1;
            return true;
        }

        private bool TryRewritePureAliasTransports(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var aliases = new List<(SourceSymbolIndex Target, SourceSymbolIndex Source)>();
            var consumerIndex = start;
            while (consumerIndex < statements.Length &&
                TryGetGeneratedDeclaration(
                    statements[consumerIndex],
                    out var target,
                    out var initializer) &&
                TryGetSymbolReference(initializer, out var source))
            {
                aliases.Add((target, source));
                consumerIndex++;
            }
            if (aliases.Count == 0 ||
                consumerIndex >= statements.Length ||
                !TryGetStatement(
                    statements[consumerIndex],
                    $"{path}[{consumerIndex}]",
                    depth,
                    out var consumer) ||
                consumer.Kind is not Avm1SourceStatementKind.Return)
            {
                return false;
            }

            var targets = aliases.Select(alias => alias.Target).ToHashSet();
            if (aliases.Any(alias =>
                    targets.Contains(alias.Source) ||
                    CountUses(alias.Target, statements, start) is not
                        { Declarations: 1, Reads: 1, Writes: 0 }))
            {
                return false;
            }

            var substitutions = new Dictionary<
                SourceSymbolIndex,
                SourceExpressionIndex>();
            for (var i = 0; i < aliases.Count; i++)
            {
                substitutions.Add(
                    aliases[i].Target,
                    _builder.AddExpression(
                        Avm1SourceExpressionKind.SymbolReference,
                        symbol: CloneSymbol(
                            aliases[i].Source,
                            $"{path}[{start + i}].expression.symbol")));
            }
            result = CloneStatement(
                statements[consumerIndex],
                $"{path}[{consumerIndex}]",
                depth,
                substitutions);
            consumed = aliases.Count + 1;
            return true;
        }

        private bool TryInlineObjectLiteralValueTransports(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var symbols = new List<SourceSymbolIndex>();
            var initializers = new List<SourceExpressionIndex>();
            var consumerIndex = start;
            Avm1SourceStatement consumer = default;
            Avm1SourceExpression literal = default;
            while (consumerIndex < statements.Length)
            {
                if (TryGetStatement(
                        statements[consumerIndex],
                        $"{path}[{consumerIndex}]",
                        depth,
                        out consumer) &&
                    IsLinearConsumer(statements[consumerIndex]) &&
                    consumer.Expression.IsValid &&
                    consumer.Expression.Value < _source.Expressions.Count &&
                    TryGetObjectLiteralConsumer(
                        consumer.Expression,
                        out literal))
                {
                    break;
                }

                if (!TryGetGeneratedDeclaration(
                        statements[consumerIndex],
                        out var symbol,
                        out var initializer) ||
                    !initializer.IsValid)
                {
                    return false;
                }
                symbols.Add(symbol);
                initializers.Add(initializer);
                consumerIndex++;
            }

            if (symbols.Count == 0 || consumerIndex >= statements.Length)
                return false;

            var symbolSet = symbols.ToHashSet();
            for (var i = 0; i < symbols.Count; i++)
            {
                if (CountUses(symbols[i], statements, start) is not
                        { Declarations: 1, Reads: 1, Writes: 0 } ||
                    ContainsAnySymbol(initializers[i], symbolSet) ||
                    ContainsDirectEval(initializers[i], depth: 0))
                {
                    return false;
                }
            }
            if (ContainsDirectEval(statements[consumerIndex]))
                return false;

            var found = new HashSet<SourceSymbolIndex>();
            if (!TryCollectDirectObjectLiteralValueTransports(
                    literal.Index,
                    symbolSet,
                    found,
                    depth: 0) ||
                !found.SetEquals(symbolSet))
            {
                return false;
            }

            var substitutions = new Dictionary<
                SourceSymbolIndex,
                SourceExpressionIndex>();
            for (var i = 0; i < symbols.Count; i++)
            {
                substitutions.Add(
                    symbols[i],
                    CloneExpression(
                        initializers[i],
                        $"{path}[{start + i}].expression",
                        depth + 1));
            }
            result = CloneStatement(
                statements[consumerIndex],
                $"{path}[{consumerIndex}]",
                depth,
                substitutions);
            consumed = consumerIndex - start + 1;
            return true;
        }

        private bool TryGetObjectLiteralConsumer(
            SourceExpressionIndex index,
            out Avm1SourceExpression literal)
        {
            literal = default;
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;

            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.ObjectLiteral)
            {
                literal = expression;
                return true;
            }
            if (expression.Kind is not Avm1SourceExpressionKind.Assignment ||
                expression.Operator is not Avm1SourceOperator.Assign ||
                expression.Children.Count != 2 ||
                !IsStableTransportAssignmentTarget(
                    _source.GetChild(expression, 0)))
            {
                return false;
            }

            var value = _source.GetChild(expression, 1);
            if (!value.IsValid || value.Value >= _source.Expressions.Count)
                return false;

            literal = _source[value];
            return literal.Kind is Avm1SourceExpressionKind.ObjectLiteral;
        }

        private bool TryCollectDirectObjectLiteralValueTransports(
            SourceExpressionIndex index,
            HashSet<SourceSymbolIndex> symbols,
            HashSet<SourceSymbolIndex> found,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var literal = _source[index];
            if (literal.Kind is not Avm1SourceExpressionKind.ObjectLiteral ||
                literal.Children.Count % 2 != 0)
            {
                return false;
            }

            for (var i = 0; i < literal.Children.Count; i += 2)
            {
                var key = _source.GetChild(literal, i);
                var value = _source.GetChild(literal, i + 1);
                if (ContainsAnySymbol(key, symbols))
                    return false;
                if (TryGetSymbolReference(value, out var symbol) &&
                    symbols.Contains(symbol))
                {
                    if (!found.Add(symbol))
                        return false;
                    continue;
                }

                if (value.IsValid &&
                    value.Value < _source.Expressions.Count &&
                    _source[value].Kind is Avm1SourceExpressionKind.ObjectLiteral)
                {
                    if (!TryCollectDirectObjectLiteralValueTransports(
                            value,
                            symbols,
                            found,
                            depth + 1))
                    {
                        return false;
                    }
                    continue;
                }

                if (ContainsAnySymbol(value, symbols))
                    return false;
            }
            return true;
        }

        private bool TryRewriteReverseEvaluationTransports(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var symbols = new List<SourceSymbolIndex>();
            var initializers = new List<SourceExpressionIndex>();
            var consumerIndex = start;
            while (consumerIndex < statements.Length &&
                TryGetGeneratedDeclaration(
                    statements[consumerIndex],
                    out var symbol,
                    out var initializer) &&
                initializer.IsValid)
            {
                symbols.Add(symbol);
                initializers.Add(initializer);
                consumerIndex++;
            }
            if (symbols.Count < 2 ||
                consumerIndex >= statements.Length ||
                !IsLinearConsumer(statements[consumerIndex]) ||
                !TryGetStatement(
                    statements[consumerIndex],
                    $"{path}[{consumerIndex}]",
                    depth,
                    out var consumer) ||
                !consumer.Expression.IsValid ||
                !TryGetExpression(
                    consumer.Expression,
                    $"{path}[{consumerIndex}].expression",
                    depth + 1,
                    out var expression))
            {
                return false;
            }

            var operandOffset = expression.Kind switch
            {
                Avm1SourceExpressionKind.Call or
                Avm1SourceExpressionKind.New => 1,
                Avm1SourceExpressionKind.ArrayLiteral => 0,
                _ => -1
            };
            if (operandOffset < 0 ||
                expression.Children.Count != symbols.Count + operandOffset)
            {
                return false;
            }

            var symbolSet = symbols.ToHashSet();
            for (var i = 0; i < symbols.Count; i++)
            {
                if (!IsSymbolReference(
                        _source.GetChild(expression, operandOffset + i),
                        symbols[symbols.Count - 1 - i]) ||
                    CountUses(symbols[i], statements, start) is not
                        { Declarations: 1, Reads: 1, Writes: 0 } ||
                    ContainsAnySymbol(initializers[i], symbolSet))
                {
                    return false;
                }
            }

            var substitutions = new Dictionary<
                SourceSymbolIndex,
                SourceExpressionIndex>();
            var firstInline = expression.Kind is
                Avm1SourceExpressionKind.ArrayLiteral
                    ? symbols.Count - 1
                    : 0;
            for (var i = firstInline; i < symbols.Count; i++)
            {
                substitutions.Add(
                    symbols[i],
                    CloneExpression(
                        initializers[i],
                        $"{path}[{start + i}].expression",
                        depth + 1));
            }
            if (firstInline == 0)
            {
                result = CloneStatement(
                    statements[consumerIndex],
                    $"{path}[{consumerIndex}]",
                    depth,
                    substitutions);
            }
            else
            {
                var rewritten = new SourceStatementIndex[firstInline + 1];
                for (var i = 0; i < firstInline; i++)
                {
                    rewritten[i] = CloneStatement(
                        statements[start + i],
                        $"{path}[{start + i}]",
                        depth);
                }
                rewritten[^1] = CloneStatement(
                    statements[consumerIndex],
                    $"{path}[{consumerIndex}]",
                    depth,
                    substitutions);
                result = _builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: rewritten);
            }
            consumed = symbols.Count + 1;
            return true;
        }

        private bool TryGetStaticObjectKey(
            SourceExpressionIndex index,
            out string key)
        {
            key = string.Empty;
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid ||
                expression.Literal.Value >= _source.Literals.Count)
            {
                return false;
            }

            var literal = _source[expression.Literal];
            if (literal.Kind is not Avm1SourceLiteralKind.String ||
                !literal.StringValue.IsValid ||
                literal.StringValue.Value >= _source.Strings.Count)
            {
                return false;
            }

            key = _source[literal.StringValue];
            return true;
        }

        private SourceStatementIndex AddAssignmentReturn(
            SourceExpressionIndex target,
            SourceExpressionIndex value,
            string path,
            int depth)
        {
            var assignment = _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children:
                [
                    CloneExpression(target, path + ".target", depth + 1),
                    CloneExpression(value, path + ".value", depth + 1)
                ]);
            return _builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: assignment);
        }

        private bool TryRewritePhiTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var symbol,
                    out var initializer) ||
                !TryGetStatement(
                    statements[start + 1],
                    $"{path}[{start + 1}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                !IsLinearConsumer(statements[start + 2]) ||
                !CanMoveTransportIntoConsumer(symbol, statements[start + 2]))
            {
                return false;
            }

            SourceExpressionIndex normalized;
            if (initializer.IsValid &&
                TryGetLogicalTransport(conditional, symbol, out var @operator, out var right))
            {
                var uses = CountTransportPrefixUses(
                    symbol,
                    statements,
                    start,
                    consumerOffset: 2);
                if (uses is not { Declarations: 1, Reads: 2, Writes: 1 })
                    return false;

                normalized = _builder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    @operator,
                    children:
                    [
                        CloneExpression(initializer, path + ".left", depth + 1),
                        CloneExpression(right, path + ".right", depth + 1)
                    ]);
            }
            else if (!initializer.IsValid &&
                TryGetConditionalTransport(
                    conditional,
                    symbol,
                    out var condition,
                    out var whenTrue,
                    out var whenFalse))
            {
                var uses = CountTransportPrefixUses(
                    symbol,
                    statements,
                    start,
                    consumerOffset: 2);
                if (uses is not { Declarations: 1, Reads: 1, Writes: 2 })
                    return false;

                normalized = _builder.AddExpression(
                    Avm1SourceExpressionKind.Conditional,
                    children:
                    [
                        CloneExpression(condition, path + ".condition", depth + 1),
                        CloneExpression(whenTrue, path + ".whenTrue", depth + 1),
                        CloneExpression(whenFalse, path + ".whenFalse", depth + 1)
                    ]);
            }
            else
            {
                return false;
            }

            var substitutions = new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
            {
                [symbol] = normalized
            };
            result = CloneStatement(
                statements[start + 2],
                $"{path}[{start + 2}]",
                depth,
                substitutions);
            consumed = 3;
            return true;
        }

        private bool TryRewriteLogicalAliasChain(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 3 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var conditionSymbol,
                    out var left) ||
                !left.IsValid ||
                !TryGetGeneratedDeclaration(
                    statements[start + 1],
                    out var resultSymbol,
                    out var alias) ||
                resultSymbol == conditionSymbol ||
                !IsSymbolReference(alias, conditionSymbol))
            {
                return false;
            }

            var updates = new List<(
                Avm1SourceOperator Operator,
                SourceExpressionIndex Right)>();
            var cursor = start + 2;
            while (cursor < statements.Length - 1 &&
                TryGetStatement(
                    statements[cursor],
                    $"{path}[{cursor}]",
                    depth,
                    out var conditional) &&
                conditional.Kind is Avm1SourceStatementKind.If &&
                conditional.Children.Count == 1 &&
                TryGetSingleAssignment(
                    _source.GetChild(conditional, 0),
                    resultSymbol,
                    out var right))
            {
                var testedSymbol = updates.Count == 0
                    ? conditionSymbol
                    : resultSymbol;
                Avm1SourceOperator @operator;
                if (IsSymbolReference(conditional.Expression, testedSymbol))
                {
                    @operator = Avm1SourceOperator.LogicalAnd;
                }
                else if (IsLogicalNotOf(
                    conditional.Expression,
                    testedSymbol))
                {
                    @operator = Avm1SourceOperator.LogicalOr;
                }
                else
                {
                    break;
                }

                updates.Add((@operator, right));
                cursor++;
            }

            if (updates.Count < 2 ||
                cursor >= statements.Length ||
                !IsLinearConsumer(statements[cursor]) ||
                !CanMoveTransportIntoConsumer(
                    resultSymbol,
                    statements[cursor]) ||
                CountUses(conditionSymbol, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 0 })
            {
                return false;
            }

            var resultUses = CountUses(
                resultSymbol,
                statements,
                start + 1,
                cursor - start);
            if (resultUses.Declarations != 1 ||
                resultUses.Reads != updates.Count ||
                resultUses.Writes != updates.Count ||
                CountUses(resultSymbol, statements, cursor + 1) is not
                    { Declarations: 0, Reads: 0, Writes: 0 })
            {
                return false;
            }

            var normalized = CloneExpression(
                left,
                $"{path}[{start}].left",
                depth + 1);
            for (var i = 0; i < updates.Count; i++)
            {
                normalized = _builder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    updates[i].Operator,
                    children:
                    [
                        normalized,
                        CloneExpression(
                            updates[i].Right,
                            $"{path}[{start + 2 + i}].right",
                            depth + 1)
                    ]);
            }

            result = CloneStatement(
                statements[cursor],
                $"{path}[{cursor}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [resultSymbol] = normalized
                });
            consumed = cursor - start + 1;
            return true;
        }

        private bool TryRewriteLogicalAliasTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 3 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var conditionSymbol,
                    out var left) ||
                !left.IsValid ||
                !TryGetGeneratedDeclaration(
                    statements[start + 1],
                    out var resultSymbol,
                    out var alias) ||
                !IsSymbolReference(alias, conditionSymbol) ||
                !TryGetStatement(
                    statements[start + 2],
                    $"{path}[{start + 2}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                conditional.Children.Count != 1 ||
                !TryGetSingleAssignment(
                    _source.GetChild(conditional, 0),
                    resultSymbol,
                    out var right) ||
                !CanMoveTransportIntoConsumer(
                    resultSymbol,
                    statements[start + 3]) ||
                CountUses(conditionSymbol, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 0 } ||
                CountUses(resultSymbol, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 1 })
            {
                return false;
            }

            Avm1SourceOperator @operator;
            if (IsSymbolReference(conditional.Expression, conditionSymbol))
            {
                @operator = Avm1SourceOperator.LogicalAnd;
            }
            else if (IsLogicalNotOf(conditional.Expression, conditionSymbol))
            {
                @operator = Avm1SourceOperator.LogicalOr;
            }
            else
            {
                return false;
            }

            var normalized = _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                @operator,
                children:
                [
                    CloneExpression(left, path + ".left", depth + 1),
                    CloneExpression(right, path + ".right", depth + 1)
                ]);
            var substitutions = new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
            {
                [resultSymbol] = normalized
            };
            result = CloneStatement(
                statements[start + 3],
                $"{path}[{start + 3}]",
                depth,
                substitutions);
            consumed = 4;
            return true;
        }

        private bool TryRewriteSplitLogicalTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 3 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var resultSymbol,
                    out var left) ||
                !left.IsValid ||
                !TryGetGeneratedDeclaration(
                    statements[start + 1],
                    out var conditionSymbol,
                    out var alias) ||
                !IsSymbolReference(alias, resultSymbol) ||
                !TryGetStatement(
                    statements[start + 2],
                    $"{path}[{start + 2}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                conditional.Children.Count != 1 ||
                !TryGetSingleAssignment(
                    _source.GetChild(conditional, 0),
                    resultSymbol,
                    out var right) ||
                !CanMoveTransportIntoConsumer(
                    resultSymbol,
                    statements[start + 3]) ||
                CountUses(conditionSymbol, statements, start) is not
                    { Declarations: 1, Reads: 1, Writes: 0 } ||
                CountUses(resultSymbol, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 1 })
            {
                return false;
            }

            Avm1SourceOperator @operator;
            if (IsSymbolReference(conditional.Expression, conditionSymbol))
            {
                @operator = Avm1SourceOperator.LogicalAnd;
            }
            else if (IsLogicalNotOf(conditional.Expression, conditionSymbol))
            {
                @operator = Avm1SourceOperator.LogicalOr;
            }
            else
            {
                return false;
            }

            var normalized = _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                @operator,
                children:
                [
                    CloneExpression(left, path + ".left", depth + 1),
                    CloneExpression(right, path + ".right", depth + 1)
                ]);
            result = CloneStatement(
                statements[start + 3],
                $"{path}[{start + 3}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [resultSymbol] = normalized
                });
            consumed = 4;
            return true;
        }

        private bool TryRewriteGuardedLogicalTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 1 >= statements.Length ||
                !TryGetStatement(
                    statements[start],
                    $"{path}[{start}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                conditional.Children.Count != 1 ||
                !TryGetBranchDefinition(
                    _source.GetChild(conditional, 0),
                    out var symbol,
                    out var right,
                    out var declares) ||
                !IsGeneratedTransportSymbol(symbol) ||
                IsCapturedSymbol(symbol) ||
                ContainsDirectEval(statements[start]))
            {
                return false;
            }

            var symbolSet = new HashSet<SourceSymbolIndex> { symbol };
            if (ContainsAnySymbol(conditional.Expression, symbolSet) ||
                ContainsAnySymbol(right, symbolSet))
            {
                return false;
            }

            var left = conditional.Expression;
            var @operator = Avm1SourceOperator.LogicalAnd;
            if (TryGetLogicalNotOperand(left, out var operand))
            {
                left = operand;
                @operator = Avm1SourceOperator.LogicalOr;
            }

            var rights = new List<SourceExpressionIndex> { right };
            var consumerIndex = start + 1;
            while (consumerIndex < statements.Length - 1 &&
                TryGetStatement(
                    statements[consumerIndex],
                    $"{path}[{consumerIndex}]",
                    depth,
                    out var nextConditional) &&
                nextConditional.Kind is Avm1SourceStatementKind.If &&
                nextConditional.Children.Count == 1 &&
                TryGetBranchDefinition(
                    _source.GetChild(nextConditional, 0),
                    out var nextSymbol,
                    out var nextRight,
                    out var nextDeclares) &&
                nextSymbol == symbol &&
                !nextDeclares &&
                (@operator is Avm1SourceOperator.LogicalAnd
                    ? IsSymbolReference(nextConditional.Expression, symbol)
                    : IsLogicalNotOf(nextConditional.Expression, symbol)) &&
                !ContainsAnySymbol(nextRight, symbolSet) &&
                !ContainsDirectEval(statements[consumerIndex]))
            {
                rights.Add(nextRight);
                consumerIndex++;
            }

            if (consumerIndex >= statements.Length ||
                !IsLinearConsumer(statements[consumerIndex]) ||
                !(CanMoveTransportIntoConsumer(
                        symbol,
                        statements[consumerIndex]) ||
                    CanMoveAdobeSingleArgumentTransportIntoConsumer(
                        symbol,
                        statements[consumerIndex])) ||
                ContainsDirectEvalInConsumerExpression(
                    statements[consumerIndex]))
            {
                return false;
            }

            var uses = CountUses(symbol, statements, start);
            var expectedDeclarations = declares ? 1 : 0;
            var expectedWrites = (declares ? 0 : 1) + rights.Count - 1;
            if (uses.Declarations != expectedDeclarations ||
                uses.Reads != rights.Count ||
                uses.Writes != expectedWrites)
            {
                return false;
            }

            var normalized = CloneExpression(
                left,
                $"{path}[{start}].condition",
                depth + 1);
            for (var i = 0; i < rights.Count; i++)
            {
                normalized = _builder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    @operator,
                    children:
                    [
                        normalized,
                        CloneExpression(
                            rights[i],
                            $"{path}[{start + i}].right",
                            depth + 1)
                    ]);
            }
            result = CloneStatement(
                statements[consumerIndex],
                $"{path}[{consumerIndex}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [symbol] = normalized
                });
            consumed = consumerIndex - start + 1;
            return true;
        }

        private bool TryRewriteAssignedLogicalAliasTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 3 >= statements.Length ||
                !TryGetBranchDefinition(
                    statements[start],
                    out var valueSymbol,
                    out _,
                    out _) ||
                !TryGetBranchDefinition(
                    statements[start + 1],
                    out var resultSymbol,
                    out var alias,
                    out var resultDeclares) ||
                resultSymbol == valueSymbol ||
                !IsGeneratedTransportSymbol(resultSymbol) ||
                !IsSymbolReference(alias, valueSymbol) ||
                !TryGetStatement(
                    statements[start + 2],
                    $"{path}[{start + 2}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                !TryGetLogicalTransport(
                    conditional,
                    resultSymbol,
                    out var @operator,
                    out var right) ||
                !IsLinearConsumer(statements[start + 3]) ||
                !CanMoveTransportIntoConsumer(
                    resultSymbol,
                    statements[start + 3]))
            {
                return false;
            }

            var uses = CountUses(resultSymbol, statements, start + 1, 3);
            var expectedDeclarations = resultDeclares ? 1 : 0;
            var expectedWrites = resultDeclares ? 1 : 2;
            if (uses.Declarations != expectedDeclarations ||
                uses.Reads != 2 ||
                uses.Writes != expectedWrites ||
                CountUses(resultSymbol, statements, start + 4) is not
                    { Declarations: 0, Reads: 0, Writes: 0 })
            {
                return false;
            }

            var normalized = _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                @operator,
                children:
                [
                    _builder.AddExpression(
                        Avm1SourceExpressionKind.SymbolReference,
                        symbol: CloneSymbol(
                            valueSymbol,
                            $"{path}[{start}].symbol")),
                    CloneExpression(
                        right,
                        $"{path}[{start + 2}].right",
                        depth + 1)
                ]);
            var rewrittenConsumer = CloneStatement(
                statements[start + 3],
                $"{path}[{start + 3}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [resultSymbol] = normalized
                });
            result = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children:
                [
                    CloneStatement(
                        statements[start],
                        $"{path}[{start}]",
                        depth),
                    rewrittenConsumer
                ]);
            consumed = 4;
            return true;
        }

        private bool TryRewriteAssignedLogicalTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetBranchDefinition(
                    statements[start],
                    out var symbol,
                    out _,
                    out var declares) ||
                !IsRegisterTransportSymbol(symbol) ||
                !TryGetStatement(
                    statements[start + 1],
                    $"{path}[{start + 1}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                !TryGetLogicalTransport(
                    conditional,
                    symbol,
                    out var @operator,
                    out var right) ||
                !IsLinearConsumer(statements[start + 2]) ||
                !CanMoveTransportIntoConsumer(
                    symbol,
                    statements[start + 2]))
            {
                return false;
            }

            var uses = CountUses(symbol, statements, start, 3);
            var expectedDeclarations = declares ? 1 : 0;
            var expectedWrites = declares ? 1 : 2;
            if (uses.Declarations != expectedDeclarations ||
                uses.Reads != 2 ||
                uses.Writes != expectedWrites ||
                CountUses(symbol, statements, start + 3) is not
                    { Declarations: 0, Reads: 0, Writes: 0 })
            {
                return false;
            }

            var normalized = _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                @operator,
                children:
                [
                    _builder.AddExpression(
                        Avm1SourceExpressionKind.SymbolReference,
                        symbol: CloneSymbol(
                            symbol,
                            $"{path}[{start}].symbol")),
                    CloneExpression(
                        right,
                        $"{path}[{start + 1}].right",
                        depth + 1)
                ]);
            var rewrittenConsumer = CloneStatement(
                statements[start + 2],
                $"{path}[{start + 2}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [symbol] = normalized
                });
            result = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children:
                [
                    CloneStatement(
                        statements[start],
                        $"{path}[{start}]",
                        depth),
                    rewrittenConsumer
                ]);
            consumed = 3;
            return true;
        }

        private bool TryRewriteConditionalLogicalTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            if (start + 2 >= statements.Length ||
                !TryGetGeneratedDeclaration(
                    statements[start],
                    out var conditionSymbol,
                    out var left) ||
                !left.IsValid ||
                !TryGetStatement(
                    statements[start + 1],
                    $"{path}[{start + 1}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                conditional.Children.Count != 1 ||
                !TryGetStatement(
                    _source.GetChild(conditional, 0),
                    $"{path}[{start + 1}].children[0]",
                    depth + 1,
                    out var branch) ||
                branch.Kind is not Avm1SourceStatementKind.Block ||
                !TryGetStatementList(
                    branch.Children,
                    $"{path}[{start + 1}].children[0].children",
                    out var branchStatements) ||
                branchStatements.Length != 3 ||
                !TryGetGeneratedDeclaration(
                    branchStatements[0],
                    out var valueSymbol,
                    out var value) ||
                !value.IsValid ||
                !TryGetAssignmentStatement(
                    branchStatements[1],
                    out var assignmentTarget,
                    out var assignmentValue) ||
                !TryGetSymbolReference(assignmentTarget, out var resultSymbol) ||
                resultSymbol == conditionSymbol ||
                resultSymbol == valueSymbol ||
                !IsSymbolReference(assignmentValue, valueSymbol) ||
                !TryGetSingleAssignment(
                    branchStatements[2],
                    conditionSymbol,
                    out var right) ||
                !TryGetStatement(
                    statements[start + 2],
                    $"{path}[{start + 2}]",
                    depth,
                    out var consumer) ||
                consumer.Kind is not Avm1SourceStatementKind.If ||
                !IsSymbolReference(consumer.Expression, conditionSymbol) ||
                CountUses(conditionSymbol, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 1 } ||
                CountUses(valueSymbol, statements, start) is not
                    { Declarations: 1, Reads: 2, Writes: 0 })
            {
                return false;
            }

            Avm1SourceOperator logicalOperator;
            if (IsSymbolReference(conditional.Expression, conditionSymbol))
            {
                logicalOperator = Avm1SourceOperator.LogicalAnd;
            }
            else if (IsLogicalNotOf(conditional.Expression, conditionSymbol))
            {
                logicalOperator = Avm1SourceOperator.LogicalOr;
            }
            else
            {
                return false;
            }

            var embeddedAssignment = _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children:
                [
                    CloneExpression(
                        assignmentTarget,
                        $"{path}[{start + 1}].assignmentTarget",
                        depth + 1),
                    CloneExpression(
                        value,
                        $"{path}[{start + 1}].value",
                        depth + 1)
                ]);
            var normalizedRight = CloneExpression(
                right,
                $"{path}[{start + 1}].right",
                depth + 1,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [valueSymbol] = embeddedAssignment
                });
            var normalizedCondition = _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                logicalOperator,
                children:
                [
                    CloneExpression(
                        left,
                        $"{path}[{start}].expression",
                        depth + 1),
                    normalizedRight
                ]);
            result = CloneStatement(
                statements[start + 2],
                $"{path}[{start + 2}]",
                depth,
                new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
                {
                    [conditionSymbol] = normalizedCondition
                });
            consumed = 3;
            return true;
        }

        private bool TryRewriteBranchPhiTransport(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var conditionalIndex = start;
            var conditionSymbol = SourceSymbolIndex.Invalid;
            var conditionInitializer = SourceExpressionIndex.Invalid;
            var hasConditionTransport = TryGetGeneratedDeclaration(
                statements[start],
                out conditionSymbol,
                out conditionInitializer) &&
                conditionInitializer.IsValid;
            if (hasConditionTransport)
                conditionalIndex++;

            var consumerIndex = conditionalIndex + 1;
            if (consumerIndex >= statements.Length ||
                !TryGetStatement(
                    statements[conditionalIndex],
                    $"{path}[{conditionalIndex}]",
                    depth,
                    out var conditional) ||
                conditional.Kind is not Avm1SourceStatementKind.If ||
                !TryGetBranchPhi(
                    conditional,
                    out var resultSymbol,
                    out var whenTrue,
                    out var whenFalse) ||
                !IsLinearConsumer(statements[consumerIndex]) ||
                !(CanMoveTransportIntoConsumer(
                        resultSymbol,
                        statements[consumerIndex]) ||
                    CanMoveAdobeSingleArgumentTransportIntoConsumer(
                        resultSymbol,
                        statements[consumerIndex]) ||
                    CanMoveAdobeInvocationArgumentTransportIntoConsumer(
                        resultSymbol,
                        statements[consumerIndex])))
            {
                return false;
            }

            var condition = conditional.Expression;
            if (hasConditionTransport)
            {
                if (IsSymbolReference(condition, conditionSymbol))
                {
                    condition = conditionInitializer;
                }
                else if (IsLogicalNotOf(condition, conditionSymbol))
                {
                    condition = conditionInitializer;
                    (whenTrue, whenFalse) = (whenFalse, whenTrue);
                }
                else
                {
                    return false;
                }

                var conditionUses = CountUses(conditionSymbol, statements, start);
                if (conditionUses is not { Declarations: 1, Reads: 1, Writes: 0 })
                    return false;
            }
            else if (TryGetLogicalNotOperand(condition, out var operand))
            {
                condition = operand;
                (whenTrue, whenFalse) = (whenFalse, whenTrue);
            }

            var resultUses = CountUses(resultSymbol, statements, start);
            if (resultUses is not { Declarations: 1, Reads: 1, Writes: 1 })
                return false;

            var normalized = _builder.AddExpression(
                Avm1SourceExpressionKind.Conditional,
                children:
                [
                    CloneExpression(condition, path + ".condition", depth + 1),
                    CloneExpression(whenTrue, path + ".whenTrue", depth + 1),
                    CloneExpression(whenFalse, path + ".whenFalse", depth + 1)
                ]);
            var substitutions = new Dictionary<SourceSymbolIndex, SourceExpressionIndex>
            {
                [resultSymbol] = normalized
            };
            result = CloneStatement(
                statements[consumerIndex],
                $"{path}[{consumerIndex}]",
                depth,
                substitutions);
            consumed = consumerIndex - start + 1;
            return true;
        }

        private bool TryInlineTransportAssignments(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var symbols = new List<SourceSymbolIndex>();
            var initializers = new List<SourceExpressionIndex>();
            var consumerIndex = start;
            while (consumerIndex < statements.Length)
            {
                if (symbols.Count > 0 &&
                    IsLinearConsumer(statements[consumerIndex]))
                {
                    var symbolSet = symbols.ToHashSet();
                    var canInline = symbolSet.Count == symbols.Count;
                    for (var i = 0; canInline && i < symbols.Count; i++)
                    {
                        var uses = CountUses(
                            symbols[i],
                            statements,
                            start,
                            consumerIndex - start + 1);
                        var allUses = CountAllUses(symbols[i]);
                        var priorUses = CountUses(
                            symbols[i],
                            statements,
                            0,
                            start);
                        if (uses is not
                                { Declarations: 0, Reads: 1, Writes: 1 } ||
                            allUses.Reads - priorUses.Reads != 1 ||
                            allUses.Writes - priorUses.Writes != 1 ||
                            IsCapturedSymbol(symbols[i]) ||
                            ContainsAnySymbol(initializers[i], symbolSet))
                        {
                            canInline = false;
                        }
                    }

                    var readOrder = new List<SourceSymbolIndex>();
                    var state = new ConsumerOrderState(
                        symbolSet,
                        symbols.Count,
                        readOrder);
                    CollectConsumerOrder(statements[consumerIndex], ref state);
                    if (canInline &&
                        !ContainsDirectEval(statements[consumerIndex]) &&
                        !state.HasBarrier &&
                        readOrder.SequenceEqual(symbols))
                    {
                        var substitutions = new Dictionary<
                            SourceSymbolIndex,
                            SourceExpressionIndex>();
                        for (var i = 0; i < symbols.Count; i++)
                        {
                            substitutions.Add(
                                symbols[i],
                                CloneExpression(
                                    initializers[i],
                                    $"{path}[{start + i}].expression",
                                    depth + 1));
                        }
                        result = CloneStatement(
                            statements[consumerIndex],
                            $"{path}[{consumerIndex}]",
                            depth,
                            substitutions);
                        consumed = consumerIndex - start + 1;
                        return true;
                    }
                }

                if (!TryGetGeneratedAssignmentDefinition(
                        statements[consumerIndex],
                        out var symbol,
                        out var initializer))
                {
                    break;
                }
                symbols.Add(symbol);
                initializers.Add(initializer);
                consumerIndex++;
            }
            return false;
        }

        private bool TryInlineTransportDeclarations(
            SourceStatementIndex[] statements,
            int start,
            string path,
            int depth,
            out SourceStatementIndex result,
            out int consumed)
        {
            result = SourceStatementIndex.Invalid;
            consumed = 0;
            var symbols = new List<SourceSymbolIndex>();
            var initializers = new List<SourceExpressionIndex>();
            var consumerIndex = start;
            while (consumerIndex < statements.Length)
            {
                if (symbols.Count > 0 &&
                    IsLinearConsumer(statements[consumerIndex]))
                {
                    var symbolSet = symbols.ToHashSet();
                    var canInline = true;
                    for (var i = 0; i < symbols.Count; i++)
                    {
                        var uses = CountUses(symbols[i], statements, start);
                        if (uses is not { Declarations: 1, Reads: 1, Writes: 0 } ||
                            ContainsAnySymbol(initializers[i], symbolSet))
                        {
                            canInline = false;
                            break;
                        }
                    }

                    var readOrder = new List<SourceSymbolIndex>();
                    var state = new ConsumerOrderState(
                        symbolSet,
                        symbols.Count,
                        readOrder);
                    CollectConsumerOrder(statements[consumerIndex], ref state);
                    var hasDirectEval = ContainsDirectEval(
                        statements[consumerIndex]);
                    var isAdobeInvocationArgumentTransport =
                        symbols.Count == 1 &&
                        IsGeneratedTransportSymbol(symbols[0]) &&
                        CanMoveAdobeInvocationArgumentTransportIntoConsumer(
                            symbols[0],
                            statements[consumerIndex]);
                    var isAdobeArrayObjectValueTransport =
                        symbols.Count == 1 &&
                        IsGeneratedTransportSymbol(symbols[0]) &&
                        CanMoveAdobeArrayObjectValueTransportIntoConsumer(
                            symbols[0],
                            statements[consumerIndex]);
                    var canCrossBarrier = hasDirectEval
                        ? CanElideDirectEvalRegisterAlias(
                            symbols,
                            initializers)
                        : initializers.All(IsLiteralExpression) ||
                            (symbols.All(IsGeneratedTransportSymbol) &&
                                symbols.Any(IsGeneratedTemporarySymbol)) ||
                            isAdobeInvocationArgumentTransport ||
                            isAdobeArrayObjectValueTransport;
                    var hasAuthoredLocal = symbols.Any(IsAuthoredLocalSymbol);
                    var consumer = _source[statements[consumerIndex]];
                    var canInlineAuthoredLocal = !hasAuthoredLocal ||
                        initializers.All(index =>
                            IsRelocatablePureExpression(index)) ||
                        symbols.Count == 1 &&
                        CanRelocateSingleUseAuthoredInitializer(
                            initializers[0]) ||
                        consumer.Kind is
                            Avm1SourceStatementKind.VariableDeclaration &&
                        IsAuthoredLocalSymbol(consumer.Symbol) ||
                        symbols.Count == 1 &&
                        (IsDirectConditionConsumer(
                                symbols[0],
                                statements[consumerIndex]) ||
                            IsStableThisMemberAssignmentConsumer(
                                statements[consumerIndex]));
                    if (canInline &&
                        canInlineAuthoredLocal &&
                        (!state.HasBarrier || canCrossBarrier) &&
                        readOrder.SequenceEqual(symbols))
                    {
                        var substitutions = new Dictionary<
                            SourceSymbolIndex,
                            SourceExpressionIndex>();
                        for (var i = 0; i < symbols.Count; i++)
                        {
                            substitutions.Add(
                                symbols[i],
                                CloneExpression(
                                    initializers[i],
                                    $"{path}[{start + i}].expression",
                                    depth + 1));
                        }
                        result = CloneStatement(
                            statements[consumerIndex],
                            $"{path}[{consumerIndex}]",
                            depth,
                            substitutions);
                        consumed = consumerIndex - start + 1;
                        return true;
                    }
                }

                if (!TryGetInlineableDeclaration(
                        statements[consumerIndex],
                        out var symbol,
                        out var initializer) ||
                    !initializer.IsValid)
                {
                    break;
                }
                symbols.Add(symbol);
                initializers.Add(initializer);
                consumerIndex++;
            }
            return false;
        }

        private bool CanElideDirectEvalRegisterAlias(
            List<SourceSymbolIndex> symbols,
            List<SourceExpressionIndex> initializers)
        {
            if (symbols.Count != 1 ||
                initializers.Count != 1 ||
                !IsGeneratedTemporarySymbol(symbols[0]) ||
                !TryGetSymbolReference(initializers[0], out var sourceSymbol) ||
                !sourceSymbol.IsValid ||
                sourceSymbol.Value >= _source.Symbols.Count ||
                IsCapturedSymbol(sourceSymbol))
            {
                return false;
            }

            return _source[sourceSymbol].Kind is
                Avm1SourceSymbolKind.Local or
                Avm1SourceSymbolKind.Parameter;
        }

        private SourceExpressionIndex CloneOptionalExpression(
            SourceExpressionIndex index,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            bool discardResult = false) =>
            index.IsValid
                ? CloneExpression(index, path, depth, substitutions, discardResult)
                : SourceExpressionIndex.Invalid;

        private SourceExpressionIndex CloneExpression(
            SourceExpressionIndex index,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions = null,
            bool discardResult = false)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetExpression(index, path, depth, out var expression))
                return AddUndefinedLiteral();
            if (!IsSupported(expression.Kind))
            {
                AddIncomplete(
                    "AVM1EQ002",
                    path,
                    $"Expression kind {expression.Kind} is outside the normalized-HIR subset.");
                return AddUndefinedLiteral();
            }
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                substitutions is not null &&
                substitutions.TryGetValue(expression.Symbol, out var replacement))
            {
                return replacement;
            }
            if (IsRedundantSelfAssignment(index))
            {
                Changed = true;
                RewriteCount++;
                return CloneExpression(
                    _source.GetChild(expression, 0),
                    path + ".children[0]",
                    depth + 1,
                    substitutions,
                    discardResult);
            }
            if (discardResult && TryCanonicalizeDiscardedStep(
                expression,
                path,
                depth,
                substitutions,
                out var discardedStep))
            {
                Changed = true;
                RewriteCount++;
                return discardedStep;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Unary &&
                expression.Operator is Avm1SourceOperator.LogicalNot &&
                expression.Children.Count == 1)
            {
                var result = NegateClonedExpression(CloneExpression(
                    _source.GetChild(expression, 0),
                    path + ".children[0]",
                    depth + 1,
                    substitutions),
                    out var simplified);
                if (simplified)
                {
                    Changed = true;
                    RewriteCount++;
                }
                return result;
            }
            if (TryFoldNumericBinary(expression, out var folded))
            {
                Changed = true;
                RewriteCount++;
                return folded;
            }
            if (TryCanonicalizeBitwiseLiteralOperands(
                expression,
                path,
                depth,
                substitutions,
                out var bitwise))
            {
                Changed = true;
                RewriteCount++;
                return bitwise;
            }
            if (TryCanonicalizeCompoundAssignment(
                expression,
                path,
                depth,
                substitutions,
                out var compound))
            {
                Changed = true;
                RewriteCount++;
                return compound;
            }
            if (TryCanonicalizeDirectEval(
                expression,
                path,
                depth,
                substitutions,
                out var evaluatedName))
            {
                Changed = true;
                RewriteCount++;
                return evaluatedName;
            }
            if (TryCanonicalizeConditionalInversion(
                expression,
                path,
                depth,
                substitutions,
                out var conditional))
            {
                Changed = true;
                RewriteCount++;
                return conditional;
            }

            var children = CloneExpressionList(
                expression.Children,
                path + ".children",
                depth + 1,
                substitutions);
            var symbol = expression.Symbol.IsValid
                ? CloneSymbol(expression.Symbol, path + ".symbol")
                : SourceSymbolIndex.Invalid;
            var name = CloneExpressionName(expression, path + ".name");
            var literal = expression.Literal.IsValid
                ? CloneLiteral(expression.Literal, path + ".literal")
                : SourceLiteralIndex.Invalid;
            var function = expression.Kind is Avm1SourceExpressionKind.FunctionLiteral
                ? CloneFunction(
                    expression.Function,
                    path + ".function",
                    depth + 1)
                : SourceFunctionIndex.Invalid;
            var flags = expression.Flags;
            if (expression.Kind is Avm1SourceExpressionKind.MemberAccess &&
                flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember) &&
                HasStaticIdentifierMemberName(expression))
            {
                flags &= ~Avm1SourceExpressionFlags.ComputedMember;
            }
            return _builder.AddExpression(
                expression.Kind,
                expression.Operator,
                symbol: symbol,
                name: name,
                literal: literal,
                function: function,
                flags: flags,
                children: children);
        }

        private bool TryCanonicalizeDirectEval(
            Avm1SourceExpression expression,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceExpressionIndex result)
        {
            result = SourceExpressionIndex.Invalid;
            if (!Avm1SourceEvalAnalysis.IsDirectEvalCall(_source, expression) ||
                expression.Children.Count != 2)
            {
                return false;
            }

            result = _builder.AddExpression(
                Avm1SourceExpressionKind.ComputedDynamicName,
                children:
                [
                    CloneExpression(
                        _source.GetChild(expression, 1),
                        path + ".children[1]",
                        depth + 1,
                        substitutions)
                ]);
            return true;
        }

        private bool TryCanonicalizeConditionalInversion(
            Avm1SourceExpression expression,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceExpressionIndex result)
        {
            result = SourceExpressionIndex.Invalid;
            if (expression.Kind is not Avm1SourceExpressionKind.Conditional ||
                expression.Children.Count != 3)
            {
                return false;
            }

            var conditionIndex = _source.GetChild(expression, 0);
            if (!conditionIndex.IsValid ||
                conditionIndex.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var condition = _source[conditionIndex];
            if (condition.Kind is not Avm1SourceExpressionKind.Binary ||
                condition.Children.Count != 2 ||
                !TryGetNegatedBinaryOperator(
                    condition.Operator,
                    out var negatedOperator) ||
                (int)negatedOperator >= (int)condition.Operator)
            {
                return false;
            }

            result = _builder.AddExpression(
                Avm1SourceExpressionKind.Conditional,
                expression.Operator,
                children:
                [
                    CloneNegatedCondition(
                        conditionIndex,
                        path + ".children[0]",
                        depth + 1,
                        substitutions),
                    CloneExpression(
                        _source.GetChild(expression, 2),
                        path + ".children[2]",
                        depth + 1,
                        substitutions),
                    CloneExpression(
                        _source.GetChild(expression, 1),
                        path + ".children[1]",
                        depth + 1,
                        substitutions)
                ]);
            return true;
        }

        private SourceFunctionIndex CloneFunction(
            SourceFunctionIndex index,
            string path,
            int depth)
        {
            if (_functions.TryGetValue(index, out var existing))
                return existing;
            if (depth > MaximumDepth)
            {
                AddIncomplete(
                    "AVM1EQ003",
                    path,
                    "Source HIR function depth exceeds the limit.");
                return SourceFunctionIndex.Invalid;
            }
            if (!index.IsValid || index.Value >= _source.Functions.Count)
            {
                AddIncomplete(
                    "AVM1EQ003",
                    path,
                    "Source HIR function handle is invalid.");
                return SourceFunctionIndex.Invalid;
            }
            if (!_activeFunctions.Add(index))
            {
                AddIncomplete(
                    "AVM1EQ003",
                    path,
                    "Source HIR function definitions contain a structural cycle.");
                return SourceFunctionIndex.Invalid;
            }

            try
            {
                var source = _source[index];
                var nameSymbol = source.NameSymbol.IsValid
                    ? CloneSymbol(source.NameSymbol, path + ".nameSymbol")
                    : SourceSymbolIndex.Invalid;
                var parameters = CloneFunctionSymbols(
                    source.Parameters,
                    path + ".parameters");
                var body = CloneStatement(
                    source.Body,
                    path + ".body",
                    depth + 1);
                var cloned = _builder.AddFunction(
                    nameSymbol,
                    body,
                    source.Flags,
                    SourceOriginIndex.Invalid,
                    parameters);
                _functions.Add(index, cloned);
                return cloned;
            }
            finally
            {
                _activeFunctions.Remove(index);
            }
        }

        private SourceSymbolIndex[] CloneFunctionSymbols(
            SourceSymbolList list,
            string path)
        {
            if (list.Count < 0 ||
                list.Start < 0 ||
                list.Start > _source.FunctionSymbols.Count - list.Count)
            {
                AddIncomplete(
                    "AVM1EQ003",
                    path,
                    "Source HIR function symbol list is invalid.");
                return [];
            }

            var result = new SourceSymbolIndex[list.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = CloneSymbol(
                    _source.FunctionSymbols[list.Start + i],
                    $"{path}[{i}]");
            }
            return result;
        }

        private bool TryCanonicalizeDiscardedStep(
            Avm1SourceExpression expression,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceExpressionIndex result)
        {
            result = SourceExpressionIndex.Invalid;
            if (expression.Kind is not Avm1SourceExpressionKind.Unary ||
                expression.Children.Count != 1 ||
                expression.Operator is not (
                    Avm1SourceOperator.PrefixIncrement or
                    Avm1SourceOperator.PrefixDecrement or
                    Avm1SourceOperator.PostfixIncrement or
                    Avm1SourceOperator.PostfixDecrement))
            {
                return false;
            }

            var target = _source.GetChild(expression, 0);
            if (!IsAssignmentTarget(target))
                return false;
            var @operator = expression.Operator is
                Avm1SourceOperator.PrefixIncrement or
                Avm1SourceOperator.PostfixIncrement
                ? Avm1SourceOperator.AddAssign
                : Avm1SourceOperator.SubtractAssign;
            result = _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                @operator,
                children:
                [
                    CloneExpression(
                        target,
                        path + ".children[0]",
                        depth + 1,
                        substitutions),
                    AddNumberLiteral(1)
                ]);
            return true;
        }

        private bool TryFoldNumericBinary(
            Avm1SourceExpression expression,
            out SourceExpressionIndex result)
        {
            result = SourceExpressionIndex.Invalid;
            if (expression.Kind is not Avm1SourceExpressionKind.Binary ||
                expression.Children.Count != 2 ||
                !TryGetNumericLiteral(_source.GetChild(expression, 0), out var left) ||
                !TryGetNumericLiteral(_source.GetChild(expression, 1), out var right))
            {
                return false;
            }

            switch (expression.Operator)
            {
                case Avm1SourceOperator.Add:
                    result = AddNumberLiteral(left + right);
                    return true;
                case Avm1SourceOperator.Subtract:
                    result = AddNumberLiteral(left - right);
                    return true;
                case Avm1SourceOperator.Multiply:
                    result = AddNumberLiteral(left * right);
                    return true;
                case Avm1SourceOperator.Divide:
                    result = AddNumberLiteral(left / right);
                    return true;
                case Avm1SourceOperator.Modulo:
                    result = AddNumberLiteral(left % right);
                    return true;
                case Avm1SourceOperator.Equal:
                case Avm1SourceOperator.StrictEqual:
                    result = AddBooleanLiteral(left == right);
                    return true;
                case Avm1SourceOperator.NotEqual:
                case Avm1SourceOperator.StrictNotEqual:
                    result = AddBooleanLiteral(left != right);
                    return true;
                case Avm1SourceOperator.Less:
                    result = AddBooleanLiteral(left < right);
                    return true;
                case Avm1SourceOperator.LessOrEqual:
                    result = AddBooleanLiteral(left <= right);
                    return true;
                case Avm1SourceOperator.Greater:
                    result = AddBooleanLiteral(left > right);
                    return true;
                case Avm1SourceOperator.GreaterOrEqual:
                    result = AddBooleanLiteral(left >= right);
                    return true;
                case Avm1SourceOperator.BitAnd:
                    result = AddNumberLiteral(ToInt32(left) & ToInt32(right));
                    return true;
                case Avm1SourceOperator.BitOr:
                    result = AddNumberLiteral(ToInt32(left) | ToInt32(right));
                    return true;
                case Avm1SourceOperator.BitXor:
                    result = AddNumberLiteral(ToInt32(left) ^ ToInt32(right));
                    return true;
                case Avm1SourceOperator.ShiftLeft:
                    result = AddNumberLiteral(
                        ToInt32(left) << ((int)ToUint32(right) & 0x1f));
                    return true;
                case Avm1SourceOperator.ShiftRight:
                    result = AddNumberLiteral(
                        ToInt32(left) >> ((int)ToUint32(right) & 0x1f));
                    return true;
                case Avm1SourceOperator.ShiftRightUnsigned:
                    result = AddNumberLiteral(
                        ToUint32(left) >> ((int)ToUint32(right) & 0x1f));
                    return true;
                default:
                    return false;
            }
        }

        private bool TryCanonicalizeBitwiseLiteralOperands(
            Avm1SourceExpression expression,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceExpressionIndex result)
        {
            result = SourceExpressionIndex.Invalid;
            if (expression.Kind is not Avm1SourceExpressionKind.Binary ||
                expression.Operator is not (
                    Avm1SourceOperator.BitAnd or
                    Avm1SourceOperator.BitOr or
                    Avm1SourceOperator.BitXor) ||
                expression.Children.Count != 2)
            {
                return false;
            }

            var leftIndex = _source.GetChild(expression, 0);
            var rightIndex = _source.GetChild(expression, 1);
            var hasLeftLiteral = TryGetNumericLiteral(leftIndex, out var left);
            var hasRightLiteral = TryGetNumericLiteral(rightIndex, out var right);
            var normalizedLeft = hasLeftLiteral ? ToInt32(left) : 0;
            var normalizedRight = hasRightLiteral ? ToInt32(right) : 0;
            if ((!hasLeftLiteral || left == normalizedLeft) &&
                (!hasRightLiteral || right == normalizedRight))
            {
                return false;
            }

            result = _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                expression.Operator,
                children:
                [
                    hasLeftLiteral
                        ? AddNumberLiteral(normalizedLeft)
                        : CloneExpression(
                            leftIndex,
                            path + ".children[0]",
                            depth + 1,
                            substitutions),
                    hasRightLiteral
                        ? AddNumberLiteral(normalizedRight)
                        : CloneExpression(
                            rightIndex,
                            path + ".children[1]",
                            depth + 1,
                            substitutions)
                ]);
            return true;
        }

        private bool TryGetNumericLiteral(SourceExpressionIndex index, out double value)
        {
            value = 0;
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid ||
                expression.Literal.Value >= _source.Literals.Count)
            {
                return false;
            }

            var literal = _source[expression.Literal];
            if (literal.Kind is Avm1SourceLiteralKind.Integer)
            {
                value = literal.IntegerValue;
                return true;
            }
            if (literal.Kind is Avm1SourceLiteralKind.Number)
            {
                value = literal.NumberValue;
                return true;
            }
            return false;
        }

        private SourceExpressionIndex AddNumberLiteral(double value) =>
            _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: _builder.AddLiteral(
                    Avm1SourceLiteralKind.Number,
                    numberValue: value));

        private SourceExpressionIndex AddBooleanLiteral(bool value) =>
            _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: _builder.AddLiteral(
                    Avm1SourceLiteralKind.Boolean,
                    booleanValue: value));

        private static int ToInt32(double value) => unchecked((int)ToUint32(value));

        private static uint ToUint32(double value)
        {
            if (!double.IsFinite(value))
                return 0;
            var wrapped = Math.Truncate(value) % 4294967296.0;
            if (wrapped < 0)
                wrapped += 4294967296.0;
            return (uint)wrapped;
        }

        private SourceExpressionIndex[] CloneExpressionList(
            SourceExpressionList list,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            bool discardResults = false)
        {
            if (!TryGetExpressionList(list, path, out var source))
                return [];
            var result = new SourceExpressionIndex[source.Length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = CloneExpression(
                    source[i],
                    $"{path}[{i}]",
                    depth,
                    substitutions,
                    discardResults);
            }
            return result;
        }

        private bool TryCanonicalizeCompoundAssignment(
            Avm1SourceExpression expression,
            string path,
            int depth,
            IReadOnlyDictionary<SourceSymbolIndex, SourceExpressionIndex>? substitutions,
            out SourceExpressionIndex result)
        {
            result = SourceExpressionIndex.Invalid;
            if (expression.Kind is not Avm1SourceExpressionKind.Assignment ||
                expression.Operator is not Avm1SourceOperator.Assign ||
                expression.Children.Count != 2)
            {
                return false;
            }

            var target = _source.GetChild(expression, 0);
            var valueIndex = _source.GetChild(expression, 1);
            if (!(IsStableAssignmentTarget(target) ||
                    IsStaticThisMemberPath(target, depth + 1)) ||
                !TryGetExpression(valueIndex, path + ".value", depth + 1, out var value) ||
                value.Kind is not Avm1SourceExpressionKind.Binary ||
                value.Children.Count != 2 ||
                !TryGetCompoundOperator(value.Operator, out var compoundOperator) ||
                !ExpressionsEqual(target, _source.GetChild(value, 0), depth + 1))
            {
                return false;
            }

            result = _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                compoundOperator,
                children:
                [
                    CloneExpression(target, path + ".target", depth + 1, substitutions),
                    CloneExpression(
                        _source.GetChild(value, 1),
                        path + ".right",
                        depth + 1,
                        substitutions)
                ]);
            return true;
        }

        private bool IsStaticThisMemberPath(
            SourceExpressionIndex index,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference)
            {
                return expression.Symbol.IsValid &&
                    expression.Symbol.Value < _source.Symbols.Count &&
                    _source[expression.Symbol].Kind is Avm1SourceSymbolKind.This;
            }
            if (expression.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                expression.Children.Count != 2 ||
                expression.Flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember))
            {
                return false;
            }

            var key = _source.GetChild(expression, 1);
            return key.IsValid &&
                key.Value < _source.Expressions.Count &&
                _source[key].Kind is Avm1SourceExpressionKind.Literal &&
                IsStaticThisMemberPath(
                    _source.GetChild(expression, 0),
                    depth + 1);
        }

        private SourceSymbolIndex CloneSymbol(SourceSymbolIndex index, string path)
        {
            if (_symbols.TryGetValue(index, out var existing))
                return existing;
            if (!index.IsValid || index.Value >= _source.Symbols.Count)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR symbol handle is invalid.");
                return SourceSymbolIndex.Invalid;
            }

            var source = _source[index];
            var flags = source.Flags;
            if (IsCanonicalRecoveryLocalName(source))
                flags |= Avm1SourceSymbolFlags.CompilerGenerated;
            var ordinal = _symbolOrdinals.GetValueOrDefault(source.Kind);
            _symbolOrdinals[source.Kind] = ordinal + 1;
            var preserveCompletionName =
                flags.HasFlag(Avm1SourceSymbolFlags.CompilerGenerated) &&
                source.Name.IsValid &&
                source.Name.Value < _source.Strings.Count &&
                IsCompletionTransportName(_source[source.Name]);
            var name = preserveCompletionName
                ? _source[source.Name]
                : source.Kind switch
                {
                    Avm1SourceSymbolKind.This => "this",
                    Avm1SourceSymbolKind.Super => "super",
                    Avm1SourceSymbolKind.Arguments => "arguments",
                    Avm1SourceSymbolKind.Root => "_root",
                    Avm1SourceSymbolKind.Parent => "_parent",
                    Avm1SourceSymbolKind.Global => "_global",
                    _ => source.Kind.ToString().ToLowerInvariant() +
                        ordinal.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                };
            var cloned = _builder.AddSymbol(
                source.Kind,
                name,
                SourceTypeIndex.Invalid,
                _unknownType,
                flags);
            _symbols.Add(index, cloned);
            return cloned;
        }

        private HashSet<SourceLabelIndex> FindElidableLabels()
        {
            var declared = new HashSet<SourceLabelIndex>();
            var required = new HashSet<SourceLabelIndex>();
            VisitBody(_method.Body);
            foreach (var function in _source.Functions)
                VisitBody(function.Body);

            declared.ExceptWith(required);
            return declared;

            void VisitBody(SourceStatementIndex body)
            {
                var scopes = new List<LabelScope>();
                Visit(body, scopes, depth: 0);
            }

            void Visit(
                SourceStatementIndex index,
                List<LabelScope> scopes,
                int depth)
            {
                if (!index.IsValid ||
                    index.Value >= _source.Statements.Count ||
                    depth > MaximumDepth)
                {
                    return;
                }

                var statement = _source[index];
                if (statement.Kind is
                    Avm1SourceStatementKind.Break or
                    Avm1SourceStatementKind.Continue)
                {
                    if (statement.Label.IsValid)
                    {
                        var exactTarget = FindScope(scopes, statement.Label);
                        var implicitTarget = FindImplicitScope(scopes, statement.Kind);
                        if (!exactTarget.IsValid ||
                            !implicitTarget.IsValid ||
                            exactTarget != implicitTarget)
                        {
                            required.Add(statement.Label);
                        }
                    }
                    return;
                }

                if (statement.Label.IsValid)
                    declared.Add(statement.Label);

                var pushesScope = statement.Label.IsValid ||
                    IsImplicitControlTarget(statement.Kind);
                if (pushesScope)
                {
                    scopes.Add(new LabelScope(
                        statement.Index,
                        statement.Kind,
                        statement.Label));
                }

                for (var i = 0; i < statement.Initializers.Count; i++)
                {
                    Visit(
                        _source.GetInitializer(statement, i),
                        scopes,
                        depth + 1);
                }
                for (var i = 0; i < statement.Children.Count; i++)
                {
                    Visit(
                        _source.GetChild(statement, i),
                        scopes,
                        depth + 1);
                }

                if (pushesScope)
                    scopes.RemoveAt(scopes.Count - 1);
            }
        }

        private static SourceStatementIndex FindScope(
            List<LabelScope> scopes,
            SourceLabelIndex label)
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                if (scopes[i].Label == label)
                    return scopes[i].Statement;
            }
            return SourceStatementIndex.Invalid;
        }

        private static SourceStatementIndex FindImplicitScope(
            List<LabelScope> scopes,
            Avm1SourceStatementKind completion)
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                var kind = scopes[i].Kind;
                if (completion is Avm1SourceStatementKind.Continue
                        ? IsLoop(kind)
                        : IsImplicitControlTarget(kind))
                {
                    return scopes[i].Statement;
                }
            }
            return SourceStatementIndex.Invalid;
        }

        private static bool IsImplicitControlTarget(
            Avm1SourceStatementKind kind) =>
            IsLoop(kind) || kind is Avm1SourceStatementKind.Switch;

        private static bool IsLoop(Avm1SourceStatementKind kind) =>
            kind is
                Avm1SourceStatementKind.While or
                Avm1SourceStatementKind.DoWhile or
                Avm1SourceStatementKind.For or
                Avm1SourceStatementKind.ForIn;

        private SourceLabelIndex CloneOptionalLabel(
            SourceLabelIndex index,
            string path)
        {
            if (!index.IsValid)
                return SourceLabelIndex.Invalid;
            if (!_elidedLabels.Contains(index))
                return CloneLabel(index, path);

            Changed = true;
            RewriteCount++;
            return SourceLabelIndex.Invalid;
        }

        private SourceLabelIndex CloneLabel(SourceLabelIndex index, string path)
        {
            if (_labels.TryGetValue(index, out var existing))
                return existing;
            if (!index.IsValid || index.Value >= _source.Labels.Count)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR label handle is invalid.");
                return SourceLabelIndex.Invalid;
            }

            var cloned = _builder.AddLabel(
                "label" + _labels.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            _labels.Add(index, cloned);
            return cloned;
        }

        private SourceLiteralIndex CloneLiteral(SourceLiteralIndex index, string path)
        {
            if (!index.IsValid || index.Value >= _source.Literals.Count)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR literal handle is invalid.");
                return _builder.AddLiteral(Avm1SourceLiteralKind.Undefined);
            }

            var literal = _source[index];
            return literal.Kind switch
            {
                Avm1SourceLiteralKind.Integer => _builder.AddLiteral(
                    Avm1SourceLiteralKind.Number,
                    numberValue: literal.IntegerValue),
                Avm1SourceLiteralKind.Number => _builder.AddLiteral(
                    Avm1SourceLiteralKind.Number,
                    numberValue: literal.NumberValue),
                Avm1SourceLiteralKind.Boolean => _builder.AddLiteral(
                    Avm1SourceLiteralKind.Boolean,
                    booleanValue: literal.BooleanValue),
                Avm1SourceLiteralKind.String => _builder.AddLiteral(
                    Avm1SourceLiteralKind.String,
                    stringValue: CloneRequiredName(literal.StringValue, path)),
                _ => _builder.AddLiteral(literal.Kind)
            };
        }

        private SourceStringIndex CloneExpressionName(
            Avm1SourceExpression expression,
            string path)
        {
            if (!expression.Name.IsValid)
                return SourceStringIndex.Invalid;
            var value = GetRequiredString(expression.Name, path);
            if (expression.Kind is Avm1SourceExpressionKind.QualifiedName)
                value = Avm1SourceProgram.NormalizeRuntimeQualifiedName(value);
            return _builder.InternString(value);
        }

        private SourceStringIndex CloneOptionalName(SourceStringIndex index, string path) =>
            index.IsValid ? CloneRequiredName(index, path) : SourceStringIndex.Invalid;

        private SourceStringIndex CloneRequiredName(SourceStringIndex index, string path) =>
            _builder.InternString(GetRequiredString(index, path));

        private string GetRequiredString(SourceStringIndex index, string path)
        {
            if (index.IsValid && index.Value < _source.Strings.Count)
                return _source[index];
            AddIncomplete("AVM1EQ003", path, "Source HIR string handle is invalid.");
            return "<invalid>";
        }

        private SourceExpressionIndex AddUndefinedLiteral() =>
            _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: _builder.AddLiteral(Avm1SourceLiteralKind.Undefined));

        private bool TryGetGeneratedDeclaration(
            SourceStatementIndex index,
            out SourceSymbolIndex symbol,
            out SourceExpressionIndex initializer)
        {
            symbol = SourceSymbolIndex.Invalid;
            initializer = SourceExpressionIndex.Invalid;
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            var statement = _source[index];
            if (statement.Kind is not Avm1SourceStatementKind.VariableDeclaration ||
                !IsGeneratedTransportSymbol(statement.Symbol))
            {
                return false;
            }
            symbol = statement.Symbol;
            initializer = statement.Expression;
            return true;
        }

        private bool TryGetGeneratedAssignmentDefinition(
            SourceStatementIndex index,
            out SourceSymbolIndex symbol,
            out SourceExpressionIndex initializer)
        {
            symbol = SourceSymbolIndex.Invalid;
            initializer = SourceExpressionIndex.Invalid;
            if (!TryGetAssignmentStatement(index, out var target, out initializer) ||
                !TryGetSymbolReference(target, out symbol) ||
                !IsGeneratedTransportSymbol(symbol) ||
                IsCompletionTransportSymbol(symbol) ||
                !initializer.IsValid ||
                IsSymbolReference(initializer, symbol))
            {
                return false;
            }
            return true;
        }

        private bool TryGetInlineableDeclaration(
            SourceStatementIndex index,
            out SourceSymbolIndex symbol,
            out SourceExpressionIndex initializer)
        {
            symbol = SourceSymbolIndex.Invalid;
            initializer = SourceExpressionIndex.Invalid;
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            var statement = _source[index];
            if (statement.Kind is not Avm1SourceStatementKind.VariableDeclaration ||
                !statement.Symbol.IsValid ||
                statement.Symbol.Value >= _source.Symbols.Count ||
                _branchSplitDeclarations.Contains(statement.Symbol) ||
                IsCompletionTransportSymbol(statement.Symbol))
            {
                return false;
            }
            var sourceSymbol = _source[statement.Symbol];
            if (sourceSymbol.Kind is not Avm1SourceSymbolKind.Local &&
                (sourceSymbol.Kind is not Avm1SourceSymbolKind.Temporary ||
                    !sourceSymbol.Flags.HasFlag(
                        Avm1SourceSymbolFlags.CompilerGenerated)))
            {
                return false;
            }
            symbol = statement.Symbol;
            initializer = statement.Expression;
            return true;
        }

        private bool IsGeneratedTransportSymbol(SourceSymbolIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Symbols.Count)
                return false;
            var symbol = _source[index];
            return symbol.Kind is
                    Avm1SourceSymbolKind.Local or
                    Avm1SourceSymbolKind.Temporary &&
                (symbol.Flags.HasFlag(
                        Avm1SourceSymbolFlags.CompilerGenerated) ||
                    IsCanonicalRecoveryLocalName(symbol));
        }

        private bool IsGeneratedTemporarySymbol(SourceSymbolIndex index) =>
            index.IsValid &&
            index.Value < _source.Symbols.Count &&
            _source[index].Kind is Avm1SourceSymbolKind.Temporary &&
            _source[index].Flags.HasFlag(
                Avm1SourceSymbolFlags.CompilerGenerated);

        private bool IsAuthoredLocalSymbol(SourceSymbolIndex index) =>
            index.IsValid &&
            index.Value < _source.Symbols.Count &&
            _source[index].Kind is Avm1SourceSymbolKind.Local &&
            !_source[index].Flags.HasFlag(
                Avm1SourceSymbolFlags.CompilerGenerated) &&
            !IsCanonicalRecoveryLocalName(_source[index]);

        private bool IsCanonicalRecoveryLocalName(Avm1SourceSymbol symbol)
        {
            if (!symbol.Name.IsValid || symbol.Name.Value >= _source.Strings.Count)
                return false;

            var name = _source[symbol.Name];
            if (name.Length > 1 && name[0] == 'v')
            {
                var isValueTemporary = true;
                for (var i = 1; i < name.Length; i++)
                {
                    if (name[i] is >= '0' and <= '9')
                        continue;

                    isValueTemporary = false;
                    break;
                }
                if (isValueTemporary)
                    return true;
            }

            const string prefix = "_loc";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                name.Length <= prefix.Length + 1)
            {
                return false;
            }

            var index = prefix.Length;
            while (index < name.Length && name[index] is >= '0' and <= '9')
                index++;
            if (index == prefix.Length || index >= name.Length || name[index] != '_')
                return false;
            index++;
            if (index == name.Length)
                return true;
            if (name[index] != 'v' || ++index == name.Length)
                return false;
            while (index < name.Length)
            {
                if (name[index] is < '0' or > '9')
                    return false;
                index++;
            }
            return true;
        }

        private bool IsRegisterTransportSymbol(SourceSymbolIndex index)
        {
            if (IsGeneratedTransportSymbol(index))
                return true;
            if (!index.IsValid || index.Value >= _source.Symbols.Count)
                return false;
            var symbol = _source[index];
            return symbol.Kind is Avm1SourceSymbolKind.Local &&
                symbol.Name.IsValid &&
                symbol.Name.Value < _source.Strings.Count &&
                _source[symbol.Name].StartsWith("__reg", StringComparison.Ordinal);
        }

        private bool TryGetLogicalTransport(
            Avm1SourceStatement conditional,
            SourceSymbolIndex symbol,
            out Avm1SourceOperator @operator,
            out SourceExpressionIndex right)
        {
            @operator = Avm1SourceOperator.None;
            right = SourceExpressionIndex.Invalid;
            if (conditional.Children.Count == 1 &&
                TryGetSingleAssignment(
                    _source.GetChild(conditional, 0),
                    symbol,
                    out right))
            {
                if (IsSymbolReference(conditional.Expression, symbol))
                {
                    @operator = Avm1SourceOperator.LogicalAnd;
                    return true;
                }
                if (IsLogicalNotOf(conditional.Expression, symbol))
                {
                    @operator = Avm1SourceOperator.LogicalOr;
                    return true;
                }
            }
            if (conditional.Children.Count == 2 &&
                IsSymbolReference(conditional.Expression, symbol) &&
                IsEmptyStatement(_source.GetChild(conditional, 0)) &&
                TryGetSingleAssignment(
                    _source.GetChild(conditional, 1),
                    symbol,
                    out right))
            {
                @operator = Avm1SourceOperator.LogicalOr;
                return true;
            }
            return false;
        }

        private bool TryGetConditionalTransport(
            Avm1SourceStatement conditional,
            SourceSymbolIndex symbol,
            out SourceExpressionIndex condition,
            out SourceExpressionIndex whenTrue,
            out SourceExpressionIndex whenFalse)
        {
            condition = conditional.Expression;
            whenTrue = SourceExpressionIndex.Invalid;
            whenFalse = SourceExpressionIndex.Invalid;
            return conditional.Children.Count == 2 &&
                TryGetSingleAssignment(
                    _source.GetChild(conditional, 0),
                    symbol,
                    out whenTrue) &&
                TryGetSingleAssignment(
                    _source.GetChild(conditional, 1),
                    symbol,
                    out whenFalse);
        }

        private bool TryGetBranchPhi(
            Avm1SourceStatement conditional,
            out SourceSymbolIndex symbol,
            out SourceExpressionIndex whenTrue,
            out SourceExpressionIndex whenFalse)
        {
            symbol = SourceSymbolIndex.Invalid;
            whenTrue = SourceExpressionIndex.Invalid;
            whenFalse = SourceExpressionIndex.Invalid;
            if (conditional.Children.Count != 2 ||
                !TryGetBranchDefinition(
                    _source.GetChild(conditional, 0),
                    out var trueSymbol,
                    out whenTrue,
                    out var trueDeclares) ||
                !TryGetBranchDefinition(
                    _source.GetChild(conditional, 1),
                    out var falseSymbol,
                    out whenFalse,
                    out var falseDeclares) ||
                trueSymbol != falseSymbol ||
                trueDeclares == falseDeclares ||
                !IsGeneratedTransportSymbol(trueSymbol))
            {
                return false;
            }

            symbol = trueSymbol;
            return true;
        }

        private bool TryGetBranchDefinition(
            SourceStatementIndex index,
            out SourceSymbolIndex symbol,
            out SourceExpressionIndex value,
            out bool declares)
        {
            symbol = SourceSymbolIndex.Invalid;
            value = SourceExpressionIndex.Invalid;
            declares = false;
            if (!TryUnwrapSingleStatement(index, out var statement))
                return false;
            if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                statement.Symbol.IsValid &&
                statement.Expression.IsValid)
            {
                symbol = statement.Symbol;
                value = statement.Expression;
                declares = true;
                return true;
            }
            if (statement.Kind is not Avm1SourceStatementKind.Expression ||
                !statement.Expression.IsValid ||
                !TryGetExpression(
                    statement.Expression,
                    ".transport.branchAssignment",
                    depth: 0,
                    out var assignment) ||
                assignment.Kind is not Avm1SourceExpressionKind.Assignment ||
                assignment.Operator is not Avm1SourceOperator.Assign ||
                assignment.Children.Count != 2)
            {
                return false;
            }

            var target = _source.GetChild(assignment, 0);
            if (!TryGetExpression(
                    target,
                    ".transport.branchTarget",
                    depth: 0,
                    out var targetExpression) ||
                targetExpression.Kind is not Avm1SourceExpressionKind.SymbolReference)
            {
                return false;
            }
            symbol = targetExpression.Symbol;
            value = _source.GetChild(assignment, 1);
            return true;
        }

        private bool TryGetSingleAssignment(
            SourceStatementIndex index,
            SourceSymbolIndex symbol,
            out SourceExpressionIndex value)
        {
            value = SourceExpressionIndex.Invalid;
            if (!TryUnwrapSingleStatement(index, out var statement))
                return false;
            if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                statement.Symbol == symbol &&
                statement.Expression.IsValid)
            {
                value = statement.Expression;
                return true;
            }
            if (statement.Kind is not Avm1SourceStatementKind.Expression ||
                !statement.Expression.IsValid ||
                !TryGetExpression(
                    statement.Expression,
                    ".transport.assignment",
                    depth: 0,
                    out var assignment) ||
                assignment.Kind is not Avm1SourceExpressionKind.Assignment ||
                assignment.Operator is not Avm1SourceOperator.Assign ||
                assignment.Children.Count != 2 ||
                !IsSymbolReference(_source.GetChild(assignment, 0), symbol))
            {
                return false;
            }
            value = _source.GetChild(assignment, 1);
            return true;
        }

        private bool TryGetAssignmentStatement(
            SourceStatementIndex index,
            out SourceExpressionIndex target,
            out SourceExpressionIndex value)
        {
            target = SourceExpressionIndex.Invalid;
            value = SourceExpressionIndex.Invalid;
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            var statement = _source[index];
            if (statement.Kind is not Avm1SourceStatementKind.Expression ||
                !statement.Expression.IsValid ||
                statement.Expression.Value >= _source.Expressions.Count)
            {
                return false;
            }
            var assignment = _source[statement.Expression];
            if (assignment.Kind is not Avm1SourceExpressionKind.Assignment ||
                assignment.Operator is not Avm1SourceOperator.Assign ||
                assignment.Children.Count != 2)
            {
                return false;
            }
            target = _source.GetChild(assignment, 0);
            value = _source.GetChild(assignment, 1);
            return true;
        }

        private bool TryGetReturnExpression(
            SourceStatementIndex index,
            out SourceExpressionIndex expression)
        {
            expression = SourceExpressionIndex.Invalid;
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            var statement = _source[index];
            if (statement.Kind is not Avm1SourceStatementKind.Return ||
                !statement.Expression.IsValid)
            {
                return false;
            }
            expression = statement.Expression;
            return true;
        }

        private bool TryUnwrapSingleStatement(
            SourceStatementIndex index,
            out Avm1SourceStatement statement)
        {
            statement = default;
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            statement = _source[index];
            while (statement.Kind is Avm1SourceStatementKind.Block)
            {
                if (statement.Children.Count != 1)
                    return false;
                var child = _source.GetChild(statement, 0);
                if (!child.IsValid || child.Value >= _source.Statements.Count)
                    return false;
                statement = _source[child];
            }
            return true;
        }

        private bool IsEmptyStatement(SourceStatementIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            var statement = _source[index];
            return statement.Kind is Avm1SourceStatementKind.Block &&
                statement.Children.Count == 0;
        }

        private bool IsSymbolReference(
            SourceExpressionIndex index,
            SourceSymbolIndex symbol) =>
            index.IsValid &&
            index.Value < _source.Expressions.Count &&
            _source[index] is
            {
                Kind: Avm1SourceExpressionKind.SymbolReference,
                Symbol: var candidate
            } &&
            candidate == symbol;

        private bool TryGetSymbolReference(
            SourceExpressionIndex index,
            out SourceSymbolIndex symbol)
        {
            symbol = SourceSymbolIndex.Invalid;
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            if (expression.Kind is not Avm1SourceExpressionKind.SymbolReference ||
                !expression.Symbol.IsValid)
            {
                return false;
            }
            symbol = expression.Symbol;
            return true;
        }

        private bool IsLogicalNotOf(
            SourceExpressionIndex index,
            SourceSymbolIndex symbol)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            return expression.Kind is Avm1SourceExpressionKind.Unary &&
                expression.Operator is Avm1SourceOperator.LogicalNot &&
                expression.Children.Count == 1 &&
                IsSymbolReference(_source.GetChild(expression, 0), symbol);
        }

        private bool TryGetLogicalNotOperand(
            SourceExpressionIndex index,
            out SourceExpressionIndex operand)
        {
            operand = SourceExpressionIndex.Invalid;
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            if (expression.Kind is not Avm1SourceExpressionKind.Unary ||
                expression.Operator is not Avm1SourceOperator.LogicalNot ||
                expression.Children.Count != 1)
            {
                return false;
            }
            operand = _source.GetChild(expression, 0);
            return true;
        }

        private bool IsAssignmentTarget(SourceExpressionIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            return expression.Kind is
                    Avm1SourceExpressionKind.SymbolReference or
                    Avm1SourceExpressionKind.DynamicName or
                    Avm1SourceExpressionKind.QualifiedName ||
                expression.Kind is Avm1SourceExpressionKind.MemberAccess &&
                expression.Children.Count == 2;
        }

        private bool IsStableAssignmentTarget(SourceExpressionIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            if (expression.Kind is
                Avm1SourceExpressionKind.SymbolReference or
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.QualifiedName)
            {
                return true;
            }

            return expression.Kind is Avm1SourceExpressionKind.MemberAccess &&
                expression.Children.Count == 2 &&
                IsPureStable(_source.GetChild(expression, 0)) &&
                IsStableMemberKey(expression);
        }

        private bool IsStableMemberKey(Avm1SourceExpression member)
        {
            var key = _source.GetChild(member, 1);
            if (IsStableComputedMemberKey(key, depth: 0))
                return true;

            if (member.Flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember))
                return false;

            return key.IsValid &&
                key.Value < _source.Expressions.Count &&
                _source[key].Kind is
                    Avm1SourceExpressionKind.Literal or
                    Avm1SourceExpressionKind.SymbolReference or
                    Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.QualifiedName;
        }

        private bool IsStableComputedMemberKey(
            SourceExpressionIndex index,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }
            if (IsPureStable(index))
                return true;

            var expression = _source[index];
            return expression.Kind is Avm1SourceExpressionKind.Binary &&
                expression.Operator is Avm1SourceOperator.Add &&
                expression.Children.Count == 2 &&
                IsStableComputedMemberKey(
                    _source.GetChild(expression, 0),
                    depth + 1) &&
                IsStableComputedMemberKey(
                    _source.GetChild(expression, 1),
                    depth + 1);
        }

        private bool IsPureStable(SourceExpressionIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;
            var expression = _source[index];
            return expression.Kind is
                Avm1SourceExpressionKind.Literal or
                Avm1SourceExpressionKind.SymbolReference;
        }

        private bool IsLinearConsumer(SourceStatementIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return false;
            var statement = _source[index];
            return statement.Kind is
                Avm1SourceStatementKind.Expression or
                Avm1SourceStatementKind.If or
                Avm1SourceStatementKind.Return or
                Avm1SourceStatementKind.VariableDeclaration;
        }

        private bool CanMoveTransportIntoConsumer(
            SourceSymbolIndex symbol,
            SourceStatementIndex consumer)
        {
            var symbols = new HashSet<SourceSymbolIndex> { symbol };
            var order = new List<SourceSymbolIndex>();
            var state = new ConsumerOrderState(symbols, 1, order);
            CollectConsumerOrder(consumer, ref state);
            return !state.HasBarrier && order.Count == 1 && order[0] == symbol;
        }

        private bool CanMoveAdobeSingleArgumentTransportIntoConsumer(
            SourceSymbolIndex symbol,
            SourceStatementIndex consumer)
        {
            if (!consumer.IsValid ||
                consumer.Value >= _source.Statements.Count ||
                IsCapturedSymbol(symbol) ||
                ContainsDirectEval(consumer))
            {
                return false;
            }

            var statement = _source[consumer];
            if (!statement.Expression.IsValid ||
                statement.Expression.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var expression = _source[statement.Expression];
            if (expression.Kind is not Avm1SourceExpressionKind.Call ||
                expression.Children.Count != 2 ||
                !IsSymbolReference(_source.GetChild(expression, 1), symbol))
            {
                return false;
            }

            var target = _source.GetChild(expression, 0);
            return target.IsValid &&
                target.Value < _source.Expressions.Count &&
                _source[target].Kind is Avm1SourceExpressionKind.MemberAccess;
        }

        private bool CanMoveAdobeInvocationArgumentTransportIntoConsumer(
            SourceSymbolIndex symbol,
            SourceStatementIndex consumer)
        {
            if (!consumer.IsValid ||
                consumer.Value >= _source.Statements.Count ||
                IsCapturedSymbol(symbol) ||
                ContainsDirectEval(consumer))
            {
                return false;
            }

            var statement = _source[consumer];
            return statement.Expression.IsValid &&
                IsAdobeInvocationArgumentTransportConsumer(
                    statement.Expression,
                    symbol,
                    depth: 0);
        }

        private bool CanMoveAdobeArrayObjectValueTransportIntoConsumer(
            SourceSymbolIndex symbol,
            SourceStatementIndex consumer)
        {
            if (!consumer.IsValid ||
                consumer.Value >= _source.Statements.Count ||
                IsCapturedSymbol(symbol) ||
                ContainsDirectEval(consumer) ||
                !TryGetAssignmentStatement(consumer, out var target, out var value) ||
                !IsStableTransportAssignmentTarget(target) ||
                !value.IsValid ||
                value.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var array = _source[value];
            if (array.Kind is not Avm1SourceExpressionKind.ArrayLiteral ||
                array.Children.Count == 0)
            {
                return false;
            }

            // Adobe-compatible AVM1 evaluates array elements from right to left.
            // A transport evaluated immediately before the assignment can only be
            // restored without crossing another element at the rightmost position.
            var rightmost = _source.GetChild(array, array.Children.Count - 1);
            if (!rightmost.IsValid || rightmost.Value >= _source.Expressions.Count)
                return false;

            var objectLiteral = _source[rightmost];
            if (objectLiteral.Kind is not Avm1SourceExpressionKind.ObjectLiteral ||
                objectLiteral.Children.Count % 2 != 0)
            {
                return false;
            }

            for (var i = 0; i < objectLiteral.Children.Count; i++)
            {
                var child = _source.GetChild(objectLiteral, i);
                if (IsSymbolReference(child, symbol))
                    return true;
                if (!IsRelocatablePureExpression(child))
                    return false;
            }
            return false;
        }

        private bool IsStableTransportAssignmentTarget(SourceExpressionIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
                return false;

            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference)
                return true;
            if (expression.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                expression.Children.Count != 2)
            {
                return false;
            }

            return IsPureStable(_source.GetChild(expression, 0)) &&
                IsLiteralExpression(_source.GetChild(expression, 1));
        }

        private bool IsAdobeInvocationArgumentTransportConsumer(
            SourceExpressionIndex index,
            SourceSymbolIndex symbol,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.Assignment &&
                expression.Operator is Avm1SourceOperator.Assign &&
                expression.Children.Count == 2)
            {
                var targetUses = new SymbolUses();
                CountExpressionUses(
                    _source.GetChild(expression, 0),
                    symbol,
                    ref targetUses,
                    assignmentTarget: false,
                    depth: 0);
                return targetUses is
                        { Declarations: 0, Reads: 0, Writes: 0 } &&
                    IsAdobeInvocationArgumentTransportConsumer(
                        _source.GetChild(expression, 1),
                        symbol,
                        depth + 1);
            }

            if (expression.Kind is not (
                    Avm1SourceExpressionKind.Call or
                    Avm1SourceExpressionKind.New) ||
                expression.Children.Count < 2)
            {
                return false;
            }

            var target = new SymbolUses();
            CountExpressionUses(
                _source.GetChild(expression, 0),
                symbol,
                ref target,
                assignmentTarget: false,
                depth: 0);
            if (target is not { Declarations: 0, Reads: 0, Writes: 0 })
                return false;

            var arguments = new SymbolUses();
            for (var i = 1; i < expression.Children.Count; i++)
            {
                CountExpressionUses(
                    _source.GetChild(expression, i),
                    symbol,
                    ref arguments,
                    assignmentTarget: false,
                    depth: 0);
            }
            return arguments is { Declarations: 0, Reads: 1, Writes: 0 };
        }

        private bool ContainsAnySymbol(
            SourceExpressionIndex expression,
            HashSet<SourceSymbolIndex> symbols)
        {
            var order = new List<SourceSymbolIndex>();
            var state = new ConsumerOrderState(symbols, symbols.Count, order);
            CollectExpressionOrder(expression, ref state, assignmentTarget: false);
            return order.Count != 0;
        }

        private bool IsLiteralExpression(SourceExpressionIndex index) =>
            index.IsValid &&
            index.Value < _source.Expressions.Count &&
            _source[index].Kind is Avm1SourceExpressionKind.Literal;

        private bool IsRelocatablePureExpression(
            SourceExpressionIndex index,
            int depth = 0)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var expression = _source[index];
            if (expression.Kind is
                Avm1SourceExpressionKind.Literal or
                Avm1SourceExpressionKind.SymbolReference)
            {
                return true;
            }
            if (expression.Kind is not Avm1SourceExpressionKind.Conditional ||
                expression.Children.Count != 3)
            {
                return false;
            }

            for (var i = 0; i < expression.Children.Count; i++)
            {
                if (!IsRelocatablePureExpression(
                        _source.GetChild(expression, i),
                        depth + 1))
                {
                    return false;
                }
            }
            return true;
        }

        private bool CanRelocateSingleUseAuthoredInitializer(
            SourceExpressionIndex index,
            int depth = 0)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }

            if (IsRelocatablePureExpression(index, depth))
                return true;

            var expression = _source[index];
            if (expression.Kind is
                Avm1SourceExpressionKind.New or
                Avm1SourceExpressionKind.FunctionLiteral)
            {
                return true;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Unary &&
                expression.Operator is
                    Avm1SourceOperator.PrefixIncrement or
                    Avm1SourceOperator.PrefixDecrement or
                    Avm1SourceOperator.PostfixIncrement or
                    Avm1SourceOperator.PostfixDecrement &&
                expression.Children.Count == 1)
            {
                return TryGetSymbolReference(
                    _source.GetChild(expression, 0),
                    out _);
            }
            if (expression.Kind is not (
                    Avm1SourceExpressionKind.Binary or
                    Avm1SourceExpressionKind.Unary) ||
                expression.Children.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < expression.Children.Count; i++)
            {
                if (!CanRelocateStableOperand(
                        _source.GetChild(expression, i),
                        depth + 1))
                {
                    return false;
                }
            }
            return true;
        }

        private bool CanRelocateStableOperand(
            SourceExpressionIndex index,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var expression = _source[index];
            if (expression.Kind is
                Avm1SourceExpressionKind.Literal or
                Avm1SourceExpressionKind.SymbolReference)
            {
                return true;
            }
            if (expression.Kind is not (
                    Avm1SourceExpressionKind.Binary or
                    Avm1SourceExpressionKind.Unary or
                    Avm1SourceExpressionKind.Conditional) ||
                expression.Children.Count == 0 ||
                expression.Operator is
                    Avm1SourceOperator.PrefixIncrement or
                    Avm1SourceOperator.PrefixDecrement or
                    Avm1SourceOperator.PostfixIncrement or
                    Avm1SourceOperator.PostfixDecrement)
            {
                return false;
            }

            for (var i = 0; i < expression.Children.Count; i++)
            {
                if (!CanRelocateStableOperand(
                        _source.GetChild(expression, i),
                        depth + 1))
                {
                    return false;
                }
            }
            return true;
        }

        private bool HasStaticIdentifierMemberName(
            Avm1SourceExpression expression)
        {
            if (expression.Children.Count != 2)
                return false;

            var key = _source.GetChild(expression, 1);
            if (!key.IsValid ||
                key.Value >= _source.Expressions.Count ||
                _source[key] is not
                {
                    Kind: Avm1SourceExpressionKind.Literal,
                    Literal.IsValid: true
                } keyExpression ||
                keyExpression.Literal.Value >= _source.Literals.Count)
            {
                return false;
            }

            var literal = _source[keyExpression.Literal];
            if (literal.Kind is not Avm1SourceLiteralKind.String ||
                !literal.StringValue.IsValid ||
                literal.StringValue.Value >= _source.Strings.Count)
            {
                return false;
            }

            return IsIdentifier(_source[literal.StringValue]);
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0 ||
                value[0] != '_' &&
                value[0] != '$' &&
                !char.IsAsciiLetter(value[0]))
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] != '_' &&
                    value[i] != '$' &&
                    !char.IsAsciiLetterOrDigit(value[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsDirectConditionConsumer(
            SourceSymbolIndex symbol,
            SourceStatementIndex consumer)
        {
            if (!consumer.IsValid || consumer.Value >= _source.Statements.Count)
                return false;

            var statement = _source[consumer];
            return statement.Kind is Avm1SourceStatementKind.If &&
                statement.Expression.IsValid &&
                CanMoveTransportIntoConsumer(symbol, consumer);
        }

        private bool IsStableThisMemberAssignmentConsumer(
            SourceStatementIndex consumer)
        {
            if (!TryGetAssignmentStatement(
                    consumer,
                    out var target,
                    out _) ||
                !target.IsValid ||
                target.Value >= _source.Expressions.Count)
            {
                return false;
            }

            var member = _source[target];
            if (member.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                member.Children.Count != 2 ||
                member.Flags.HasFlag(
                    Avm1SourceExpressionFlags.ComputedMember))
            {
                return false;
            }

            var receiver = _source.GetChild(member, 0);
            if (!TryGetSymbolReference(receiver, out var receiverSymbol) ||
                receiverSymbol.Value >= _source.Symbols.Count ||
                _source[receiverSymbol].Kind is not Avm1SourceSymbolKind.This)
            {
                return false;
            }

            var key = _source.GetChild(member, 1);
            return key.IsValid &&
                key.Value < _source.Expressions.Count &&
                _source[key].Kind is Avm1SourceExpressionKind.Literal;
        }

        private bool IsCapturedSymbol(SourceSymbolIndex symbol)
        {
            for (var functionIndex = 0;
                functionIndex < _source.Functions.Count;
                functionIndex++)
            {
                var function = _source.Functions[functionIndex];
                for (var captureIndex = 0;
                    captureIndex < function.Captures.Count;
                    captureIndex++)
                {
                    if (_source.GetCapture(function, captureIndex) == symbol)
                        return true;
                }
            }
            return false;
        }

        private bool ContainsDirectEval(SourceStatementIndex index)
            => ContainsDirectEval(index, depth: 0);

        private bool ContainsDirectEvalInConsumerExpression(
            SourceStatementIndex index)
        {
            if (!index.IsValid || index.Value >= _source.Statements.Count)
                return true;

            var statement = _source[index];
            return statement.Expression.IsValid &&
                ContainsDirectEval(statement.Expression, depth: 0);
        }

        private bool ContainsDirectEval(SourceStatementIndex index, int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Statements.Count)
            {
                return true;
            }
            var statement = _source[index];
            if (statement.Expression.IsValid &&
                ContainsDirectEval(statement.Expression, depth + 1) ||
                statement.SecondaryExpression.IsValid &&
                ContainsDirectEval(statement.SecondaryExpression, depth + 1))
            {
                return true;
            }
            for (var i = 0; i < statement.Initializers.Count; i++)
            {
                if (ContainsDirectEval(
                    _source.GetInitializer(statement, i),
                    depth + 1))
                {
                    return true;
                }
            }
            for (var i = 0; i < statement.Expressions.Count; i++)
            {
                if (ContainsDirectEval(
                    _source.GetExpression(statement, i),
                    depth + 1))
                {
                    return true;
                }
            }
            for (var i = 0; i < statement.Children.Count; i++)
            {
                if (ContainsDirectEval(_source.GetChild(statement, i), depth + 1))
                    return true;
            }
            return false;
        }

        private bool ContainsDirectEval(SourceExpressionIndex index, int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return true;
            }

            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.ComputedDynamicName ||
                Avm1SourceEvalAnalysis.IsDirectEvalCall(_source, expression))
            {
                return true;
            }
            for (var i = 0; i < expression.Children.Count; i++)
            {
                if (ContainsDirectEval(_source.GetChild(expression, i), depth + 1))
                    return true;
            }
            return false;
        }

        private void CollectConsumerOrder(
            SourceStatementIndex index,
            ref ConsumerOrderState state)
        {
            if (!index.IsValid || index.Value >= _source.Statements.Count)
            {
                state.HasBarrier = true;
                return;
            }
            var statement = _source[index];
            if (statement.Expression.IsValid)
                CollectExpressionOrder(statement.Expression, ref state, assignmentTarget: false);
        }

        private void CollectExpressionOrder(
            SourceExpressionIndex index,
            ref ConsumerOrderState state,
            bool assignmentTarget)
        {
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
            {
                state.HasBarrier = true;
                return;
            }
            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference)
            {
                if (!assignmentTarget && state.Symbols.Contains(expression.Symbol))
                    state.ReadOrder.Add(expression.Symbol);
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Literal)
                return;
            if (expression.Kind is Avm1SourceExpressionKind.Assignment &&
                expression.Children.Count == 2)
            {
                var target = _source.GetChild(expression, 0);
                CollectAssignmentTargetOrder(target, ref state);
                CollectExpressionOrder(
                    _source.GetChild(expression, 1),
                    ref state,
                    assignmentTarget: false);
                MarkBarrier(ref state);
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Delete &&
                expression.Children.Count == 1)
            {
                CollectAssignmentTargetOrder(
                    _source.GetChild(expression, 0),
                    ref state);
                MarkBarrier(ref state);
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Call or
                Avm1SourceExpressionKind.New)
            {
                for (var i = 0; i < expression.Children.Count; i++)
                {
                    var child = _source.GetChild(expression, i);
                    if (i == 0)
                    {
                        CollectInvocationTargetOrder(child, ref state);
                        continue;
                    }
                    CollectExpressionOrder(child, ref state, assignmentTarget: false);
                }
                MarkBarrier(ref state);
                return;
            }

            for (var i = 0; i < expression.Children.Count; i++)
            {
                CollectExpressionOrder(
                    _source.GetChild(expression, i),
                    ref state,
                    assignmentTarget: false);
            }
            if (expression.Kind is
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.ComputedDynamicName or
                Avm1SourceExpressionKind.QualifiedName or
                Avm1SourceExpressionKind.MemberAccess or
                Avm1SourceExpressionKind.Delete or
                Avm1SourceExpressionKind.Unary or
                Avm1SourceExpressionKind.Binary or
                Avm1SourceExpressionKind.ArrayLiteral or
                Avm1SourceExpressionKind.ObjectLiteral)
            {
                MarkBarrier(ref state);
            }
        }

        private void CollectInvocationTargetOrder(
            SourceExpressionIndex target,
            ref ConsumerOrderState state)
        {
            if (!target.IsValid || target.Value >= _source.Expressions.Count)
            {
                state.HasBarrier = true;
                return;
            }
            var expression = _source[target];
            if (expression.Kind is
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.QualifiedName)
            {
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.MemberAccess &&
                expression.Children.Count == 2)
            {
                CollectExpressionOrder(
                    _source.GetChild(expression, 0),
                    ref state,
                    assignmentTarget: false);
                CollectExpressionOrder(
                    _source.GetChild(expression, 1),
                    ref state,
                    assignmentTarget: false);
                return;
            }
            CollectExpressionOrder(target, ref state, assignmentTarget: false);
        }

        private void CollectAssignmentTargetOrder(
            SourceExpressionIndex target,
            ref ConsumerOrderState state)
        {
            if (!target.IsValid || target.Value >= _source.Expressions.Count)
            {
                state.HasBarrier = true;
                return;
            }
            var expression = _source[target];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference or
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.QualifiedName)
            {
                return;
            }
            for (var i = 0; i < expression.Children.Count; i++)
            {
                CollectExpressionOrder(
                    _source.GetChild(expression, i),
                    ref state,
                    assignmentTarget: false);
            }
        }

        private static void MarkBarrier(ref ConsumerOrderState state)
        {
            if (state.ReadOrder.Count < state.RequiredReadCount)
                state.HasBarrier = true;
        }

        private SymbolUses CountUses(
            SourceSymbolIndex symbol,
            SourceStatementIndex[] statements,
            int start)
            => CountUses(symbol, statements, start, statements.Length - start);

        private SymbolUses CountAllUses(SourceSymbolIndex symbol)
        {
            var uses = new SymbolUses();
            CountStatementUses(_method.Body, symbol, ref uses, depth: 0);
            foreach (var function in _source.Functions)
                CountStatementUses(function.Body, symbol, ref uses, depth: 0);
            return uses;
        }

        private SymbolUses CountUses(
            SourceSymbolIndex symbol,
            SourceStatementIndex[] statements,
            int start,
            int count)
        {
            var uses = new SymbolUses();
            var end = Math.Min(statements.Length, checked(start + count));
            for (var i = start; i < end; i++)
                CountStatementUses(statements[i], symbol, ref uses, depth: 0);
            return uses;
        }

        private SymbolUses CountTransportPrefixUses(
            SourceSymbolIndex symbol,
            SourceStatementIndex[] statements,
            int start,
            int consumerOffset)
        {
            var uses = CountUses(symbol, statements, start, consumerOffset);
            var consumerIndex = start + consumerOffset;
            if (consumerIndex >= statements.Length)
                return uses;

            var consumer = _source[statements[consumerIndex]];
            if (consumer.Expression.IsValid)
            {
                CountExpressionUses(
                    consumer.Expression,
                    symbol,
                    ref uses,
                    assignmentTarget: false,
                    depth: 0);
            }
            return uses;
        }

        private void CountStatementUses(
            SourceStatementIndex index,
            SourceSymbolIndex symbol,
            ref SymbolUses uses,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Statements.Count)
            {
                return;
            }
            var statement = _source[index];
            if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                statement.Symbol == symbol)
            {
                uses.Declarations++;
            }
            if (statement.Expression.IsValid)
                CountExpressionUses(statement.Expression, symbol, ref uses, false, depth + 1);
            if (statement.SecondaryExpression.IsValid)
            {
                CountExpressionUses(
                    statement.SecondaryExpression,
                    symbol,
                    ref uses,
                    false,
                    depth + 1);
            }
            for (var i = 0; i < statement.Children.Count; i++)
            {
                CountStatementUses(
                    _source.GetChild(statement, i),
                    symbol,
                    ref uses,
                    depth + 1);
            }
            for (var i = 0; i < statement.Initializers.Count; i++)
            {
                CountStatementUses(
                    _source.GetInitializer(statement, i),
                    symbol,
                    ref uses,
                    depth + 1);
            }
            for (var i = 0; i < statement.Expressions.Count; i++)
            {
                CountExpressionUses(
                    _source.GetExpression(statement, i),
                    symbol,
                    ref uses,
                    false,
                    depth + 1);
            }
        }

        private void CountExpressionUses(
            SourceExpressionIndex index,
            SourceSymbolIndex symbol,
            ref SymbolUses uses,
            bool assignmentTarget,
            int depth)
        {
            if (depth > MaximumDepth ||
                !index.IsValid ||
                index.Value >= _source.Expressions.Count)
            {
                return;
            }
            var expression = _source[index];
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                expression.Symbol == symbol)
            {
                if (assignmentTarget)
                    uses.Writes++;
                else
                    uses.Reads++;
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Assignment &&
                expression.Children.Count == 2)
            {
                var target = _source.GetChild(expression, 0);
                CountExpressionUses(target, symbol, ref uses, true, depth + 1);
                if (expression.Operator is not Avm1SourceOperator.Assign &&
                    IsSymbolReference(target, symbol))
                {
                    uses.Reads++;
                }
                CountExpressionUses(
                    _source.GetChild(expression, 1),
                    symbol,
                    ref uses,
                    false,
                    depth + 1);
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Delete &&
                expression.Children.Count == 1)
            {
                CountExpressionUses(
                    _source.GetChild(expression, 0),
                    symbol,
                    ref uses,
                    assignmentTarget: true,
                    depth + 1);
                return;
            }
            if (expression.Kind is Avm1SourceExpressionKind.Unary &&
                expression.Operator is
                    Avm1SourceOperator.PrefixIncrement or
                    Avm1SourceOperator.PrefixDecrement or
                    Avm1SourceOperator.PostfixIncrement or
                    Avm1SourceOperator.PostfixDecrement &&
                expression.Children.Count == 1)
            {
                var target = _source.GetChild(expression, 0);
                CountExpressionUses(target, symbol, ref uses, true, depth + 1);
                if (IsSymbolReference(target, symbol))
                    uses.Reads++;
                return;
            }
            for (var i = 0; i < expression.Children.Count; i++)
            {
                CountExpressionUses(
                    _source.GetChild(expression, i),
                    symbol,
                    ref uses,
                    false,
                    depth + 1);
            }
        }

        private bool ExpressionsEqual(
            SourceExpressionIndex leftIndex,
            SourceExpressionIndex rightIndex,
            int depth)
        {
            if (leftIndex == rightIndex)
                return true;
            if (depth > MaximumDepth ||
                !leftIndex.IsValid ||
                leftIndex.Value >= _source.Expressions.Count ||
                !rightIndex.IsValid ||
                rightIndex.Value >= _source.Expressions.Count)
            {
                return false;
            }
            var left = _source[leftIndex];
            var right = _source[rightIndex];
            if (left.Kind != right.Kind ||
                left.Operator != right.Operator ||
                left.Symbol != right.Symbol ||
                !NamesEqual(left.Name, right.Name) ||
                !LiteralsEqual(left.Literal, right.Literal) ||
                left.Children.Count != right.Children.Count)
            {
                return false;
            }
            for (var i = 0; i < left.Children.Count; i++)
            {
                if (!ExpressionsEqual(
                    _source.GetChild(left, i),
                    _source.GetChild(right, i),
                    depth + 1))
                {
                    return false;
                }
            }
            return true;
        }

        private bool NamesEqual(SourceStringIndex left, SourceStringIndex right) =>
            left.IsValid == right.IsValid &&
            (!left.IsValid ||
                left.Value < _source.Strings.Count &&
                right.Value < _source.Strings.Count &&
                string.Equals(_source[left], _source[right], StringComparison.Ordinal));

        private bool LiteralsEqual(SourceLiteralIndex left, SourceLiteralIndex right)
        {
            if (left.IsValid != right.IsValid)
                return false;
            if (!left.IsValid)
                return true;
            if (left.Value >= _source.Literals.Count || right.Value >= _source.Literals.Count)
                return false;
            var leftLiteral = _source[left];
            var rightLiteral = _source[right];
            if (leftLiteral.Kind != rightLiteral.Kind)
                return false;
            return leftLiteral.Kind switch
            {
                Avm1SourceLiteralKind.Boolean =>
                    leftLiteral.BooleanValue == rightLiteral.BooleanValue,
                Avm1SourceLiteralKind.Integer =>
                    leftLiteral.IntegerValue == rightLiteral.IntegerValue,
                Avm1SourceLiteralKind.Number =>
                    BitConverter.DoubleToInt64Bits(leftLiteral.NumberValue) ==
                    BitConverter.DoubleToInt64Bits(rightLiteral.NumberValue),
                Avm1SourceLiteralKind.String =>
                    NamesEqual(leftLiteral.StringValue, rightLiteral.StringValue),
                _ => true
            };
        }

        private bool TryGetStatement(
            SourceStatementIndex index,
            string path,
            int depth,
            out Avm1SourceStatement statement)
        {
            statement = default;
            if (depth > MaximumDepth)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR statement depth exceeds the limit.");
                return false;
            }
            if (!index.IsValid || index.Value >= _source.Statements.Count)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR statement handle is invalid.");
                return false;
            }
            statement = _source[index];
            return true;
        }

        private bool TryGetExpression(
            SourceExpressionIndex index,
            string path,
            int depth,
            out Avm1SourceExpression expression)
        {
            expression = default;
            if (depth > MaximumDepth)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR expression depth exceeds the limit.");
                return false;
            }
            if (!index.IsValid || index.Value >= _source.Expressions.Count)
            {
                AddIncomplete(
                    "AVM1EQ003",
                    path,
                    $"Source HIR expression handle {index.Value} is invalid for " +
                    $"an arena with {_source.Expressions.Count} expressions.");
                return false;
            }
            expression = _source[index];
            return true;
        }

        private bool TryGetStatementList(
            SourceStatementList list,
            string path,
            out SourceStatementIndex[] result)
        {
            if (list.Count < 0 ||
                list.Start < 0 ||
                list.Start > _source.StatementChildren.Count - list.Count)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR statement list is invalid.");
                result = [];
                return false;
            }
            result = new SourceStatementIndex[list.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = _source.StatementChildren[list.Start + i];
            return true;
        }

        private bool TryGetExpressionList(
            SourceExpressionList list,
            string path,
            out SourceExpressionIndex[] result)
        {
            if (list.Count < 0 ||
                list.Start < 0 ||
                list.Start > _source.ExpressionChildren.Count - list.Count)
            {
                AddIncomplete("AVM1EQ003", path, "Source HIR expression list is invalid.");
                result = [];
                return false;
            }
            result = new SourceExpressionIndex[list.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = _source.ExpressionChildren[list.Start + i];
            return true;
        }

        private void AddIncomplete(string code, string path, string message)
        {
            if (_diagnostics.Count == 0)
                _diagnostics.Add(new Avm1SourceNormalizationDiagnostic(code, path, message));
        }

        private static bool IsSupported(Avm1SourceStatementKind kind) =>
            kind is
                Avm1SourceStatementKind.Block or
                Avm1SourceStatementKind.Expression or
                Avm1SourceStatementKind.VariableDeclaration or
                Avm1SourceStatementKind.FunctionDeclaration or
                Avm1SourceStatementKind.Return or
                Avm1SourceStatementKind.Throw or
                Avm1SourceStatementKind.If or
                Avm1SourceStatementKind.IfFrameLoaded or
                Avm1SourceStatementKind.While or
                Avm1SourceStatementKind.DoWhile or
                Avm1SourceStatementKind.For or
                Avm1SourceStatementKind.ForIn or
                Avm1SourceStatementKind.With or
                Avm1SourceStatementKind.TellTarget or
                Avm1SourceStatementKind.Try or
                Avm1SourceStatementKind.CatchClause or
                Avm1SourceStatementKind.FinallyClause or
                Avm1SourceStatementKind.Switch or
                Avm1SourceStatementKind.SwitchCase or
                Avm1SourceStatementKind.SwitchDefault or
                Avm1SourceStatementKind.Break or
                Avm1SourceStatementKind.Continue;

        private static bool IsSupported(Avm1SourceExpressionKind kind) =>
            kind is
                Avm1SourceExpressionKind.Literal or
                Avm1SourceExpressionKind.SymbolReference or
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.ComputedDynamicName or
                Avm1SourceExpressionKind.QualifiedName or
                Avm1SourceExpressionKind.Assignment or
                Avm1SourceExpressionKind.Unary or
                Avm1SourceExpressionKind.Binary or
                Avm1SourceExpressionKind.Conditional or
                Avm1SourceExpressionKind.MemberAccess or
                Avm1SourceExpressionKind.Delete or
                Avm1SourceExpressionKind.IntrinsicCall or
                Avm1SourceExpressionKind.Call or
                Avm1SourceExpressionKind.New or
                Avm1SourceExpressionKind.ArrayLiteral or
                Avm1SourceExpressionKind.ObjectLiteral or
                Avm1SourceExpressionKind.FunctionLiteral;

        private static bool TryGetCompoundOperator(
            Avm1SourceOperator binary,
            out Avm1SourceOperator compound)
        {
            compound = binary switch
            {
                Avm1SourceOperator.Add => Avm1SourceOperator.AddAssign,
                Avm1SourceOperator.Subtract => Avm1SourceOperator.SubtractAssign,
                Avm1SourceOperator.Multiply => Avm1SourceOperator.MultiplyAssign,
                Avm1SourceOperator.Divide => Avm1SourceOperator.DivideAssign,
                Avm1SourceOperator.Modulo => Avm1SourceOperator.ModuloAssign,
                Avm1SourceOperator.BitAnd => Avm1SourceOperator.BitAndAssign,
                Avm1SourceOperator.BitOr => Avm1SourceOperator.BitOrAssign,
                Avm1SourceOperator.BitXor => Avm1SourceOperator.BitXorAssign,
                Avm1SourceOperator.ShiftLeft => Avm1SourceOperator.ShiftLeftAssign,
                Avm1SourceOperator.ShiftRight => Avm1SourceOperator.ShiftRightAssign,
                Avm1SourceOperator.ShiftRightUnsigned =>
                    Avm1SourceOperator.ShiftRightUnsignedAssign,
                _ => Avm1SourceOperator.None
            };
            return compound is not Avm1SourceOperator.None;
        }

        private struct SymbolUses
        {
            public int Declarations;
            public int Reads;
            public int Writes;
        }

        private ref struct ConsumerOrderState
        {
            public ConsumerOrderState(
                HashSet<SourceSymbolIndex> symbols,
                int requiredReadCount,
                List<SourceSymbolIndex> readOrder)
            {
                Symbols = symbols;
                RequiredReadCount = requiredReadCount;
                ReadOrder = readOrder;
            }

            public HashSet<SourceSymbolIndex> Symbols { get; }

            public int RequiredReadCount { get; }

            public List<SourceSymbolIndex> ReadOrder { get; }

            public bool HasBarrier { get; set; }
        }
    }
}
