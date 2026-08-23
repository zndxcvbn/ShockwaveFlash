namespace ShockwaveFlash.Avm1.Source;

public enum Avm1SourceEquivalenceStatus : byte
{
    Equivalent,
    Different,
    Incomplete
}

public readonly record struct Avm1SourceEquivalenceDiagnostic(
    string Code,
    string Path,
    string Message);

public sealed class Avm1SourceEquivalenceResult
{
    private readonly Avm1SourceEquivalenceDiagnostic[] _diagnostics;

    internal Avm1SourceEquivalenceResult(
        Avm1SourceEquivalenceStatus status,
        int expectedRewriteCount,
        int actualRewriteCount,
        Avm1SourceEquivalenceDiagnostic[] diagnostics)
    {
        Status = status;
        ExpectedRewriteCount = expectedRewriteCount;
        ActualRewriteCount = actualRewriteCount;
        _diagnostics = diagnostics;
    }

    public Avm1SourceEquivalenceStatus Status { get; }

    public bool IsEquivalent => Status is Avm1SourceEquivalenceStatus.Equivalent;

    public int ExpectedRewriteCount { get; }

    public int ActualRewriteCount { get; }

    public IReadOnlyList<Avm1SourceEquivalenceDiagnostic> Diagnostics => _diagnostics;
}

public static class Avm1SourceEquivalence
{
    public static Avm1SourceEquivalenceResult Compare(
        Avm1SourceMethod expected,
        Avm1SourceMethod actual,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        cancellationToken.ThrowIfCancellationRequested();

        var expectedNormalization = Avm1SourceNormalizer.Normalize(
            expected,
            cancellationToken);
        var actualNormalization = Avm1SourceNormalizer.Normalize(
            actual,
            cancellationToken);
        var diagnostics = new List<Avm1SourceEquivalenceDiagnostic>();
        AddNormalizationDiagnostics(
            "expected",
            expectedNormalization.Diagnostics,
            diagnostics);
        AddNormalizationDiagnostics(
            "actual",
            actualNormalization.Diagnostics,
            diagnostics);
        if (diagnostics.Count != 0)
        {
            return new Avm1SourceEquivalenceResult(
                Avm1SourceEquivalenceStatus.Incomplete,
                expectedNormalization.RewriteCount,
                actualNormalization.RewriteCount,
                diagnostics.ToArray());
        }

        var comparer = new StructuralComparer(
            expectedNormalization.Method,
            actualNormalization.Method,
            cancellationToken);
        if (!comparer.Compare(out var mismatch))
        {
            diagnostics.Add(mismatch);
            return new Avm1SourceEquivalenceResult(
                Avm1SourceEquivalenceStatus.Different,
                expectedNormalization.RewriteCount,
                actualNormalization.RewriteCount,
                diagnostics.ToArray());
        }

        return new Avm1SourceEquivalenceResult(
            Avm1SourceEquivalenceStatus.Equivalent,
            expectedNormalization.RewriteCount,
            actualNormalization.RewriteCount,
            []);
    }

    private static void AddNormalizationDiagnostics(
        string side,
        IReadOnlyList<Avm1SourceNormalizationDiagnostic> source,
        List<Avm1SourceEquivalenceDiagnostic> destination)
    {
        for (var i = 0; i < source.Count; i++)
        {
            var diagnostic = source[i];
            destination.Add(new Avm1SourceEquivalenceDiagnostic(
                diagnostic.Code,
                side + diagnostic.Path,
                diagnostic.Message));
        }
    }

    private sealed class StructuralComparer
    {
        private const int MaximumDepth = 2048;
        private readonly Avm1SourceMethod _expected;
        private readonly Avm1SourceMethod _actual;
        private readonly CancellationToken _cancellationToken;

        public StructuralComparer(
            Avm1SourceMethod expected,
            Avm1SourceMethod actual,
            CancellationToken cancellationToken)
        {
            _expected = expected;
            _actual = actual;
            _cancellationToken = cancellationToken;
        }

        public bool Compare(out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (!CompareSymbols(out mismatch) ||
                !CompareLabels(out mismatch) ||
                !CompareFunctions(out mismatch))
                return false;

            if (_expected.Parameters.Count != _actual.Parameters.Count)
            {
                mismatch = Different(
                    ".parameters",
                    $"Parameter count {_expected.Parameters.Count} does not match " +
                    $"{_actual.Parameters.Count}.");
                return false;
            }
            for (var i = 0; i < _expected.Parameters.Count; i++)
            {
                if (_expected.Parameters[i] == _actual.Parameters[i])
                    continue;

                mismatch = Different(
                    $".parameters[{i}]",
                    $"Canonical parameter {_expected.Parameters[i]} does not match " +
                    $"{_actual.Parameters[i]}.");
                return false;
            }

            return CompareStatement(
                _expected.Body,
                _actual.Body,
                ".body",
                depth: 0,
                out mismatch);
        }

        private bool CompareFunctions(
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (_expected.Arena.Functions.Count != _actual.Arena.Functions.Count)
            {
                mismatch = Different(
                    ".functions",
                    $"Canonical function count {_expected.Arena.Functions.Count} " +
                    $"does not match {_actual.Arena.Functions.Count}.");
                return false;
            }

            for (var i = 0; i < _expected.Arena.Functions.Count; i++)
            {
                var expected = _expected.Arena.Functions[i];
                var actual = _actual.Arena.Functions[i];
                var path = $".functions[{i}]";
                if (expected.NameSymbol != actual.NameSymbol)
                {
                    mismatch = Different(
                        path + ".nameSymbol",
                        $"Canonical function symbol {expected.NameSymbol} does " +
                        $"not match {actual.NameSymbol}.");
                    return false;
                }
                if (expected.Flags != actual.Flags)
                {
                    mismatch = Different(
                        path + ".flags",
                        $"Function flags {expected.Flags} do not match " +
                        $"{actual.Flags}.");
                    return false;
                }
                if (!CompareFunctionSymbols(
                        expected.Parameters,
                        actual.Parameters,
                        path + ".parameters",
                        out mismatch) ||
                    !CompareFunctionSymbols(
                        expected.Captures,
                        actual.Captures,
                        path + ".captures",
                        out mismatch) ||
                    !CompareStatement(
                        expected.Body,
                        actual.Body,
                        path + ".body",
                        depth: 0,
                        out mismatch))
                {
                    return false;
                }
            }

            mismatch = default;
            return true;
        }

        private bool CompareFunctionSymbols(
            SourceSymbolList expected,
            SourceSymbolList actual,
            string path,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (expected.Count != actual.Count)
            {
                mismatch = Different(
                    path,
                    $"Function symbol count {expected.Count} does not match " +
                    $"{actual.Count}.");
                return false;
            }
            for (var i = 0; i < expected.Count; i++)
            {
                var expectedSymbol = _expected.Arena.FunctionSymbols[
                    expected.Start + i];
                var actualSymbol = _actual.Arena.FunctionSymbols[
                    actual.Start + i];
                if (expectedSymbol == actualSymbol)
                    continue;

                mismatch = Different(
                    $"{path}[{i}]",
                    $"Canonical function symbol {expectedSymbol} does not match " +
                    $"{actualSymbol}.");
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareSymbols(out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (_expected.Arena.Symbols.Count != _actual.Arena.Symbols.Count)
            {
                mismatch = Different(
                    ".symbols",
                    $"Canonical symbol count {_expected.Arena.Symbols.Count} does not match " +
                    $"{_actual.Arena.Symbols.Count}.");
                return false;
            }
            for (var i = 0; i < _expected.Arena.Symbols.Count; i++)
            {
                var expected = _expected.Arena.Symbols[i];
                var actual = _actual.Arena.Symbols[i];
                if (expected.Kind == actual.Kind &&
                    CompareOptionalString(
                        _expected.Arena,
                        expected.Name,
                        _actual.Arena,
                        actual.Name))
                {
                    continue;
                }

                mismatch = Different(
                    $".symbols[{i}]",
                    $"Canonical symbol {expected.Kind}/{GetString(_expected.Arena, expected.Name)} " +
                    $"does not match {actual.Kind}/{GetString(_actual.Arena, actual.Name)}.");
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareLabels(out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (_expected.Arena.Labels.Count != _actual.Arena.Labels.Count)
            {
                mismatch = Different(
                    ".labels",
                    $"Canonical label count {_expected.Arena.Labels.Count} does not match " +
                    $"{_actual.Arena.Labels.Count}.");
                return false;
            }
            for (var i = 0; i < _expected.Arena.Labels.Count; i++)
            {
                var expected = _expected.Arena.Labels[i];
                var actual = _actual.Arena.Labels[i];
                if (CompareOptionalString(
                    _expected.Arena,
                    expected.Name,
                    _actual.Arena,
                    actual.Name))
                {
                    continue;
                }

                mismatch = Different(
                    $".labels[{i}]",
                    $"Canonical label {GetString(_expected.Arena, expected.Name)} does not match " +
                    $"{GetString(_actual.Arena, actual.Name)}.");
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareStatement(
            SourceStatementIndex expectedIndex,
            SourceStatementIndex actualIndex,
            string path,
            int depth,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaximumDepth)
            {
                mismatch = Different(path, "Normalized statement depth exceeds the comparison limit.");
                return false;
            }

            var expected = _expected.Arena[expectedIndex];
            var actual = _actual.Arena[actualIndex];
            if (expected.Kind != actual.Kind)
            {
                mismatch = Different(
                    path,
                    $"Statement kind {expected.Kind} does not match {actual.Kind}.");
                return false;
            }
            if (expected.Flags != actual.Flags)
            {
                mismatch = Different(
                    path,
                    $"Statement flags {expected.Flags} do not match {actual.Flags}.");
                return false;
            }
            if (expected.Symbol != actual.Symbol)
            {
                mismatch = Different(
                    path + ".symbol",
                    $"Canonical symbol {expected.Symbol} does not match {actual.Symbol}.");
                return false;
            }
            if (expected.Label != actual.Label)
            {
                mismatch = Different(
                    path + ".label",
                    $"Canonical label {expected.Label} does not match {actual.Label}.");
                return false;
            }
            if (!CompareOptionalString(
                _expected.Arena,
                expected.Name,
                _actual.Arena,
                actual.Name))
            {
                mismatch = Different(path + ".name", "Statement names do not match.");
                return false;
            }
            if (!CompareOptionalExpression(
                expected.Expression,
                actual.Expression,
                path + ".expression",
                depth + 1,
                out mismatch) ||
                !CompareOptionalExpression(
                    expected.SecondaryExpression,
                    actual.SecondaryExpression,
                    path + ".secondaryExpression",
                    depth + 1,
                    out mismatch))
            {
                return false;
            }
            if (!CompareStatementList(
                    expected.Children,
                    actual.Children,
                    path + ".children",
                    depth + 1,
                    out mismatch) ||
                !CompareStatementList(
                    expected.Initializers,
                    actual.Initializers,
                    path + ".initializers",
                    depth + 1,
                    out mismatch) ||
                !CompareExpressionList(
                    expected.Expressions,
                    actual.Expressions,
                    path + ".expressions",
                    depth + 1,
                    out mismatch))
            {
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareStatementList(
            SourceStatementList expected,
            SourceStatementList actual,
            string path,
            int depth,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (expected.Count != actual.Count)
            {
                mismatch = Different(
                    path,
                    $"Statement count {expected.Count} does not match {actual.Count}.");
                return false;
            }
            for (var i = 0; i < expected.Count; i++)
            {
                if (CompareStatement(
                    _expected.Arena.StatementChildren[expected.Start + i],
                    _actual.Arena.StatementChildren[actual.Start + i],
                    $"{path}[{i}]",
                    depth,
                    out mismatch))
                {
                    continue;
                }
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareExpressionList(
            SourceExpressionList expected,
            SourceExpressionList actual,
            string path,
            int depth,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (expected.Count != actual.Count)
            {
                mismatch = Different(
                    path,
                    $"Expression count {expected.Count} does not match {actual.Count}.");
                return false;
            }
            for (var i = 0; i < expected.Count; i++)
            {
                if (CompareExpression(
                    _expected.Arena.ExpressionChildren[expected.Start + i],
                    _actual.Arena.ExpressionChildren[actual.Start + i],
                    $"{path}[{i}]",
                    depth,
                    out mismatch))
                {
                    continue;
                }
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareOptionalExpression(
            SourceExpressionIndex expected,
            SourceExpressionIndex actual,
            string path,
            int depth,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (expected.IsValid != actual.IsValid)
            {
                mismatch = Different(path, "Expression presence does not match.");
                return false;
            }
            if (!expected.IsValid)
            {
                mismatch = default;
                return true;
            }
            return CompareExpression(expected, actual, path, depth, out mismatch);
        }

        private bool CompareExpression(
            SourceExpressionIndex expectedIndex,
            SourceExpressionIndex actualIndex,
            string path,
            int depth,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaximumDepth)
            {
                mismatch = Different(path, "Normalized expression depth exceeds the comparison limit.");
                return false;
            }

            var expected = _expected.Arena[expectedIndex];
            var actual = _actual.Arena[actualIndex];
            if (expected.Kind != actual.Kind || expected.Operator != actual.Operator)
            {
                mismatch = Different(
                    path,
                    $"Expression {expected.Kind}/{expected.Operator} does not match " +
                    $"{actual.Kind}/{actual.Operator}.");
                return false;
            }
            if (expected.Symbol != actual.Symbol)
            {
                mismatch = Different(
                    path + ".symbol",
                    $"Canonical symbol {expected.Symbol} does not match {actual.Symbol}.");
                return false;
            }
            if (expected.Flags != actual.Flags)
            {
                mismatch = Different(
                    path,
                    $"Expression flags {expected.Flags} do not match {actual.Flags}.");
                return false;
            }
            if (expected.Function != actual.Function)
            {
                mismatch = Different(
                    path + ".function",
                    $"Canonical function {expected.Function} does not match " +
                    $"{actual.Function}.");
                return false;
            }
            if (!CompareOptionalString(
                _expected.Arena,
                expected.Name,
                _actual.Arena,
                actual.Name))
            {
                mismatch = Different(path + ".name", "Expression names do not match.");
                return false;
            }
            if (!CompareOptionalLiteral(
                expected.Literal,
                actual.Literal,
                path + ".literal",
                out mismatch))
            {
                return false;
            }
            if (!CompareExpressionList(
                expected.Children,
                actual.Children,
                path + ".children",
                depth + 1,
                out mismatch))
            {
                return false;
            }

            mismatch = default;
            return true;
        }

        private bool CompareOptionalLiteral(
            SourceLiteralIndex expectedIndex,
            SourceLiteralIndex actualIndex,
            string path,
            out Avm1SourceEquivalenceDiagnostic mismatch)
        {
            if (expectedIndex.IsValid != actualIndex.IsValid)
            {
                mismatch = Different(path, "Literal presence does not match.");
                return false;
            }
            if (!expectedIndex.IsValid)
            {
                mismatch = default;
                return true;
            }

            var expected = _expected.Arena[expectedIndex];
            var actual = _actual.Arena[actualIndex];
            var equal = expected.Kind == actual.Kind && expected.Kind switch
            {
                Avm1SourceLiteralKind.Boolean =>
                    expected.BooleanValue == actual.BooleanValue,
                Avm1SourceLiteralKind.Number =>
                    EqualNumbers(expected.NumberValue, actual.NumberValue),
                Avm1SourceLiteralKind.String => CompareOptionalString(
                    _expected.Arena,
                    expected.StringValue,
                    _actual.Arena,
                    actual.StringValue),
                _ => true
            };
            if (!equal)
            {
                mismatch = Different(path, "Literal values do not match.");
                return false;
            }

            mismatch = default;
            return true;
        }

        private static bool CompareOptionalString(
            Avm1SourceArena expectedArena,
            SourceStringIndex expected,
            Avm1SourceArena actualArena,
            SourceStringIndex actual) =>
            expected.IsValid == actual.IsValid &&
            (!expected.IsValid ||
                string.Equals(
                    expectedArena[expected],
                    actualArena[actual],
                    StringComparison.Ordinal));

        private static string GetString(Avm1SourceArena arena, SourceStringIndex index) =>
            index.IsValid ? arena[index] : "<none>";

        private static bool EqualNumbers(double expected, double actual)
        {
            if (double.IsNaN(expected) && double.IsNaN(actual))
                return true;
            return BitConverter.DoubleToInt64Bits(expected) ==
                BitConverter.DoubleToInt64Bits(actual);
        }

        private static Avm1SourceEquivalenceDiagnostic Different(
            string path,
            string message) =>
            new("AVM1EQ100", path, message);
    }
}
