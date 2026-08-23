using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

[Flags]
public enum Avm1ClosureCodeUnitFlags : ushort
{
    None = 0,
    CapturesLexicalSymbols = 1 << 0,
    CapturesDynamicScope = 1 << 1,
    OwnsCapturedSymbols = 1 << 2,
    ContainsWith = 1 << 3,
    ExecutesInDynamicScope = 1 << 4,
    UsesDynamicLookup = 1 << 5,
    InvokesEval = 1 << 6,
    UsesArguments = 1 << 7,
    UsesThis = 1 << 8,
    UsesSuper = 1 << 9,
    UsesRoot = 1 << 10,
    UsesParent = 1 << 11,
    UsesGlobal = 1 << 12
}

[Flags]
public enum Avm1ClosureSymbolFlags : ushort
{
    None = 0,
    StorageCandidate = 1 << 0,
    CapturedByClosure = 1 << 1,
    AddressableByEval = 1 << 2,
    ObservedThroughArguments = 1 << 3,
    DynamicScopeSensitive = 1 << 4,
    NamedActivationRequired = 1 << 5,
    RegisterEligible = 1 << 6
}

[Flags]
public enum Avm1ClosureCaptureFlags : byte
{
    None = 0,
    Referenced = 1 << 0,
    DeclaredMetadata = 1 << 1
}

public readonly record struct Avm1ClosureCaptureList(int Start, int Count)
{
    public static readonly Avm1ClosureCaptureList Empty = new(0, 0);
}

public readonly record struct Avm1ClosureCodeUnit(
    SourceCodeUnitIndex Index,
    SourceCodeUnitIndex Parent,
    SourceFunctionIndex Function,
    SourceSymbolList OwnedSymbols,
    Avm1ClosureCaptureList Captures,
    Avm1ClosureCodeUnitFlags Flags);

public readonly record struct Avm1ClosureCapture(
    SourceSymbolIndex Symbol,
    SourceCodeUnitIndex DeclarationCodeUnit,
    Avm1SourceExpressionUse Uses,
    Avm1ClosureCaptureFlags Flags);

public readonly record struct Avm1ClosureSymbolFacts(
    SourceSymbolIndex Symbol,
    SourceCodeUnitIndex DeclarationCodeUnit,
    Avm1SourceExpressionUse Uses,
    Avm1ClosureSymbolFlags Flags)
{
    public bool IsRegisterEligible =>
        Flags.HasFlag(Avm1ClosureSymbolFlags.RegisterEligible);

    public bool RequiresNamedActivation =>
        Flags.HasFlag(Avm1ClosureSymbolFlags.NamedActivationRequired);
}

public readonly record struct Avm1ClosureDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    SourceOriginIndex Origin,
    string Message);

public sealed class Avm1ClosureAnalysis
{
    private readonly Avm1ClosureCodeUnit[] _codeUnits;
    private readonly SourceSymbolIndex[] _ownedSymbols;
    private readonly Avm1ClosureCapture[] _captures;
    private readonly Avm1ClosureSymbolFacts[] _symbolFacts;
    private readonly Avm1ClosureDiagnostic[] _diagnostics;

    private Avm1ClosureAnalysis(
        Avm1SourceMethod method,
        Avm1SourceScopeAnalysis scopeAnalysis,
        Avm1ClosureCodeUnit[] codeUnits,
        SourceSymbolIndex[] ownedSymbols,
        Avm1ClosureCapture[] captures,
        Avm1ClosureSymbolFacts[] symbolFacts,
        Avm1ClosureDiagnostic[] diagnostics,
        int captureMetadataMismatchCount)
    {
        Method = method;
        ScopeAnalysis = scopeAnalysis;
        _codeUnits = codeUnits;
        _ownedSymbols = ownedSymbols;
        _captures = captures;
        _symbolFacts = symbolFacts;
        _diagnostics = diagnostics;
        CaptureMetadataMismatchCount = captureMetadataMismatchCount;
    }

    public Avm1SourceMethod Method { get; }

    public Avm1SourceScopeAnalysis ScopeAnalysis { get; }

    public IReadOnlyList<Avm1ClosureCodeUnit> CodeUnits => _codeUnits;

    public IReadOnlyList<Avm1ClosureSymbolFacts> SymbolFacts => _symbolFacts;

    public IReadOnlyList<Avm1ClosureDiagnostic> Diagnostics => _diagnostics;

    public SourceCodeUnitIndex RootCodeUnit => ScopeAnalysis.RootCodeUnit;

    public int CaptureMetadataMismatchCount { get; }

    public bool IsComplete =>
        ScopeAnalysis.IsComplete &&
        !_diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    public Avm1ClosureCodeUnit this[SourceCodeUnitIndex codeUnit] =>
        _codeUnits[codeUnit.Value];

    public Avm1ClosureSymbolFacts this[SourceSymbolIndex symbol] =>
        _symbolFacts[symbol.Value];

    public SourceSymbolIndex GetOwnedSymbol(
        Avm1ClosureCodeUnit codeUnit,
        int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            index,
            codeUnit.OwnedSymbols.Count);
        return _ownedSymbols[codeUnit.OwnedSymbols.Start + index];
    }

    public Avm1ClosureCapture GetCapture(
        Avm1ClosureCodeUnit codeUnit,
        int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            index,
            codeUnit.Captures.Count);
        return _captures[codeUnit.Captures.Start + index];
    }

    public static Avm1ClosureAnalysis Analyze(
        Avm1SourceMethod method,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        cancellationToken.ThrowIfCancellationRequested();

        var scopes = Avm1SourceScopeAnalysis.Analyze(method);
        return new Analyzer(method, scopes, cancellationToken).Run();
    }

    private sealed class Analyzer
    {
        private readonly Avm1SourceMethod _method;
        private readonly Avm1SourceArena _arena;
        private readonly Avm1SourceScopeAnalysis _scopes;
        private readonly CancellationToken _cancellationToken;
        private readonly MutableCodeUnit[] _codeUnits;
        private readonly MutableSymbolFacts[] _symbolFacts;
        private readonly List<Avm1ClosureDiagnostic> _diagnostics = [];

        public Analyzer(
            Avm1SourceMethod method,
            Avm1SourceScopeAnalysis scopes,
            CancellationToken cancellationToken)
        {
            _method = method;
            _arena = method.Arena;
            _scopes = scopes;
            _cancellationToken = cancellationToken;
            _codeUnits = scopes.CodeUnits
                .Select(codeUnit => new MutableCodeUnit(codeUnit))
                .ToArray();
            _symbolFacts = new MutableSymbolFacts[_arena.Symbols.Count];
        }

        public Avm1ClosureAnalysis Run()
        {
            if (!_scopes.IsComplete)
            {
                _diagnostics.Add(new Avm1ClosureDiagnostic(
                    "AVM1CLS001",
                    Avm1CompilationDiagnosticSeverity.Error,
                    SourceOriginIndex.Invalid,
                    $"Source scope analysis contains {_scopes.ConflictCount} " +
                    "ownership conflict(s)."));
            }

            InitializeSymbols();
            InitializeFunctionFlags();
            AnalyzeStatements();
            AnalyzeExpressions();
            ImportDeclaredCaptures();
            ApplyEvalConstraints();
            ApplyArgumentsConstraints();
            FinalizeStorageFlags();
            return BuildResult();
        }

        private void InitializeSymbols()
        {
            foreach (var symbol in _arena.Symbols)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var declarationCodeUnit = _scopes.GetCodeUnit(symbol.Index);
                var facts = new MutableSymbolFacts(symbol.Index, declarationCodeUnit);
                if (IsStorageSymbol(symbol.Kind) && declarationCodeUnit.IsValid)
                {
                    facts.Flags |= Avm1ClosureSymbolFlags.StorageCandidate;
                    if (symbol.Kind is Avm1SourceSymbolKind.Function)
                    {
                        facts.Flags |=
                            Avm1ClosureSymbolFlags.NamedActivationRequired;
                    }
                    _codeUnits[declarationCodeUnit.Value].OwnedSymbols.Add(symbol.Index);
                }
                _symbolFacts[symbol.Index.Value] = facts;
            }
        }

        private void InitializeFunctionFlags()
        {
            foreach (var codeUnit in _scopes.CodeUnits)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!codeUnit.Function.IsValid)
                    continue;

                var function = _arena[codeUnit.Function];
                if (function.Flags.HasFlag(
                    Avm1SourceFunctionFlags.CapturesDynamicScope))
                {
                    _codeUnits[codeUnit.Index.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.CapturesDynamicScope |
                        Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope;
                }
            }
        }

        private void AnalyzeStatements()
        {
            foreach (var statement in _arena.Statements)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (statement.Kind is not Avm1SourceStatementKind.With)
                    continue;

                var codeUnit = _scopes.GetCodeUnit(statement.Index);
                if (codeUnit.IsValid)
                {
                    _codeUnits[codeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.ContainsWith;
                }
            }
        }

        private void AnalyzeExpressions()
        {
            foreach (var expression in _arena.Expressions)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var codeUnit = _scopes.GetCodeUnit(expression.Index);
                if (!codeUnit.IsValid)
                    continue;

                var dynamicScope = _scopes.IsInDynamicScope(expression.Index);
                if (dynamicScope)
                {
                    _codeUnits[codeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope;
                }

                if (expression.Kind is
                    Avm1SourceExpressionKind.DynamicName or
                    Avm1SourceExpressionKind.ComputedDynamicName)
                {
                    _codeUnits[codeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesDynamicLookup;
                }

                if (expression.Kind is Avm1SourceExpressionKind.DynamicName &&
                    expression.Name.IsValid &&
                    _arena[expression.Name] == "arguments")
                {
                    _codeUnits[codeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesArguments;
                }

                if (Avm1SourceEvalAnalysis.IsEvalExpression(
                    _arena,
                    expression.Index))
                {
                    _codeUnits[codeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.InvokesEval |
                        Avm1ClosureCodeUnitFlags.UsesDynamicLookup;
                }

                if (expression.Kind is not Avm1SourceExpressionKind.SymbolReference ||
                    !expression.Symbol.IsValid ||
                    expression.Symbol.Value >= _symbolFacts.Length)
                {
                    continue;
                }

                AnalyzeSymbolReference(expression, codeUnit, dynamicScope);
            }
        }

        private void AnalyzeSymbolReference(
            Avm1SourceExpression expression,
            SourceCodeUnitIndex referenceCodeUnit,
            bool dynamicScope)
        {
            var symbol = _arena[expression.Symbol];
            var facts = _symbolFacts[expression.Symbol.Value];
            var use = _scopes.GetUse(expression.Index);
            facts.Uses |= use;

            switch (symbol.Kind)
            {
                case Avm1SourceSymbolKind.Arguments:
                    _codeUnits[referenceCodeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesArguments;
                    break;

                case Avm1SourceSymbolKind.This:
                    _codeUnits[referenceCodeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesThis;
                    break;

                case Avm1SourceSymbolKind.Super:
                    _codeUnits[referenceCodeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesSuper;
                    break;

                case Avm1SourceSymbolKind.Root:
                    _codeUnits[referenceCodeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesRoot;
                    break;

                case Avm1SourceSymbolKind.Parent:
                    _codeUnits[referenceCodeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesParent;
                    break;

                case Avm1SourceSymbolKind.Global:
                    _codeUnits[referenceCodeUnit.Value].Flags |=
                        Avm1ClosureCodeUnitFlags.UsesGlobal;
                    break;
            }

            if (dynamicScope &&
                facts.Flags.HasFlag(Avm1ClosureSymbolFlags.StorageCandidate))
            {
                facts.Flags |= Avm1ClosureSymbolFlags.DynamicScopeSensitive;
            }

            _symbolFacts[expression.Symbol.Value] = facts;

            var declarationCodeUnit = facts.DeclarationCodeUnit;
            if (facts.Flags.HasFlag(Avm1ClosureSymbolFlags.StorageCandidate) &&
                declarationCodeUnit.IsValid &&
                declarationCodeUnit != referenceCodeUnit)
            {
                if (IsAncestor(declarationCodeUnit, referenceCodeUnit))
                {
                    AddCapture(
                        referenceCodeUnit,
                        expression.Symbol,
                        declarationCodeUnit,
                        use,
                        Avm1ClosureCaptureFlags.Referenced);
                }
                else
                {
                    _diagnostics.Add(new Avm1ClosureDiagnostic(
                        "AVM1CLS003",
                        Avm1CompilationDiagnosticSeverity.Error,
                        expression.Origin,
                        $"Symbol {expression.Symbol} is referenced from " +
                        $"unrelated code unit {referenceCodeUnit}."));
                }
            }

        }

        private void ImportDeclaredCaptures()
        {
            foreach (var codeUnit in _scopes.CodeUnits)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!codeUnit.Function.IsValid)
                    continue;

                var function = _arena[codeUnit.Function];
                for (var i = 0; i < function.Captures.Count; i++)
                {
                    var symbol = _arena.GetCapture(function, i);
                    if (!symbol.IsValid || symbol.Value >= _symbolFacts.Length)
                    {
                        AddInvalidCaptureDiagnostic(function, symbol);
                        continue;
                    }

                    var declarationCodeUnit =
                        _symbolFacts[symbol.Value].DeclarationCodeUnit;
                    if (!declarationCodeUnit.IsValid ||
                        !IsAncestor(declarationCodeUnit, codeUnit.Index))
                    {
                        _diagnostics.Add(new Avm1ClosureDiagnostic(
                            "AVM1CLS002",
                            Avm1CompilationDiagnosticSeverity.Warning,
                            function.Origin,
                            $"Declared capture {symbol} does not belong to an " +
                            $"ancestor of code unit {codeUnit.Index}."));
                        continue;
                    }

                    AddCapture(
                        codeUnit.Index,
                        symbol,
                        declarationCodeUnit,
                        Avm1SourceExpressionUse.None,
                        Avm1ClosureCaptureFlags.DeclaredMetadata);
                }
            }
        }

        private void AddInvalidCaptureDiagnostic(
            Avm1SourceFunction function,
            SourceSymbolIndex symbol) =>
            _diagnostics.Add(new Avm1ClosureDiagnostic(
                "AVM1CLS002",
                Avm1CompilationDiagnosticSeverity.Warning,
                function.Origin,
                $"Function {function.Index} declares invalid capture {symbol}."));

        private void AddCapture(
            SourceCodeUnitIndex codeUnit,
            SourceSymbolIndex symbol,
            SourceCodeUnitIndex declarationCodeUnit,
            Avm1SourceExpressionUse uses,
            Avm1ClosureCaptureFlags flags)
        {
            var captures = _codeUnits[codeUnit.Value].Captures;
            if (!captures.TryGetValue(symbol, out var capture))
            {
                capture = new MutableCapture(symbol, declarationCodeUnit);
                captures.Add(symbol, capture);
            }
            capture.Uses |= uses;
            capture.Flags |= flags;

            _codeUnits[codeUnit.Value].Flags |=
                Avm1ClosureCodeUnitFlags.CapturesLexicalSymbols;
            _codeUnits[declarationCodeUnit.Value].Flags |=
                Avm1ClosureCodeUnitFlags.OwnsCapturedSymbols;
            _symbolFacts[symbol.Value].Flags |=
                Avm1ClosureSymbolFlags.CapturedByClosure;
        }

        private void ApplyEvalConstraints()
        {
            foreach (var codeUnit in _codeUnits)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!codeUnit.Flags.HasFlag(Avm1ClosureCodeUnitFlags.InvokesEval))
                    continue;

                for (var current = codeUnit.Source.Index;
                    current.IsValid;
                    current = _codeUnits[current.Value].Source.Parent)
                {
                    foreach (var symbol in _codeUnits[current.Value].OwnedSymbols)
                    {
                        _symbolFacts[symbol.Value].Flags |=
                            Avm1ClosureSymbolFlags.AddressableByEval;
                    }
                }
            }
        }

        private void ApplyArgumentsConstraints()
        {
            foreach (var codeUnit in _codeUnits)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!codeUnit.Flags.HasFlag(
                    Avm1ClosureCodeUnitFlags.UsesArguments))
                {
                    continue;
                }

                foreach (var symbolIndex in codeUnit.OwnedSymbols)
                {
                    if (_arena[symbolIndex].Kind is Avm1SourceSymbolKind.Parameter)
                    {
                        _symbolFacts[symbolIndex.Value].Flags |=
                            Avm1ClosureSymbolFlags.ObservedThroughArguments;
                    }
                }
            }
        }

        private void FinalizeStorageFlags()
        {
            for (var i = 0; i < _symbolFacts.Length; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var facts = _symbolFacts[i];
                if (!facts.Flags.HasFlag(
                    Avm1ClosureSymbolFlags.StorageCandidate))
                {
                    continue;
                }

                var activationConstraints =
                    Avm1ClosureSymbolFlags.CapturedByClosure |
                    Avm1ClosureSymbolFlags.AddressableByEval |
                    Avm1ClosureSymbolFlags.ObservedThroughArguments |
                    Avm1ClosureSymbolFlags.DynamicScopeSensitive;
                var requiresActivation =
                    facts.Flags.HasFlag(
                        Avm1ClosureSymbolFlags.NamedActivationRequired) ||
                    (facts.Flags & activationConstraints) != 0;
                facts.Flags |= requiresActivation
                    ? Avm1ClosureSymbolFlags.NamedActivationRequired
                    : Avm1ClosureSymbolFlags.RegisterEligible;
                _symbolFacts[i] = facts;
            }
        }

        private Avm1ClosureAnalysis BuildResult()
        {
            var codeUnits = new Avm1ClosureCodeUnit[_codeUnits.Length];
            var ownedSymbols = new List<SourceSymbolIndex>();
            var captures = new List<Avm1ClosureCapture>();
            var mismatchCount = 0;

            for (var i = 0; i < _codeUnits.Length; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var mutable = _codeUnits[i];
                mutable.OwnedSymbols.Sort(static (left, right) =>
                    left.Value.CompareTo(right.Value));
                var ownedStart = ownedSymbols.Count;
                ownedSymbols.AddRange(mutable.OwnedSymbols);

                var captureStart = captures.Count;
                foreach (var capture in mutable.Captures.Values
                    .OrderBy(capture => capture.Symbol.Value))
                {
                    var hasReference = capture.Flags.HasFlag(
                        Avm1ClosureCaptureFlags.Referenced);
                    var hasMetadata = capture.Flags.HasFlag(
                        Avm1ClosureCaptureFlags.DeclaredMetadata);
                    if (hasReference != hasMetadata)
                        mismatchCount++;

                    captures.Add(new Avm1ClosureCapture(
                        capture.Symbol,
                        capture.DeclarationCodeUnit,
                        capture.Uses,
                        capture.Flags));
                }

                codeUnits[i] = new Avm1ClosureCodeUnit(
                    mutable.Source.Index,
                    mutable.Source.Parent,
                    mutable.Source.Function,
                    new SourceSymbolList(
                        ownedStart,
                        mutable.OwnedSymbols.Count),
                    new Avm1ClosureCaptureList(
                        captureStart,
                        mutable.Captures.Count),
                    mutable.Flags);
            }

            var symbolFacts = new Avm1ClosureSymbolFacts[_symbolFacts.Length];
            for (var i = 0; i < symbolFacts.Length; i++)
            {
                var mutable = _symbolFacts[i];
                symbolFacts[i] = new Avm1ClosureSymbolFacts(
                    mutable.Symbol,
                    mutable.DeclarationCodeUnit,
                    mutable.Uses,
                    mutable.Flags);
            }

            return new Avm1ClosureAnalysis(
                _method,
                _scopes,
                codeUnits,
                ownedSymbols.ToArray(),
                captures.ToArray(),
                symbolFacts,
                _diagnostics.ToArray(),
                mismatchCount);
        }

        private bool IsAncestor(
            SourceCodeUnitIndex ancestor,
            SourceCodeUnitIndex descendant)
        {
            for (var current = _codeUnits[descendant.Value].Source.Parent;
                current.IsValid;
                current = _codeUnits[current.Value].Source.Parent)
            {
                if (current == ancestor)
                    return true;
            }
            return false;
        }

        private static bool IsStorageSymbol(Avm1SourceSymbolKind kind) =>
            kind is
                Avm1SourceSymbolKind.Local or
                Avm1SourceSymbolKind.Parameter or
                Avm1SourceSymbolKind.Temporary or
                Avm1SourceSymbolKind.Function or
                Avm1SourceSymbolKind.Catch;

        private sealed class MutableCodeUnit(Avm1SourceCodeUnit source)
        {
            public Avm1SourceCodeUnit Source { get; } = source;

            public List<SourceSymbolIndex> OwnedSymbols { get; } = [];

            public Dictionary<SourceSymbolIndex, MutableCapture> Captures { get; } = [];

            public Avm1ClosureCodeUnitFlags Flags { get; set; }
        }

        private sealed class MutableCapture(
            SourceSymbolIndex symbol,
            SourceCodeUnitIndex declarationCodeUnit)
        {
            public SourceSymbolIndex Symbol { get; } = symbol;

            public SourceCodeUnitIndex DeclarationCodeUnit { get; } = declarationCodeUnit;

            public Avm1SourceExpressionUse Uses { get; set; }

            public Avm1ClosureCaptureFlags Flags { get; set; }
        }

        private struct MutableSymbolFacts(
            SourceSymbolIndex symbol,
            SourceCodeUnitIndex declarationCodeUnit)
        {
            public SourceSymbolIndex Symbol { get; } = symbol;

            public SourceCodeUnitIndex DeclarationCodeUnit { get; } = declarationCodeUnit;

            public Avm1SourceExpressionUse Uses { get; set; }

            public Avm1ClosureSymbolFlags Flags { get; set; }
        }
    }
}
