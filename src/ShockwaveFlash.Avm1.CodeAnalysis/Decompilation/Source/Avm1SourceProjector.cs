using System.Globalization;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation;

public static class Avm1SourceProjector
{
    public static Avm1SourceMethod ProjectMethod(Avm1MethodDecompilation method)
        => ProjectMethod(method, registerAliases: null);

    internal static Avm1SourceMethod ProjectMethod(
        Avm1MethodDecompilation method,
        IReadOnlyDictionary<(int Register, int Version), string>? registerAliases,
        IReadOnlySet<int>? includedStatementNodes = null,
        bool coalesceNonInterferingRegisterVersions = true,
        Avm1SourceProjectionHints? projectionHints = null,
        bool classAbiActionsHandledExternally = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        return new Projector(
            method,
            new ProjectionSession(),
            outerLexicalSymbols: null,
            selfNameSymbol: SourceSymbolIndex.Invalid,
            registerAliases,
            includedStatementNodes,
            coalesceNonInterferingRegisterVersions,
            projectionHints,
            classAbiActionsHandledExternally).ProjectRoot();
    }

    internal static Avm1SourceExpressionFragment ProjectExpression(
        Avm1MethodDecompilation method,
        AstIndex expression,
        IReadOnlyDictionary<(int Register, int Version), string>? registerAliases = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!expression.IsValid)
            throw new ArgumentOutOfRangeException(nameof(expression));

        return new Projector(
            method,
            new ProjectionSession(),
            outerLexicalSymbols: null,
            selfNameSymbol: SourceSymbolIndex.Invalid,
            registerAliases,
            includedStatementNodes: null,
            coalesceNonInterferingRegisterVersions: true,
            projectionHints: null,
            classAbiActionsHandledExternally: false)
            .ProjectExpressionFragment(expression);
    }

    private sealed class ProjectionSession
    {
        private int _nextRecoveryUnit;
        private readonly List<SourceSymbolCoalescing> _symbolCoalescings = [];
        private readonly List<SourceSymbolPreferredName> _symbolPreferredNames = [];

        public Avm1SourceArena.Builder Builder { get; } = new();

        public List<Avm1SourceDiagnostic> Diagnostics { get; } = [];

        public bool RequiresSymbolRemapping =>
            _symbolCoalescings.Count != 0 || _symbolPreferredNames.Count != 0;

        public int AllocateRecoveryUnit() => _nextRecoveryUnit++;

        public void AddSymbolCoalescing(
            SourceSymbolIndex representative,
            SourceSymbolIndex merged,
            string preferredName)
        {
            if (!representative.IsValid || !merged.IsValid || representative == merged)
                return;

            _symbolCoalescings.Add(new SourceSymbolCoalescing(
                representative,
                merged,
                preferredName));
        }

        public void AddSymbolPreferredName(
            SourceSymbolIndex symbol,
            string preferredName)
        {
            if (!symbol.IsValid)
                return;
            _symbolPreferredNames.Add(new SourceSymbolPreferredName(symbol, preferredName));
        }

        public SourceSymbolRemapPlan BuildSymbolRemapPlan(Avm1SourceArena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);
            var symbolCount = arena.Symbols.Count;
            var interference = BuildExpressionInterference(arena);
            var parents = new int[symbolCount];
            for (var index = 0; index < parents.Length; index++)
                parents[index] = index;

            int Find(int index)
            {
                var parent = parents[index];
                if (parent == index)
                    return index;
                var root = Find(parent);
                parents[index] = root;
                return root;
            }

            bool Interferes(int left, int right)
            {
                foreach (var pair in interference)
                {
                    var pairLeft = Find(pair.Left);
                    var pairRight = Find(pair.Right);
                    if ((pairLeft == left && pairRight == right) ||
                        (pairLeft == right && pairRight == left))
                    {
                        return true;
                    }
                }

                return false;
            }

            foreach (var coalescing in _symbolCoalescings)
            {
                if ((uint)coalescing.Representative.Value >= (uint)symbolCount ||
                    (uint)coalescing.Merged.Value >= (uint)symbolCount)
                {
                    continue;
                }

                var left = Find(coalescing.Representative.Value);
                var right = Find(coalescing.Merged.Value);
                if (left == right || Interferes(left, right))
                    continue;
                var representative = Math.Min(left, right);
                parents[Math.Max(left, right)] = representative;
            }

            var representatives = new SourceSymbolIndex[symbolCount];
            for (var index = 0; index < representatives.Length; index++)
                representatives[index] = new SourceSymbolIndex(Find(index));

            var preferredNames = new Dictionary<SourceSymbolIndex, string>();
            foreach (var coalescing in _symbolCoalescings)
            {
                if ((uint)coalescing.Representative.Value >= (uint)symbolCount)
                    continue;
                preferredNames[representatives[coalescing.Representative.Value]] =
                    coalescing.PreferredName;
            }
            foreach (var preferredName in _symbolPreferredNames)
            {
                if ((uint)preferredName.Symbol.Value >= (uint)symbolCount)
                    continue;
                preferredNames[representatives[preferredName.Symbol.Value]] =
                    preferredName.Name;
            }

            return new SourceSymbolRemapPlan(representatives, preferredNames);
        }

        private HashSet<SourceSymbolPair> BuildExpressionInterference(
            Avm1SourceArena arena)
        {
            var candidates = _symbolCoalescings
                .SelectMany(coalescing => new[]
                {
                    coalescing.Representative.Value,
                    coalescing.Merged.Value
                })
                .ToHashSet();
            var result = new HashSet<SourceSymbolPair>();
            if (candidates.Count < 2)
                return result;

            var symbolsByExpression = new HashSet<int>?[arena.Expressions.Count];
            HashSet<int> Collect(SourceExpressionIndex expressionIndex)
            {
                var cached = symbolsByExpression[expressionIndex.Value];
                if (cached is not null)
                    return cached;

                var expression = arena[expressionIndex];
                var symbols = new HashSet<int>();
                if (expression.Symbol.IsValid &&
                    candidates.Contains(expression.Symbol.Value))
                {
                    symbols.Add(expression.Symbol.Value);
                }

                for (var childIndex = 0;
                    childIndex < expression.Children.Count;
                    childIndex++)
                {
                    symbols.UnionWith(Collect(arena.GetChild(expression, childIndex)));
                }

                symbolsByExpression[expressionIndex.Value] = symbols;
                return symbols;
            }

            foreach (var expression in arena.Expressions)
            {
                var symbols = Collect(expression.Index).ToArray();
                for (var left = 0; left < symbols.Length; left++)
                {
                    for (var right = left + 1; right < symbols.Length; right++)
                    {
                        result.Add(SourceSymbolPair.Create(
                            symbols[left],
                            symbols[right]));
                    }
                }
            }

            return result;
        }

        private readonly record struct SourceSymbolCoalescing(
            SourceSymbolIndex Representative,
            SourceSymbolIndex Merged,
            string PreferredName);

        private readonly record struct SourceSymbolPreferredName(
            SourceSymbolIndex Symbol,
            string Name);

        private readonly record struct SourceSymbolPair(int Left, int Right)
        {
            public static SourceSymbolPair Create(int left, int right) =>
                left <= right
                    ? new SourceSymbolPair(left, right)
                    : new SourceSymbolPair(right, left);
        }

        public readonly record struct SourceSymbolRemapPlan(
            SourceSymbolIndex[] Representatives,
            IReadOnlyDictionary<SourceSymbolIndex, string> PreferredNames);
    }

    private sealed class Projector
    {
        private const string UnsupportedStatementCode = "AVM1SRC001";
        private const string UnsupportedExpressionCode = "AVM1SRC002";
        private const string InvalidControlTargetCode = "AVM1SRC003";
        private const string UnsupportedActionCode = "AVM1SRC004";
        private const string RecoveryDiagnosticCode = "AVM1SRC005";
        private const string ClassAbiActionCode = "AVM1SRC006";

        private readonly ProjectionSession _session;
        private readonly Avm1MethodDecompilation _method;
        private readonly Avm1AstArena _ast;
        private readonly AstIndex[] _reachableNodes;
        private readonly Avm1SourceArena.Builder _builder;
        private readonly List<Avm1SourceDiagnostic> _diagnostics;
        private readonly List<SourceSymbolIndex> _parameters = [];
        private readonly List<SourceSymbolIndex> _captures = [];
        private readonly HashSet<SourceSymbolIndex> _captureSet = [];
        private readonly Dictionary<int, SourceSymbolIndex> _symbolsByRecoveryId = [];
        private readonly Dictionary<(int Register, int Version), SourceSymbolIndex> _fallbackRegisters = [];
        private readonly Dictionary<int, SourceSymbolIndex> _temporarySymbols = [];
        private readonly Dictionary<int, SourceSymbolIndex> _parametersByRegister = [];
        private readonly Dictionary<int, SourceSymbolIndex> _registerSymbolOverrides = [];
        private readonly IReadOnlyDictionary<(int Register, int Version), string> _registerAliases;
        private readonly IReadOnlySet<int>? _includedStatementNodes;
        private readonly bool _coalesceNonInterferingRegisterVersions;
        private readonly Avm1SourceProjectionHints? _projectionHints;
        private readonly bool _classAbiActionsHandledExternally;
        private readonly Dictionary<string, SourceSymbolIndex> _lexicalSymbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceSymbolIndex> _scopedLexicalSymbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceSymbolIndex> _outerLexicalSymbols;
        private readonly Dictionary<string, SourceSymbolIndex> _specialSymbols = new(StringComparer.Ordinal);
        private readonly Dictionary<int, SourceSymbolIndex> _functionDeclarationSymbols = [];
        private readonly int[] _projectedExpressions;
        private Dictionary<(int Node, int BindingContext), SourceExpressionIndex>?
            _contextualProjectedExpressions;
        private readonly List<SourceStatementIndex> _pendingExpressionDeclarations = [];
        private int[]? _definitionByValue;
        private readonly Dictionary<int, Avm1AstNodeKind> _controlScopes = [];
        private readonly Dictionary<int, SourceLabelIndex> _controlLabels = [];
        private readonly HashSet<SourceSymbolIndex> _declaredSymbols = [];
        private readonly HashSet<SourceSymbolIndex> _emittedDeclarations = [];
        private readonly SourceTypeIndex _unknownType;
        private readonly int _recoveryUnit;
        private int _bindingContext;
        private int _dynamicScopeDepth;
        private int _nextBindingContext;
        private int _nextNestedFunctionHint;

        public Projector(
            Avm1MethodDecompilation method,
            ProjectionSession session,
            IReadOnlyDictionary<string, SourceSymbolIndex>? outerLexicalSymbols,
            SourceSymbolIndex selfNameSymbol,
            IReadOnlyDictionary<(int Register, int Version), string>? registerAliases,
            IReadOnlySet<int>? includedStatementNodes,
            bool coalesceNonInterferingRegisterVersions,
            Avm1SourceProjectionHints? projectionHints,
            bool classAbiActionsHandledExternally)
        {
            _session = session;
            _method = method;
            _ast = method.StructuredAst;
            _builder = session.Builder;
            _diagnostics = session.Diagnostics;
            _recoveryUnit = session.AllocateRecoveryUnit();
            _outerLexicalSymbols = outerLexicalSymbols is null
                ? new Dictionary<string, SourceSymbolIndex>(StringComparer.Ordinal)
                : new Dictionary<string, SourceSymbolIndex>(
                    outerLexicalSymbols,
                    StringComparer.Ordinal);
            _registerAliases = registerAliases ??
                new Dictionary<(int Register, int Version), string>();
            _includedStatementNodes = includedStatementNodes;
            _coalesceNonInterferingRegisterVersions =
                coalesceNonInterferingRegisterVersions;
            _projectionHints = projectionHints;
            _classAbiActionsHandledExternally = classAbiActionsHandledExternally;
            _reachableNodes = CollectReachableNodes(_ast);
            _projectedExpressions = new int[_ast.Nodes.Count];
            Array.Fill(_projectedExpressions, SourceExpressionIndex.Invalid.Value);
            ReserveBuilderCapacity();
            _unknownType = _builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
            AddParameters(method.Core.Context);
            if (selfNameSymbol.IsValid)
            {
                var selfName = _builder.GetString(_builder.GetSymbol(selfNameSymbol).Name);
                _lexicalSymbols.TryAdd(selfName, selfNameSymbol);
            }
            PredeclareLexicalSymbols();
            PrepareControlLabels();
        }

        private void ReserveBuilderCapacity()
        {
            var statementCount = 0;
            var literalCount = 0;
            var stringCount = 0;
            foreach (var index in _reachableNodes)
            {
                var node = _ast[index];
                if (node.Kind < Avm1AstNodeKind.Literal)
                    statementCount++;
                if (node.Kind is not Avm1AstNodeKind.Literal)
                    continue;

                literalCount++;
                if (node.StartAction.Value >= 0 &&
                    _method.ValueAnalysis[new ValueIndex(node.StartAction.Value)]
                        .ConstantKind is Avm1ConstantKind.String)
                {
                    stringCount++;
                }
            }

            _builder.EnsureAdditionalCapacity(
                expressionCount: _reachableNodes.Length,
                expressionChildCount: _ast.Children.Count,
                statementCount,
                statementChildCount: (int)Math.Min(
                    _ast.Children.Count,
                    (long)statementCount * 2),
                literalCount,
                originCount: _reachableNodes.Length,
                stringCount);
        }

        public Avm1SourceMethod ProjectRoot()
        {
            var codeUnit = ProjectCodeUnit();
            var arena = _builder.MoveToArena();
            if (!_session.RequiresSymbolRemapping)
            {
                return new Avm1SourceMethod(
                    arena,
                    codeUnit.Body,
                    codeUnit.Parameters,
                    _diagnostics.ToArray());
            }

            var remapPlan = _session.BuildSymbolRemapPlan(arena);
            var remapped = arena.RemapSymbols(
                remapPlan.Representatives,
                remapPlan.PreferredNames);
            var parameters = codeUnit.Parameters
                .Select(parameter => remapped.Map[parameter.Value])
                .ToArray();
            return new Avm1SourceMethod(
                remapped.Arena,
                codeUnit.Body,
                parameters,
                _diagnostics.ToArray());
        }

        public Avm1SourceExpressionFragment ProjectExpressionFragment(AstIndex expression)
        {
            var projected = ProjectExpression(expression);
            if (HasRecoveryDiagnostics)
            {
                var representedActions = CollectDiagnosticActions(expression);
                ImportRecoveryDiagnostics(representedActions, representedActions);
            }
            return new Avm1SourceExpressionFragment(
                _builder.MoveToArena(),
                projected,
                _diagnostics.ToArray());
        }

        private ProjectedCodeUnit ProjectCodeUnit()
        {
            if (HasRecoveryDiagnostics)
            {
                var relevantActions = CollectCodeUnitDiagnosticActions();
                var representedActions = relevantActions ?? CollectDiagnosticActions(_ast.Root);
                ImportRecoveryDiagnostics(relevantActions, representedActions);
            }
            var codeUnit = _method.IrreducibleControlFlow.IsIrreducible
                ? ProjectIrreducibleCodeUnit()
                : !_classAbiActionsHandledExternally &&
                    TryGetReachableClassAbiActions(out var classAbiActions)
                    ? ProjectClassAbiCodeUnit(classAbiActions)
                : new ProjectedCodeUnit(
                    ProjectStatement(_ast.Root),
                    _parameters.ToArray(),
                    _captures.ToArray());
            if (_coalesceNonInterferingRegisterVersions ||
                _projectionHints is { HasSourceRegisters: true })
                RecordRegisterSymbolCoalescings();
            return codeUnit;
        }

        private bool TryGetReachableClassAbiActions(out string actions)
        {
            if (!_method.TacIr.Instructions.Any(instruction =>
                instruction.Op is Avm1TacOp.Extends or Avm1TacOp.Implements))
            {
                actions = string.Empty;
                return false;
            }

            var hasExtends = false;
            var hasImplements = false;
            foreach (var instruction in _method.TacIr.Instructions)
            {
                if (instruction.Op is not (Avm1TacOp.Extends or Avm1TacOp.Implements) ||
                    !IsReachable(instruction.Action))
                {
                    continue;
                }

                hasExtends |= instruction.Op is Avm1TacOp.Extends;
                hasImplements |= instruction.Op is Avm1TacOp.Implements;
            }

            actions = (hasExtends, hasImplements) switch
            {
                (true, true) => "ActionExtends and ActionImplementsOp",
                (true, false) => "ActionExtends",
                (false, true) => "ActionImplementsOp",
                _ => string.Empty
            };
            return actions.Length != 0;
        }

        private bool IsReachable(ActionIndex action)
        {
            return !_method.ControlFlowGraph.TryGetBlockForAction(action, out var block) ||
                _method.Reachability.IsReachable(block);
        }

        private ProjectedCodeUnit ProjectClassAbiCodeUnit(string actions)
        {
            const string description = "class ABI action stream";
            var origin = _builder.AddOrigin(
                _recoveryUnit,
                recoveryNode: -1,
                startOffset: 0,
                endOffset: _method.Instructions.RootBytecode.Length);
            _diagnostics.Add(new Avm1SourceDiagnostic(
                ClassAbiActionCode,
                Avm1SourceDiagnosticSeverity.Error,
                origin,
                $"The code unit contains {actions}, whose semantics belong to " +
                    "a class declaration rather than a standalone AS2 method. " +
                    "Use class-aware projection; preserving the complete AVM1 " +
                    "action body instead of dropping inheritance or interface semantics."));
            var opaque = _builder.AddOpaque(
                Avm1SourceOpaqueKind.RecoveryStatement,
                description,
                origin,
                _method.Instructions.RootBytecode.Span);
            var statement = _builder.AddStatement(
                Avm1SourceStatementKind.Opaque,
                name: _builder.InternString(description),
                opaque: opaque,
                origin: origin);
            var body = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                origin: origin,
                children: [statement]);
            return new ProjectedCodeUnit(
                body,
                _parameters.ToArray(),
                _captures.ToArray());
        }

        private void RecordRegisterSymbolCoalescings()
        {
            var materializedRegisterVersions = _ast.Nodes
                .Where(node => node.Kind is
                    Avm1AstNodeKind.AssignRegister or
                    Avm1AstNodeKind.CompoundAssignRegister or
                    Avm1AstNodeKind.Register)
                .Select(node => (Register: node.Block.Value, Version: node.Merge.Value))
                .Where(key =>
                    key.Register >= 0 &&
                    (_coalesceNonInterferingRegisterVersions ||
                     _projectionHints?.IsSourceRegister(key.Register) is true))
                .ToHashSet();
            var coalescings = Avm1ScopeAnalysis
                .FindNonInterferingMaterializedRegisterVersions(
                    _method.TacIr,
                    _method.ControlFlowGraph,
                    _method.RegionAnalysis,
                    _method.RegisterSsa,
                    _method.SymbolTable,
                    access => materializedRegisterVersions.Contains(
                        (access.Register, access.Version)));
            foreach (var coalescing in coalescings)
            {
                if (!_symbolsByRecoveryId.TryGetValue(
                        coalescing.Representative.Id,
                        out var representative) ||
                    !_symbolsByRecoveryId.TryGetValue(
                        coalescing.Merged.Id,
                        out var merged))
                {
                    continue;
                }

                _session.AddSymbolCoalescing(
                    representative,
                    merged,
                    GetCoalescedRegisterName(
                        coalescing.Representative.Register));
            }

            foreach (var registerGroup in materializedRegisterVersions
                .GroupBy(key => key.Register))
            {
                var projectedSymbols = new HashSet<SourceSymbolIndex>();
                foreach (var key in registerGroup)
                {
                    if (!_method.SymbolTable.TryGetRegisterSymbol(
                            key.Register,
                            key.Version,
                            out var recoverySymbol) ||
                        !Avm1SymbolTable.IsGeneratedRegisterSymbol(recoverySymbol) ||
                        !_symbolsByRecoveryId.TryGetValue(
                            recoverySymbol.Id,
                            out var projectedSymbol))
                    {
                        continue;
                    }

                    projectedSymbols.Add(projectedSymbol);
                }

                if (projectedSymbols.Count == 1)
                {
                    _session.AddSymbolPreferredName(
                        projectedSymbols.Single(),
                        GetCoalescedRegisterName(registerGroup.Key));
                }
            }
        }

        private string GetCoalescedRegisterName(int register) =>
            _projectionHints?.PreservesSourceIdentity(register) is true
                ? "__reg" + register.ToString(CultureInfo.InvariantCulture)
                : $"_loc{register.ToString(CultureInfo.InvariantCulture)}_";

        private ProjectedCodeUnit ProjectIrreducibleCodeUnit()
        {
            var regionCount = _method.IrreducibleControlFlow.Regions.Count;
            var description = regionCount == 1
                ? "irreducible AVM1 control flow"
                : $"irreducible AVM1 control flow ({regionCount} regions)";
            var origin = _builder.AddOrigin(
                _recoveryUnit,
                recoveryNode: -1,
                startOffset: 0,
                endOffset: _method.Instructions.RootBytecode.Length);
            _diagnostics.Add(new Avm1SourceDiagnostic(
                RecoveryDiagnosticCode,
                Avm1SourceDiagnosticSeverity.Error,
                origin,
                "The method contains a reachable multi-entry strongly connected " +
                    "control-flow region; preserving the complete AVM1 action body " +
                    "instead of emitting incorrect structured AS2."));
            var opaque = _builder.AddOpaque(
                Avm1SourceOpaqueKind.RecoveryStatement,
                description,
                origin,
                _method.Instructions.RootBytecode.Span);
            var statement = _builder.AddStatement(
                Avm1SourceStatementKind.Opaque,
                name: _builder.InternString(description),
                opaque: opaque,
                origin: origin);
            var body = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                origin: origin,
                children: [statement]);
            return new ProjectedCodeUnit(
                body,
                _parameters.ToArray(),
                _captures.ToArray());
        }

        private void AddParameters(FunctionContext? context)
        {
            if (context is null)
                return;

            foreach (var parameter in context.Parameters)
            {
                var symbol = _builder.AddSymbol(
                    Avm1SourceSymbolKind.Parameter,
                    parameter.Name,
                    SourceTypeIndex.Invalid,
                    _unknownType,
                    Avm1SourceSymbolFlags.DeclarationProvided);
                _parameters.Add(symbol);
                _declaredSymbols.Add(symbol);
                _emittedDeclarations.Add(symbol);
                _lexicalSymbols.TryAdd(parameter.Name, symbol);
                if (parameter.Register != 0)
                    _parametersByRegister.TryAdd(parameter.Register, symbol);
            }
        }

        private void PredeclareLexicalSymbols()
        {
            foreach (var index in _reachableNodes)
            {
                if (!IsIncludedStatement(index))
                    continue;
                var node = _ast[index];
                if (node.Kind is Avm1AstNodeKind.DeclareVariable &&
                    node.Children.Count > 0 &&
                    TryGetStringLiteral(_ast.GetChild(node, 0), out var name) &&
                    IsIdentifier(name) &&
                    !_lexicalSymbols.ContainsKey(name))
                {
                    var symbol = _builder.AddSymbol(
                        Avm1SourceSymbolKind.Local,
                        name,
                        SourceTypeIndex.Invalid,
                        _unknownType,
                        IsCompilerTemporaryName(name)
                            ? Avm1SourceSymbolFlags.CompilerGenerated
                            : Avm1SourceSymbolFlags.None);
                    _lexicalSymbols.Add(name, symbol);
                }

                if (!TryGetFunctionDeclaration(node, out var expressionIndex, out var function))
                {
                    continue;
                }

                if (!_lexicalSymbols.TryGetValue(function.Name, out var functionSymbol))
                {
                    functionSymbol = _builder.AddSymbol(
                        Avm1SourceSymbolKind.Function,
                        function.Name,
                        SourceTypeIndex.Invalid,
                        _builder.GetBuiltInType(Avm1SourceTypeKind.Function),
                        Avm1SourceSymbolFlags.DeclarationProvided);
                    _lexicalSymbols.Add(function.Name, functionSymbol);
                    _declaredSymbols.Add(functionSymbol);
                }

                _functionDeclarationSymbols.TryAdd(expressionIndex.Value, functionSymbol);
            }
        }

        private void PrepareControlLabels()
        {
            foreach (var index in _reachableNodes)
            {
                if (!IsIncludedStatement(index))
                    continue;
                var node = _ast[index];
                if (node.Block.IsValid && IsControlScope(node.Kind))
                    _controlScopes.TryAdd(node.Block.Value, node.Kind);
            }

            foreach (var index in _reachableNodes)
            {
                if (!IsIncludedStatement(index))
                    continue;
                var node = _ast[index];
                if (node.Kind is not (Avm1AstNodeKind.Break or Avm1AstNodeKind.Continue) ||
                    !node.Block.IsValid ||
                    _controlLabels.ContainsKey(node.Block.Value) ||
                    !_controlScopes.TryGetValue(node.Block.Value, out var scopeKind))
                {
                    continue;
                }

                var prefix = scopeKind is Avm1AstNodeKind.Switch ? "switch_b" : "loop_b";
                _controlLabels.Add(
                    node.Block.Value,
                    _builder.AddLabel(prefix + node.Block.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        private static AstIndex[] CollectReachableNodes(Avm1AstArena ast)
        {
            var result = new List<AstIndex>(ast.Nodes.Count);
            var pending = new Stack<AstIndex>();
            var visited = new bool[ast.Nodes.Count];
            pending.Push(ast.Root);
            while (pending.Count > 0)
            {
                var index = pending.Pop();
                if (!index.IsValid ||
                    (uint)index.Value >= (uint)visited.Length ||
                    visited[index.Value])
                {
                    continue;
                }

                visited[index.Value] = true;
                result.Add(index);
                var node = ast[index];
                for (var i = node.Children.Count - 1; i >= 0; i--)
                    pending.Push(ast.GetChild(node, i));
            }
            return result.ToArray();
        }

        private SourceStatementIndex ProjectStatement(AstIndex index)
        {
            if (!IsIncludedStatement(index))
                return SourceStatementIndex.Invalid;

            var node = _ast[index];
            var origin = AddOrigin(index);
            switch (node.Kind)
            {
                case Avm1AstNodeKind.Root:
                case Avm1AstNodeKind.Block:
                    return ProjectBlock(node, origin);

                case Avm1AstNodeKind.If:
                    return ProjectIf(node, origin);

                case Avm1AstNodeKind.IfFrameLoaded:
                    return ProjectIfFrameLoaded(node, origin);

                case Avm1AstNodeKind.While:
                    return ProjectLoop(node, origin, Avm1SourceStatementKind.While);

                case Avm1AstNodeKind.DoWhile:
                    return ProjectLoop(node, origin, Avm1SourceStatementKind.DoWhile);

                case Avm1AstNodeKind.For:
                    return ProjectFor(node, origin);

                case Avm1AstNodeKind.ForIn:
                    return ProjectForIn(node, origin);

                case Avm1AstNodeKind.With:
                    return ProjectWith(node, origin);

                case Avm1AstNodeKind.TargetControl:
                    return ProjectUnstructuredTargetControl(node, origin);

                case Avm1AstNodeKind.Try:
                    return ProjectTry(node, origin);

                case Avm1AstNodeKind.Switch:
                    return ProjectSwitch(node, origin);

                case Avm1AstNodeKind.Break:
                    return ProjectControlJump(node, origin, Avm1SourceStatementKind.Break);

                case Avm1AstNodeKind.Continue:
                    return ProjectControlJump(node, origin, Avm1SourceStatementKind.Continue);

                case Avm1AstNodeKind.AssignRegister:
                    return ProjectRegisterAssignment(node, origin, Avm1SourceOperator.Assign);

                case Avm1AstNodeKind.AssignTemp:
                    return TryGetFunctionDeclaration(node, out var functionExpression, out _)
                        ? ProjectFunctionDeclaration(functionExpression, origin)
                        : ProjectTemporaryAssignment(node, origin);

                case Avm1AstNodeKind.CompoundAssignRegister:
                    return ProjectRegisterAssignment(
                        node,
                        origin,
                        GetCompoundOperator((ActionOpcode)node.StartAction.Value));

                case Avm1AstNodeKind.AssignVariable:
                case Avm1AstNodeKind.CompoundAssignVariable:
                    {
                        var target = ProjectVariableReference(_ast.GetChild(node, 0));
                        var value = ProjectExpression(_ast.GetChild(node, 1));
                        var @operator = node.Kind is Avm1AstNodeKind.AssignVariable
                            ? Avm1SourceOperator.Assign
                            : GetCompoundOperator((ActionOpcode)node.StartAction.Value);
                        return AddExpressionStatement(
                            AddAssignment(target, value, @operator, origin),
                            origin);
                    }

                case Avm1AstNodeKind.AssignMember:
                case Avm1AstNodeKind.CompoundAssignMember:
                    {
                        var target = AddMemberAccess(
                            ProjectExpression(_ast.GetChild(node, 0)),
                            ProjectExpression(_ast.GetChild(node, 1)),
                            node.Merge.Value == 1,
                            origin);
                        var value = ProjectExpression(_ast.GetChild(node, 2));
                        var @operator = node.Kind is Avm1AstNodeKind.AssignMember
                            ? Avm1SourceOperator.Assign
                            : GetCompoundOperator((ActionOpcode)node.StartAction.Value);
                        return AddExpressionStatement(
                            AddAssignment(target, value, @operator, origin),
                            origin);
                    }

                case Avm1AstNodeKind.DeclareVariable:
                    return ProjectVariableDeclaration(node, origin);

                case Avm1AstNodeKind.ExpressionStatement:
                    {
                        var expressionIndex = _ast.GetChild(node, 0);
                        var expression = ProjectExpression(expressionIndex);
                        var projected = _builder.GetExpression(expression);
                        if (projected.Kind is Avm1SourceExpressionKind.FunctionLiteral &&
                            projected.Function.IsValid)
                        {
                            var function = _builder.GetFunction(projected.Function);
                            if (function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration))
                                return AddFunctionDeclaration(expression, function, origin);
                        }

                        return AddExpressionStatement(expression, origin);
                    }

                case Avm1AstNodeKind.Return:
                    return _builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: node.Children.Count == 0
                            ? SourceExpressionIndex.Invalid
                            : ProjectExpression(_ast.GetChild(node, 0)),
                        origin: origin);

                case Avm1AstNodeKind.Throw:
                    return _builder.AddStatement(
                        Avm1SourceStatementKind.Throw,
                        expression: ProjectExpression(_ast.GetChild(node, 0)),
                        origin: origin);

                case Avm1AstNodeKind.Opaque:
                    return ProjectOpaqueAction(node, origin);

                default:
                    return AddUnsupportedStatement(node, origin);
            }
        }

        private SourceStatementIndex ProjectBlock(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var children = new List<SourceStatementIndex>(node.Children.Count);
            for (var i = 0; i < node.Children.Count; i++)
            {
                if (TryProjectTellTarget(node, i, out var tellTarget, out var end))
                {
                    children.Add(tellTarget);
                    i = end;
                    continue;
                }

                var pendingStart = _pendingExpressionDeclarations.Count;
                var child = ProjectStatement(_ast.GetChild(node, i));
                for (var pending = pendingStart;
                    pending < _pendingExpressionDeclarations.Count;
                    pending++)
                {
                    children.Add(_pendingExpressionDeclarations[pending]);
                }
                if (_pendingExpressionDeclarations.Count > pendingStart)
                {
                    _pendingExpressionDeclarations.RemoveRange(
                        pendingStart,
                        _pendingExpressionDeclarations.Count - pendingStart);
                }
                if (child.IsValid)
                    children.Add(child);
            }
            return _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                origin: origin,
                children: children);
        }

        private bool TryProjectTellTarget(
            Avm1AstNode block,
            int start,
            out SourceStatementIndex result,
            out int end) =>
            TryProjectTellTarget(
                block,
                start,
                AstIndex.Invalid,
                out result,
                out end);

        private bool TryProjectTellTarget(
            Avm1AstNode block,
            int start,
            AstIndex parentTarget,
            out SourceStatementIndex result,
            out int end)
        {
            result = SourceStatementIndex.Invalid;
            end = start;
            var enterIndex = _ast.GetChild(block, start);
            var enter = _ast[enterIndex];
            if (!IsTargetControl(enter) ||
                IsTargetRestore(enter, parentTarget) ||
                !TryFindTellTargetEnd(block, start, parentTarget, out end))
            {
                return false;
            }

            var origin = AddOrigin(enterIndex);
            var targetIndex = _ast.GetChild(enter, 0);
            var target = ProjectExpression(targetIndex);
            var bodyChildren = new List<SourceStatementIndex>(end - start - 1);
            var parentBindingContext = _bindingContext;
            _bindingContext = ++_nextBindingContext;
            _dynamicScopeDepth++;
            try
            {
                for (var i = start + 1; i < end; i++)
                {
                    var recoveryChild = _ast[_ast.GetChild(block, i)];
                    if (IsTargetControl(recoveryChild) &&
                        TryProjectTellTarget(
                            block,
                            i,
                            targetIndex,
                            out var nested,
                            out var nestedEnd))
                    {
                        bodyChildren.Add(nested);
                        i = nestedEnd;
                        continue;
                    }

                    var child = ProjectStatement(_ast.GetChild(block, i));
                    if (child.IsValid)
                        bodyChildren.Add(child);
                }
            }
            finally
            {
                _dynamicScopeDepth--;
                _bindingContext = parentBindingContext;
            }

            var body = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                origin: origin,
                children: bodyChildren);
            result = _builder.AddStatement(
                Avm1SourceStatementKind.TellTarget,
                expression: target,
                origin: origin,
                children: [body]);
            return true;
        }

        private bool TryFindTellTargetEnd(
            Avm1AstNode block,
            int start,
            AstIndex parentTarget,
            out int end)
        {
            end = start;
            var enter = _ast[_ast.GetChild(block, start)];
            if (!IsTargetControl(enter))
                return false;

            var target = _ast.GetChild(enter, 0);
            for (var i = start + 1; i < block.Children.Count; i++)
            {
                var candidate = _ast[_ast.GetChild(block, i)];
                if (!IsTargetControl(candidate))
                    continue;
                if (IsTargetRestore(candidate, parentTarget))
                {
                    end = i;
                    return true;
                }
                if (!TryFindTellTargetEnd(
                    block,
                    i,
                    target,
                    out var nestedEnd))
                {
                    return false;
                }
                i = nestedEnd;
            }

            return false;
        }

        private bool IsTargetRestore(
            Avm1AstNode node,
            AstIndex parentTarget) =>
            parentTarget.IsValid
                ? AreEquivalentTargetExpressions(
                    _ast.GetChild(node, 0),
                    parentTarget,
                    depth: 0)
                : IsStaticTargetReset(node);

        private bool AreEquivalentTargetExpressions(
            AstIndex leftIndex,
            AstIndex rightIndex,
            int depth)
        {
            if (leftIndex == rightIndex)
                return true;
            if (depth > 64 || !leftIndex.IsValid || !rightIndex.IsValid)
                return false;

            if (TryGetTargetValue(leftIndex, out var leftValue) &&
                TryGetTargetValue(rightIndex, out var rightValue) &&
                AreEquivalentTargetValues(
                    leftValue,
                    rightValue,
                    new HashSet<(int Left, int Right)>()))
            {
                return true;
            }

            var left = _ast[leftIndex];
            var right = _ast[rightIndex];
            if (left.Kind != right.Kind || left.Children.Count != right.Children.Count)
                return false;

            if (left.Kind is Avm1AstNodeKind.Literal)
            {
                if (!left.StartAction.IsValid || !right.StartAction.IsValid)
                    return left.StartAction == right.StartAction &&
                        left.IntOperand == right.IntOperand;
                var leftFact = _method.ValueAnalysis[new ValueIndex(
                    left.StartAction.Value)];
                var rightFact = _method.ValueAnalysis[new ValueIndex(
                    right.StartAction.Value)];
                return leftFact.ConstantKind == rightFact.ConstantKind &&
                    leftFact.BooleanValue == rightFact.BooleanValue &&
                    leftFact.IntegerValue == rightFact.IntegerValue &&
                    leftFact.NumberValue.Equals(rightFact.NumberValue) &&
                    string.Equals(
                        leftFact.StringValue,
                        rightFact.StringValue,
                        StringComparison.Ordinal);
            }

            if (left.Kind is Avm1AstNodeKind.Register)
                return left.Block == right.Block && left.Merge == right.Merge;
            if (left.Kind is Avm1AstNodeKind.TempVar)
                return left.StartAction == right.StartAction;
            if (left.StartAction != right.StartAction ||
                left.IntOperand != right.IntOperand ||
                left.Block != right.Block ||
                left.Merge != right.Merge)
            {
                return false;
            }

            for (var i = 0; i < left.Children.Count; i++)
            {
                if (!AreEquivalentTargetExpressions(
                    _ast.GetChild(left, i),
                    _ast.GetChild(right, i),
                    depth + 1))
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryGetTargetValue(
            AstIndex expressionIndex,
            out ValueIndex value)
        {
            var expression = _ast[expressionIndex];
            if ((expression.Kind is
                    Avm1AstNodeKind.TempVar or
                    Avm1AstNodeKind.Literal) &&
                expression.StartAction.IsValid)
            {
                value = new ValueIndex(expression.StartAction.Value);
                return true;
            }

            if (expression.Kind is Avm1AstNodeKind.Register)
            {
                foreach (var access in _method.RegisterSsa.Accesses)
                {
                    if (access.Kind is Avm1RegisterAccessKind.Write &&
                        access.Register == expression.Block.Value &&
                        access.Version == expression.Merge.Value &&
                        access.Source.IsValid)
                    {
                        value = access.Source;
                        return true;
                    }
                }
            }

            value = ValueIndex.Invalid;
            return false;
        }

        private bool AreEquivalentTargetValues(
            ValueIndex left,
            ValueIndex right,
            HashSet<(int Left, int Right)> visited)
        {
            if (left == right)
                return true;
            if (!left.IsValid || !right.IsValid ||
                !visited.Add((left.Value, right.Value)))
            {
                return false;
            }

            return TryGetTransportSource(left, out var leftSource) &&
                    AreEquivalentTargetValues(
                        leftSource,
                        right,
                        visited) ||
                TryGetTransportSource(right, out var rightSource) &&
                    AreEquivalentTargetValues(
                        left,
                        rightSource,
                        visited);
        }

        private bool TryGetTransportSource(
            ValueIndex value,
            out ValueIndex source)
        {
            foreach (var instruction in _method.TacIr.Instructions)
            {
                if (instruction.Result != value)
                    continue;
                if (instruction.Op is Avm1TacOp.Copy &&
                    instruction.Operand0.IsValid)
                {
                    source = instruction.Operand0;
                    return true;
                }
                if (instruction.Op is Avm1TacOp.LoadRegister)
                {
                    foreach (var access in _method.RegisterSsa.Accesses)
                    {
                        if (access.Instruction == instruction.Index &&
                            access.Source.IsValid)
                        {
                            source = access.Source;
                            return true;
                        }
                    }
                }
                break;
            }

            source = ValueIndex.Invalid;
            return false;
        }

        private bool IsStaticTargetReset(Avm1AstNode node)
        {
            if (!IsTargetControl(node))
                return false;

            var target = _ast[_ast.GetChild(node, 0)];
            if (target.Kind is not Avm1AstNodeKind.Literal ||
                !target.StartAction.IsValid)
            {
                return false;
            }

            var fact = _method.ValueAnalysis[new ValueIndex(
                target.StartAction.Value)];
            return fact.ConstantKind is Avm1ConstantKind.String &&
                fact.StringValue is "";
        }

        private static bool IsTargetControl(Avm1AstNode node) =>
            node.Kind is Avm1AstNodeKind.TargetControl &&
            node.Children.Count == 1;

        private SourceStatementIndex ProjectUnstructuredTargetControl(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            const string description =
                "unstructured AVM1 target-scope transition";
            _diagnostics.Add(new Avm1SourceDiagnostic(
                RecoveryDiagnosticCode,
                Avm1SourceDiagnosticSeverity.Error,
                origin,
                "SetTarget could not be paired with a safe restoration in the " +
                    "same structured block; preserving its original bytecode."));
            var opaque = _builder.AddOpaque(
                Avm1SourceOpaqueKind.RecoveryStatement,
                description,
                origin,
                GetOpaqueBytecode(node));
            return _builder.AddStatement(
                Avm1SourceStatementKind.Opaque,
                name: _builder.InternString(description),
                opaque: opaque,
                origin: origin);
        }

        private SourceStatementIndex ProjectIf(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (node.Children.Count is < 2 or > 3)
                return AddUnsupportedStatement(node, origin, "If node has an invalid child layout.");

            var branches = new List<SourceStatementIndex>(node.Children.Count - 1);
            var thenBranch = ProjectStatement(_ast.GetChild(node, 1));
            branches.Add(thenBranch.IsValid ? thenBranch : AddEmptyBlock(origin));
            if (node.Children.Count == 3)
            {
                var elseBranch = ProjectStatement(_ast.GetChild(node, 2));
                if (elseBranch.IsValid)
                    branches.Add(elseBranch);
            }

            return _builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: ProjectExpression(_ast.GetChild(node, 0)),
                origin: origin,
                children: branches);
        }

        private SourceStatementIndex ProjectIfFrameLoaded(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var instruction = GetOriginInstruction(node);
            SourceExpressionIndex frame;
            AstIndex bodyIndex;
            switch (instruction?.Action)
            {
                case ActionWaitForFrame wait when node.Children.Count == 1:
                    frame = AddIntegerLiteral(wait.Frame + 1, origin);
                    bodyIndex = _ast.GetChild(node, 0);
                    break;
                case ActionWaitForFrame2 when node.Children.Count == 2:
                    frame = ProjectExpression(_ast.GetChild(node, 0));
                    bodyIndex = _ast.GetChild(node, 1);
                    break;
                default:
                    return AddUnsupportedStatement(
                        node,
                        origin,
                        "ifFrameLoaded node has an invalid action or child layout.");
            }

            var body = ProjectStatement(bodyIndex);
            return _builder.AddStatement(
                Avm1SourceStatementKind.IfFrameLoaded,
                expression: frame,
                origin: origin,
                children: [body.IsValid ? body : AddEmptyBlock(origin)]);
        }

        private SourceStatementIndex ProjectLoop(
            Avm1AstNode node,
            SourceOriginIndex origin,
            Avm1SourceStatementKind kind)
        {
            if (node.Children.Count == 0)
                return AddUnsupportedStatement(node, origin, "Loop node has no condition.");

            var body = new List<SourceStatementIndex>(node.Children.Count - 1);
            for (var i = 1; i < node.Children.Count; i++)
            {
                var child = ProjectStatement(_ast.GetChild(node, i));
                if (child.IsValid)
                    body.Add(child);
            }
            if (body.Count == 0)
                body.Add(AddEmptyBlock(origin));

            return _builder.AddStatement(
                kind,
                expression: ProjectExpression(_ast.GetChild(node, 0)),
                label: GetControlScopeLabel(node.Block),
                origin: origin,
                children: body);
        }

        private SourceStatementIndex ProjectControlJump(
            Avm1AstNode node,
            SourceOriginIndex origin,
            Avm1SourceStatementKind kind)
        {
            var label = SourceLabelIndex.Invalid;
            if (node.Block.IsValid && !_controlLabels.TryGetValue(node.Block.Value, out label))
            {
                _diagnostics.Add(new Avm1SourceDiagnostic(
                    InvalidControlTargetCode,
                    Avm1SourceDiagnosticSeverity.Error,
                    origin,
                    $"Recovery AST {node.Kind} target b{node.Block.Value} does not resolve to a control scope."));
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Opaque,
                    name: _builder.InternString(node.Kind.ToString()),
                    origin: origin);
            }

            return _builder.AddStatement(kind, label: label, origin: origin);
        }

        private SourceStatementIndex ProjectFor(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (node.Children.Count < 3)
                return AddUnsupportedStatement(node, origin, "For node has an invalid child layout.");

            var initializerClause = _ast[_ast.GetChild(node, 0)];
            var updateClause = _ast[_ast.GetChild(node, 2)];
            if (initializerClause.Kind is not Avm1AstNodeKind.ForInitializerList ||
                updateClause.Kind is not Avm1AstNodeKind.ForUpdateList)
            {
                return AddUnsupportedStatement(
                    node,
                    origin,
                    "For node does not contain explicit clause-list nodes.");
            }

            var aliasRegisters = GetForCounterRegisters(initializerClause, updateClause);
            if (aliasRegisters.Length == 0)
                return ProjectForCore(node, origin, initializerClause, updateClause);

            var previousContext = _bindingContext;
            var previousAliases = new Dictionary<int, SourceSymbolIndex>();
            var newAliases = new HashSet<int>();
            foreach (var register in aliasRegisters)
            {
                if (_registerSymbolOverrides.TryGetValue(register, out var previousAlias))
                    previousAliases.Add(register, previousAlias);
                else
                    newAliases.Add(register);

                _registerSymbolOverrides[register] = CreateForCounterSymbol(
                    register,
                    initializerClause);
            }
            _bindingContext = ++_nextBindingContext;

            try
            {
                return ProjectForCore(node, origin, initializerClause, updateClause);
            }
            finally
            {
                _bindingContext = previousContext;
                foreach (var register in newAliases)
                    _registerSymbolOverrides.Remove(register);
                foreach (var (register, symbol) in previousAliases)
                    _registerSymbolOverrides[register] = symbol;
            }
        }

        private bool IsIncludedStatement(AstIndex index) =>
            _includedStatementNodes is null || _includedStatementNodes.Contains(index.Value);

        private SourceStatementIndex AddEmptyBlock(SourceOriginIndex origin) =>
            _builder.AddStatement(Avm1SourceStatementKind.Block, origin: origin);

        private SourceStatementIndex ProjectForCore(
            Avm1AstNode node,
            SourceOriginIndex origin,
            Avm1AstNode initializerClause,
            Avm1AstNode updateClause)
        {

            var initializers = new SourceStatementIndex[initializerClause.Children.Count];
            for (var i = 0; i < initializers.Length; i++)
                initializers[i] = ProjectStatement(_ast.GetChild(initializerClause, i));

            var updates = new SourceExpressionIndex[updateClause.Children.Count];
            for (var i = 0; i < updates.Length; i++)
            {
                var updateIndex = _ast.GetChild(updateClause, i);
                if (TryProjectForRegisterStep(updateIndex, out var step))
                {
                    updates[i] = step;
                    continue;
                }

                var updateStatement = ProjectStatement(updateIndex);
                var projectedUpdate = _builder.GetStatement(updateStatement);
                if (projectedUpdate.Kind is not Avm1SourceStatementKind.Expression ||
                    !projectedUpdate.Expression.IsValid)
                {
                    return AddUnsupportedStatement(
                        node,
                        origin,
                        "For update is not representable as a Source HIR expression.");
                }
                updates[i] = projectedUpdate.Expression;
            }

            var body = new List<SourceStatementIndex>(node.Children.Count - 3);
            for (var i = 3; i < node.Children.Count; i++)
            {
                var child = ProjectStatement(_ast.GetChild(node, i));
                if (child.IsValid)
                    body.Add(child);
            }
            if (body.Count == 0)
                body.Add(AddEmptyBlock(origin));

            return _builder.AddStatement(
                Avm1SourceStatementKind.For,
                expression: ProjectExpression(_ast.GetChild(node, 1)),
                label: GetControlScopeLabel(node.Block),
                origin: origin,
                children: body,
                initializers: initializers,
                expressions: updates);
        }

        private SourceSymbolIndex CreateForCounterSymbol(
            int register,
            Avm1AstNode initializerClause)
        {
            var inferredType = _unknownType;
            for (var i = 0; i < initializerClause.Children.Count; i++)
            {
                var initializer = _ast[_ast.GetChild(initializerClause, i)];
                if (initializer.Kind is not Avm1AstNodeKind.AssignRegister ||
                    initializer.Block.Value != register ||
                    !_method.SymbolTable.TryGetRegisterSymbol(
                        register,
                        initializer.Merge.Value,
                        out var symbol))
                {
                    continue;
                }

                inferredType = MapType(symbol.Type);
                return GetRecoverySymbol(symbol);
            }

            return _builder.AddSymbol(
                Avm1SourceSymbolKind.Local,
                "_loc" + register.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + "_",
                SourceTypeIndex.Invalid,
                inferredType,
                Avm1SourceSymbolFlags.CompilerGenerated);
        }

        private int[] GetForCounterRegisters(
            Avm1AstNode initializerClause,
            Avm1AstNode updateClause)
        {
            var initializedRegisters = new HashSet<int>();
            for (var i = 0; i < initializerClause.Children.Count; i++)
            {
                var initializer = _ast[_ast.GetChild(initializerClause, i)];
                if (initializer.Kind is Avm1AstNodeKind.AssignRegister)
                    initializedRegisters.Add(initializer.Block.Value);
            }

            var result = new List<int>();
            for (var i = 0; i < updateClause.Children.Count; i++)
            {
                var update = _ast[_ast.GetChild(updateClause, i)];
                if (update.Kind is not (
                        Avm1AstNodeKind.AssignRegister or
                        Avm1AstNodeKind.CompoundAssignRegister) ||
                    !initializedRegisters.Contains(update.Block.Value) ||
                    result.Contains(update.Block.Value))
                {
                    continue;
                }

                result.Add(update.Block.Value);
            }

            result.Sort();
            return result.ToArray();
        }

        private bool TryProjectForRegisterStep(
            AstIndex statementIndex,
            out SourceExpressionIndex expression)
        {
            expression = SourceExpressionIndex.Invalid;
            var assignment = _ast[statementIndex];
            if (assignment.Kind is not Avm1AstNodeKind.AssignRegister ||
                assignment.Children.Count != 1)
            {
                return false;
            }

            var valueIndex = _ast.GetChild(assignment, 0);
            var value = _ast[valueIndex];
            var opcode = (ActionOpcode)value.StartAction.Value;
            if (value.Kind is not Avm1AstNodeKind.Unary ||
                opcode is not (ActionOpcode.Increment or ActionOpcode.Decrement) ||
                value.Children.Count != 1)
            {
                return false;
            }

            var operand = _ast[_ast.GetChild(value, 0)];
            if (operand.Kind is not Avm1AstNodeKind.Register ||
                operand.Block != assignment.Block)
            {
                return false;
            }

            var origin = AddOrigin(statementIndex);
            expression = _builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                opcode is ActionOpcode.Increment
                    ? Avm1SourceOperator.PostfixIncrement
                    : Avm1SourceOperator.PostfixDecrement,
                origin: origin,
                children:
                [
                    AddSymbolReference(
                        GetRegisterSymbol(assignment.Block.Value, assignment.Merge.Value),
                        origin)
                ]);
            return true;
        }

        private SourceStatementIndex ProjectForIn(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (node.Children.Count < 2)
                return AddUnsupportedStatement(node, origin, "For-in node has an invalid child layout.");

            var keyIndex = _ast.GetChild(node, 0);
            var key = ProjectExpression(keyIndex);
            var flags = Avm1SourceStatementFlags.None;
            if (_ast[keyIndex].Kind is Avm1AstNodeKind.Register)
            {
                var keyExpression = _builder.GetExpression(key);
                if (!keyExpression.Symbol.IsValid)
                {
                    return AddUnsupportedStatement(
                        node,
                        origin,
                        "For-in register key has no projected symbol.");
                }

                _declaredSymbols.Add(keyExpression.Symbol);
                flags |= Avm1SourceStatementFlags.ForInDeclaresKey;
            }

            var body = new List<SourceStatementIndex>(node.Children.Count - 2);
            for (var i = 2; i < node.Children.Count; i++)
            {
                var child = ProjectStatement(_ast.GetChild(node, i));
                if (child.IsValid)
                    body.Add(child);
            }
            if (body.Count == 0)
                body.Add(AddEmptyBlock(origin));

            return _builder.AddStatement(
                Avm1SourceStatementKind.ForIn,
                expression: key,
                secondaryExpression: ProjectExpression(_ast.GetChild(node, 1)),
                label: GetControlScopeLabel(node.Block),
                flags: flags,
                origin: origin,
                children: body);
        }

        private SourceStatementIndex ProjectSwitch(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (node.Children.Count == 0)
                return AddUnsupportedStatement(node, origin, "Switch node has no discriminator.");

            var labels = new List<SourceStatementIndex>(node.Children.Count - 1);
            var hasDefault = false;
            for (var i = 1; i < node.Children.Count; i++)
            {
                var labelIndex = _ast.GetChild(node, i);
                if (!IsIncludedStatement(labelIndex))
                    continue;
                var label = _ast[labelIndex];
                labels.Add(label.Kind switch
                {
                    Avm1AstNodeKind.SwitchCase => ProjectSwitchCase(labelIndex, label),
                    Avm1AstNodeKind.SwitchDefault when !hasDefault =>
                        ProjectSwitchDefault(labelIndex, label),
                    Avm1AstNodeKind.SwitchDefault => AddUnsupportedStatement(
                        node,
                        origin,
                        "Switch node contains more than one default label."),
                    _ => AddUnsupportedStatement(
                        node,
                        origin,
                        $"Switch child {label.Kind} is not a case or default label.")
                });
                hasDefault |= label.Kind is Avm1AstNodeKind.SwitchDefault;
            }

            return _builder.AddStatement(
                Avm1SourceStatementKind.Switch,
                expression: ProjectExpression(_ast.GetChild(node, 0)),
                label: GetControlScopeLabel(node.Block),
                origin: origin,
                children: labels);
        }

        private SourceStatementIndex ProjectSwitchCase(
            AstIndex index,
            Avm1AstNode node)
        {
            var origin = AddOrigin(index);
            if (node.Children.Count is < 1 or > 2)
                return AddUnsupportedStatement(node, origin, "Switch case has an invalid child layout.");

            var body = node.Children.Count == 2
                ? ProjectStatement(_ast.GetChild(node, 1))
                : SourceStatementIndex.Invalid;
            return _builder.AddStatement(
                Avm1SourceStatementKind.SwitchCase,
                expression: ProjectExpression(_ast.GetChild(node, 0)),
                origin: origin,
                children: body.IsValid ? [body] : []);
        }

        private SourceStatementIndex ProjectSwitchDefault(
            AstIndex index,
            Avm1AstNode node)
        {
            var origin = AddOrigin(index);
            if (node.Children.Count > 1)
                return AddUnsupportedStatement(node, origin, "Switch default has an invalid child layout.");

            var body = node.Children.Count == 1
                ? ProjectStatement(_ast.GetChild(node, 0))
                : SourceStatementIndex.Invalid;
            return _builder.AddStatement(
                Avm1SourceStatementKind.SwitchDefault,
                origin: origin,
                children: body.IsValid ? [body] : []);
        }

        private SourceStatementIndex ProjectWith(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (node.Children.Count != 2)
                return AddUnsupportedStatement(node, origin, "With node has an invalid child layout.");

            var scope = ProjectExpression(_ast.GetChild(node, 0));
            SourceStatementIndex body;
            var parentBindingContext = _bindingContext;
            _bindingContext = ++_nextBindingContext;
            _dynamicScopeDepth++;
            try
            {
                body = ProjectStatement(_ast.GetChild(node, 1));
            }
            finally
            {
                _dynamicScopeDepth--;
                _bindingContext = parentBindingContext;
            }

            return _builder.AddStatement(
                Avm1SourceStatementKind.With,
                expression: scope,
                origin: origin,
                children: [body.IsValid ? body : AddEmptyBlock(origin)]);
        }

        private SourceStatementIndex ProjectTry(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (!node.StartAction.IsValid ||
                _method.Instructions[node.StartAction].Action is not ActionTry tryAction)
            {
                return AddUnsupportedStatement(node, origin, "Try node has no ActionTry metadata.");
            }

            var hasCatch = tryAction.Flags.HasFlag(TryFlags.CatchBlock) ||
                !tryAction.CatchBody.IsEmpty;
            var hasFinally = tryAction.Flags.HasFlag(TryFlags.FinallyBlock) ||
                !tryAction.FinallyBody.IsEmpty;
            var expectedChildren = 1 + (hasCatch ? 1 : 0) + (hasFinally ? 1 : 0);
            if ((!hasCatch && !hasFinally) || node.Children.Count != expectedChildren)
            {
                return AddUnsupportedStatement(
                    node,
                    origin,
                    "Try node clauses do not match its ActionTry metadata.");
            }

            var clauses = new SourceStatementIndex[expectedChildren];
            clauses[0] = ProjectStatement(_ast.GetChild(node, 0));
            if (!clauses[0].IsValid)
                clauses[0] = AddEmptyBlock(origin);
            var recoveryChild = 1;
            var sourceChild = 1;

            if (hasCatch)
            {
                if (!TryGetTryInstructionRegion(node.StartAction, out var region))
                    return AddUnsupportedStatement(node, origin, "Try catch region metadata is missing.");

                var catchSymbol = CreateCatchSymbol(tryAction, region);
                var catchBodyIndex = _ast.GetChild(node, recoveryChild++);
                var catchBody = ProjectCatchBody(
                    catchBodyIndex,
                    tryAction,
                    catchSymbol);
                if (!catchBody.IsValid)
                    catchBody = AddEmptyBlock(AddOrigin(catchBodyIndex));
                clauses[sourceChild++] = _builder.AddStatement(
                    Avm1SourceStatementKind.CatchClause,
                    symbol: catchSymbol,
                    origin: AddOrigin(catchBodyIndex),
                    children: [catchBody]);
            }

            if (hasFinally)
            {
                var finallyBodyIndex = _ast.GetChild(node, recoveryChild);
                var finallyBody = ProjectStatement(finallyBodyIndex);
                if (!finallyBody.IsValid)
                    finallyBody = AddEmptyBlock(AddOrigin(finallyBodyIndex));
                clauses[sourceChild] = _builder.AddStatement(
                    Avm1SourceStatementKind.FinallyClause,
                    origin: AddOrigin(finallyBodyIndex),
                    children: [finallyBody]);
            }

            return _builder.AddStatement(
                Avm1SourceStatementKind.Try,
                origin: origin,
                children: clauses);
        }

        private SourceStatementIndex ProjectCatchBody(
            AstIndex body,
            ActionTry tryAction,
            SourceSymbolIndex catchSymbol)
        {
            var parentBindingContext = _bindingContext;
            _bindingContext = ++_nextBindingContext;
            var catchInRegister = tryAction.Flags.HasFlag(TryFlags.CatchInRegister);
            var hadPreviousBinding = false;
            var previousBinding = SourceSymbolIndex.Invalid;
            var bindingName = tryAction.CatchVariable;
            if (catchInRegister)
            {
                hadPreviousBinding = _registerSymbolOverrides.TryGetValue(
                    tryAction.CatchRegister,
                    out previousBinding);
                _registerSymbolOverrides[tryAction.CatchRegister] = catchSymbol;
            }
            else
            {
                hadPreviousBinding = _scopedLexicalSymbols.TryGetValue(
                    bindingName,
                    out previousBinding);
                _scopedLexicalSymbols[bindingName] = catchSymbol;
            }

            try
            {
                return ProjectStatement(body);
            }
            finally
            {
                if (catchInRegister)
                {
                    if (hadPreviousBinding)
                        _registerSymbolOverrides[tryAction.CatchRegister] = previousBinding;
                    else
                        _registerSymbolOverrides.Remove(tryAction.CatchRegister);
                }
                else if (hadPreviousBinding)
                {
                    _scopedLexicalSymbols[bindingName] = previousBinding;
                }
                else
                {
                    _scopedLexicalSymbols.Remove(bindingName);
                }

                _bindingContext = parentBindingContext;
            }
        }

        private SourceSymbolIndex CreateCatchSymbol(
            ActionTry tryAction,
            Avm1TryInstructionRegion region)
        {
            var flags = Avm1SourceSymbolFlags.DeclarationProvided;
            var inferredType = _unknownType;
            string name;
            if (tryAction.Flags.HasFlag(TryFlags.CatchInRegister))
            {
                if (TryGetCatchRegisterAccess(
                        region.CatchEnterAction,
                        tryAction.CatchRegister,
                        out var access) &&
                    _method.SymbolTable.TryGetRegisterSymbol(
                        access.Register,
                        access.Version,
                        out var recoverySymbol))
                {
                    name = recoverySymbol.Name;
                    inferredType = MapType(recoverySymbol.Type);
                }
                else
                {
                    name = "_loc" + tryAction.CatchRegister.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "_";
                }

                if (name.StartsWith("_loc", StringComparison.Ordinal))
                    flags |= Avm1SourceSymbolFlags.CompilerGenerated;
            }
            else if (IsIdentifier(tryAction.CatchVariable))
            {
                name = tryAction.CatchVariable;
            }
            else
            {
                name = "_catch_";
                flags |= Avm1SourceSymbolFlags.CompilerGenerated;
            }

            var symbol = _builder.AddSymbol(
                Avm1SourceSymbolKind.Catch,
                name,
                SourceTypeIndex.Invalid,
                inferredType,
                flags);
            _declaredSymbols.Add(symbol);
            return symbol;
        }

        private bool TryGetTryInstructionRegion(
            ActionIndex enterAction,
            out Avm1TryInstructionRegion region)
        {
            foreach (var candidate in _method.Instructions.TryRegions)
            {
                if (candidate.EnterAction != enterAction)
                    continue;

                region = candidate;
                return true;
            }

            region = default;
            return false;
        }

        private bool TryGetCatchRegisterAccess(
            ActionIndex catchAction,
            int register,
            out Avm1RegisterAccess access)
        {
            foreach (var candidate in _method.RegisterSsa.Accesses)
            {
                if (candidate.Action != catchAction ||
                    candidate.Register != register ||
                    candidate.Kind is not Avm1RegisterAccessKind.Write)
                {
                    continue;
                }

                access = candidate;
                return true;
            }

            access = default;
            return false;
        }

        private SourceLabelIndex GetControlScopeLabel(BlockIndex block)
        {
            return block.IsValid && _controlLabels.TryGetValue(block.Value, out var label)
                ? label
                : SourceLabelIndex.Invalid;
        }

        private SourceStatementIndex ProjectRegisterAssignment(
            Avm1AstNode node,
            SourceOriginIndex origin,
            Avm1SourceOperator @operator)
        {
            var symbol = GetRegisterSymbol(node.Block.Value, node.Merge.Value);
            var target = AddSymbolReference(symbol, origin);
            var value = ProjectExpression(_ast.GetChild(node, 0));
            _declaredSymbols.Add(symbol);
            if (_emittedDeclarations.Add(symbol))
            {
                var initializer = value;
                if (@operator is not Avm1SourceOperator.Assign)
                {
                    initializer = _builder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        GetBinaryOperator(@operator),
                        origin: origin,
                        children: [target, value]);
                }
                else if (IsUndefinedLiteral(value))
                {
                    initializer = SourceExpressionIndex.Invalid;
                }

                return _builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: initializer,
                    symbol: symbol,
                    origin: origin);
            }

            return AddExpressionStatement(
                AddAssignment(target, value, @operator, origin),
                origin);
        }

        private SourceStatementIndex ProjectTemporaryAssignment(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var valueId = node.StartAction.Value;
            var symbol = GetTemporarySymbol(valueId);
            var value = ProjectExpression(_ast.GetChild(node, 0));
            _declaredSymbols.Add(symbol);
            if (_emittedDeclarations.Add(symbol))
            {
                return _builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: IsUndefinedLiteral(value)
                        ? SourceExpressionIndex.Invalid
                        : value,
                    symbol: symbol,
                    origin: origin);
            }

            return AddExpressionStatement(
                AddAssignment(
                    AddSymbolReference(symbol, origin),
                    value,
                    Avm1SourceOperator.Assign,
                    origin),
                origin);
        }

        private SourceStatementIndex ProjectVariableDeclaration(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var nameIndex = _ast.GetChild(node, 0);
            if (!TryGetStringLiteral(nameIndex, out var name) || !IsIdentifier(name))
                return AddUnsupportedStatement(node, origin, "Variable declaration name is not a static identifier.");

            if (!_lexicalSymbols.TryGetValue(name, out var symbol))
            {
                symbol = _builder.AddSymbol(
                    Avm1SourceSymbolKind.Local,
                    name,
                    SourceTypeIndex.Invalid,
                    _unknownType,
                    IsCompilerTemporaryName(name)
                        ? Avm1SourceSymbolFlags.CompilerGenerated
                        : Avm1SourceSymbolFlags.None);
                _lexicalSymbols.Add(name, symbol);
            }

            _declaredSymbols.Add(symbol);
            _emittedDeclarations.Add(symbol);
            return _builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: node.Children.Count > 1
                    ? ProjectExpression(_ast.GetChild(node, 1))
                    : SourceExpressionIndex.Invalid,
                symbol: symbol,
                origin: origin);
        }

        private SourceStatementIndex AddExpressionStatement(
            SourceExpressionIndex expression,
            SourceOriginIndex origin)
        {
            return _builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression,
                origin: origin);
        }

        private SourceStatementIndex AddUnsupportedStatement(
            Avm1AstNode node,
            SourceOriginIndex origin,
            string? detail = null)
        {
            var message = detail ?? $"Recovery AST statement {node.Kind} is not projected yet.";
            _diagnostics.Add(new Avm1SourceDiagnostic(
                UnsupportedStatementCode,
                Avm1SourceDiagnosticSeverity.Error,
                origin,
                message));
            var opaque = _builder.AddOpaque(
                Avm1SourceOpaqueKind.RecoveryStatement,
                message,
                origin,
                GetOpaqueBytecode(node));
            return _builder.AddStatement(
                Avm1SourceStatementKind.Opaque,
                name: _builder.InternString(node.Kind.ToString()),
                opaque: opaque,
                origin: origin);
        }

        private SourceStatementIndex ProjectOpaqueAction(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var instruction = GetOriginInstruction(node);
            var description = instruction is null
                ? "unknown AVM1 action"
                : $"{instruction.Value.Action.Opcode} at byte offset 0x{instruction.Value.Offset:X}";
            _diagnostics.Add(new Avm1SourceDiagnostic(
                UnsupportedActionCode,
                Avm1SourceDiagnosticSeverity.Error,
                origin,
                $"Unsupported AVM1 action {description}; semantic source round-trip is unavailable."));
            var opaque = _builder.AddOpaque(
                Avm1SourceOpaqueKind.UnsupportedAction,
                description,
                origin,
                GetOpaqueBytecode(node));
            return _builder.AddStatement(
                Avm1SourceStatementKind.Opaque,
                name: _builder.InternString(description),
                opaque: opaque,
                origin: origin);
        }

        private SourceExpressionIndex ProjectExpression(AstIndex index)
        {
            if (!index.IsValid)
                return AddUndefinedLiteral(SourceOriginIndex.Invalid);

            if (_bindingContext == 0)
            {
                var cached = _projectedExpressions[index.Value];
                if (cached >= 0)
                    return new SourceExpressionIndex(cached);
            }
            else if (_contextualProjectedExpressions?.TryGetValue(
                (index.Value, _bindingContext),
                out var contextual) is true)
            {
                return contextual;
            }

            var node = _ast[index];
            var origin = AddOrigin(index);
            var projected = node.Kind switch
            {
                Avm1AstNodeKind.Literal => ProjectLiteral(node, origin),
                Avm1AstNodeKind.Register => ProjectRegisterReference(node, origin),
                Avm1AstNodeKind.TempVar => AddSymbolReference(
                    GetTemporarySymbol(node.StartAction.Value),
                    origin),
                Avm1AstNodeKind.Variable => ProjectVariableReference(_ast.GetChild(node, 0)),
                Avm1AstNodeKind.AssignMember or
                    Avm1AstNodeKind.CompoundAssignMember =>
                    ProjectMemberAssignment(node, origin),
                Avm1AstNodeKind.AssignVariable or
                    Avm1AstNodeKind.CompoundAssignVariable => AddAssignment(
                    ProjectVariableReference(_ast.GetChild(node, 0)),
                    ProjectExpression(_ast.GetChild(node, 1)),
                    node.Kind is Avm1AstNodeKind.AssignVariable
                        ? Avm1SourceOperator.Assign
                        : GetCompoundOperator((ActionOpcode)node.StartAction.Value),
                    origin),
                Avm1AstNodeKind.AssignRegister => ProjectRegisterAssignmentExpression(
                    node,
                    origin,
                    Avm1SourceOperator.Assign),
                Avm1AstNodeKind.CompoundAssignRegister => ProjectRegisterAssignmentExpression(
                    node,
                    origin,
                    GetCompoundOperator((ActionOpcode)node.StartAction.Value)),
                Avm1AstNodeKind.Prefix => ProjectStepExpression(node, origin, prefix: true),
                Avm1AstNodeKind.Postfix => ProjectStepExpression(node, origin, prefix: false),
                Avm1AstNodeKind.Unary => ProjectUnary(node, origin),
                Avm1AstNodeKind.Binary => ProjectBinary(node, origin),
                Avm1AstNodeKind.Conditional => _builder.AddExpression(
                    Avm1SourceExpressionKind.Conditional,
                    origin: origin,
                    children: ProjectExpressionChildren(node, 0)),
                Avm1AstNodeKind.MemberAccess => AddMemberAccess(
                    ProjectExpression(_ast.GetChild(node, 0)),
                    ProjectExpression(_ast.GetChild(node, 1)),
                    node.Merge.Value == 1,
                    origin),
                Avm1AstNodeKind.Delete => _builder.AddExpression(
                    Avm1SourceExpressionKind.Delete,
                    origin: origin,
                    children: [ProjectExpression(_ast.GetChild(node, 0))]),
                Avm1AstNodeKind.Intrinsic => ProjectIntrinsic(node, origin),
                Avm1AstNodeKind.CallFunction => ProjectFunctionCall(node, origin),
                Avm1AstNodeKind.NewObject => ProjectNewObject(node, origin),
                Avm1AstNodeKind.NewMethod => ProjectMethodCall(node, origin, isNew: true),
                Avm1AstNodeKind.CallMethod => ProjectMethodCall(node, origin, isNew: false),
                Avm1AstNodeKind.ArrayLiteral => _builder.AddExpression(
                    Avm1SourceExpressionKind.ArrayLiteral,
                    origin: origin,
                    children: ProjectExpressionChildren(node, 0)),
                Avm1AstNodeKind.ObjectLiteral => _builder.AddExpression(
                    Avm1SourceExpressionKind.ObjectLiteral,
                    origin: origin,
                    children: ProjectExpressionChildren(node, 0)),
                Avm1AstNodeKind.FunctionLiteral => ProjectFunctionLiteral(node, origin),
                _ => AddUnsupportedExpression(node, origin)
            };

            if (_bindingContext == 0)
            {
                _projectedExpressions[index.Value] = projected.Value;
            }
            else
            {
                (_contextualProjectedExpressions ??= [])
                    .Add((index.Value, _bindingContext), projected);
            }
            return projected;
        }

        private SourceExpressionIndex ProjectRegisterReference(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var key = (Register: node.Block.Value, Version: node.Merge.Value);
            if (_registerAliases.TryGetValue(key, out var alias))
            {
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.QualifiedName,
                    name: _builder.InternString(alias),
                    origin: origin);
            }

            return AddSymbolReference(
                GetRegisterSymbol(key.Register, key.Version),
                origin);
        }

        private SourceExpressionIndex ProjectLiteral(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (node.StartAction.Value < 0)
            {
                if (node.IntOperand is Avm1AstArena.SyntheticTrueLiteral)
                {
                    var syntheticLiteral = _builder.AddLiteral(
                        Avm1SourceLiteralKind.Boolean,
                        booleanValue: true);
                    return _builder.AddExpression(
                        Avm1SourceExpressionKind.Literal,
                        literal: syntheticLiteral,
                        origin: origin);
                }

                return AddUndefinedLiteral(origin);
            }

            if (TryProjectScopedRegisterReference(node, origin, out var reference))
                return reference;

            var fact = _method.ValueAnalysis[new ValueIndex(node.StartAction.Value)];
            var literal = fact.ConstantKind switch
            {
                Avm1ConstantKind.Undefined => _builder.AddLiteral(Avm1SourceLiteralKind.Undefined),
                Avm1ConstantKind.Null => _builder.AddLiteral(Avm1SourceLiteralKind.Null),
                Avm1ConstantKind.Boolean => _builder.AddLiteral(
                    Avm1SourceLiteralKind.Boolean,
                    booleanValue: fact.BooleanValue),
                Avm1ConstantKind.Integer => _builder.AddLiteral(
                    Avm1SourceLiteralKind.Integer,
                    integerValue: fact.IntegerValue),
                Avm1ConstantKind.Number => _builder.AddLiteral(
                    Avm1SourceLiteralKind.Number,
                    numberValue: fact.NumberValue),
                Avm1ConstantKind.String => _builder.AddLiteral(
                    Avm1SourceLiteralKind.String,
                    stringValue: _builder.InternString(fact.StringValue ?? string.Empty)),
                _ => _builder.AddLiteral(Avm1SourceLiteralKind.Undefined)
            };
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: literal,
                origin: origin);
        }

        private bool TryProjectScopedRegisterReference(
            Avm1AstNode node,
            SourceOriginIndex origin,
            out SourceExpressionIndex reference)
        {
            reference = SourceExpressionIndex.Invalid;
            var value = node.StartAction.Value;
            if (_registerSymbolOverrides.Count == 0 ||
                value < 0)
            {
                return false;
            }

            _definitionByValue ??= CreateDefinitionMap(_method);
            if (value >= _definitionByValue.Length)
                return false;

            var definitionIndex = _definitionByValue[value];
            if (definitionIndex < 0)
                return false;

            var definition = _method.TacIr[new IrIndex(definitionIndex)];
            if (definition.Op is not Avm1TacOp.LoadRegister ||
                !_registerSymbolOverrides.TryGetValue(
                    definition.IntOperand,
                    out var symbol))
            {
                return false;
            }

            reference = AddSymbolReference(symbol, origin);
            return true;
        }

        private static int[] CreateDefinitionMap(Avm1MethodDecompilation method)
        {
            var result = new int[method.ValueAnalysis.Facts.Count];
            Array.Fill(result, -1);
            foreach (var instruction in method.TacIr.Instructions)
            {
                if (instruction.Result.IsValid &&
                    instruction.Result.Value < result.Length)
                {
                    result[instruction.Result.Value] = instruction.Index.Value;
                }
            }

            return result;
        }

        private SourceExpressionIndex ProjectFunctionLiteral(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (!TryGetFunctionDefinition(node, out var definition))
                return AddUnsupportedExpression(node, origin);

            var isDeclaration = _functionDeclarationSymbols.TryGetValue(
                node.Index.Value,
                out var declarationSymbol);
            var nameSymbol = isDeclaration
                ? declarationSymbol
                : SourceSymbolIndex.Invalid;
            if (definition.Name.Length > 0 && !IsIdentifier(definition.Name))
            {
                _diagnostics.Add(new Avm1SourceDiagnostic(
                    UnsupportedExpressionCode,
                    Avm1SourceDiagnosticSeverity.Error,
                    origin,
                    $"Named function '{definition.Name}' is not a valid AS2 identifier."));
            }
            if (!isDeclaration && IsIdentifier(definition.Name))
            {
                nameSymbol = _builder.AddSymbol(
                    Avm1SourceSymbolKind.Function,
                    definition.Name,
                    SourceTypeIndex.Invalid,
                    _builder.GetBuiltInType(Avm1SourceTypeKind.Function),
                    Avm1SourceSymbolFlags.DeclarationProvided);
            }

            var bodyActions = Avm1Decompiler.DecodeActions(
                definition.Body,
                _method.Instructions.SwfVersion).ToList();
            var outerActions = _method.Instructions.Instructions
                .Select(instruction => instruction.Action)
                .ToArray();
            var activePool = Text.Avm1ClassDecompiler.FindActiveConstantPool(
                outerActions,
                node.StartAction.Value);
            if (activePool is not null)
                bodyActions.Insert(0, new ActionConstantPool(activePool));

            var nestedProjectionHints = _projectionHints?.GetNestedFunction(
                _nextNestedFunctionHint++);
            var nestedMethod = Avm1Decompiler.DecompileMethod(
                bodyActions,
                _method.Instructions.SwfVersion,
                new FunctionContext(definition.Flags, definition.Parameters),
                _method.TypeEnvironment,
                new Avm1MethodDecompilationOptions
                {
                    TimelineLayout = _method.TimelineLayout,
                    SourceProjectionHints = nestedProjectionHints
                });
            var capturesDynamicScope = _dynamicScopeDepth != 0;
            var nestedProjector = new Projector(
                nestedMethod,
                _session,
                capturesDynamicScope ? null : BuildCaptureEnvironment(),
                nameSymbol,
                registerAliases: null,
                includedStatementNodes: null,
                coalesceNonInterferingRegisterVersions:
                    _coalesceNonInterferingRegisterVersions,
                projectionHints: nestedProjectionHints,
                classAbiActionsHandledExternally: false);
            var nestedCodeUnit = nestedProjector.ProjectCodeUnit();
            if (!isDeclaration &&
                !nameSymbol.IsValid &&
                definition.Flags.HasFlag(FunctionFlags.PreloadArguments) &&
                TryExtractCanonicalFunctionSelfBinding(
                    nestedCodeUnit,
                    out var recoveredNameSymbol,
                    out var recoveredBody))
            {
                nameSymbol = recoveredNameSymbol;
                nestedCodeUnit = nestedCodeUnit with
                {
                    Body = recoveredBody
                };
            }
            var flags = (isDeclaration
                ? Avm1SourceFunctionFlags.Declaration
                : Avm1SourceFunctionFlags.None) |
                (capturesDynamicScope
                    ? Avm1SourceFunctionFlags.CapturesDynamicScope
                    : Avm1SourceFunctionFlags.None);
            var function = _builder.AddFunction(
                nameSymbol,
                nestedCodeUnit.Body,
                flags,
                origin,
                nestedCodeUnit.Parameters,
                nestedCodeUnit.Captures);
            return _builder.AddExpression(
                Avm1SourceExpressionKind.FunctionLiteral,
                function: function,
                origin: origin);
        }

        private SourceStatementIndex ProjectFunctionDeclaration(
            AstIndex expressionIndex,
            SourceOriginIndex origin)
        {
            var expression = ProjectExpression(expressionIndex);
            var projected = _builder.GetExpression(expression);
            if (projected.Kind is not Avm1SourceExpressionKind.FunctionLiteral ||
                !projected.Function.IsValid)
            {
                throw new InvalidOperationException(
                    "A recovered function declaration did not project to a function expression.");
            }

            return AddFunctionDeclaration(
                expression,
                _builder.GetFunction(projected.Function),
                origin);
        }

        private SourceStatementIndex AddFunctionDeclaration(
            SourceExpressionIndex expression,
            Avm1SourceFunction function,
            SourceOriginIndex origin)
        {
            if (!function.NameSymbol.IsValid ||
                !function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration))
            {
                throw new InvalidOperationException(
                    "A Source HIR function declaration has no declared function symbol.");
            }

            return _builder.AddStatement(
                Avm1SourceStatementKind.FunctionDeclaration,
                expression: expression,
                symbol: function.NameSymbol,
                origin: origin);
        }

        private bool TryGetFunctionDeclaration(
            Avm1AstNode statement,
            out AstIndex expressionIndex,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out ActionDefineFunction2? function)
        {
            expressionIndex = AstIndex.Invalid;
            function = null;
            if (statement.Kind is not (
                    Avm1AstNodeKind.ExpressionStatement or
                    Avm1AstNodeKind.AssignTemp) ||
                statement.Children.Count != 1)
            {
                return false;
            }

            expressionIndex = _ast.GetChild(statement, 0);
            var expression = _ast[expressionIndex];
            return expression.Kind is Avm1AstNodeKind.FunctionLiteral &&
                TryGetFunctionDefinition(expression, out function) &&
                IsIdentifier(function.Name);
        }

        private Dictionary<string, SourceSymbolIndex> BuildCaptureEnvironment()
        {
            var result = new Dictionary<string, SourceSymbolIndex>(
                _outerLexicalSymbols,
                StringComparer.Ordinal);
            foreach (var (name, symbol) in _lexicalSymbols)
                result[name] = symbol;
            foreach (var (name, symbol) in _scopedLexicalSymbols)
                result[name] = symbol;
            return result;
        }

        private bool TryGetFunctionDefinition(
            Avm1AstNode node,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out ActionDefineFunction2? function)
        {
            function = null;
            if (!node.StartAction.IsValid)
                return false;

            function = _method.Instructions[node.StartAction].Action switch
            {
                ActionDefineFunction2 function2 => function2,
                ActionDefineFunction function1 => Text.Avm1ClassDecompiler.UpgradeFunction(function1),
                _ => null
            };
            return function is not null;
        }

        private SourceExpressionIndex ProjectVariableReference(AstIndex nameIndex)
        {
            if (TryGetStringLiteral(nameIndex, out var name))
                return ResolveNameReference(name, AddOrigin(nameIndex));

            var origin = AddOrigin(nameIndex);
            return _builder.AddExpression(
                Avm1SourceExpressionKind.ComputedDynamicName,
                origin: origin,
                children: [ProjectExpression(nameIndex)]);
        }

        private SourceExpressionIndex ResolveNameReference(string name, SourceOriginIndex origin)
        {
            if (_scopedLexicalSymbols.TryGetValue(name, out var scopedSymbol))
                return AddSymbolReference(scopedSymbol, origin);

            if (_dynamicScopeDepth != 0 &&
                IsIdentifier(name) &&
                !TryGetSpecialSymbolKind(name, out _))
            {
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.DynamicName,
                    name: _builder.InternString(name),
                    origin: origin);
            }

            if (_lexicalSymbols.TryGetValue(name, out var lexicalSymbol))
                return AddSymbolReference(lexicalSymbol, origin);

            if (_outerLexicalSymbols.TryGetValue(name, out var capturedSymbol))
            {
                if (_captureSet.Add(capturedSymbol))
                    _captures.Add(capturedSymbol);
                return AddSymbolReference(capturedSymbol, origin);
            }

            if (TryGetSpecialSymbolKind(name, out var specialKind))
            {
                if (!_specialSymbols.TryGetValue(name, out var specialSymbol))
                {
                    specialSymbol = _builder.AddSymbol(
                        specialKind,
                        name,
                        SourceTypeIndex.Invalid,
                        _unknownType,
                        Avm1SourceSymbolFlags.DeclarationProvided);
                    _specialSymbols.Add(name, specialSymbol);
                    _declaredSymbols.Add(specialSymbol);
                }
                return AddSymbolReference(specialSymbol, origin);
            }

            if (IsIdentifier(name))
            {
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.DynamicName,
                    name: _builder.InternString(name),
                    origin: origin);
            }

            return _builder.AddExpression(
                Avm1SourceExpressionKind.ComputedDynamicName,
                origin: origin,
                children: [AddStringLiteral(name, origin)]);
        }

        private SourceExpressionIndex ProjectMemberAssignment(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var target = AddMemberAccess(
                ProjectExpression(_ast.GetChild(node, 0)),
                ProjectExpression(_ast.GetChild(node, 1)),
                node.Merge.Value == 1,
                origin);
            return AddAssignment(
                target,
                ProjectExpression(_ast.GetChild(node, 2)),
                node.Kind is Avm1AstNodeKind.AssignMember
                    ? Avm1SourceOperator.Assign
                    : GetCompoundOperator((ActionOpcode)node.StartAction.Value),
                origin);
        }

        private SourceExpressionIndex ProjectStepExpression(
            Avm1AstNode node,
            SourceOriginIndex origin,
            bool prefix)
        {
            var increment = node.StartAction.Value == (int)ActionOpcode.Increment;
            var @operator = (prefix, increment) switch
            {
                (true, true) => Avm1SourceOperator.PrefixIncrement,
                (true, false) => Avm1SourceOperator.PrefixDecrement,
                (false, true) => Avm1SourceOperator.PostfixIncrement,
                _ => Avm1SourceOperator.PostfixDecrement
            };
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                @operator,
                origin: origin,
                children: [ProjectExpression(_ast.GetChild(node, 0))]);
        }

        private SourceExpressionIndex ProjectUnary(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var operand = ProjectExpression(_ast.GetChild(node, 0));
            if (node.StartAction.Value == 1)
            {
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Unary,
                    Avm1SourceOperator.LogicalNot,
                    origin: origin,
                    children: [operand]);
            }

            var opcode = (ActionOpcode)node.StartAction.Value;
            if (opcode is ActionOpcode.Increment or ActionOpcode.Decrement)
            {
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    opcode is ActionOpcode.Increment
                        ? Avm1SourceOperator.Add
                        : Avm1SourceOperator.Subtract,
                    origin: origin,
                    children: [operand, AddIntegerLiteral(1, origin)]);
            }

            return _builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                opcode is ActionOpcode.TypeOf
                    ? Avm1SourceOperator.TypeOf
                    : Avm1SourceOperator.BitwiseNot,
                origin: origin,
                children: [operand]);
        }

        private SourceExpressionIndex ProjectBinary(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                GetBinaryOperator((ActionOpcode)node.StartAction.Value, node.Merge.Value == 1),
                origin: origin,
                children:
                [
                    ProjectExpression(_ast.GetChild(node, 0)),
                    ProjectExpression(_ast.GetChild(node, 1))
                ]);
        }

        private SourceExpressionIndex ProjectIntrinsic(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var opcode = (ActionOpcode)node.StartAction.Value;
            if (opcode is ActionOpcode.GetURL)
                return ProjectImmediateGetUrl(node, origin);
            if (opcode is ActionOpcode.GetURL2)
                return ProjectStackGetUrl(node, origin);
            if (opcode is ActionOpcode.GotoFrame or ActionOpcode.GoToLabel)
                return ProjectImmediateTimelineGoto(node, origin);
            if (opcode is ActionOpcode.GotoFrame2)
                return ProjectStackTimelineGoto(node, origin);

            var name = opcode switch
            {
                ActionOpcode.Trace => "trace",
                ActionOpcode.GetTime => "getTimer",
                ActionOpcode.TargetPath => "targetPath",
                ActionOpcode.ToNumber => "Number",
                ActionOpcode.ToString => "String",
                ActionOpcode.ToInteger => "int",
                ActionOpcode.RandomNumber => "random",
                ActionOpcode.StringLength => "length",
                ActionOpcode.MBStringLength => "mblength",
                ActionOpcode.CharToAscii => "ord",
                ActionOpcode.AsciiToChar => "chr",
                ActionOpcode.MBCharToAscii => "mbord",
                ActionOpcode.MBAsciiToChar => "mbchr",
                ActionOpcode.StringExtract => "substring",
                ActionOpcode.MBStringExtract => "mbsubstring",
                ActionOpcode.GetProperty => "getProperty",
                ActionOpcode.SetProperty => "setProperty",
                ActionOpcode.CloneSprite => "duplicateMovieClip",
                ActionOpcode.RemoveSprite => "removeMovieClip",
                ActionOpcode.StartDrag => "startDrag",
                ActionOpcode.EndDrag => "stopDrag",
                ActionOpcode.NextFrame => "nextFrame",
                ActionOpcode.PreviousFrame => "prevFrame",
                ActionOpcode.Play => "play",
                ActionOpcode.Stop => "stop",
                ActionOpcode.StopSounds => "stopAllSounds",
                ActionOpcode.ToggleQuality => "toggleHighQuality",
                ActionOpcode.Call => "call",
                _ => "undefined"
            };
            var children = ProjectExpressionChildren(node, 0).ToList();
            return _builder.AddExpression(
                Avm1SourceExpressionKind.IntrinsicCall,
                name: _builder.InternString(name),
                origin: origin,
                children: children);
        }

        private SourceExpressionIndex ProjectStackTimelineGoto(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var instruction = GetOriginInstruction(node);
            if (instruction?.Action is not ActionGotoFrame2 action)
                return AddUnsupportedExpression(node, origin);

            var children = new List<SourceExpressionIndex>(2);
            if (action.HasSceneBias)
            {
                if (_method.TimelineLayout is null ||
                    !_method.TimelineLayout.TryGetSceneByFrameOffset(
                        action.SceneBias,
                        out var scene))
                {
                    return AddUnsupportedExpression(
                        node,
                        origin,
                        $"ActionGotoFrame2 scene bias {action.SceneBias} has no matching timeline scene metadata.");
                }
                children.Add(AddStringLiteral(scene.Name, origin));
            }
            children.Add(ProjectExpression(_ast.GetChild(node, 0)));

            return _builder.AddExpression(
                Avm1SourceExpressionKind.IntrinsicCall,
                name: _builder.InternString(
                    action.Play ? "gotoAndPlay" : "gotoAndStop"),
                origin: origin,
                children: children);
        }

        private SourceExpressionIndex ProjectImmediateTimelineGoto(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var instruction = GetOriginInstruction(node);
            SourceExpressionIndex target;
            switch (instruction?.Action)
            {
                case Swf1.ActionGotoFrame gotoFrame:
                    target = AddIntegerLiteral(gotoFrame.Frame + 1, origin);
                    break;
                case Swf3.ActionGoToLabel gotoLabel:
                    target = AddStringLiteral(gotoLabel.Label, origin);
                    break;
                default:
                    return AddUndefinedLiteral(origin);
            }

            return _builder.AddExpression(
                Avm1SourceExpressionKind.IntrinsicCall,
                name: _builder.InternString(
                    node.IntOperand == 1 ? "gotoAndPlay" : "gotoAndStop"),
                origin: origin,
                children: [target]);
        }

        private SourceExpressionIndex ProjectStackGetUrl(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var flags = (GetUrlFlags)(byte)node.IntOperand;
            var loadTarget = flags.HasFlag(GetUrlFlags.LoadTarget);
            var loadVariables = flags.HasFlag(GetUrlFlags.LoadVariables);
            var url = ProjectExpression(_ast.GetChild(node, 0));
            var targetIndex = _ast.GetChild(node, 1);
            var arguments = new List<SourceExpressionIndex>(3) { url };
            string name;

            var level = SourceExpressionIndex.Invalid;
            var hasLevel = !loadTarget &&
                TryProjectLevelArgument(targetIndex, origin, out level);
            if (!loadTarget && loadVariables && !hasLevel)
                return AddUnsupportedExpression(node, origin);

            if (hasLevel)
            {
                name = loadVariables ? "loadVariablesNum" : "loadMovieNum";
                arguments.Add(level);
            }
            else
            {
                name = (loadTarget, loadVariables) switch
                {
                    (true, true) => "loadVariables",
                    (true, false) => "loadMovie",
                    (false, true) => throw new InvalidOperationException(),
                    _ => "getURL"
                };
                arguments.Add(ProjectExpression(targetIndex));
            }

            var method = flags & GetUrlFlags.MethodMask;
            if (method is GetUrlFlags.MethodGet or GetUrlFlags.MethodPost)
            {
                arguments.Add(AddStringLiteral(
                    method is GetUrlFlags.MethodGet ? "GET" : "POST",
                    origin));
            }

            return _builder.AddExpression(
                Avm1SourceExpressionKind.IntrinsicCall,
                name: _builder.InternString(name),
                origin: origin,
                children: arguments);
        }

        private SourceExpressionIndex ProjectImmediateGetUrl(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            if (!node.OriginAction.IsValid ||
                node.OriginAction.Value >= _method.Instructions.Count ||
                _method.Instructions[node.OriginAction].Action is not ActionGetURL action)
            {
                return AddUnsupportedExpression(node, origin);
            }

            const string fsCommandPrefix = "FSCommand:";
            var isFsCommand = action.Url.StartsWith(
                fsCommandPrefix,
                StringComparison.Ordinal);
            var level = 0;
            var isLevelLoad = !isFsCommand &&
                TryParseLevelTarget(action.Target, out level);
            var arguments = new List<SourceExpressionIndex>(2)
            {
                AddStringLiteral(
                    isFsCommand ? action.Url[fsCommandPrefix.Length..] : action.Url,
                    origin)
            };
            if (isLevelLoad)
                arguments.Add(AddIntegerLiteral(level, origin));
            else if (action.Target.Length != 0)
                arguments.Add(AddStringLiteral(action.Target, origin));

            return _builder.AddExpression(
                Avm1SourceExpressionKind.IntrinsicCall,
                name: _builder.InternString(isFsCommand
                    ? "fscommand"
                    : isLevelLoad ? "loadMovieNum" : "getURL"),
                origin: origin,
                children: arguments);
        }

        private bool TryProjectLevelArgument(
            AstIndex targetIndex,
            SourceOriginIndex origin,
            out SourceExpressionIndex level)
        {
            if (TryGetStringLiteral(targetIndex, out var target) &&
                TryParseLevelTarget(target, out var literalLevel))
            {
                level = AddIntegerLiteral(literalLevel, origin);
                return true;
            }

            if (targetIndex.IsValid)
            {
                var targetNode = _ast[targetIndex];
                if (targetNode.Kind is Avm1AstNodeKind.Binary &&
                    (ActionOpcode)targetNode.StartAction.Value is
                        ActionOpcode.StringAdd &&
                    Avm1AstArena.GetChildCount(targetNode) == 2 &&
                    TryGetStringLiteral(
                        _ast.GetChild(targetNode, 0),
                        out var prefix) &&
                    prefix == "_level")
                {
                    level = ProjectExpression(_ast.GetChild(targetNode, 1));
                    return true;
                }
            }

            level = SourceExpressionIndex.Invalid;
            return false;
        }

        private static bool TryParseLevelTarget(string target, out int level)
        {
            level = 0;
            const string prefix = "_level";
            return target.StartsWith(prefix, StringComparison.Ordinal) &&
                target.Length > prefix.Length &&
                int.TryParse(
                    target.AsSpan(prefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out level);
        }

        private SourceExpressionIndex ProjectFunctionCall(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var calleeIndex = _ast.GetChild(node, 0);
            var callee = TryGetStringLiteral(calleeIndex, out var name) && IsIdentifier(name)
                ? ResolveNameReference(name, AddOrigin(calleeIndex))
                : ProjectExpression(calleeIndex);
            var children = ProjectExpressionChildren(node, 1, callee);
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                origin: origin,
                children: children);
        }

        private SourceExpressionIndex ProjectNewObject(
            Avm1AstNode node,
            SourceOriginIndex origin)
        {
            var constructorIndex = _ast.GetChild(node, 0);
            var constructor = TryGetStringLiteral(constructorIndex, out var name) && IsQualifiedIdentifier(name)
                ? _builder.AddExpression(
                    Avm1SourceExpressionKind.QualifiedName,
                    name: _builder.InternString(name),
                    origin: AddOrigin(constructorIndex))
                : ProjectExpression(constructorIndex);
            return _builder.AddExpression(
                Avm1SourceExpressionKind.New,
                origin: origin,
                children: ProjectExpressionChildren(node, 1, constructor));
        }

        private SourceExpressionIndex ProjectMethodCall(
            Avm1AstNode node,
            SourceOriginIndex origin,
            bool isNew)
        {
            var target = ProjectExpression(_ast.GetChild(node, 0));
            var nameIndex = _ast.GetChild(node, 1);
            var argumentCount = node.Children.Count - 2;
            if (!isNew && TryGetStringLiteral(nameIndex, out var accessorName))
            {
                if (argumentCount == 0 &&
                    TryGetAccessorProperty(accessorName, "__get__", out var getterProperty))
                {
                    return AddMemberAccess(
                        target,
                        AddStringLiteral(getterProperty, origin),
                        computed: false,
                        origin);
                }

                if (argumentCount == 1 &&
                    TryGetAccessorProperty(accessorName, "__set__", out var setterProperty))
                {
                    var property = AddMemberAccess(
                        target,
                        AddStringLiteral(setterProperty, origin),
                        computed: false,
                        origin);
                    return AddAssignment(
                        property,
                        ProjectExpression(_ast.GetChild(node, 2)),
                        Avm1SourceOperator.Assign,
                        origin);
                }
            }

            SourceExpressionIndex callee;
            if (TryGetStringLiteral(nameIndex, out var methodName))
            {
                callee = methodName.Length > 0
                    ? AddMemberAccess(
                        target,
                        AddStringLiteral(methodName, AddOrigin(nameIndex)),
                        !IsIdentifier(methodName),
                        origin)
                    : target;
            }
            else
            {
                var projectedName = ProjectExpression(nameIndex);
                callee = IsUndefinedLiteral(projectedName)
                    ? target
                    : AddMemberAccess(
                        target,
                        projectedName,
                        computed: true,
                        origin);
            }

            return _builder.AddExpression(
                isNew ? Avm1SourceExpressionKind.New : Avm1SourceExpressionKind.Call,
                origin: origin,
                children: ProjectExpressionChildren(node, 2, callee));
        }

        private SourceExpressionIndex[] ProjectExpressionChildren(
            Avm1AstNode node,
            int firstChild,
            SourceExpressionIndex? prefix = null)
        {
            var prefixCount = prefix.HasValue ? 1 : 0;
            var children = new SourceExpressionIndex[node.Children.Count - firstChild + prefixCount];
            var target = 0;
            if (prefix is { } prefixValue)
                children[target++] = prefixValue;
            for (var i = firstChild; i < node.Children.Count; i++)
                children[target++] = ProjectExpression(_ast.GetChild(node, i));
            return children;
        }

        private SourceExpressionIndex AddMemberAccess(
            SourceExpressionIndex target,
            SourceExpressionIndex member,
            bool computed,
            SourceOriginIndex origin)
        {
            return _builder.AddExpression(
                Avm1SourceExpressionKind.MemberAccess,
                flags: computed
                    ? Avm1SourceExpressionFlags.ComputedMember
                    : Avm1SourceExpressionFlags.None,
                origin: origin,
                children: [target, member]);
        }

        private SourceExpressionIndex AddAssignment(
            SourceExpressionIndex target,
            SourceExpressionIndex value,
            Avm1SourceOperator @operator,
            SourceOriginIndex origin)
        {
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                @operator,
                origin: origin,
                children: [target, value]);
        }

        private SourceExpressionIndex ProjectRegisterAssignmentExpression(
            Avm1AstNode node,
            SourceOriginIndex origin,
            Avm1SourceOperator @operator)
        {
            var symbol = GetRegisterSymbol(node.Block.Value, node.Merge.Value);
            _declaredSymbols.Add(symbol);
            if (_emittedDeclarations.Add(symbol))
            {
                _pendingExpressionDeclarations.Add(_builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    symbol: symbol,
                    origin: origin));
            }

            return AddAssignment(
                AddSymbolReference(symbol, origin),
                ProjectExpression(_ast.GetChild(node, 0)),
                @operator,
                origin);
        }

        private bool TryExtractCanonicalFunctionSelfBinding(
            ProjectedCodeUnit codeUnit,
            out SourceSymbolIndex nameSymbol,
            out SourceStatementIndex body)
        {
            nameSymbol = SourceSymbolIndex.Invalid;
            body = codeUnit.Body;
            var root = _builder.GetStatement(codeUnit.Body);
            while (root.Kind is Avm1SourceStatementKind.Block &&
                root.Children.Count == 1)
            {
                var child = _builder.GetStatement(
                    _builder.GetStatementChild(root, 0));
                if (child.Kind is not Avm1SourceStatementKind.Block)
                    break;
                root = child;
            }
            if (root.Kind is not Avm1SourceStatementKind.Block ||
                root.Children.Count == 0)
            {
                return false;
            }

            var declaration = _builder.GetStatement(
                _builder.GetStatementChild(root, 0));
            if (declaration.Kind is not
                    Avm1SourceStatementKind.VariableDeclaration ||
                !declaration.Symbol.IsValid ||
                !declaration.Expression.IsValid)
            {
                return false;
            }

            var selfSymbol = _builder.GetSymbol(declaration.Symbol);
            var initializer = _builder.GetExpression(declaration.Expression);
            if (selfSymbol.Kind is not Avm1SourceSymbolKind.Local ||
                initializer.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                initializer.Children.Count != 2)
            {
                return false;
            }

            var receiver = _builder.GetExpression(
                _builder.GetExpressionChild(initializer, 0));
            var key = _builder.GetExpression(
                _builder.GetExpressionChild(initializer, 1));
            if (!IsArgumentsExpression(receiver) ||
                !IsProjectedStringLiteral(key, "callee"))
            {
                return false;
            }

            var children = new SourceStatementIndex[root.Children.Count - 1];
            for (var i = 0; i < children.Length; i++)
                children[i] = _builder.GetStatementChild(root, i + 1);

            _builder.PromoteToFunctionSymbol(declaration.Symbol);
            nameSymbol = declaration.Symbol;
            body = _builder.AddStatement(
                Avm1SourceStatementKind.Block,
                origin: root.Origin,
                children: children);
            return true;
        }

        private bool IsArgumentsExpression(Avm1SourceExpression expression)
        {
            if (expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                expression.Symbol.IsValid)
            {
                return IsArgumentsSymbol(expression.Symbol);
            }

            return (expression.Kind is
                Avm1SourceExpressionKind.DynamicName or
                Avm1SourceExpressionKind.QualifiedName) &&
                expression.Name.IsValid &&
                _builder.GetString(expression.Name).Equals(
                    "arguments",
                    StringComparison.Ordinal);
        }

        private bool IsArgumentsSymbol(SourceSymbolIndex index)
        {
            var symbol = _builder.GetSymbol(index);
            return symbol.Kind is Avm1SourceSymbolKind.Arguments ||
                symbol.Name.IsValid &&
                _builder.GetString(symbol.Name).Equals(
                    "arguments",
                    StringComparison.Ordinal);
        }

        private bool IsProjectedStringLiteral(
            Avm1SourceExpression expression,
            string expected)
        {
            if (expression.Kind is not Avm1SourceExpressionKind.Literal ||
                !expression.Literal.IsValid)
            {
                return false;
            }

            var literal = _builder.GetLiteral(expression.Literal);
            return literal.Kind is Avm1SourceLiteralKind.String &&
                literal.StringValue.IsValid &&
                _builder.GetString(literal.StringValue).Equals(
                    expected,
                    StringComparison.Ordinal);
        }

        private SourceExpressionIndex AddSymbolReference(
            SourceSymbolIndex symbol,
            SourceOriginIndex origin)
        {
            return _builder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: symbol,
                origin: origin);
        }

        private SourceExpressionIndex AddUndefinedLiteral(SourceOriginIndex origin)
        {
            var literal = _builder.AddLiteral(Avm1SourceLiteralKind.Undefined);
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: literal,
                origin: origin);
        }

        private SourceExpressionIndex AddIntegerLiteral(int value, SourceOriginIndex origin)
        {
            var literal = _builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: value);
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: literal,
                origin: origin);
        }

        private SourceExpressionIndex AddStringLiteral(string value, SourceOriginIndex origin)
        {
            var literal = _builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: _builder.InternString(value));
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: literal,
                origin: origin);
        }

        private SourceExpressionIndex AddUnsupportedExpression(
            Avm1AstNode node,
            SourceOriginIndex origin) =>
            AddUnsupportedExpression(
                node,
                origin,
                $"Recovery AST expression {node.Kind} is not projected yet.");

        private SourceExpressionIndex AddUnsupportedExpression(
            Avm1AstNode node,
            SourceOriginIndex origin,
            string description)
        {
            _diagnostics.Add(new Avm1SourceDiagnostic(
                UnsupportedExpressionCode,
                Avm1SourceDiagnosticSeverity.Error,
                origin,
                description));
            var opaque = _builder.AddOpaque(
                Avm1SourceOpaqueKind.RecoveryExpression,
                description,
                origin,
                GetOpaqueBytecode(node));
            return _builder.AddExpression(
                Avm1SourceExpressionKind.Opaque,
                name: _builder.InternString(node.Kind.ToString()),
                opaque: opaque,
                origin: origin);
        }

        private SourceSymbolIndex GetRegisterSymbol(int register, int version)
        {
            if (_registerSymbolOverrides.TryGetValue(register, out var overrideSymbol))
                return overrideSymbol;

            if (_method.SymbolTable.TryGetRegisterSymbol(register, version, out var recoverySymbol))
                return GetRecoverySymbol(recoverySymbol);

            if (version == 0 &&
                _parametersByRegister.TryGetValue(register, out var parameter))
            {
                return parameter;
            }

            var key = (register, version);
            if (_fallbackRegisters.TryGetValue(key, out var existing))
                return existing;

            var created = _builder.AddSymbol(
                Avm1SourceSymbolKind.Local,
                $"_loc{register.ToString(System.Globalization.CultureInfo.InvariantCulture)}_v{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                SourceTypeIndex.Invalid,
                _unknownType,
                Avm1SourceSymbolFlags.CompilerGenerated);
            _fallbackRegisters.Add(key, created);
            return created;
        }

        private SourceSymbolIndex GetTemporarySymbol(int value)
        {
            if (_temporarySymbols.TryGetValue(value, out var existing))
                return existing;

            var valueIndex = new ValueIndex(value);
            if (_method.SymbolTable.TryGetTempSymbol(valueIndex, out var recoverySymbol))
            {
                var projected = GetRecoverySymbol(recoverySymbol);
                _temporarySymbols.Add(value, projected);
                return projected;
            }

            var created = _builder.AddSymbol(
                Avm1SourceSymbolKind.Temporary,
                "v" + value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceTypeIndex.Invalid,
                MapType(_method.ValueAnalysis[valueIndex].Type),
                Avm1SourceSymbolFlags.CompilerGenerated);
            _temporarySymbols.Add(value, created);
            return created;
        }

        private SourceSymbolIndex GetRecoverySymbol(Avm1Symbol symbol)
        {
            if (_symbolsByRecoveryId.TryGetValue(symbol.Id, out var existing))
                return existing;

            if (symbol.Register >= 0 &&
                symbol.IsDeclared &&
                _parametersByRegister.TryGetValue(symbol.Register, out var parameter))
            {
                _symbolsByRecoveryId.Add(symbol.Id, parameter);
                return parameter;
            }

            var kind = GetSymbolKind(symbol);
            var flags = Avm1SourceSymbolFlags.None;
            var preservesSourceIdentity =
                kind is Avm1SourceSymbolKind.Local &&
                symbol.Register >= 0 &&
                _projectionHints?.PreservesSourceIdentity(symbol.Register) is true;
            if (!preservesSourceIdentity &&
                (symbol.Register < 0 ||
                Avm1SymbolTable.IsGeneratedRegisterSymbol(symbol) ||
                IsCompilerTemporaryName(symbol.Name)))
            {
                flags |= Avm1SourceSymbolFlags.CompilerGenerated;
            }
            if (symbol.IsDeclared || kind is not (Avm1SourceSymbolKind.Local or Avm1SourceSymbolKind.Temporary))
                flags |= Avm1SourceSymbolFlags.DeclarationProvided;

            var created = _builder.AddSymbol(
                kind,
                preservesSourceIdentity
                    ? "__reg" + symbol.Register.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    : symbol.Name,
                SourceTypeIndex.Invalid,
                MapType(symbol.Type),
                flags);
            _symbolsByRecoveryId.Add(symbol.Id, created);
            if (symbol.IsDeclared || flags.HasFlag(Avm1SourceSymbolFlags.DeclarationProvided))
                _declaredSymbols.Add(created);
            if (kind is Avm1SourceSymbolKind.Local or Avm1SourceSymbolKind.Temporary)
                _lexicalSymbols.TryAdd(symbol.Name, created);
            else
                _specialSymbols.TryAdd(symbol.Name, created);
            return created;
        }

        private static bool IsCompilerTemporaryName(string name)
        {
            if (IsCompletionTemporaryName(name))
                return true;

            const string prefix = "__avm1_spill_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var suffix = name.AsSpan(prefix.Length);
            while (!suffix.IsEmpty)
            {
                var separator = suffix.IndexOf('_');
                var segment = separator < 0 ? suffix : suffix[..separator];
                if (!IsCompilerTemporarySegment(segment))
                    return false;
                if (separator < 0)
                    return true;
                suffix = suffix[(separator + 1)..];
            }
            return false;
        }

        private static bool IsCompletionTemporaryName(string name)
        {
            const string prefix = "__avm1_completion";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var suffix = name.AsSpan(prefix.Length);
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

        private static bool IsCompilerTemporarySegment(ReadOnlySpan<char> segment)
        {
            if (segment.StartsWith("try", StringComparison.Ordinal))
                segment = segment[3..];
            else if (segment.StartsWith("with", StringComparison.Ordinal))
                segment = segment[4..];

            if (segment.IsEmpty)
                return false;
            foreach (var character in segment)
            {
                if (character is < '0' or > '9')
                    return false;
            }
            return true;
        }

        private SourceTypeIndex MapType(Avm1InferredType type)
        {
            var sourceType = type switch
            {
                Avm1InferredType.Undefined => Avm1SourceTypeKind.Undefined,
                Avm1InferredType.Null => Avm1SourceTypeKind.Null,
                Avm1InferredType.Boolean => Avm1SourceTypeKind.Boolean,
                Avm1InferredType.Integer or Avm1InferredType.Number => Avm1SourceTypeKind.Number,
                Avm1InferredType.String => Avm1SourceTypeKind.String,
                Avm1InferredType.Object => Avm1SourceTypeKind.Object,
                Avm1InferredType.Array => Avm1SourceTypeKind.Array,
                _ => Avm1SourceTypeKind.Unknown
            };
            return _builder.GetBuiltInType(sourceType);
        }

        private SourceOriginIndex AddOrigin(AstIndex index)
        {
            var node = _ast[index];
            var instruction = GetOriginInstruction(node);
            return instruction is null
                ? _builder.AddOrigin(_recoveryUnit, index.Value)
                : _builder.AddOrigin(
                    _recoveryUnit,
                    index.Value,
                    instruction.Value.Offset,
                    instruction.Value.EndOffset);
        }

        private Avm1Instruction? GetOriginInstruction(Avm1AstNode node)
        {
            var action = node.OriginAction.IsValid
                ? node.OriginAction
                : node.Kind is Avm1AstNodeKind.Opaque
                    ? node.StartAction
                    : ActionIndex.Invalid;
            return action.IsValid && action.Value < _method.Instructions.Count
                ? _method.Instructions[action]
                : null;
        }

        private byte[] GetOpaqueBytecode(Avm1AstNode node)
        {
            var first = node.Kind is Avm1AstNodeKind.Opaque && node.StartAction.IsValid
                ? node.StartAction.Value
                : node.OriginAction.Value;
            var end = node.Kind is Avm1AstNodeKind.Opaque && node.EndAction.IsValid
                ? node.EndAction.Value
                : first + 1;
            if (first < 0 || first >= _method.Instructions.Count)
                return [];

            end = Math.Clamp(end, first + 1, _method.Instructions.Count);
            var actions = new Action[end - first];
            for (var i = first; i < end; i++)
                actions[i - first] = _method.Instructions[new ActionIndex(i)].Action;
            return Action.EncodeCollection(actions, _method.Instructions.SwfVersion).ToArray();
        }

        private HashSet<int>? CollectCodeUnitDiagnosticActions()
        {
            if (_includedStatementNodes is null)
                return null;

            var result = new HashSet<int>();
            foreach (var indexValue in _includedStatementNodes)
            {
                if (indexValue < 0 || indexValue >= _ast.Nodes.Count)
                    continue;

                var index = new AstIndex(indexValue);
                if (!_ast[index].OriginAction.IsValid)
                    continue;

                CollectDiagnosticActions(index, result);
            }
            return result;
        }

        private HashSet<int> CollectDiagnosticActions(AstIndex root)
        {
            var result = new HashSet<int>();
            if (root == _ast.Root)
            {
                foreach (var index in _reachableNodes)
                    CollectDiagnosticActions(_ast[index], result);
                return result;
            }

            CollectDiagnosticActions(root, result);
            return result;
        }

        private void CollectDiagnosticActions(AstIndex root, HashSet<int> result)
        {
            var pending = new Stack<AstIndex>();
            var visited = new bool[_ast.Nodes.Count];
            pending.Push(root);
            while (pending.Count > 0)
            {
                var index = pending.Pop();
                if (!index.IsValid ||
                    (uint)index.Value >= (uint)visited.Length ||
                    visited[index.Value])
                {
                    continue;
                }

                visited[index.Value] = true;
                var node = _ast[index];
                CollectDiagnosticActions(node, result);

                for (var i = 0; i < node.Children.Count; i++)
                    pending.Push(_ast.GetChild(node, i));
            }
        }

        private void CollectDiagnosticActions(
            Avm1AstNode node,
            HashSet<int> result)
        {
            if (node.OriginAction.IsValid)
                result.Add(node.OriginAction.Value);
            if (node.Kind is not Avm1AstNodeKind.Opaque || !node.StartAction.IsValid)
                return;

            var end = node.EndAction.IsValid
                ? Math.Min(node.EndAction.Value, _method.Instructions.Count)
                : node.StartAction.Value + 1;
            for (var action = node.StartAction.Value; action < end; action++)
                result.Add(action);
        }

        private void ImportRecoveryDiagnostics(
            IReadOnlySet<int>? relevantActions,
            IReadOnlySet<int> representedActions)
        {
            var seen = new HashSet<(ActionIndex Action, string Message)>();
            ImportRecoveryDiagnostics(
                _method.StackIr.Diagnostics,
                relevantActions,
                representedActions,
                seen);
            ImportRecoveryDiagnostics(
                _method.StackDepthAnalysis.Diagnostics,
                relevantActions,
                representedActions,
                seen);
        }

        private bool HasRecoveryDiagnostics =>
            _method.StackIr.Diagnostics.Count != 0 ||
            _method.StackDepthAnalysis.Diagnostics.Count != 0;

        private void ImportRecoveryDiagnostics(
            IEnumerable<Avm1Diagnostic> diagnostics,
            IReadOnlySet<int>? relevantActions,
            IReadOnlySet<int> representedActions,
            HashSet<(ActionIndex Action, string Message)> seen)
        {
            foreach (var diagnostic in diagnostics)
            {
                if (relevantActions is not null &&
                    (!diagnostic.Action.IsValid ||
                        !relevantActions.Contains(diagnostic.Action.Value)))
                {
                    continue;
                }
                if (!seen.Add((diagnostic.Action, diagnostic.Message)))
                    continue;

                var origin = AddActionOrigin(diagnostic.Action);
                _diagnostics.Add(new Avm1SourceDiagnostic(
                    RecoveryDiagnosticCode,
                    GetSourceSeverity(diagnostic, representedActions),
                    origin,
                    diagnostic.Message));
            }
        }

        private static Avm1SourceDiagnosticSeverity GetSourceSeverity(
            Avm1Diagnostic diagnostic,
            IReadOnlySet<int> representedActions) => diagnostic.Severity switch
            {
                Avm1DiagnosticSeverity.Error => Avm1SourceDiagnosticSeverity.Error,
                Avm1DiagnosticSeverity.Info => Avm1SourceDiagnosticSeverity.Info,
                Avm1DiagnosticSeverity.Warning when diagnostic.Message.StartsWith(
                    "Stack underflow",
                    StringComparison.Ordinal) &&
                    diagnostic.Action.IsValid &&
                    representedActions.Contains(diagnostic.Action.Value) =>
                    Avm1SourceDiagnosticSeverity.Error,
                _ => Avm1SourceDiagnosticSeverity.Warning
            };

        private SourceOriginIndex AddActionOrigin(ActionIndex action)
        {
            if (!action.IsValid || action.Value >= _method.Instructions.Count)
                return _builder.AddOrigin(_recoveryUnit, recoveryNode: -1);

            var instruction = _method.Instructions[action];
            return _builder.AddOrigin(
                _recoveryUnit,
                recoveryNode: -1,
                instruction.Offset,
                instruction.EndOffset);
        }

        private bool IsUndefinedLiteral(SourceExpressionIndex index)
        {
            if (!index.IsValid)
                return true;
            var expression = GetProjectedExpression(index);
            return expression.Kind is Avm1SourceExpressionKind.Literal &&
                expression.Literal.IsValid &&
                GetProjectedLiteral(expression.Literal).Kind is Avm1SourceLiteralKind.Undefined;
        }

        private Avm1SourceExpression GetProjectedExpression(SourceExpressionIndex index)
        {
            return _builder.GetExpression(index);
        }

        private Avm1SourceLiteral GetProjectedLiteral(SourceLiteralIndex index)
        {
            return _builder.GetLiteral(index);
        }

        private bool TryGetStringLiteral(AstIndex index, out string value)
        {
            value = string.Empty;
            if (!index.IsValid)
                return false;

            var node = _ast[index];
            if (node.Kind is not Avm1AstNodeKind.Literal || node.StartAction.Value < 0)
                return false;

            var fact = _method.ValueAnalysis[new ValueIndex(node.StartAction.Value)];
            if (fact.ConstantKind is not Avm1ConstantKind.String || fact.StringValue is null)
                return false;

            value = fact.StringValue;
            return true;
        }

        private static Avm1SourceSymbolKind GetSymbolKind(Avm1Symbol symbol)
        {
            if (TryGetSpecialSymbolKind(symbol.Name, out var specialKind))
                return specialKind;
            return symbol.Register < 0
                ? Avm1SourceSymbolKind.Temporary
                : Avm1SourceSymbolKind.Local;
        }

        private static bool TryGetSpecialSymbolKind(
            string name,
            out Avm1SourceSymbolKind kind)
        {
            kind = name switch
            {
                "this" => Avm1SourceSymbolKind.This,
                "super" => Avm1SourceSymbolKind.Super,
                "arguments" => Avm1SourceSymbolKind.Arguments,
                "_root" => Avm1SourceSymbolKind.Root,
                "_parent" => Avm1SourceSymbolKind.Parent,
                "_global" => Avm1SourceSymbolKind.Global,
                _ => Avm1SourceSymbolKind.Local
            };
            return kind is not Avm1SourceSymbolKind.Local;
        }

        private static bool IsControlScope(Avm1AstNodeKind kind) => kind is
            Avm1AstNodeKind.While or
            Avm1AstNodeKind.DoWhile or
            Avm1AstNodeKind.For or
            Avm1AstNodeKind.ForIn or
            Avm1AstNodeKind.Switch;

        private static Avm1SourceOperator GetBinaryOperator(ActionOpcode opcode, bool inverted = false)
        {
            if (inverted)
            {
                return opcode switch
                {
                    ActionOpcode.Equals or ActionOpcode.Equals2 or ActionOpcode.StringEquals => Avm1SourceOperator.NotEqual,
                    ActionOpcode.StrictEquals => Avm1SourceOperator.StrictNotEqual,
                    ActionOpcode.Less or ActionOpcode.Less2 or ActionOpcode.StringLess => Avm1SourceOperator.GreaterOrEqual,
                    ActionOpcode.Greater or ActionOpcode.StringGreater => Avm1SourceOperator.LessOrEqual,
                    _ => throw new InvalidOperationException($"Cannot invert AVM1 binary opcode {opcode}.")
                };
            }

            return opcode switch
            {
                ActionOpcode.Add or ActionOpcode.Add2 or ActionOpcode.StringAdd => Avm1SourceOperator.Add,
                ActionOpcode.Subtract => Avm1SourceOperator.Subtract,
                ActionOpcode.Multiply => Avm1SourceOperator.Multiply,
                ActionOpcode.Divide => Avm1SourceOperator.Divide,
                ActionOpcode.Modulo => Avm1SourceOperator.Modulo,
                ActionOpcode.Equals or ActionOpcode.Equals2 or ActionOpcode.StringEquals => Avm1SourceOperator.Equal,
                ActionOpcode.StrictEquals => Avm1SourceOperator.StrictEqual,
                ActionOpcode.Less or ActionOpcode.Less2 or ActionOpcode.StringLess => Avm1SourceOperator.Less,
                ActionOpcode.Greater or ActionOpcode.StringGreater => Avm1SourceOperator.Greater,
                ActionOpcode.And => Avm1SourceOperator.LogicalAnd,
                ActionOpcode.Or => Avm1SourceOperator.LogicalOr,
                ActionOpcode.BitAnd => Avm1SourceOperator.BitAnd,
                ActionOpcode.BitOr => Avm1SourceOperator.BitOr,
                ActionOpcode.BitXor => Avm1SourceOperator.BitXor,
                ActionOpcode.BitLShift => Avm1SourceOperator.ShiftLeft,
                ActionOpcode.BitRShift => Avm1SourceOperator.ShiftRight,
                ActionOpcode.BitURShift => Avm1SourceOperator.ShiftRightUnsigned,
                ActionOpcode.InstanceOf => Avm1SourceOperator.InstanceOf,
                _ => throw new InvalidOperationException($"AVM1 binary opcode {opcode} has no Source HIR operator.")
            };
        }

        private static Avm1SourceOperator GetCompoundOperator(ActionOpcode opcode)
        {
            return GetBinaryOperator(opcode) switch
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
                Avm1SourceOperator.ShiftRightUnsigned => Avm1SourceOperator.ShiftRightUnsignedAssign,
                var @operator => throw new InvalidOperationException(
                    $"Source HIR operator {@operator} has no compound assignment form.")
            };
        }

        private static Avm1SourceOperator GetBinaryOperator(Avm1SourceOperator compoundOperator)
        {
            return compoundOperator switch
            {
                Avm1SourceOperator.AddAssign => Avm1SourceOperator.Add,
                Avm1SourceOperator.SubtractAssign => Avm1SourceOperator.Subtract,
                Avm1SourceOperator.MultiplyAssign => Avm1SourceOperator.Multiply,
                Avm1SourceOperator.DivideAssign => Avm1SourceOperator.Divide,
                Avm1SourceOperator.ModuloAssign => Avm1SourceOperator.Modulo,
                Avm1SourceOperator.BitAndAssign => Avm1SourceOperator.BitAnd,
                Avm1SourceOperator.BitOrAssign => Avm1SourceOperator.BitOr,
                Avm1SourceOperator.BitXorAssign => Avm1SourceOperator.BitXor,
                Avm1SourceOperator.ShiftLeftAssign => Avm1SourceOperator.ShiftLeft,
                Avm1SourceOperator.ShiftRightAssign => Avm1SourceOperator.ShiftRight,
                Avm1SourceOperator.ShiftRightUnsignedAssign => Avm1SourceOperator.ShiftRightUnsigned,
                _ => throw new InvalidOperationException(
                    $"Source HIR operator {compoundOperator} is not a compound assignment.")
            };
        }

        private static bool TryGetAccessorProperty(
            string methodName,
            string prefix,
            out string propertyName)
        {
            propertyName = string.Empty;
            if (!methodName.StartsWith(prefix, StringComparison.Ordinal) || methodName.Length == prefix.Length)
                return false;

            propertyName = methodName[prefix.Length..];
            return IsIdentifier(propertyName);
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0 ||
                value[0] != '_' && value[0] != '$' && !char.IsAsciiLetter(value[0]))
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] != '_' && value[i] != '$' && !char.IsAsciiLetterOrDigit(value[i]))
                    return false;
            }
            return true;
        }

        private static bool IsQualifiedIdentifier(string value)
        {
            if (value.Length == 0)
                return false;
            var parts = value.Split('.');
            return parts.All(IsIdentifier);
        }

        private readonly record struct ProjectedCodeUnit(
            SourceStatementIndex Body,
            SourceSymbolIndex[] Parameters,
            SourceSymbolIndex[] Captures);
    }
}
