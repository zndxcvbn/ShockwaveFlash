namespace ShockwaveFlash.Avm1.Source;

public enum Avm1SourceTypeCheckingMode : byte
{
    None,
    Conservative,
    Strict
}

public readonly record struct Avm1SourceTypeCheckDiagnostic(
    string Code,
    Avm1SourceDiagnosticSeverity Severity,
    Avm1SourceArena? Arena,
    SourceOriginIndex Origin,
    SourceProgramSymbolIndex Symbol,
    string Message);

public sealed class Avm1SourceTypeCheckResult
{
    private readonly Avm1SourceTypeCheckDiagnostic[] _diagnostics;

    internal Avm1SourceTypeCheckResult(
        Avm1SourceProgram program,
        Avm1SourceTypeCheckingMode mode,
        Avm1SourceTypeCheckDiagnostic[] diagnostics)
    {
        Program = program;
        Mode = mode;
        _diagnostics = diagnostics;
    }

    public Avm1SourceProgram Program { get; }

    public Avm1SourceTypeCheckingMode Mode { get; }

    public IReadOnlyList<Avm1SourceTypeCheckDiagnostic> Diagnostics =>
        _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic =>
        diagnostic.Severity is Avm1SourceDiagnosticSeverity.Error);
}

public static class Avm1SourceTypeChecker
{
    public static Avm1SourceTypeCheckResult Check(
        Avm1SourceProgram program,
        Avm1SourceTypeCheckingMode mode =
            Avm1SourceTypeCheckingMode.Conservative,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode is Avm1SourceTypeCheckingMode.None)
            return new Avm1SourceTypeCheckResult(program, mode, []);

        var checker = new Checker(program, mode, cancellationToken);
        return checker.Check();
    }

    private sealed class Checker
    {
        private readonly Avm1SourceProgram _program;
        private readonly Avm1SourceTypeCheckingMode _mode;
        private readonly CancellationToken _cancellationToken;
        private readonly List<Avm1SourceTypeCheckDiagnostic> _diagnostics = [];
        private readonly HashSet<(int Symbol, int Type, string Role)>
            _unresolvedTypes = [];

        public Checker(
            Avm1SourceProgram program,
            Avm1SourceTypeCheckingMode mode,
            CancellationToken cancellationToken)
        {
            _program = program;
            _mode = mode;
            _cancellationToken = cancellationToken;
        }

        public Avm1SourceTypeCheckResult Check()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_program.SemanticAnalysis.ReachedFixedPoint)
            {
                Add(
                    "AVM1TYP003",
                    Avm1SourceDiagnosticSeverity.Error,
                    arena: null,
                    SourceOriginIndex.Invalid,
                    SourceProgramSymbolIndex.Invalid,
                    "Source type and binding analysis did not reach a fixed point.");
            }

            CheckDeclarationsAndRelationships();
            CheckInheritanceCycles();
            CheckInvocations();
            return new Avm1SourceTypeCheckResult(
                _program,
                _mode,
                _diagnostics.ToArray());
        }

        private void CheckDeclarationsAndRelationships()
        {
            foreach (var symbol in _program.Symbols)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (symbol.Flags.HasFlag(Avm1SourceProgramSymbolFlags.External))
                    continue;

                CheckResolvedType(symbol.Index, symbol.DeclaredType, "declared type");
                if (_program.TryGetSignature(symbol.Index, out var signature))
                {
                    for (var i = 0; i < signature.Parameters.Count; i++)
                    {
                        CheckResolvedType(
                            symbol.Index,
                            _program.GetParameter(signature, i).Type,
                            $"parameter {i + 1} type");
                    }
                }

                if (symbol.Kind is not (
                        Avm1SourceProgramSymbolKind.Class or
                        Avm1SourceProgramSymbolKind.Interface))
                {
                    continue;
                }

                if (_program.TryGetBaseType(symbol.Index, out var baseType))
                {
                    CheckResolvedType(symbol.Index, baseType, "base type");
                    if (TryGetTypeSymbol(baseType, out var baseSymbol) &&
                        _program[baseSymbol].Kind is
                            Avm1SourceProgramSymbolKind.Interface)
                    {
                        AddPolicyDiagnostic(
                            "AVM1TYP004",
                            symbol.Index,
                            $"{symbol.QualifiedName} declares interface " +
                            $"{_program[baseSymbol].QualifiedName} as its base class.");
                    }
                }

                var interfaces = _program.GetInterfaceTypes(symbol.Index);
                for (var i = 0; i < interfaces.Count; i++)
                {
                    var interfaceType = _program.GetInterfaceType(interfaces, i);
                    CheckResolvedType(
                        symbol.Index,
                        interfaceType,
                        symbol.Kind is Avm1SourceProgramSymbolKind.Interface
                            ? "extended interface"
                            : "implemented interface");
                    if (TryGetTypeSymbol(interfaceType, out var interfaceSymbol) &&
                        _program[interfaceSymbol].Kind is
                            Avm1SourceProgramSymbolKind.Class)
                    {
                        AddPolicyDiagnostic(
                            "AVM1TYP004",
                            symbol.Index,
                            $"{symbol.QualifiedName} uses class " +
                            $"{_program[interfaceSymbol].QualifiedName} as an interface.");
                    }
                }
            }
        }

        private void CheckResolvedType(
            SourceProgramSymbolIndex owner,
            SourceProgramTypeIndex type,
            string role)
        {
            if (!type.IsValid ||
                _program[type] is not
                {
                    Kind: Avm1SourceTypeKind.Nominal,
                    Symbol.IsValid: false,
                    Name: { } name
                } ||
                !_unresolvedTypes.Add((owner.Value, type.Value, role)))
            {
                return;
            }

            AddPolicyDiagnostic(
                "AVM1TYP001",
                owner,
                $"Cannot resolve {role} {name.Value} for " +
                $"{_program[owner].QualifiedName}.");
        }

        private void CheckInheritanceCycles()
        {
            var states = new byte[_program.Symbols.Count];
            var path = new List<SourceProgramSymbolIndex>();
            var reported = new HashSet<int>();
            foreach (var symbol in _program.Symbols)
            {
                if (symbol.Flags.HasFlag(Avm1SourceProgramSymbolFlags.External) ||
                    symbol.Kind is not (
                        Avm1SourceProgramSymbolKind.Class or
                        Avm1SourceProgramSymbolKind.Interface) ||
                    states[symbol.Index.Value] != 0)
                {
                    continue;
                }
                VisitInheritance(symbol.Index, states, path, reported);
            }
        }

        private void VisitInheritance(
            SourceProgramSymbolIndex symbol,
            byte[] states,
            List<SourceProgramSymbolIndex> path,
            HashSet<int> reported)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            states[symbol.Value] = 1;
            path.Add(symbol);
            foreach (var target in GetInheritanceTargets(symbol))
            {
                if (_program[target].Flags.HasFlag(
                        Avm1SourceProgramSymbolFlags.External))
                {
                    continue;
                }
                if (states[target.Value] == 0)
                {
                    VisitInheritance(target, states, path, reported);
                    continue;
                }
                if (states[target.Value] != 1 || !reported.Add(target.Value))
                    continue;

                var cycleStart = path.FindIndex(item => item == target);
                var names = path.Skip(Math.Max(0, cycleStart))
                    .Select(item => _program[item].QualifiedName.Value)
                    .Append(_program[target].QualifiedName.Value);
                Add(
                    "AVM1TYP005",
                    Avm1SourceDiagnosticSeverity.Error,
                    arena: null,
                    SourceOriginIndex.Invalid,
                    symbol,
                    "Inheritance cycle: " + string.Join(" -> ", names) + ".");
            }
            path.RemoveAt(path.Count - 1);
            states[symbol.Value] = 2;
        }

        private IEnumerable<SourceProgramSymbolIndex> GetInheritanceTargets(
            SourceProgramSymbolIndex symbol)
        {
            var declaration = _program[symbol];
            if (declaration.Kind is Avm1SourceProgramSymbolKind.Class &&
                _program.TryGetBaseType(symbol, out var baseType) &&
                TryGetTypeSymbol(baseType, out var baseSymbol))
            {
                yield return baseSymbol;
            }
            if (declaration.Kind is not Avm1SourceProgramSymbolKind.Interface)
                yield break;

            var interfaces = _program.GetInterfaceTypes(symbol);
            for (var i = 0; i < interfaces.Count; i++)
            {
                if (TryGetTypeSymbol(
                        _program.GetInterfaceType(interfaces, i),
                        out var interfaceSymbol))
                {
                    yield return interfaceSymbol;
                }
            }
        }

        private void CheckInvocations()
        {
            foreach (var arenaBindings in _program.ArenaBindings)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var arena = arenaBindings.Arena;
                foreach (var expression in arena.Expressions)
                {
                    if (expression.Kind is not (
                            Avm1SourceExpressionKind.Call or
                            Avm1SourceExpressionKind.New) ||
                        expression.Children.Count == 0)
                    {
                        continue;
                    }

                    var target = arena.GetChild(expression, 0);
                    var binding = arenaBindings[target];
                    if (!binding.IsBound ||
                        !_program.TryGetSignature(binding.Symbol, out var signature))
                    {
                        continue;
                    }

                    var argumentCount = expression.Children.Count - 1;
                    var required = 0;
                    var hasRest = false;
                    for (var i = 0; i < signature.Parameters.Count; i++)
                    {
                        var parameter = _program.GetParameter(signature, i);
                        if (parameter.Flags.HasFlag(
                                Avm1SourceProgramParameterFlags.Rest))
                        {
                            hasRest = true;
                        }
                        else if (!parameter.Flags.HasFlag(
                                     Avm1SourceProgramParameterFlags.Optional))
                        {
                            required++;
                        }
                    }

                    if (argumentCount >= required &&
                        (hasRest || argumentCount <= signature.Parameters.Count))
                    {
                        continue;
                    }

                    var expected = hasRest
                        ? $"at least {required}"
                        : required == signature.Parameters.Count
                            ? required.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : $"{required} to {signature.Parameters.Count}";
                    Add(
                        "AVM1TYP002",
                        PolicySeverity,
                        arena,
                        expression.Origin,
                        arenaBindings.ContainingMember,
                        $"Call to {_program[binding.Symbol].QualifiedName} has " +
                        $"{argumentCount} argument(s); expected {expected}.");
                }
            }
        }

        private bool TryGetTypeSymbol(
            SourceProgramTypeIndex type,
            out SourceProgramSymbolIndex symbol)
        {
            if (type.IsValid &&
                _program[type] is
                {
                    Kind: Avm1SourceTypeKind.Nominal,
                    Symbol.IsValid: true
                } nominal)
            {
                symbol = nominal.Symbol;
                return true;
            }
            symbol = SourceProgramSymbolIndex.Invalid;
            return false;
        }

        private Avm1SourceDiagnosticSeverity PolicySeverity =>
            _mode is Avm1SourceTypeCheckingMode.Strict
                ? Avm1SourceDiagnosticSeverity.Error
                : Avm1SourceDiagnosticSeverity.Warning;

        private void AddPolicyDiagnostic(
            string code,
            SourceProgramSymbolIndex symbol,
            string message) =>
            Add(
                code,
                PolicySeverity,
                arena: null,
                SourceOriginIndex.Invalid,
                symbol,
                message);

        private void Add(
            string code,
            Avm1SourceDiagnosticSeverity severity,
            Avm1SourceArena? arena,
            SourceOriginIndex origin,
            SourceProgramSymbolIndex symbol,
            string message) =>
            _diagnostics.Add(new Avm1SourceTypeCheckDiagnostic(
                code,
                severity,
                arena,
                origin,
                symbol,
                message));
    }
}
