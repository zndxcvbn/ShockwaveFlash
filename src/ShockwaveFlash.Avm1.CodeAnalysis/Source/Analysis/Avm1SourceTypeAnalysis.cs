namespace ShockwaveFlash.Avm1.Source;

[Flags]
public enum Avm1SourceTypeProvenance : ushort
{
    None = 0,
    Declaration = 1 << 0,
    ModelInference = 1 << 1,
    Literal = 1 << 2,
    Constructor = 1 << 3,
    Operator = 1 << 4,
    Assignment = 1 << 5,
    Return = 1 << 6,
    Member = 1 << 7,
    Function = 1 << 8,
    Intrinsic = 1 << 9,
    Parameter = 1 << 10,
    Dynamic = 1 << 11,
    Unsupported = 1 << 12,
    Reference = 1 << 13
}

public readonly record struct Avm1SourceTypeFact(
    SourceProgramTypeIndex Type,
    Avm1SourceTypeProvenance Provenance,
    bool IsAnnotationSafe)
{
    public static readonly Avm1SourceTypeFact None = new(
        SourceProgramTypeIndex.Invalid,
        Avm1SourceTypeProvenance.None,
        IsAnnotationSafe: false);

    public bool HasEvidence =>
        Type.IsValid && Provenance is not Avm1SourceTypeProvenance.None;
}

public sealed class Avm1SourceArenaTypeFacts
{
    private readonly Avm1SourceTypeFact[] _expressions;
    private readonly Avm1SourceTypeFact[] _symbols;
    private readonly Avm1SourceTypeFact[] _functionReturns;

    internal Avm1SourceArenaTypeFacts(
        Avm1SourceArena arena,
        Avm1SourceTypeFact[] expressions,
        Avm1SourceTypeFact[] symbols,
        Avm1SourceTypeFact[] functionReturns,
        Avm1SourceTypeFact rootReturnType)
    {
        Arena = arena;
        _expressions = expressions;
        _symbols = symbols;
        _functionReturns = functionReturns;
        RootReturnType = rootReturnType;
    }

    public Avm1SourceArena Arena { get; }

    public IReadOnlyList<Avm1SourceTypeFact> Expressions => _expressions;

    public IReadOnlyList<Avm1SourceTypeFact> Symbols => _symbols;

    public IReadOnlyList<Avm1SourceTypeFact> FunctionReturns => _functionReturns;

    public Avm1SourceTypeFact RootReturnType { get; }

    public Avm1SourceTypeFact this[SourceExpressionIndex expression] =>
        _expressions[expression.Value];

    public Avm1SourceTypeFact this[SourceSymbolIndex symbol] =>
        _symbols[symbol.Value];

    public Avm1SourceTypeFact this[SourceFunctionIndex function] =>
        _functionReturns[function.Value];
}

public sealed class Avm1SourceTypeAnalysis
{
    private const int MaximumIterations = 256;

    private readonly Avm1SourceTypeFact[] _programSymbols;
    private readonly Avm1SourceArenaTypeFacts[] _arenas;
    private readonly Dictionary<Avm1SourceArena, Avm1SourceArenaTypeFacts> _arenasByIdentity;

    private Avm1SourceTypeAnalysis(
        Avm1SourceProgram program,
        Avm1SourceTypeFact[] programSymbols,
        Avm1SourceArenaTypeFacts[] arenas,
        bool reachedFixedPoint,
        int iterationCount)
    {
        Program = program;
        _programSymbols = programSymbols;
        _arenas = arenas;
        _arenasByIdentity = new Dictionary<Avm1SourceArena, Avm1SourceArenaTypeFacts>(
            ReferenceEqualityComparer.Instance);
        foreach (var arena in arenas)
            _arenasByIdentity.Add(arena.Arena, arena);
        ReachedFixedPoint = reachedFixedPoint;
        IterationCount = iterationCount;
    }

    public Avm1SourceProgram Program { get; }

    public IReadOnlyList<Avm1SourceTypeFact> ProgramSymbols => _programSymbols;

    public IReadOnlyList<Avm1SourceArenaTypeFacts> Arenas => _arenas;

    public bool ReachedFixedPoint { get; }

    public int IterationCount { get; }

    public Avm1SourceTypeFact this[SourceProgramSymbolIndex symbol] =>
        _programSymbols[symbol.Value];

    public bool TryGetArenaFacts(
        Avm1SourceArena arena,
        out Avm1SourceArenaTypeFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(arena);
        return _arenasByIdentity.TryGetValue(arena, out facts);
    }

    public Avm1SourceTypeFact GetExpressionFact(
        Avm1SourceArena arena,
        SourceExpressionIndex expression) =>
        GetArenaFacts(arena)[expression];

    public Avm1SourceTypeFact GetSymbolFact(
        Avm1SourceArena arena,
        SourceSymbolIndex symbol) =>
        GetArenaFacts(arena)[symbol];

    public Avm1SourceTypeFact GetFunctionReturnFact(
        Avm1SourceArena arena,
        SourceFunctionIndex function) =>
        GetArenaFacts(arena)[function];

    public Avm1SourceTypeFact GetMethodReturnFact(Avm1SourceMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return GetArenaFacts(method.Arena).RootReturnType;
    }

    public bool TryGetAnnotation(
        Avm1SourceTypeFact fact,
        out Avm1SourceTypeReference? type)
    {
        type = null;
        if (!ReachedFixedPoint || !fact.IsAnnotationSafe || !fact.Type.IsValid)
            return false;

        var programType = Program[fact.Type];
        var name = programType.Kind switch
        {
            Avm1SourceTypeKind.Boolean => "Boolean",
            Avm1SourceTypeKind.Number => "Number",
            Avm1SourceTypeKind.String => "String",
            Avm1SourceTypeKind.Object => "Object",
            Avm1SourceTypeKind.Array => "Array",
            Avm1SourceTypeKind.Function => "Function",
            Avm1SourceTypeKind.Void => "Void",
            Avm1SourceTypeKind.Nominal when programType.Name is not null =>
                programType.Name.Value,
            _ => null
        };
        if (name is null)
            return false;

        type = new Avm1SourceTypeReference(new Avm1SourceQualifiedName(name));
        return true;
    }

    internal static Avm1SourceTypeAnalysis Build(
        Avm1SourceProgram program,
        IReadOnlyList<Avm1SourceArenaBindings> bindings)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(bindings);
        return new Analyzer(program, bindings).Run();
    }

    private Avm1SourceArenaTypeFacts GetArenaFacts(Avm1SourceArena arena)
    {
        if (!_arenasByIdentity.TryGetValue(arena, out var facts))
        {
            throw new ArgumentException(
                "The arena does not belong to this Source program.",
                nameof(arena));
        }
        return facts;
    }

    private sealed class Analyzer
    {
        private readonly Avm1SourceProgram _program;
        private readonly SourceProgramTypeIndex _unknownType;
        private readonly SourceProgramTypeIndex _conflictType;
        private readonly SourceProgramTypeIndex _undefinedType;
        private readonly SourceProgramTypeIndex _nullType;
        private readonly SourceProgramTypeIndex _booleanType;
        private readonly SourceProgramTypeIndex _numberType;
        private readonly SourceProgramTypeIndex _stringType;
        private readonly SourceProgramTypeIndex _objectType;
        private readonly SourceProgramTypeIndex _arrayType;
        private readonly SourceProgramTypeIndex _functionType;
        private readonly SourceProgramTypeIndex _voidType;
        private readonly Avm1SourceTypeFact[] _baseProgramFacts;
        private readonly ArenaState[] _arenas;

        public Analyzer(
            Avm1SourceProgram program,
            IReadOnlyList<Avm1SourceArenaBindings> bindings)
        {
            _program = program;
            _unknownType = program.GetBuiltInType(Avm1SourceTypeKind.Unknown);
            _conflictType = program.GetBuiltInType(Avm1SourceTypeKind.Conflict);
            _undefinedType = program.GetBuiltInType(Avm1SourceTypeKind.Undefined);
            _nullType = program.GetBuiltInType(Avm1SourceTypeKind.Null);
            _booleanType = program.GetBuiltInType(Avm1SourceTypeKind.Boolean);
            _numberType = program.GetBuiltInType(Avm1SourceTypeKind.Number);
            _stringType = program.GetBuiltInType(Avm1SourceTypeKind.String);
            _objectType = program.GetBuiltInType(Avm1SourceTypeKind.Object);
            _arrayType = program.GetBuiltInType(Avm1SourceTypeKind.Array);
            _functionType = program.GetBuiltInType(Avm1SourceTypeKind.Function);
            _voidType = program.GetBuiltInType(Avm1SourceTypeKind.Void);
            _baseProgramFacts = BuildBaseProgramFacts();
            _arenas = BuildArenaStates(bindings);
        }

        public Avm1SourceTypeAnalysis Run()
        {
            var programFacts = (Avm1SourceTypeFact[])_baseProgramFacts.Clone();
            var reachedFixedPoint = false;
            var iterationCount = 0;

            for (; iterationCount < MaximumIterations; iterationCount++)
            {
                foreach (var arena in _arenas)
                    ComputeExpressionFacts(arena, programFacts);

                var nextProgramFacts = (Avm1SourceTypeFact[])_baseProgramFacts.Clone();
                var programAssignments = CreateAccumulators(nextProgramFacts.Length);
                for (var i = 0; i < _baseProgramFacts.Length; i++)
                {
                    var seed = _baseProgramFacts[i];
                    if (seed.HasEvidence &&
                        seed.Provenance.HasFlag(
                            Avm1SourceTypeProvenance.ModelInference) &&
                        !seed.Provenance.HasFlag(
                            Avm1SourceTypeProvenance.Declaration))
                    {
                        programAssignments[i].Add(seed, this);
                    }
                }
                var changed = false;

                foreach (var arena in _arenas)
                {
                    var nextSymbols = BuildNextSymbolFacts(arena, programAssignments);
                    var nextFunctionReturns = new Avm1SourceTypeFact[arena.Arena.Functions.Count];
                    for (var i = 0; i < nextFunctionReturns.Length; i++)
                    {
                        nextFunctionReturns[i] = InferReturnType(
                            arena,
                            arena.Arena[new SourceFunctionIndex(i)].Body);
                    }

                    var nextRootReturn = arena.RootStatement.IsValid
                        ? InferReturnType(arena, arena.RootStatement)
                        : Avm1SourceTypeFact.None;

                    if (arena.ProgramMember.IsValid)
                    {
                        var member = _program[arena.ProgramMember];
                        if (member.Kind is Avm1SourceProgramSymbolKind.Field &&
                            arena.RootExpression.IsValid)
                        {
                            programAssignments[arena.ProgramMember.Value].Add(
                                arena.ExpressionFacts[arena.RootExpression.Value],
                                this);
                        }
                        else if (member.Kind is
                                Avm1SourceProgramSymbolKind.Method or
                                Avm1SourceProgramSymbolKind.Getter or
                                Avm1SourceProgramSymbolKind.Setter)
                        {
                            programAssignments[arena.ProgramMember.Value].Add(
                                nextRootReturn,
                                this);
                        }
                    }

                    changed |= !nextSymbols.AsSpan().SequenceEqual(arena.SymbolFacts);
                    changed |= !nextFunctionReturns.AsSpan().SequenceEqual(arena.FunctionReturnFacts);
                    changed |= nextRootReturn != arena.RootReturnFact;
                    arena.NextSymbolFacts = nextSymbols;
                    arena.NextFunctionReturnFacts = nextFunctionReturns;
                    arena.NextRootReturnFact = nextRootReturn;
                }

                for (var i = 0; i < nextProgramFacts.Length; i++)
                {
                    if (_baseProgramFacts[i].Provenance.HasFlag(
                            Avm1SourceTypeProvenance.Declaration) ||
                        _baseProgramFacts[i].Provenance.HasFlag(
                            Avm1SourceTypeProvenance.Reference))
                        continue;
                    if (programAssignments[i].HasValue)
                        nextProgramFacts[i] = programAssignments[i].Fact;
                }

                changed |= !nextProgramFacts.AsSpan().SequenceEqual(programFacts);
                programFacts = nextProgramFacts;
                foreach (var arena in _arenas)
                {
                    arena.SymbolFacts = arena.NextSymbolFacts;
                    arena.FunctionReturnFacts = arena.NextFunctionReturnFacts;
                    arena.RootReturnFact = arena.NextRootReturnFact;
                }

                if (!changed)
                {
                    reachedFixedPoint = true;
                    iterationCount++;
                    break;
                }
            }

            foreach (var arena in _arenas)
                ComputeExpressionFacts(arena, programFacts);

            var arenaResults = _arenas.Select(arena => new Avm1SourceArenaTypeFacts(
                arena.Arena,
                (Avm1SourceTypeFact[])arena.ExpressionFacts.Clone(),
                (Avm1SourceTypeFact[])arena.SymbolFacts.Clone(),
                (Avm1SourceTypeFact[])arena.FunctionReturnFacts.Clone(),
                arena.RootReturnFact)).ToArray();
            return new Avm1SourceTypeAnalysis(
                _program,
                programFacts,
                arenaResults,
                reachedFixedPoint,
                iterationCount);
        }

        private Avm1SourceTypeFact[] BuildBaseProgramFacts()
        {
            var result = new Avm1SourceTypeFact[_program.Symbols.Count];
            Array.Fill(result, Avm1SourceTypeFact.None);
            foreach (var symbol in _program.Symbols)
            {
                var declaredKind = symbol.DeclaredType.IsValid
                    ? _program[symbol.DeclaredType].Kind
                    : Avm1SourceTypeKind.Unknown;
                if (declaredKind is not Avm1SourceTypeKind.Unknown)
                {
                    result[symbol.Index.Value] = Known(
                        symbol.DeclaredType,
                        symbol.Flags.HasFlag(Avm1SourceProgramSymbolFlags.External)
                            ? Avm1SourceTypeProvenance.Reference
                            : Avm1SourceTypeProvenance.Declaration);
                    continue;
                }

                var inferredKind = symbol.InferredType.IsValid
                    ? _program[symbol.InferredType].Kind
                    : Avm1SourceTypeKind.Unknown;
                if (inferredKind is not Avm1SourceTypeKind.Unknown)
                {
                    result[symbol.Index.Value] = Known(
                        symbol.InferredType,
                        Avm1SourceTypeProvenance.ModelInference,
                        IsSourceAnnotationType(inferredKind));
                }
            }
            return result;
        }

        private ArenaState[] BuildArenaStates(
            IReadOnlyList<Avm1SourceArenaBindings> bindings)
        {
            var owners = new Dictionary<Avm1SourceArena, ArenaOwner>(
                ReferenceEqualityComparer.Instance);
            foreach (var file in _program.Files)
            {
                foreach (var sourceClass in file.Classes)
                {
                    foreach (var member in sourceClass.Members)
                    {
                        _program.TryGetSymbol(member, out var programMember);
                        switch (member)
                        {
                            case Avm1SourceField { Initializer: { } fieldInitializer }:
                                owners.Add(
                                    fieldInitializer.Arena,
                                    new ArenaOwner(
                                        SourceStatementIndex.Invalid,
                                        fieldInitializer.Expression,
                                        programMember));
                                break;
                            case Avm1SourceMethodDeclaration method:
                                owners.Add(
                                    method.Body.Arena,
                                    new ArenaOwner(
                                        method.Body.Body,
                                        SourceExpressionIndex.Invalid,
                                        programMember));
                                break;
                        }
                    }

                    if (sourceClass.Initializer is { } classInitializer)
                    {
                        owners.Add(
                            classInitializer.Body.Arena,
                            new ArenaOwner(
                                classInitializer.Body.Body,
                                SourceExpressionIndex.Invalid,
                                SourceProgramSymbolIndex.Invalid));
                    }
                }
            }

            var result = new ArenaState[bindings.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var arenaBindings = bindings[i];
                if (!owners.TryGetValue(arenaBindings.Arena, out var owner))
                    owner = ArenaOwner.Empty;
                result[i] = new ArenaState(
                    arenaBindings,
                    owner,
                    BuildBaseSymbolFacts(arenaBindings.Arena));
            }
            return result;
        }

        private Avm1SourceTypeFact[] BuildBaseSymbolFacts(Avm1SourceArena arena)
        {
            var result = new Avm1SourceTypeFact[arena.Symbols.Count];
            Array.Fill(result, Avm1SourceTypeFact.None);
            foreach (var symbol in arena.Symbols)
            {
                if (symbol.DeclaredType.IsValid &&
                    TryMapArenaType(arena, symbol.DeclaredType, out var declaredType))
                {
                    result[symbol.Index.Value] = Known(
                        declaredType,
                        Avm1SourceTypeProvenance.Declaration);
                    continue;
                }

                if (symbol.Kind is
                        Avm1SourceSymbolKind.Local or
                        Avm1SourceSymbolKind.Temporary &&
                    symbol.InferredType.IsValid &&
                    TryMapArenaType(arena, symbol.InferredType, out var inferredType) &&
                    _program[inferredType].Kind is not Avm1SourceTypeKind.Unknown)
                {
                    result[symbol.Index.Value] = Known(
                        inferredType,
                        Avm1SourceTypeProvenance.ModelInference,
                        IsSourceAnnotationType(_program[inferredType].Kind));
                    continue;
                }

                result[symbol.Index.Value] = symbol.Kind switch
                {
                    Avm1SourceSymbolKind.Function => Known(
                        _functionType,
                        Avm1SourceTypeProvenance.Function),
                    Avm1SourceSymbolKind.Parameter => Unknown(
                        Avm1SourceTypeProvenance.Parameter),
                    Avm1SourceSymbolKind.Catch => Unknown(
                        Avm1SourceTypeProvenance.Dynamic),
                    Avm1SourceSymbolKind.Arguments => Known(
                        _objectType,
                        Avm1SourceTypeProvenance.Intrinsic),
                    Avm1SourceSymbolKind.Root or
                    Avm1SourceSymbolKind.Parent or
                    Avm1SourceSymbolKind.Global => Unknown(
                        Avm1SourceTypeProvenance.Dynamic),
                    _ => Avm1SourceTypeFact.None
                };
            }
            return result;
        }

        private void ComputeExpressionFacts(
            ArenaState arena,
            Avm1SourceTypeFact[] programFacts)
        {
            arena.ExpressionFacts = new Avm1SourceTypeFact[arena.Arena.Expressions.Count];
            var states = new byte[arena.ExpressionFacts.Length];
            for (var i = 0; i < arena.ExpressionFacts.Length; i++)
            {
                InferExpression(
                    arena,
                    new SourceExpressionIndex(i),
                    programFacts,
                    states);
            }
        }

        private Avm1SourceTypeFact InferExpression(
            ArenaState arena,
            SourceExpressionIndex index,
            Avm1SourceTypeFact[] programFacts,
            byte[] states)
        {
            if (!index.IsValid)
                return Avm1SourceTypeFact.None;
            if (states[index.Value] == 2)
                return arena.ExpressionFacts[index.Value];
            if (states[index.Value] == 1)
                return Unknown(Avm1SourceTypeProvenance.Unsupported);

            states[index.Value] = 1;
            var expression = arena.Arena[index];
            Avm1SourceTypeFact Child(int childIndex) => InferExpression(
                arena,
                arena.Arena.GetChild(expression, childIndex),
                programFacts,
                states);

            var fact = expression.Kind switch
            {
                Avm1SourceExpressionKind.Literal => InferLiteral(arena.Arena, expression),
                Avm1SourceExpressionKind.SymbolReference when expression.Symbol.IsValid =>
                    arena.SymbolFacts[expression.Symbol.Value],
                Avm1SourceExpressionKind.SymbolReference => Unknown(
                    Avm1SourceTypeProvenance.Unsupported),
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.ComputedDynamicName => Unknown(
                    Avm1SourceTypeProvenance.Dynamic),
                Avm1SourceExpressionKind.QualifiedName => InferBoundReference(
                    arena,
                    index,
                    programFacts,
                    fallback: Unknown(Avm1SourceTypeProvenance.Dynamic)),
                Avm1SourceExpressionKind.Assignment when expression.Children.Count >= 2 =>
                    InferOperator(expression.Operator, Child(0), Child(1)),
                Avm1SourceExpressionKind.Unary when expression.Children.Count >= 1 =>
                    InferOperator(expression.Operator, Child(0), Avm1SourceTypeFact.None),
                Avm1SourceExpressionKind.Binary when expression.Children.Count >= 2 =>
                    InferOperator(expression.Operator, Child(0), Child(1)),
                Avm1SourceExpressionKind.Sequence when expression.Children.Count >= 1 =>
                    AddProvenance(
                        Child(expression.Children.Count - 1),
                        Avm1SourceTypeProvenance.Operator),
                Avm1SourceExpressionKind.Conditional when expression.Children.Count >= 3 =>
                    AddProvenance(
                        Join(Child(1), Child(2)),
                        Avm1SourceTypeProvenance.Operator),
                Avm1SourceExpressionKind.MemberAccess => InferMember(
                    arena,
                    index,
                    expression,
                    programFacts,
                    Child),
                Avm1SourceExpressionKind.Delete => Known(
                    _booleanType,
                    Avm1SourceTypeProvenance.Operator),
                Avm1SourceExpressionKind.IntrinsicCall => InferIntrinsic(arena.Arena, expression),
                Avm1SourceExpressionKind.Call => InferCall(
                    arena,
                    expression,
                    programFacts),
                Avm1SourceExpressionKind.New => InferNew(
                    arena,
                    expression,
                    programFacts),
                Avm1SourceExpressionKind.ArrayLiteral => Known(
                    _arrayType,
                    Avm1SourceTypeProvenance.Literal),
                Avm1SourceExpressionKind.ObjectLiteral => Known(
                    _objectType,
                    Avm1SourceTypeProvenance.Literal),
                Avm1SourceExpressionKind.FunctionLiteral => Known(
                    _functionType,
                    Avm1SourceTypeProvenance.Function),
                Avm1SourceExpressionKind.Opaque => Unknown(
                    Avm1SourceTypeProvenance.Unsupported),
                _ => Unknown(Avm1SourceTypeProvenance.Unsupported)
            };

            arena.ExpressionFacts[index.Value] = fact;
            states[index.Value] = 2;
            return fact;
        }

        private Avm1SourceTypeFact InferLiteral(
            Avm1SourceArena arena,
            Avm1SourceExpression expression)
        {
            if (!expression.Literal.IsValid)
                return Unknown(Avm1SourceTypeProvenance.Unsupported);
            return arena[expression.Literal].Kind switch
            {
                Avm1SourceLiteralKind.Undefined => Known(
                    _undefinedType,
                    Avm1SourceTypeProvenance.Literal,
                    annotationSafe: false),
                Avm1SourceLiteralKind.Null => Known(
                    _nullType,
                    Avm1SourceTypeProvenance.Literal,
                    annotationSafe: false),
                Avm1SourceLiteralKind.Boolean => Known(
                    _booleanType,
                    Avm1SourceTypeProvenance.Literal),
                Avm1SourceLiteralKind.Integer or Avm1SourceLiteralKind.Number => Known(
                    _numberType,
                    Avm1SourceTypeProvenance.Literal),
                Avm1SourceLiteralKind.String => Known(
                    _stringType,
                    Avm1SourceTypeProvenance.Literal),
                _ => Unknown(Avm1SourceTypeProvenance.Unsupported)
            };
        }

        private Avm1SourceTypeFact InferMember(
            ArenaState arena,
            SourceExpressionIndex index,
            Avm1SourceExpression expression,
            Avm1SourceTypeFact[] programFacts,
            Func<int, Avm1SourceTypeFact> child)
        {
            var bound = InferBoundReference(
                arena,
                index,
                programFacts,
                Avm1SourceTypeFact.None);
            if (bound.HasEvidence)
                return AddProvenance(bound, Avm1SourceTypeProvenance.Member);

            if (expression.Children.Count >= 2 &&
                TryGetStringLiteral(
                    arena.Arena,
                    arena.Arena.GetChild(expression, 1),
                    out var memberName) &&
                memberName == "length")
            {
                var receiver = child(0);
                if (IsType(receiver, Avm1SourceTypeKind.Array) ||
                    IsType(receiver, Avm1SourceTypeKind.String))
                {
                    return Known(
                        _numberType,
                        receiver.Provenance | Avm1SourceTypeProvenance.Member,
                        receiver.IsAnnotationSafe);
                }
            }

            return Unknown(Avm1SourceTypeProvenance.Dynamic);
        }

        private Avm1SourceTypeFact InferCall(
            ArenaState arena,
            Avm1SourceExpression expression,
            Avm1SourceTypeFact[] programFacts)
        {
            if (expression.Children.Count == 0)
                return Unknown(Avm1SourceTypeProvenance.Unsupported);

            var callee = arena.Arena.GetChild(expression, 0);
            var binding = arena.Bindings[callee];
            if (binding.IsBound)
            {
                var symbol = _program[binding.Symbol];
                if (symbol.Kind is Avm1SourceProgramSymbolKind.Class or
                    Avm1SourceProgramSymbolKind.Interface)
                {
                    return AddProvenance(
                        programFacts[binding.Symbol.Value],
                        Avm1SourceTypeProvenance.Constructor);
                }
                if (symbol.Kind is
                        Avm1SourceProgramSymbolKind.Method or
                        Avm1SourceProgramSymbolKind.Getter or
                        Avm1SourceProgramSymbolKind.Setter)
                {
                    return programFacts[binding.Symbol.Value];
                }
            }

            if (TryGetExpressionName(arena.Arena, callee, out var name))
            {
                return name switch
                {
                    "Number" => Known(_numberType, Avm1SourceTypeProvenance.Intrinsic),
                    "String" => Known(_stringType, Avm1SourceTypeProvenance.Intrinsic),
                    "Boolean" => Known(_booleanType, Avm1SourceTypeProvenance.Intrinsic),
                    _ => Unknown(Avm1SourceTypeProvenance.Dynamic)
                };
            }

            return Unknown(Avm1SourceTypeProvenance.Dynamic);
        }

        private Avm1SourceTypeFact InferNew(
            ArenaState arena,
            Avm1SourceExpression expression,
            Avm1SourceTypeFact[] programFacts)
        {
            if (expression.Children.Count == 0)
                return Known(_objectType, Avm1SourceTypeProvenance.Constructor);

            var callee = arena.Arena.GetChild(expression, 0);
            var binding = arena.Bindings[callee];
            if (binding.IsBound &&
                _program[binding.Symbol].Kind is Avm1SourceProgramSymbolKind.Class or
                    Avm1SourceProgramSymbolKind.Interface)
            {
                return AddProvenance(
                    programFacts[binding.Symbol.Value],
                    Avm1SourceTypeProvenance.Constructor);
            }

            if (TryGetExpressionName(arena.Arena, callee, out var name))
            {
                return name switch
                {
                    "Array" => Known(_arrayType, Avm1SourceTypeProvenance.Constructor),
                    "Object" => Known(_objectType, Avm1SourceTypeProvenance.Constructor),
                    "Function" => Known(_functionType, Avm1SourceTypeProvenance.Constructor),
                    _ => Known(_objectType, Avm1SourceTypeProvenance.Constructor)
                };
            }

            return Known(_objectType, Avm1SourceTypeProvenance.Constructor);
        }

        private Avm1SourceTypeFact InferBoundReference(
            ArenaState arena,
            SourceExpressionIndex expression,
            Avm1SourceTypeFact[] programFacts,
            Avm1SourceTypeFact fallback)
        {
            var binding = arena.Bindings[expression];
            if (!binding.IsBound)
                return fallback;

            var symbol = _program[binding.Symbol];
            return symbol.Kind switch
            {
                Avm1SourceProgramSymbolKind.Class or
                Avm1SourceProgramSymbolKind.Interface or
                Avm1SourceProgramSymbolKind.Field => programFacts[binding.Symbol.Value],
                Avm1SourceProgramSymbolKind.Getter => programFacts[binding.Symbol.Value],
                Avm1SourceProgramSymbolKind.Method or
                Avm1SourceProgramSymbolKind.Constructor or
                Avm1SourceProgramSymbolKind.Setter when
                    !arena.Bindings.GetUse(expression).HasFlag(Avm1SourceExpressionUse.Invoke) =>
                    Known(_functionType, Avm1SourceTypeProvenance.Member),
                Avm1SourceProgramSymbolKind.Method or
                Avm1SourceProgramSymbolKind.Constructor or
                Avm1SourceProgramSymbolKind.Setter => programFacts[binding.Symbol.Value],
                _ => fallback
            };
        }

        private Avm1SourceTypeFact InferIntrinsic(
            Avm1SourceArena arena,
            Avm1SourceExpression expression)
        {
            var name = expression.Name.IsValid ? arena[expression.Name] : string.Empty;
            return name switch
            {
                "Number" or "int" or "getTimer" or "random" or
                    "length" or "mblength" or "ord" or "mbord" => Known(
                    _numberType,
                    Avm1SourceTypeProvenance.Intrinsic),
                "String" or "targetPath" or "chr" or "mbchr" or
                    "substring" or "mbsubstring" => Known(
                    _stringType,
                    Avm1SourceTypeProvenance.Intrinsic),
                "getProperty" => Unknown(
                    Avm1SourceTypeProvenance.Intrinsic),
                "trace" or "nextFrame" or "prevFrame" or "play" or
                    "stop" or "stopAllSounds" or "toggleHighQuality" or
                    "call" or "gotoAndPlay" or "gotoAndStop" or
                    "setProperty" or "duplicateMovieClip" or
                    "removeMovieClip" or "startDrag" or "stopDrag" or
                    "getURL" or "fscommand" or
                    "loadMovie" or "loadMovieNum" or "loadVariables" or
                    "loadVariablesNum" => Known(
                    _undefinedType,
                    Avm1SourceTypeProvenance.Intrinsic,
                    annotationSafe: false),
                _ => Unknown(Avm1SourceTypeProvenance.Unsupported)
            };
        }

        private Avm1SourceTypeFact InferOperator(
            Avm1SourceOperator @operator,
            Avm1SourceTypeFact left,
            Avm1SourceTypeFact right)
        {
            switch (@operator)
            {
                case Avm1SourceOperator.Assign:
                    return AddProvenance(right, Avm1SourceTypeProvenance.Assignment);
                case Avm1SourceOperator.AddAssign:
                case Avm1SourceOperator.Add:
                    if (IsType(left, Avm1SourceTypeKind.Number) &&
                        IsType(right, Avm1SourceTypeKind.Number))
                    {
                        return Known(
                            _numberType,
                            left.Provenance | right.Provenance | Avm1SourceTypeProvenance.Operator,
                            left.IsAnnotationSafe && right.IsAnnotationSafe);
                    }
                    if (IsType(left, Avm1SourceTypeKind.String) &&
                        IsType(right, Avm1SourceTypeKind.String))
                    {
                        return Known(
                            _stringType,
                            left.Provenance | right.Provenance | Avm1SourceTypeProvenance.Operator,
                            left.IsAnnotationSafe && right.IsAnnotationSafe);
                    }
                    return Unknown(Avm1SourceTypeProvenance.Operator);
                case Avm1SourceOperator.SubtractAssign:
                case Avm1SourceOperator.MultiplyAssign:
                case Avm1SourceOperator.DivideAssign:
                case Avm1SourceOperator.ModuloAssign:
                case Avm1SourceOperator.BitAndAssign:
                case Avm1SourceOperator.BitOrAssign:
                case Avm1SourceOperator.BitXorAssign:
                case Avm1SourceOperator.ShiftLeftAssign:
                case Avm1SourceOperator.ShiftRightAssign:
                case Avm1SourceOperator.ShiftRightUnsignedAssign:
                case Avm1SourceOperator.PrefixIncrement:
                case Avm1SourceOperator.PrefixDecrement:
                case Avm1SourceOperator.PostfixIncrement:
                case Avm1SourceOperator.PostfixDecrement:
                case Avm1SourceOperator.BitwiseNot:
                case Avm1SourceOperator.UnaryPlus:
                case Avm1SourceOperator.UnaryMinus:
                case Avm1SourceOperator.Subtract:
                case Avm1SourceOperator.Multiply:
                case Avm1SourceOperator.Divide:
                case Avm1SourceOperator.Modulo:
                case Avm1SourceOperator.BitAnd:
                case Avm1SourceOperator.BitOr:
                case Avm1SourceOperator.BitXor:
                case Avm1SourceOperator.ShiftLeft:
                case Avm1SourceOperator.ShiftRight:
                case Avm1SourceOperator.ShiftRightUnsigned:
                    return Known(_numberType, Avm1SourceTypeProvenance.Operator);
                case Avm1SourceOperator.LogicalNot:
                case Avm1SourceOperator.Equal:
                case Avm1SourceOperator.NotEqual:
                case Avm1SourceOperator.StrictEqual:
                case Avm1SourceOperator.StrictNotEqual:
                case Avm1SourceOperator.Less:
                case Avm1SourceOperator.LessOrEqual:
                case Avm1SourceOperator.Greater:
                case Avm1SourceOperator.GreaterOrEqual:
                case Avm1SourceOperator.In:
                case Avm1SourceOperator.InstanceOf:
                    return Known(_booleanType, Avm1SourceTypeProvenance.Operator);
                case Avm1SourceOperator.TypeOf:
                    return Known(_stringType, Avm1SourceTypeProvenance.Operator);
                case Avm1SourceOperator.Void:
                    return Known(
                        _undefinedType,
                        Avm1SourceTypeProvenance.Operator,
                        annotationSafe: false);
                case Avm1SourceOperator.LogicalAnd:
                case Avm1SourceOperator.LogicalOr:
                    return AddProvenance(
                        Join(left, right),
                        Avm1SourceTypeProvenance.Operator);
                default:
                    return Unknown(Avm1SourceTypeProvenance.Unsupported);
            }
        }

        private Avm1SourceTypeFact[] BuildNextSymbolFacts(
            ArenaState arena,
            FactAccumulator[] programAssignments)
        {
            var next = (Avm1SourceTypeFact[])arena.BaseSymbolFacts.Clone();
            var assignments = CreateAccumulators(next.Length);
            var declarationInitializers = new bool[next.Length];

            for (var i = 0; i < arena.BaseSymbolFacts.Length; i++)
            {
                var seed = arena.BaseSymbolFacts[i];
                if (seed.HasEvidence &&
                    seed.Provenance.HasFlag(
                        Avm1SourceTypeProvenance.ModelInference) &&
                    !seed.Provenance.HasFlag(
                        Avm1SourceTypeProvenance.Declaration))
                {
                    assignments[i].Add(seed, this);
                }
            }

            foreach (var statement in arena.Arena.Statements)
            {
                if (statement.Kind is Avm1SourceStatementKind.VariableDeclaration &&
                    statement.Symbol.IsValid &&
                    statement.Expression.IsValid)
                {
                    assignments[statement.Symbol.Value].Add(
                        arena.ExpressionFacts[statement.Expression.Value],
                        this);
                    declarationInitializers[statement.Symbol.Value] = true;
                }

                if (statement.Kind is Avm1SourceStatementKind.ForIn &&
                    statement.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey) &&
                    TryGetReferencedSymbol(arena.Arena, statement.Expression, out var keySymbol))
                {
                    assignments[keySymbol.Value].Add(
                        Known(_stringType, Avm1SourceTypeProvenance.Intrinsic),
                        this);
                    declarationInitializers[keySymbol.Value] = true;
                }
            }

            foreach (var expression in arena.Arena.Expressions)
            {
                if (expression.Kind is Avm1SourceExpressionKind.Assignment &&
                    expression.Children.Count >= 2)
                {
                    var target = arena.Arena.GetChild(expression, 0);
                    var source = arena.ExpressionFacts[expression.Index.Value];
                    AddAssignment(
                        arena,
                        target,
                        source,
                        assignments,
                        programAssignments);
                }
                else if (expression.Kind is Avm1SourceExpressionKind.Unary &&
                    expression.Operator is
                        Avm1SourceOperator.PrefixIncrement or
                        Avm1SourceOperator.PrefixDecrement or
                        Avm1SourceOperator.PostfixIncrement or
                        Avm1SourceOperator.PostfixDecrement &&
                    expression.Children.Count > 0)
                {
                    AddAssignment(
                        arena,
                        arena.Arena.GetChild(expression, 0),
                        Known(_numberType, Avm1SourceTypeProvenance.Operator),
                        assignments,
                        programAssignments);
                }
            }

            for (var i = 0; i < next.Length; i++)
            {
                var canInfer = !next[i].HasEvidence ||
                    next[i].Provenance.HasFlag(
                        Avm1SourceTypeProvenance.ModelInference);
                if (!canInfer || !assignments[i].HasValue)
                    continue;
                var fact = assignments[i].Fact;
                var kind = arena.Arena[new SourceSymbolIndex(i)].Kind;
                var annotationSafe = fact.IsAnnotationSafe &&
                    declarationInitializers[i] &&
                    kind is Avm1SourceSymbolKind.Local or Avm1SourceSymbolKind.Temporary;
                next[i] = fact with { IsAnnotationSafe = annotationSafe };
            }
            return next;
        }

        private void AddAssignment(
            ArenaState arena,
            SourceExpressionIndex target,
            Avm1SourceTypeFact source,
            FactAccumulator[] localAssignments,
            FactAccumulator[] programAssignments)
        {
            if (TryGetReferencedSymbol(arena.Arena, target, out var localSymbol))
            {
                localAssignments[localSymbol.Value].Add(
                    AddProvenance(source, Avm1SourceTypeProvenance.Assignment),
                    this);
                return;
            }

            var binding = arena.Bindings[target];
            if (binding.IsBound &&
                _program[binding.Symbol].Kind is Avm1SourceProgramSymbolKind.Field)
            {
                programAssignments[binding.Symbol.Value].Add(
                    AddProvenance(source, Avm1SourceTypeProvenance.Assignment),
                    this);
            }
        }

        private Avm1SourceTypeFact InferReturnType(
            ArenaState arena,
            SourceStatementIndex root)
        {
            var accumulator = new FactAccumulator();
            var hasValueReturn = false;
            var hasVoidReturn = false;
            var visited = new HashSet<int>();
            CollectReturns(root);

            if (!hasValueReturn)
            {
                return Known(
                    _voidType,
                    Avm1SourceTypeProvenance.Return,
                    annotationSafe: true);
            }
            if (hasVoidReturn)
            {
                return new Avm1SourceTypeFact(
                    _conflictType,
                    Avm1SourceTypeProvenance.Return,
                    IsAnnotationSafe: false);
            }

            var result = accumulator.HasValue
                ? AddProvenance(accumulator.Fact, Avm1SourceTypeProvenance.Return)
                : Avm1SourceTypeFact.None;
            return result with
            {
                IsAnnotationSafe = result.IsAnnotationSafe && AlwaysCompletes(root)
            };

            void CollectReturns(SourceStatementIndex statementIndex)
            {
                if (!statementIndex.IsValid || !visited.Add(statementIndex.Value))
                    return;
                var statement = arena.Arena[statementIndex];
                if (statement.Kind is Avm1SourceStatementKind.Return)
                {
                    if (statement.Expression.IsValid)
                    {
                        hasValueReturn = true;
                        accumulator.Add(
                            arena.ExpressionFacts[statement.Expression.Value],
                            this);
                    }
                    else
                    {
                        hasVoidReturn = true;
                    }
                    return;
                }

                for (var i = 0; i < statement.Initializers.Count; i++)
                    CollectReturns(arena.Arena.GetInitializer(statement, i));
                for (var i = 0; i < statement.Children.Count; i++)
                    CollectReturns(arena.Arena.GetChild(statement, i));
            }

            bool AlwaysCompletes(SourceStatementIndex statementIndex)
            {
                if (!statementIndex.IsValid)
                    return false;
                var statement = arena.Arena[statementIndex];
                switch (statement.Kind)
                {
                    case Avm1SourceStatementKind.Return:
                    case Avm1SourceStatementKind.Throw:
                        return true;
                    case Avm1SourceStatementKind.Block:
                    case Avm1SourceStatementKind.SwitchCase:
                    case Avm1SourceStatementKind.SwitchDefault:
                        for (var i = 0; i < statement.Children.Count; i++)
                        {
                            if (AlwaysCompletes(arena.Arena.GetChild(statement, i)))
                                return true;
                        }
                        return false;
                    case Avm1SourceStatementKind.If when statement.Children.Count == 2:
                        return AlwaysCompletes(arena.Arena.GetChild(statement, 0)) &&
                            AlwaysCompletes(arena.Arena.GetChild(statement, 1));
                    default:
                        return false;
                }
            }
        }

        private Avm1SourceTypeFact Join(
            Avm1SourceTypeFact left,
            Avm1SourceTypeFact right)
        {
            if (!left.HasEvidence || !right.HasEvidence)
                return Avm1SourceTypeFact.None;

            var leftKind = _program[left.Type].Kind;
            var rightKind = _program[right.Type].Kind;
            var provenance = left.Provenance | right.Provenance;
            if (leftKind is Avm1SourceTypeKind.Conflict ||
                rightKind is Avm1SourceTypeKind.Conflict)
            {
                return new Avm1SourceTypeFact(
                    _conflictType,
                    provenance,
                    IsAnnotationSafe: false);
            }
            if (leftKind is Avm1SourceTypeKind.Unknown ||
                rightKind is Avm1SourceTypeKind.Unknown)
            {
                return new Avm1SourceTypeFact(
                    _unknownType,
                    provenance,
                    IsAnnotationSafe: false);
            }
            if (left.Type == right.Type)
            {
                return new Avm1SourceTypeFact(
                    left.Type,
                    provenance,
                    left.IsAnnotationSafe && right.IsAnnotationSafe);
            }

            return new Avm1SourceTypeFact(
                _conflictType,
                provenance,
                IsAnnotationSafe: false);
        }

        private static Avm1SourceTypeFact Known(
            SourceProgramTypeIndex type,
            Avm1SourceTypeProvenance provenance,
            bool annotationSafe = true) =>
            new(type, provenance, annotationSafe);

        private Avm1SourceTypeFact Unknown(Avm1SourceTypeProvenance provenance) =>
            new(_unknownType, provenance, IsAnnotationSafe: false);

        private static Avm1SourceTypeFact AddProvenance(
            Avm1SourceTypeFact fact,
            Avm1SourceTypeProvenance provenance) =>
            fact.HasEvidence ? fact with { Provenance = fact.Provenance | provenance } : fact;

        private bool IsType(Avm1SourceTypeFact fact, Avm1SourceTypeKind kind) =>
            fact.Type.IsValid && _program[fact.Type].Kind == kind;

        private static bool IsSourceAnnotationType(Avm1SourceTypeKind kind) =>
            kind is
                Avm1SourceTypeKind.Boolean or
                Avm1SourceTypeKind.Number or
                Avm1SourceTypeKind.String or
                Avm1SourceTypeKind.Object or
                Avm1SourceTypeKind.Array or
                Avm1SourceTypeKind.Function or
                Avm1SourceTypeKind.Void or
                Avm1SourceTypeKind.Nominal;

        private bool TryMapArenaType(
            Avm1SourceArena arena,
            SourceTypeIndex sourceType,
            out SourceProgramTypeIndex programType)
        {
            var type = arena[sourceType];
            if (type.Kind is Avm1SourceTypeKind.Nominal && type.Name.IsValid)
                return _program.TryGetType(arena[type.Name], out programType);
            if (type.Kind is not Avm1SourceTypeKind.Nominal)
            {
                programType = _program.GetBuiltInType(type.Kind);
                return true;
            }

            programType = SourceProgramTypeIndex.Invalid;
            return false;
        }

        private static FactAccumulator[] CreateAccumulators(int count)
        {
            return new FactAccumulator[count];
        }

        private static bool TryGetReferencedSymbol(
            Avm1SourceArena arena,
            SourceExpressionIndex expressionIndex,
            out SourceSymbolIndex symbol)
        {
            symbol = SourceSymbolIndex.Invalid;
            if (!expressionIndex.IsValid)
                return false;
            var expression = arena[expressionIndex];
            if (expression.Kind is not Avm1SourceExpressionKind.SymbolReference ||
                !expression.Symbol.IsValid)
            {
                return false;
            }
            symbol = expression.Symbol;
            return true;
        }

        private static bool TryGetStringLiteral(
            Avm1SourceArena arena,
            SourceExpressionIndex expressionIndex,
            out string value)
        {
            value = string.Empty;
            if (!expressionIndex.IsValid)
                return false;
            var expression = arena[expressionIndex];
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid)
            {
                return false;
            }
            var literal = arena[expression.Literal];
            if (literal.Kind is not Avm1SourceLiteralKind.String ||
                !literal.StringValue.IsValid)
            {
                return false;
            }
            value = arena[literal.StringValue];
            return true;
        }

        private static bool TryGetExpressionName(
            Avm1SourceArena arena,
            SourceExpressionIndex expressionIndex,
            out string name)
        {
            name = string.Empty;
            if (!expressionIndex.IsValid)
                return false;
            var expression = arena[expressionIndex];
            if (expression.Kind is
                    Avm1SourceExpressionKind.DynamicName or
                    Avm1SourceExpressionKind.QualifiedName &&
                expression.Name.IsValid)
            {
                name = arena[expression.Name];
                return true;
            }
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                expression.Symbol.IsValid)
            {
                var symbol = arena[expression.Symbol];
                if (symbol.Name.IsValid)
                {
                    name = arena[symbol.Name];
                    return true;
                }
            }
            return false;
        }

        private sealed class ArenaState
        {
            public ArenaState(
                Avm1SourceArenaBindings bindings,
                ArenaOwner owner,
                Avm1SourceTypeFact[] baseSymbolFacts)
            {
                Bindings = bindings;
                Arena = bindings.Arena;
                RootStatement = owner.RootStatement;
                RootExpression = owner.RootExpression;
                ProgramMember = owner.ProgramMember;
                BaseSymbolFacts = baseSymbolFacts;
                SymbolFacts = (Avm1SourceTypeFact[])baseSymbolFacts.Clone();
                NextSymbolFacts = SymbolFacts;
                ExpressionFacts = new Avm1SourceTypeFact[Arena.Expressions.Count];
                FunctionReturnFacts = new Avm1SourceTypeFact[Arena.Functions.Count];
                Array.Fill(FunctionReturnFacts, Avm1SourceTypeFact.None);
                NextFunctionReturnFacts = FunctionReturnFacts;
                RootReturnFact = Avm1SourceTypeFact.None;
                NextRootReturnFact = Avm1SourceTypeFact.None;
            }

            public Avm1SourceArenaBindings Bindings { get; }

            public Avm1SourceArena Arena { get; }

            public SourceStatementIndex RootStatement { get; }

            public SourceExpressionIndex RootExpression { get; }

            public SourceProgramSymbolIndex ProgramMember { get; }

            public Avm1SourceTypeFact[] BaseSymbolFacts { get; }

            public Avm1SourceTypeFact[] SymbolFacts { get; set; }

            public Avm1SourceTypeFact[] NextSymbolFacts { get; set; }

            public Avm1SourceTypeFact[] ExpressionFacts { get; set; }

            public Avm1SourceTypeFact[] FunctionReturnFacts { get; set; }

            public Avm1SourceTypeFact[] NextFunctionReturnFacts { get; set; }

            public Avm1SourceTypeFact RootReturnFact { get; set; }

            public Avm1SourceTypeFact NextRootReturnFact { get; set; }
        }

        private struct FactAccumulator
        {
            public bool HasValue { get; private set; }

            public Avm1SourceTypeFact Fact { get; private set; }

            public void Add(Avm1SourceTypeFact fact, Analyzer analyzer)
            {
                if (!HasValue)
                {
                    Fact = fact;
                    HasValue = true;
                    return;
                }
                Fact = analyzer.Join(Fact, fact);
            }
        }

        private readonly record struct ArenaOwner(
            SourceStatementIndex RootStatement,
            SourceExpressionIndex RootExpression,
            SourceProgramSymbolIndex ProgramMember)
        {
            public static readonly ArenaOwner Empty = new(
                SourceStatementIndex.Invalid,
                SourceExpressionIndex.Invalid,
                SourceProgramSymbolIndex.Invalid);
        }
    }
}
