namespace ShockwaveFlash.Avm1.Source;

public sealed class Avm1SourceArena
{
    private readonly byte[] _opaqueBytecode;

    private Avm1SourceArena(
        IReadOnlyList<Avm1SourceExpression> expressions,
        IReadOnlyList<SourceExpressionIndex> expressionChildren,
        IReadOnlyList<Avm1SourceStatement> statements,
        IReadOnlyList<SourceStatementIndex> statementChildren,
        IReadOnlyList<Avm1SourceFunction> functions,
        IReadOnlyList<SourceSymbolIndex> functionSymbols,
        IReadOnlyList<Avm1SourceSymbol> symbols,
        IReadOnlyList<Avm1SourceLabel> labels,
        IReadOnlyList<Avm1SourceType> types,
        IReadOnlyList<string> strings,
        IReadOnlyList<Avm1SourceLiteral> literals,
        IReadOnlyList<Avm1SourceOrigin> origins,
        IReadOnlyList<Avm1SourceOpaqueRegion> opaqueRegions,
        byte[] opaqueBytecode)
    {
        Expressions = expressions;
        ExpressionChildren = expressionChildren;
        Statements = statements;
        StatementChildren = statementChildren;
        Functions = functions;
        FunctionSymbols = functionSymbols;
        Symbols = symbols;
        Labels = labels;
        Types = types;
        Strings = strings;
        Literals = literals;
        Origins = origins;
        OpaqueRegions = opaqueRegions;
        _opaqueBytecode = opaqueBytecode;
    }

    public IReadOnlyList<Avm1SourceExpression> Expressions { get; }

    public IReadOnlyList<SourceExpressionIndex> ExpressionChildren { get; }

    public IReadOnlyList<Avm1SourceStatement> Statements { get; }

    public IReadOnlyList<SourceStatementIndex> StatementChildren { get; }

    public IReadOnlyList<Avm1SourceFunction> Functions { get; }

    public IReadOnlyList<SourceSymbolIndex> FunctionSymbols { get; }

    public IReadOnlyList<Avm1SourceSymbol> Symbols { get; }

    public IReadOnlyList<Avm1SourceLabel> Labels { get; }

    public IReadOnlyList<Avm1SourceType> Types { get; }

    public IReadOnlyList<string> Strings { get; }

    public IReadOnlyList<Avm1SourceLiteral> Literals { get; }

    public IReadOnlyList<Avm1SourceOrigin> Origins { get; }

    public IReadOnlyList<Avm1SourceOpaqueRegion> OpaqueRegions { get; }

    public IReadOnlyList<byte> OpaqueBytecode => _opaqueBytecode;

    public Avm1SourceExpression this[SourceExpressionIndex index] => Expressions[index.Value];

    public Avm1SourceStatement this[SourceStatementIndex index] => Statements[index.Value];

    public Avm1SourceFunction this[SourceFunctionIndex index] => Functions[index.Value];

    public Avm1SourceSymbol this[SourceSymbolIndex index] => Symbols[index.Value];

    public Avm1SourceLabel this[SourceLabelIndex index] => Labels[index.Value];

    public Avm1SourceType this[SourceTypeIndex index] => Types[index.Value];

    public string this[SourceStringIndex index] => Strings[index.Value];

    public Avm1SourceLiteral this[SourceLiteralIndex index] => Literals[index.Value];

    public Avm1SourceOrigin this[SourceOriginIndex index] => Origins[index.Value];

    public Avm1SourceOpaqueRegion this[SourceOpaqueIndex index] =>
        OpaqueRegions[index.Value];

    public ReadOnlyMemory<byte> GetBytecode(Avm1SourceOpaqueRegion region) =>
        new(
            _opaqueBytecode,
            region.Bytecode.Start,
            region.Bytecode.Count);

    internal Avm1SourceMethod CreateReturnMethod(
        SourceExpressionIndex expression,
        Avm1SourceDiagnostic[] diagnostics)
    {
        if (!expression.IsValid || expression.Value >= Expressions.Count)
            throw new ArgumentOutOfRangeException(nameof(expression));
        ArgumentNullException.ThrowIfNull(diagnostics);

        var statements = Statements.ToArray();
        Array.Resize(ref statements, statements.Length + 1);
        var body = new SourceStatementIndex(statements.Length - 1);
        statements[^1] = new Avm1SourceStatement(
            body,
            Avm1SourceStatementKind.Return,
            expression,
            SourceExpressionIndex.Invalid,
            SourceSymbolIndex.Invalid,
            SourceStringIndex.Invalid,
            SourceLabelIndex.Invalid,
            SourceOpaqueIndex.Invalid,
            Avm1SourceStatementFlags.None,
            SourceOriginIndex.Invalid,
            SourceStatementList.Empty,
            SourceStatementList.Empty,
            SourceExpressionList.Empty);

        var arena = new Avm1SourceArena(
            Expressions.ToArray(),
            ExpressionChildren.ToArray(),
            statements,
            StatementChildren.ToArray(),
            Functions.ToArray(),
            FunctionSymbols.ToArray(),
            Symbols.ToArray(),
            Labels.ToArray(),
            Types.ToArray(),
            Strings.ToArray(),
            Literals.ToArray(),
            Origins.ToArray(),
            OpaqueRegions.ToArray(),
            _opaqueBytecode.ToArray());
        return new Avm1SourceMethod(arena, body, [], diagnostics);
    }

    internal (Avm1SourceArena Arena, SourceSymbolIndex[] Map) RemapSymbols(
        IReadOnlyList<SourceSymbolIndex> representatives,
        IReadOnlyDictionary<SourceSymbolIndex, string> preferredNames)
    {
        ArgumentNullException.ThrowIfNull(representatives);
        ArgumentNullException.ThrowIfNull(preferredNames);
        if (representatives.Count != Symbols.Count)
            throw new ArgumentException("A representative is required for every source symbol.", nameof(representatives));

        if (preferredNames.Count == 0)
        {
            var identityMap = new SourceSymbolIndex[Symbols.Count];
            var isIdentity = true;
            for (var index = 0; index < identityMap.Length; index++)
            {
                var symbol = new SourceSymbolIndex(index);
                identityMap[index] = symbol;
                if (representatives[index] != symbol)
                    isIdentity = false;
            }

            if (isIdentity)
                return (this, identityMap);
        }

        var strings = Strings.ToList();
        var stringIndices = new Dictionary<string, SourceStringIndex>(StringComparer.Ordinal);
        for (var index = 0; index < strings.Count; index++)
            stringIndices.TryAdd(strings[index], new SourceStringIndex(index));

        SourceStringIndex InternString(string value)
        {
            if (stringIndices.TryGetValue(value, out var existing))
                return existing;

            var created = new SourceStringIndex(strings.Count);
            strings.Add(value);
            stringIndices.Add(value, created);
            return created;
        }

        var groups = Enumerable.Range(0, Symbols.Count)
            .GroupBy(index => representatives[index])
            .OrderBy(group => group.Key.Value)
            .ToArray();
        var symbols = new Avm1SourceSymbol[groups.Length];
        var map = new SourceSymbolIndex[Symbols.Count];
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            var representative = group.Key;
            if (!representative.IsValid || representative.Value >= Symbols.Count)
                throw new ArgumentException("A source-symbol representative is invalid.", nameof(representatives));

            var source = Symbols[representative.Value];
            var flags = Avm1SourceSymbolFlags.None;
            foreach (var oldIndex in group)
                flags |= Symbols[oldIndex].Flags;

            var newIndex = new SourceSymbolIndex(groupIndex);
            var name = preferredNames.TryGetValue(representative, out var preferredName)
                ? InternString(preferredName)
                : source.Name;
            symbols[groupIndex] = source with
            {
                Index = newIndex,
                Name = name,
                Flags = flags
            };
            foreach (var oldIndex in group)
                map[oldIndex] = newIndex;
        }

        SourceSymbolIndex Remap(SourceSymbolIndex symbol) =>
            symbol.IsValid && symbol.Value < map.Length
                ? map[symbol.Value]
                : symbol;

        var expressions = Expressions
            .Select(expression => expression with { Symbol = Remap(expression.Symbol) })
            .ToArray();
        var statements = Statements
            .Select(statement => statement with { Symbol = Remap(statement.Symbol) })
            .ToArray();
        var functions = Functions
            .Select(function => function with { NameSymbol = Remap(function.NameSymbol) })
            .ToArray();
        var functionSymbols = FunctionSymbols.Select(Remap).ToArray();
        var arena = new Avm1SourceArena(
            expressions,
            ExpressionChildren.ToArray(),
            statements,
            StatementChildren.ToArray(),
            functions,
            functionSymbols,
            symbols,
            Labels.ToArray(),
            Types.ToArray(),
            strings.ToArray(),
            Literals.ToArray(),
            Origins.ToArray(),
            OpaqueRegions.ToArray(),
            _opaqueBytecode.ToArray());
        return (arena, map);
    }

    public SourceExpressionIndex GetChild(Avm1SourceExpression expression, int childIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(childIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(childIndex, expression.Children.Count);
        return ExpressionChildren[expression.Children.Start + childIndex];
    }

    public SourceStatementIndex GetChild(Avm1SourceStatement statement, int childIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(childIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(childIndex, statement.Children.Count);
        return StatementChildren[statement.Children.Start + childIndex];
    }

    public SourceStatementIndex GetInitializer(Avm1SourceStatement statement, int initializerIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initializerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(initializerIndex, statement.Initializers.Count);
        return StatementChildren[statement.Initializers.Start + initializerIndex];
    }

    public SourceExpressionIndex GetExpression(Avm1SourceStatement statement, int expressionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expressionIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(expressionIndex, statement.Expressions.Count);
        return ExpressionChildren[statement.Expressions.Start + expressionIndex];
    }

    public SourceSymbolIndex GetParameter(Avm1SourceFunction function, int parameterIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parameterIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(parameterIndex, function.Parameters.Count);
        return FunctionSymbols[function.Parameters.Start + parameterIndex];
    }

    public SourceSymbolIndex GetCapture(Avm1SourceFunction function, int captureIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(captureIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(captureIndex, function.Captures.Count);
        return FunctionSymbols[function.Captures.Start + captureIndex];
    }

    internal sealed class Builder
    {
        private readonly List<Avm1SourceExpression> _expressions = [];
        private readonly List<SourceExpressionIndex> _expressionChildren = [];
        private readonly List<Avm1SourceStatement> _statements = [];
        private readonly List<SourceStatementIndex> _statementChildren = [];
        private readonly List<Avm1SourceFunction> _functions = [];
        private readonly List<SourceSymbolIndex> _functionSymbols = [];
        private readonly List<Avm1SourceSymbol> _symbols = [];
        private readonly List<Avm1SourceLabel> _labels = [];
        private readonly List<Avm1SourceType> _types = [];
        private readonly List<string> _strings = [];
        private readonly List<Avm1SourceLiteral> _literals = [];
        private readonly List<Avm1SourceOrigin> _origins = [];
        private readonly List<Avm1SourceOpaqueRegion> _opaqueRegions = [];
        private readonly List<byte> _opaqueBytecode = [];
        private readonly Dictionary<string, SourceStringIndex> _stringIndices = new(StringComparer.Ordinal);
        private readonly Dictionary<Avm1SourceTypeKind, SourceTypeIndex> _builtInTypes = [];
        private readonly Dictionary<string, SourceTypeIndex> _nominalTypes =
            new(StringComparer.Ordinal);
        private bool _isConsumed;

        public void EnsureAdditionalCapacity(
            int expressionCount,
            int expressionChildCount,
            int statementCount,
            int statementChildCount,
            int literalCount,
            int originCount,
            int stringCount)
        {
            EnsureMutable();
            ArgumentOutOfRangeException.ThrowIfNegative(expressionCount);
            ArgumentOutOfRangeException.ThrowIfNegative(expressionChildCount);
            ArgumentOutOfRangeException.ThrowIfNegative(statementCount);
            ArgumentOutOfRangeException.ThrowIfNegative(statementChildCount);
            ArgumentOutOfRangeException.ThrowIfNegative(literalCount);
            ArgumentOutOfRangeException.ThrowIfNegative(originCount);
            ArgumentOutOfRangeException.ThrowIfNegative(stringCount);

            _expressions.EnsureCapacity(checked(_expressions.Count + expressionCount));
            _expressionChildren.EnsureCapacity(checked(
                _expressionChildren.Count + expressionChildCount));
            _statements.EnsureCapacity(checked(_statements.Count + statementCount));
            _statementChildren.EnsureCapacity(checked(
                _statementChildren.Count + statementChildCount));
            _literals.EnsureCapacity(checked(_literals.Count + literalCount));
            _origins.EnsureCapacity(checked(_origins.Count + originCount));
            _strings.EnsureCapacity(checked(_strings.Count + stringCount));
            _stringIndices.EnsureCapacity(checked(_stringIndices.Count + stringCount));
        }

        public SourceExpressionIndex AddExpression(
            Avm1SourceExpressionKind kind,
            Avm1SourceOperator @operator = Avm1SourceOperator.None,
            SourceSymbolIndex? symbol = null,
            SourceStringIndex? name = null,
            SourceLiteralIndex? literal = null,
            SourceFunctionIndex? function = null,
            SourceOpaqueIndex? opaque = null,
            Avm1SourceExpressionFlags flags = Avm1SourceExpressionFlags.None,
            SourceOriginIndex? origin = null,
            IReadOnlyList<SourceExpressionIndex>? children = null)
        {
            EnsureMutable();
            var index = new SourceExpressionIndex(_expressions.Count);
            _expressions.Add(new Avm1SourceExpression(
                index,
                kind,
                @operator,
                symbol ?? SourceSymbolIndex.Invalid,
                name ?? SourceStringIndex.Invalid,
                literal ?? SourceLiteralIndex.Invalid,
                function ?? SourceFunctionIndex.Invalid,
                opaque ?? SourceOpaqueIndex.Invalid,
                flags,
                origin ?? SourceOriginIndex.Invalid,
                AddExpressionChildren(children)));
            return index;
        }

        public SourceFunctionIndex AddFunction(
            SourceSymbolIndex nameSymbol,
            SourceStatementIndex body,
            Avm1SourceFunctionFlags flags,
            SourceOriginIndex origin,
            IReadOnlyList<SourceSymbolIndex>? parameters = null,
            IReadOnlyList<SourceSymbolIndex>? captures = null)
        {
            EnsureMutable();
            var index = new SourceFunctionIndex(_functions.Count);
            _functions.Add(new Avm1SourceFunction(
                index,
                nameSymbol,
                body,
                AddFunctionSymbols(parameters),
                AddFunctionSymbols(captures),
                flags,
                origin));
            return index;
        }

        public SourceStatementIndex AddStatement(
            Avm1SourceStatementKind kind,
            SourceExpressionIndex? expression = null,
            SourceExpressionIndex? secondaryExpression = null,
            SourceSymbolIndex? symbol = null,
            SourceStringIndex? name = null,
            SourceLabelIndex? label = null,
            SourceOpaqueIndex? opaque = null,
            Avm1SourceStatementFlags flags = Avm1SourceStatementFlags.None,
            SourceOriginIndex? origin = null,
            IReadOnlyList<SourceStatementIndex>? children = null,
            IReadOnlyList<SourceStatementIndex>? initializers = null,
            IReadOnlyList<SourceExpressionIndex>? expressions = null)
        {
            EnsureMutable();
            var index = new SourceStatementIndex(_statements.Count);
            _statements.Add(new Avm1SourceStatement(
                index,
                kind,
                expression ?? SourceExpressionIndex.Invalid,
                secondaryExpression ?? SourceExpressionIndex.Invalid,
                symbol ?? SourceSymbolIndex.Invalid,
                name ?? SourceStringIndex.Invalid,
                label ?? SourceLabelIndex.Invalid,
                opaque ?? SourceOpaqueIndex.Invalid,
                flags,
                origin ?? SourceOriginIndex.Invalid,
                AddStatementChildren(children),
                AddStatementChildren(initializers),
                AddExpressionChildren(expressions)));
            return index;
        }

        public SourceSymbolIndex AddSymbol(
            Avm1SourceSymbolKind kind,
            string name,
            SourceTypeIndex declaredType,
            SourceTypeIndex inferredType,
            Avm1SourceSymbolFlags flags = Avm1SourceSymbolFlags.None)
        {
            EnsureMutable();
            var index = new SourceSymbolIndex(_symbols.Count);
            _symbols.Add(new Avm1SourceSymbol(
                index,
                kind,
                InternString(name),
                declaredType,
                inferredType,
                flags));
            return index;
        }

        public SourceLabelIndex AddLabel(string name)
        {
            EnsureMutable();
            var index = new SourceLabelIndex(_labels.Count);
            _labels.Add(new Avm1SourceLabel(index, InternString(name)));
            return index;
        }

        public SourceTypeIndex GetBuiltInType(Avm1SourceTypeKind kind)
        {
            EnsureMutable();
            if (kind is Avm1SourceTypeKind.Nominal)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (_builtInTypes.TryGetValue(kind, out var existing))
                return existing;

            var index = new SourceTypeIndex(_types.Count);
            _types.Add(new Avm1SourceType(index, kind, SourceStringIndex.Invalid));
            _builtInTypes.Add(kind, index);
            return index;
        }

        public SourceTypeIndex GetNominalType(string qualifiedName)
        {
            EnsureMutable();
            ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
            if (_nominalTypes.TryGetValue(qualifiedName, out var existing))
                return existing;

            var index = new SourceTypeIndex(_types.Count);
            _types.Add(new Avm1SourceType(
                index,
                Avm1SourceTypeKind.Nominal,
                InternString(qualifiedName)));
            _nominalTypes.Add(qualifiedName, index);
            return index;
        }

        public SourceStringIndex InternString(string value)
        {
            EnsureMutable();
            ArgumentNullException.ThrowIfNull(value);
            if (_stringIndices.TryGetValue(value, out var existing))
                return existing;

            var index = new SourceStringIndex(_strings.Count);
            _strings.Add(value);
            _stringIndices.Add(value, index);
            return index;
        }

        public SourceLiteralIndex AddLiteral(
            Avm1SourceLiteralKind kind,
            bool booleanValue = false,
            int integerValue = 0,
            double numberValue = 0,
            SourceStringIndex? stringValue = null)
        {
            EnsureMutable();
            var index = new SourceLiteralIndex(_literals.Count);
            _literals.Add(new Avm1SourceLiteral(
                index,
                kind,
                booleanValue,
                integerValue,
                numberValue,
                stringValue ?? SourceStringIndex.Invalid));
            return index;
        }

        public SourceOriginIndex AddOrigin(
            int recoveryUnit,
            int recoveryNode,
            int startOffset = -1,
            int endOffset = -1)
        {
            EnsureMutable();
            var index = new SourceOriginIndex(_origins.Count);
            _origins.Add(new Avm1SourceOrigin(
                index,
                recoveryUnit,
                recoveryNode,
                startOffset,
                endOffset));
            return index;
        }

        public SourceOpaqueIndex AddOpaque(
            Avm1SourceOpaqueKind kind,
            string description,
            SourceOriginIndex origin,
            ReadOnlySpan<byte> bytecode = default)
        {
            EnsureMutable();
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
            var bytes = new Avm1SourceByteList(
                _opaqueBytecode.Count,
                bytecode.Length);
            foreach (var value in bytecode)
                _opaqueBytecode.Add(value);

            var index = new SourceOpaqueIndex(_opaqueRegions.Count);
            _opaqueRegions.Add(new Avm1SourceOpaqueRegion(
                index,
                kind,
                InternString(description),
                origin,
                bytes));
            return index;
        }

        public Avm1SourceExpression GetExpression(SourceExpressionIndex index) =>
            _expressions[index.Value];

        public Avm1SourceStatement GetStatement(SourceStatementIndex index) =>
            _statements[index.Value];

        public SourceStatementIndex GetStatementChild(
            Avm1SourceStatement statement,
            int childIndex)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(childIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                childIndex,
                statement.Children.Count);
            return _statementChildren[statement.Children.Start + childIndex];
        }

        public SourceStatementIndex GetStatementInitializer(
            Avm1SourceStatement statement,
            int initializerIndex)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(initializerIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                initializerIndex,
                statement.Initializers.Count);
            return _statementChildren[
                statement.Initializers.Start + initializerIndex];
        }

        public SourceExpressionIndex GetStatementExpression(
            Avm1SourceStatement statement,
            int expressionIndex)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expressionIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                expressionIndex,
                statement.Expressions.Count);
            return _expressionChildren[
                statement.Expressions.Start + expressionIndex];
        }

        public SourceExpressionIndex GetExpressionChild(
            Avm1SourceExpression expression,
            int childIndex)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(childIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                childIndex,
                expression.Children.Count);
            return _expressionChildren[expression.Children.Start + childIndex];
        }

        public Avm1SourceFunction GetFunction(SourceFunctionIndex index) =>
            _functions[index.Value];

        public Avm1SourceSymbol GetSymbol(SourceSymbolIndex index) =>
            _symbols[index.Value];

        public void PromoteToFunctionSymbol(SourceSymbolIndex index)
        {
            EnsureMutable();
            var symbol = _symbols[index.Value];
            if (symbol.Kind is not (
                Avm1SourceSymbolKind.Local or
                Avm1SourceSymbolKind.Function))
            {
                throw new InvalidOperationException(
                    $"Source symbol {index} cannot represent a function name.");
            }

            _symbols[index.Value] = symbol with
            {
                Kind = Avm1SourceSymbolKind.Function
            };
        }

        public string GetString(SourceStringIndex index) =>
            _strings[index.Value];

        public Avm1SourceLiteral GetLiteral(SourceLiteralIndex index) =>
            _literals[index.Value];

        public Avm1SourceArena ToArena()
        {
            EnsureMutable();
            return new Avm1SourceArena(
                _expressions.ToArray(),
                _expressionChildren.ToArray(),
                _statements.ToArray(),
                _statementChildren.ToArray(),
                _functions.ToArray(),
                _functionSymbols.ToArray(),
                _symbols.ToArray(),
                _labels.ToArray(),
                _types.ToArray(),
                _strings.ToArray(),
                _literals.ToArray(),
                _origins.ToArray(),
                _opaqueRegions.ToArray(),
                _opaqueBytecode.ToArray());
        }

        public Avm1SourceArena MoveToArena()
        {
            EnsureMutable();
            _isConsumed = true;
            return new Avm1SourceArena(
                _expressions.AsReadOnly(),
                _expressionChildren.AsReadOnly(),
                _statements.AsReadOnly(),
                _statementChildren.AsReadOnly(),
                _functions.AsReadOnly(),
                _functionSymbols.AsReadOnly(),
                _symbols.AsReadOnly(),
                _labels.AsReadOnly(),
                _types.AsReadOnly(),
                _strings.AsReadOnly(),
                _literals.AsReadOnly(),
                _origins.AsReadOnly(),
                _opaqueRegions.AsReadOnly(),
                _opaqueBytecode.ToArray());
        }

        private void EnsureMutable()
        {
            if (_isConsumed)
            {
                throw new InvalidOperationException(
                    "The source arena builder has transferred ownership of its buffers.");
            }
        }

        private SourceExpressionList AddExpressionChildren(IReadOnlyList<SourceExpressionIndex>? children)
        {
            if (children is null || children.Count == 0)
                return SourceExpressionList.Empty;

            var start = _expressionChildren.Count;
            foreach (var child in children)
                _expressionChildren.Add(child);
            return new SourceExpressionList(start, children.Count);
        }

        private SourceStatementList AddStatementChildren(IReadOnlyList<SourceStatementIndex>? children)
        {
            if (children is null || children.Count == 0)
                return SourceStatementList.Empty;

            var start = _statementChildren.Count;
            foreach (var child in children)
                _statementChildren.Add(child);
            return new SourceStatementList(start, children.Count);
        }

        private SourceSymbolList AddFunctionSymbols(IReadOnlyList<SourceSymbolIndex>? symbols)
        {
            if (symbols is null || symbols.Count == 0)
                return SourceSymbolList.Empty;

            var start = _functionSymbols.Count;
            foreach (var symbol in symbols)
                _functionSymbols.Add(symbol);
            return new SourceSymbolList(start, symbols.Count);
        }
    }
}

public enum Avm1SourceExpressionKind : byte
{
    Literal,
    SymbolReference,
    DynamicName,
    ComputedDynamicName,
    QualifiedName,
    Assignment,
    Unary,
    Binary,
    Sequence,
    Conditional,
    MemberAccess,
    Delete,
    IntrinsicCall,
    Call,
    New,
    ArrayLiteral,
    ObjectLiteral,
    FunctionLiteral,
    Opaque
}

public enum Avm1SourceStatementKind : byte
{
    Block,
    Expression,
    VariableDeclaration,
    FunctionDeclaration,
    Return,
    Throw,
    If,
    IfFrameLoaded,
    While,
    DoWhile,
    For,
    ForIn,
    With,
    TellTarget,
    Try,
    CatchClause,
    FinallyClause,
    Switch,
    SwitchCase,
    SwitchDefault,
    Break,
    Continue,
    Opaque
}

public enum Avm1SourceOperator : byte
{
    None,
    Assign,
    AddAssign,
    SubtractAssign,
    MultiplyAssign,
    DivideAssign,
    ModuloAssign,
    BitAndAssign,
    BitOrAssign,
    BitXorAssign,
    ShiftLeftAssign,
    ShiftRightAssign,
    ShiftRightUnsignedAssign,
    PrefixIncrement,
    PrefixDecrement,
    PostfixIncrement,
    PostfixDecrement,
    LogicalNot,
    BitwiseNot,
    TypeOf,
    UnaryPlus,
    UnaryMinus,
    Void,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    StrictEqual,
    StrictNotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    LogicalAnd,
    LogicalOr,
    BitAnd,
    BitOr,
    BitXor,
    ShiftLeft,
    ShiftRight,
    ShiftRightUnsigned,
    In,
    InstanceOf
}

[Flags]
public enum Avm1SourceExpressionFlags : byte
{
    None = 0,
    ComputedMember = 1 << 0
}

[Flags]
public enum Avm1SourceStatementFlags : byte
{
    None = 0,
    ForInDeclaresKey = 1 << 0
}

public enum Avm1SourceSymbolKind : byte
{
    Local,
    Parameter,
    Temporary,
    Function,
    Catch,
    This,
    Super,
    Arguments,
    Root,
    Parent,
    Global
}

[Flags]
public enum Avm1SourceSymbolFlags : byte
{
    None = 0,
    CompilerGenerated = 1 << 0,
    DeclarationProvided = 1 << 1
}

public enum Avm1SourceTypeKind : byte
{
    Unknown,
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Object,
    Array,
    Function,
    Nominal,
    Conflict,
    Void
}

public enum Avm1SourceLiteralKind : byte
{
    Undefined,
    Null,
    Boolean,
    Integer,
    Number,
    String
}

[Flags]
public enum Avm1SourceFunctionFlags : byte
{
    None = 0,
    Declaration = 1 << 0,
    CapturesDynamicScope = 1 << 1
}

public readonly record struct Avm1SourceExpression(
    SourceExpressionIndex Index,
    Avm1SourceExpressionKind Kind,
    Avm1SourceOperator Operator,
    SourceSymbolIndex Symbol,
    SourceStringIndex Name,
    SourceLiteralIndex Literal,
    SourceFunctionIndex Function,
    SourceOpaqueIndex Opaque,
    Avm1SourceExpressionFlags Flags,
    SourceOriginIndex Origin,
    SourceExpressionList Children);

public readonly record struct Avm1SourceStatement(
    SourceStatementIndex Index,
    Avm1SourceStatementKind Kind,
    SourceExpressionIndex Expression,
    SourceExpressionIndex SecondaryExpression,
    SourceSymbolIndex Symbol,
    SourceStringIndex Name,
    SourceLabelIndex Label,
    SourceOpaqueIndex Opaque,
    Avm1SourceStatementFlags Flags,
    SourceOriginIndex Origin,
    SourceStatementList Children,
    SourceStatementList Initializers,
    SourceExpressionList Expressions);

public readonly record struct Avm1SourceFunction(
    SourceFunctionIndex Index,
    SourceSymbolIndex NameSymbol,
    SourceStatementIndex Body,
    SourceSymbolList Parameters,
    SourceSymbolList Captures,
    Avm1SourceFunctionFlags Flags,
    SourceOriginIndex Origin);

public readonly record struct Avm1SourceSymbol(
    SourceSymbolIndex Index,
    Avm1SourceSymbolKind Kind,
    SourceStringIndex Name,
    SourceTypeIndex DeclaredType,
    SourceTypeIndex InferredType,
    Avm1SourceSymbolFlags Flags);

public readonly record struct Avm1SourceLabel(
    SourceLabelIndex Index,
    SourceStringIndex Name);

public readonly record struct Avm1SourceType(
    SourceTypeIndex Index,
    Avm1SourceTypeKind Kind,
    SourceStringIndex Name);

public readonly record struct Avm1SourceLiteral(
    SourceLiteralIndex Index,
    Avm1SourceLiteralKind Kind,
    bool BooleanValue,
    int IntegerValue,
    double NumberValue,
    SourceStringIndex StringValue);

public readonly record struct Avm1SourceOrigin(
    SourceOriginIndex Index,
    int RecoveryUnit,
    int RecoveryNode,
    int StartOffset,
    int EndOffset);

public enum Avm1SourceOpaqueKind : byte
{
    UnsupportedAction,
    RecoveryStatement,
    RecoveryExpression,
    FieldExpression
}

public readonly record struct Avm1SourceOpaqueRegion(
    SourceOpaqueIndex Index,
    Avm1SourceOpaqueKind Kind,
    SourceStringIndex Description,
    SourceOriginIndex Origin,
    Avm1SourceByteList Bytecode);

public readonly record struct Avm1SourceByteList(int Start, int Count)
{
    public static readonly Avm1SourceByteList Empty = new(0, 0);
}
