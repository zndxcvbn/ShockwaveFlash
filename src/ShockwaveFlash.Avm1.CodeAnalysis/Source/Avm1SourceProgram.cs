using System.Diagnostics.CodeAnalysis;

namespace ShockwaveFlash.Avm1.Source;

public enum Avm1SourceProgramSymbolKind : byte
{
    Class,
    Interface,
    Field,
    Constructor,
    Method,
    Getter,
    Setter
}

[Flags]
public enum Avm1SourceProgramSymbolFlags : byte
{
    None = 0,
    External = 1 << 0
}

public readonly record struct Avm1SourceProgramSymbol(
    SourceProgramSymbolIndex Index,
    Avm1SourceProgramSymbolKind Kind,
    string Name,
    Avm1SourceQualifiedName QualifiedName,
    SourceProgramSymbolIndex ContainingSymbol,
    SourceProgramTypeIndex DeclaredType,
    SourceProgramTypeIndex InferredType,
    Avm1SourceDeclarationModifiers Modifiers,
    Avm1SourceProgramSymbolFlags Flags,
    SourceProgramSignatureIndex Signature);

public readonly record struct Avm1SourceProgramType(
    SourceProgramTypeIndex Index,
    Avm1SourceTypeKind Kind,
    Avm1SourceQualifiedName? Name,
    SourceProgramSymbolIndex Symbol);

[Flags]
public enum Avm1SourceProgramParameterFlags : byte
{
    None = 0,
    Optional = 1 << 0,
    Rest = 1 << 1
}

public readonly record struct Avm1SourceProgramParameter(
    string Name,
    SourceProgramTypeIndex Type,
    Avm1SourceProgramParameterFlags Flags);

public readonly record struct Avm1SourceProgramSignature(
    SourceProgramSignatureIndex Index,
    SourceProgramParameterList Parameters);

public enum Avm1SourceBindingKind : byte
{
    None,
    Bound,
    Dynamic,
    Ambiguous
}

[Flags]
public enum Avm1SourceExpressionUse : byte
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Invoke = 1 << 2,
    Construct = 1 << 3,
    Delete = 1 << 4,
    ReadWrite = Read | Write
}

public readonly record struct Avm1SourceExpressionBinding(
    Avm1SourceBindingKind Kind,
    SourceProgramSymbolIndex Symbol,
    SourceProgramSymbolList Candidates)
{
    public static readonly Avm1SourceExpressionBinding None = new(
        Avm1SourceBindingKind.None,
        SourceProgramSymbolIndex.Invalid,
        SourceProgramSymbolList.Empty);

    public bool IsBound => Kind is Avm1SourceBindingKind.Bound;
}

public sealed class Avm1SourceArenaBindings
{
    private readonly Avm1SourceExpressionBinding[] _bindings;
    private readonly SourceProgramSymbolIndex[] _candidateSymbols;

    internal Avm1SourceArenaBindings(
        Avm1SourceArena arena,
        SourceProgramSymbolIndex containingClass,
        SourceProgramSymbolIndex containingMember,
        bool isStaticContext,
        Avm1SourceExpressionBinding[] bindings,
        Avm1SourceScopeAnalysis scopeAnalysis,
        SourceProgramSymbolIndex[] candidateSymbols)
    {
        Arena = arena;
        ContainingClass = containingClass;
        ContainingMember = containingMember;
        IsStaticContext = isStaticContext;
        _bindings = bindings;
        ScopeAnalysis = scopeAnalysis;
        _candidateSymbols = candidateSymbols;
    }

    public Avm1SourceArena Arena { get; }

    public SourceProgramSymbolIndex ContainingClass { get; }

    public SourceProgramSymbolIndex ContainingMember { get; }

    public bool IsStaticContext { get; }

    public Avm1SourceScopeAnalysis ScopeAnalysis { get; }

    public IReadOnlyList<Avm1SourceExpressionBinding> Bindings => _bindings;

    public IReadOnlyList<SourceProgramSymbolIndex> CandidateSymbols => _candidateSymbols;

    public Avm1SourceExpressionBinding this[SourceExpressionIndex expression] =>
        _bindings[expression.Value];

    public Avm1SourceExpressionUse GetUse(SourceExpressionIndex expression) =>
        ScopeAnalysis.GetUse(expression);

    public bool IsInDynamicScope(SourceExpressionIndex expression) =>
        ScopeAnalysis.IsInDynamicScope(expression);

    public SourceProgramSymbolIndex GetCandidate(
        Avm1SourceExpressionBinding binding,
        int candidateIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            candidateIndex,
            binding.Candidates.Count);
        return _candidateSymbols[binding.Candidates.Start + candidateIndex];
    }

    internal Avm1SourceArenaBindings WithBindings(
        Avm1SourceExpressionBinding[] bindings,
        SourceProgramSymbolIndex[] candidateSymbols) =>
        new(
            Arena,
            ContainingClass,
            ContainingMember,
            IsStaticContext,
            bindings,
            ScopeAnalysis,
            candidateSymbols);
}

public sealed class Avm1SourceProgram
{
    private readonly Avm1SourceFile[] _files;
    private readonly Avm1SourceProgramSymbol[] _symbols;
    private readonly Avm1SourceProgramType[] _types;
    private readonly Avm1SourceProgramSignature[] _signatures;
    private readonly Avm1SourceProgramParameter[] _parameters;
    private readonly SourceProgramSymbolIndex[] _baseClasses;
    private readonly SourceProgramTypeIndex[] _baseTypes;
    private readonly SourceProgramTypeList[] _interfaceTypeLists;
    private readonly SourceProgramTypeIndex[] _interfaceTypes;
    private readonly Avm1SourceArenaBindings[] _initialArenaBindings;
    private readonly Dictionary<Avm1SourceClass, SourceProgramSymbolIndex> _classSymbols;
    private readonly Dictionary<Avm1SourceClassMember, SourceProgramSymbolIndex> _memberSymbols;
    private readonly Dictionary<string, SourceProgramSymbolIndex[]> _classesByName;
    private readonly Dictionary<string, SourceProgramTypeIndex> _typesByName;
    private readonly Dictionary<Avm1SourceTypeKind, SourceProgramTypeIndex> _builtInTypes;
    private readonly Dictionary<SourceProgramSymbolIndex, Dictionary<string, SourceProgramSymbolIndex[]>>
        _membersByClass;
    private readonly Lazy<Avm1SourceSemanticAnalysis> _semanticAnalysis;

    private Avm1SourceProgram(BuildResult result)
    {
        _files = result.Files;
        _symbols = result.Symbols;
        _types = result.Types;
        _signatures = result.Signatures;
        _parameters = result.Parameters;
        _baseClasses = result.BaseClasses;
        _baseTypes = result.BaseTypes;
        _interfaceTypeLists = result.InterfaceTypeLists;
        _interfaceTypes = result.InterfaceTypes;
        _initialArenaBindings = result.ArenaBindings;
        _classSymbols = result.ClassSymbols;
        _memberSymbols = result.MemberSymbols;
        _classesByName = result.ClassesByName;
        _typesByName = result.TypesByName;
        _builtInTypes = result.BuiltInTypes;
        _membersByClass = BuildMemberIndex(_symbols);
        _semanticAnalysis = new Lazy<Avm1SourceSemanticAnalysis>(
            () => Avm1SourceSemanticAnalysis.Build(this, _initialArenaBindings),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<Avm1SourceFile> Files => _files;

    public IReadOnlyList<Avm1SourceProgramSymbol> Symbols => _symbols;

    public IReadOnlyList<Avm1SourceProgramType> Types => _types;

    public IReadOnlyList<Avm1SourceProgramSignature> Signatures => _signatures;

    public IReadOnlyList<Avm1SourceProgramParameter> Parameters => _parameters;

    public IReadOnlyList<SourceProgramTypeIndex> InterfaceTypes => _interfaceTypes;

    public IReadOnlyList<Avm1SourceArenaBindings> ArenaBindings =>
        SemanticAnalysis.ArenaBindings;

    public Avm1SourceTypeAnalysis TypeAnalysis => SemanticAnalysis.TypeAnalysis;

    public Avm1SourceSemanticAnalysis SemanticAnalysis => _semanticAnalysis.Value;

    public bool IsComplete => _files.All(file => file.IsComplete);

    public bool IsSemanticallyComplete => IsComplete && SemanticAnalysis.IsComplete;

    public Avm1SourceProgramSymbol this[SourceProgramSymbolIndex symbol] =>
        _symbols[symbol.Value];

    public Avm1SourceProgramType this[SourceProgramTypeIndex type] =>
        _types[type.Value];

    public Avm1SourceProgramSignature this[SourceProgramSignatureIndex signature] =>
        _signatures[signature.Value];

    public static Avm1SourceProgram Create(
        IEnumerable<Avm1SourceFile> files,
        IAvm1ReferenceProvider? referenceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new Avm1SourceProgram(
            new Builder(files.ToArray(), referenceProvider).Build());
    }

    public Avm1SourceProgramProjection CreateProjection(
        Avm1SourceProjectionOptions? options = null) =>
        Avm1SourceProgramProjection.Create(this, options);

    public bool TryGetSymbol(
        Avm1SourceClass declaration,
        out SourceProgramSymbolIndex symbol)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return _classSymbols.TryGetValue(declaration, out symbol);
    }

    public bool TryGetSymbol(
        Avm1SourceClassMember declaration,
        out SourceProgramSymbolIndex symbol)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return _memberSymbols.TryGetValue(declaration, out symbol);
    }

    public bool TryGetClassSymbol(
        string qualifiedName,
        out SourceProgramSymbolIndex symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        var normalizedName = NormalizeRuntimeQualifiedName(qualifiedName);
        if (_classesByName.TryGetValue(normalizedName, out var candidates) &&
            candidates.Length == 1)
        {
            symbol = candidates[0];
            return true;
        }

        symbol = SourceProgramSymbolIndex.Invalid;
        return false;
    }

    public bool TryGetBaseClass(
        SourceProgramSymbolIndex classSymbol,
        out SourceProgramSymbolIndex baseClass)
    {
        if (!classSymbol.IsValid || classSymbol.Value >= _baseClasses.Length)
        {
            baseClass = SourceProgramSymbolIndex.Invalid;
            return false;
        }

        baseClass = _baseClasses[classSymbol.Value];
        return baseClass.IsValid;
    }

    public bool TryGetBaseType(
        SourceProgramSymbolIndex classSymbol,
        out SourceProgramTypeIndex baseType)
    {
        if (!classSymbol.IsValid || classSymbol.Value >= _baseTypes.Length)
        {
            baseType = SourceProgramTypeIndex.Invalid;
            return false;
        }

        baseType = _baseTypes[classSymbol.Value];
        return baseType.IsValid;
    }

    public SourceProgramTypeList GetInterfaceTypes(
        SourceProgramSymbolIndex classSymbol)
    {
        if (!classSymbol.IsValid || classSymbol.Value >= _interfaceTypeLists.Length)
            return SourceProgramTypeList.Empty;
        return _interfaceTypeLists[classSymbol.Value];
    }

    public SourceProgramTypeIndex GetInterfaceType(
        SourceProgramTypeList interfaces,
        int interfaceIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(interfaceIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            interfaceIndex,
            interfaces.Count);
        return _interfaceTypes[interfaces.Start + interfaceIndex];
    }

    public bool TryGetSignature(
        SourceProgramSymbolIndex symbol,
        out Avm1SourceProgramSignature signature)
    {
        if (!symbol.IsValid || symbol.Value >= _symbols.Length)
        {
            signature = default;
            return false;
        }

        var signatureIndex = _symbols[symbol.Value].Signature;
        if (!signatureIndex.IsValid)
        {
            signature = default;
            return false;
        }

        signature = _signatures[signatureIndex.Value];
        return true;
    }

    public Avm1SourceProgramParameter GetParameter(
        Avm1SourceProgramSignature signature,
        int parameterIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parameterIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            parameterIndex,
            signature.Parameters.Count);
        return _parameters[signature.Parameters.Start + parameterIndex];
    }

    public SourceProgramTypeIndex GetBuiltInType(Avm1SourceTypeKind kind)
    {
        if (kind is Avm1SourceTypeKind.Nominal)
            throw new ArgumentOutOfRangeException(nameof(kind));
        return _builtInTypes[kind];
    }

    public bool TryGetType(
        Avm1SourceTypeReference reference,
        out SourceProgramTypeIndex type)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return TryGetType(reference.Name.Value, out type);
    }

    public bool TryGetType(string name, out SourceProgramTypeIndex type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (TryGetBuiltInTypeKind(name, out var builtInKind))
        {
            type = _builtInTypes[builtInKind];
            return true;
        }

        return _typesByName.TryGetValue(NormalizeRuntimeQualifiedName(name), out type);
    }

    public bool TryGetBindings(
        Avm1SourceArena arena,
        [NotNullWhen(true)] out Avm1SourceArenaBindings? bindings)
    {
        ArgumentNullException.ThrowIfNull(arena);
        return SemanticAnalysis.TryGetBindings(arena, out bindings);
    }

    public Avm1SourceExpressionBinding GetBinding(
        Avm1SourceArena arena,
        SourceExpressionIndex expression)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!SemanticAnalysis.TryGetBindings(arena, out var bindings))
            throw new ArgumentException("The arena does not belong to this Source program.", nameof(arena));
        return bindings[expression];
    }

    internal void CollectInstanceMemberCandidates(
        SourceProgramSymbolIndex classSymbol,
        string name,
        Avm1SourceExpressionUse use,
        List<SourceProgramSymbolIndex> candidates)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(candidates);

        var candidateStart = candidates.Count;
        var current = classSymbol;
        for (var depth = 0;
            current.IsValid && depth < _symbols.Length;
            depth++)
        {
            if (_membersByClass.TryGetValue(current, out var lookup) &&
                lookup.TryGetValue(name, out var directMembers))
            {
                foreach (var candidate in directMembers)
                {
                    var symbol = _symbols[candidate.Value];
                    if (symbol.Kind is Avm1SourceProgramSymbolKind.Constructor ||
                        symbol.Modifiers.HasFlag(
                            Avm1SourceDeclarationModifiers.Static) ||
                        !IsCompatibleMemberUse(symbol.Kind, use))
                    {
                        continue;
                    }
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count != candidateStart)
                break;
            current = current.Value < _baseClasses.Length
                ? _baseClasses[current.Value]
                : SourceProgramSymbolIndex.Invalid;
        }

        if (!IsReadWrite(use) || candidates.Count == candidateStart)
            return;

        var hasField = false;
        var hasGetter = false;
        var hasSetter = false;
        for (var i = candidateStart; i < candidates.Count; i++)
        {
            switch (_symbols[candidates[i].Value].Kind)
            {
                case Avm1SourceProgramSymbolKind.Field:
                    hasField = true;
                    break;
                case Avm1SourceProgramSymbolKind.Getter:
                    hasGetter = true;
                    break;
                case Avm1SourceProgramSymbolKind.Setter:
                    hasSetter = true;
                    break;
            }
        }

        if (!hasField && !(hasGetter && hasSetter))
            candidates.RemoveRange(candidateStart, candidates.Count - candidateStart);
    }

    private static Dictionary<SourceProgramSymbolIndex, Dictionary<string, SourceProgramSymbolIndex[]>>
        BuildMemberIndex(IReadOnlyList<Avm1SourceProgramSymbol> symbols)
    {
        var builders = new Dictionary<SourceProgramSymbolIndex, Dictionary<string, List<SourceProgramSymbolIndex>>>();
        foreach (var symbol in symbols)
        {
            if (!symbol.ContainingSymbol.IsValid ||
                symbol.Kind is Avm1SourceProgramSymbolKind.Class or
                    Avm1SourceProgramSymbolKind.Interface)
            {
                continue;
            }

            if (!builders.TryGetValue(symbol.ContainingSymbol, out var classMembers))
            {
                classMembers = new Dictionary<string, List<SourceProgramSymbolIndex>>(
                    StringComparer.Ordinal);
                builders.Add(symbol.ContainingSymbol, classMembers);
            }
            if (!classMembers.TryGetValue(symbol.Name, out var namedMembers))
            {
                namedMembers = [];
                classMembers.Add(symbol.Name, namedMembers);
            }
            namedMembers.Add(symbol.Index);
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToDictionary(
                member => member.Key,
                member => member.Value.ToArray(),
                StringComparer.Ordinal));
    }

    private static bool IsCompatibleMemberUse(
        Avm1SourceProgramSymbolKind kind,
        Avm1SourceExpressionUse use)
    {
        if (use is Avm1SourceExpressionUse.None)
            use = Avm1SourceExpressionUse.Read;
        if (use.HasFlag(Avm1SourceExpressionUse.Delete))
            return false;

        var writes = use.HasFlag(Avm1SourceExpressionUse.Write);
        var reads = use.HasFlag(Avm1SourceExpressionUse.Read) ||
            use.HasFlag(Avm1SourceExpressionUse.Invoke) ||
            use.HasFlag(Avm1SourceExpressionUse.Construct);
        if (writes && reads)
        {
            return kind is
                Avm1SourceProgramSymbolKind.Field or
                Avm1SourceProgramSymbolKind.Getter or
                Avm1SourceProgramSymbolKind.Setter;
        }
        if (writes)
        {
            return kind is
                Avm1SourceProgramSymbolKind.Field or
                Avm1SourceProgramSymbolKind.Setter;
        }
        return kind is
            Avm1SourceProgramSymbolKind.Field or
            Avm1SourceProgramSymbolKind.Getter or
            Avm1SourceProgramSymbolKind.Method;
    }

    private static bool IsReadWrite(Avm1SourceExpressionUse use) =>
        use.HasFlag(Avm1SourceExpressionUse.Write) &&
        (use.HasFlag(Avm1SourceExpressionUse.Read) ||
         use.HasFlag(Avm1SourceExpressionUse.Invoke) ||
         use.HasFlag(Avm1SourceExpressionUse.Construct));

    internal static string NormalizeRuntimeQualifiedName(string name)
    {
        var normalized = name;
        while (true)
        {
            if (normalized.StartsWith("_global.", StringComparison.Ordinal))
            {
                normalized = normalized["_global.".Length..];
                continue;
            }

            if (normalized.StartsWith("__Packages.", StringComparison.Ordinal))
            {
                normalized = normalized["__Packages.".Length..];
                continue;
            }

            return normalized;
        }
    }

    private static bool TryGetBuiltInTypeKind(
        string name,
        out Avm1SourceTypeKind kind)
    {
        kind = name switch
        {
            "Boolean" => Avm1SourceTypeKind.Boolean,
            "Number" => Avm1SourceTypeKind.Number,
            "String" => Avm1SourceTypeKind.String,
            "Object" => Avm1SourceTypeKind.Object,
            "Array" => Avm1SourceTypeKind.Array,
            "Function" => Avm1SourceTypeKind.Function,
            "Void" or "void" => Avm1SourceTypeKind.Void,
            _ => Avm1SourceTypeKind.Unknown
        };
        return kind is not Avm1SourceTypeKind.Unknown;
    }

    private sealed class Builder
    {
        private static readonly List<SourceProgramSymbolIndex> EmptySymbolList = [];

        private readonly Avm1SourceFile[] _files;
        private readonly Avm1SourceReferenceClass[] _referenceClasses;
        private readonly List<Avm1SourceProgramSymbol> _symbols = [];
        private readonly List<Avm1SourceProgramType> _types = [];
        private readonly List<Avm1SourceProgramSignature> _signatures = [];
        private readonly List<Avm1SourceProgramParameter> _parameters = [];
        private readonly List<Avm1SourceArenaBindings> _arenaBindings = [];
        private readonly Dictionary<Avm1SourceClass, SourceProgramSymbolIndex> _classSymbols =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Avm1SourceClassMember, SourceProgramSymbolIndex> _memberSymbols =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, List<SourceProgramSymbolIndex>> _classesByName =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceProgramTypeIndex> _typesByName =
            new(StringComparer.Ordinal);
        private readonly Dictionary<Avm1SourceTypeKind, SourceProgramTypeIndex> _builtInTypes = [];
        private readonly Dictionary<SourceProgramSymbolIndex, Dictionary<string, List<SourceProgramSymbolIndex>>>
            _membersByClass = [];
        private readonly List<Avm1SourceClass> _orderedClasses = [];
        private readonly List<SourceProgramSymbolIndex> _orderedClassSymbols = [];
        private readonly List<ReferenceClassEntry> _orderedReferenceClasses = [];
        private readonly Dictionary<SourceProgramSymbolIndex, string> _baseTypeNames = [];
        private readonly Dictionary<SourceProgramSymbolIndex, List<string>>
            _interfaceTypeNames = [];
        private readonly Dictionary<SourceProgramSymbolIndex, SourceProgramSymbolIndex>
            _baseClasses = [];
        private readonly Dictionary<SourceProgramSymbolIndex, SourceProgramTypeIndex>
            _baseTypes = [];
        private readonly Dictionary<SourceProgramSymbolIndex, List<SourceProgramTypeIndex>>
            _interfaceTypesByClass = [];
        private readonly List<ArenaContext> _arenaContexts = [];
        private readonly Dictionary<Avm1SourceArena, int> _arenaContextIndices =
            new(ReferenceEqualityComparer.Instance);

        public Builder(
            Avm1SourceFile[] files,
            IAvm1ReferenceProvider? referenceProvider)
        {
            _files = files;
            var classes = referenceProvider?.GetClasses();
            if (referenceProvider is not null && classes is null)
            {
                throw new InvalidOperationException(
                    "The AVM1 reference provider returned a null class sequence.");
            }
            _referenceClasses = classes?.ToArray() ?? [];
            if (_referenceClasses.Any(sourceClass => sourceClass is null))
            {
                throw new InvalidOperationException(
                    "The AVM1 reference provider returned a null class.");
            }
        }

        public BuildResult Build()
        {
            AddBuiltInTypes();
            AddClassSymbols();
            AddReferenceClassSymbols();
            AddClassTypes();
            AddMemberSymbolsAndArenaContexts();
            AddReferenceMembers();
            AddReferencedTypes();
            AddClassRelationships();
            BindArenas();

            var interfaceTables = BuildInterfaceTypeTables();

            return new BuildResult(
                _files,
                _symbols.ToArray(),
                _types.ToArray(),
                _signatures.ToArray(),
                _parameters.ToArray(),
                BuildBaseClassTable(),
                BuildBaseTypeTable(),
                interfaceTables.Lists,
                interfaceTables.Types,
                _arenaBindings.ToArray(),
                _classSymbols,
                _memberSymbols,
                _classesByName.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal),
                _typesByName,
                _builtInTypes);
        }

        private void AddBuiltInTypes()
        {
            foreach (var kind in Enum.GetValues<Avm1SourceTypeKind>())
            {
                if (kind is Avm1SourceTypeKind.Nominal)
                    continue;
                var index = new SourceProgramTypeIndex(_types.Count);
                _types.Add(new Avm1SourceProgramType(
                    index,
                    kind,
                    null,
                    SourceProgramSymbolIndex.Invalid));
                _builtInTypes.Add(kind, index);
            }
        }

        private void AddClassSymbols()
        {
            foreach (var file in _files)
            {
                foreach (var sourceClass in file.Classes)
                {
                    var index = new SourceProgramSymbolIndex(_symbols.Count);
                    var normalizedName = NormalizeRuntimeQualifiedName(sourceClass.Name.Value);
                    var qualifiedName = normalizedName == sourceClass.Name.Value
                        ? sourceClass.Name
                        : new Avm1SourceQualifiedName(normalizedName);
                    _symbols.Add(new Avm1SourceProgramSymbol(
                        index,
                        sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface
                            ? Avm1SourceProgramSymbolKind.Interface
                            : Avm1SourceProgramSymbolKind.Class,
                        qualifiedName.SimpleName,
                        qualifiedName,
                        SourceProgramSymbolIndex.Invalid,
                        SourceProgramTypeIndex.Invalid,
                        SourceProgramTypeIndex.Invalid,
                        sourceClass.Modifiers,
                        Avm1SourceProgramSymbolFlags.None,
                        SourceProgramSignatureIndex.Invalid));
                    _classSymbols.Add(sourceClass, index);
                    _orderedClasses.Add(sourceClass);
                    _orderedClassSymbols.Add(index);
                    if (sourceClass.BaseType is not null)
                        _baseTypeNames.Add(index, sourceClass.BaseType.Value);
                    if (sourceClass.Interfaces.Count != 0)
                    {
                        _interfaceTypeNames.Add(
                            index,
                            sourceClass.Interfaces
                                .Select(type => type.Value)
                                .ToList());
                    }
                    if (!_classesByName.TryGetValue(normalizedName, out var candidates))
                    {
                        candidates = [];
                        _classesByName.Add(normalizedName, candidates);
                    }
                    candidates.Add(index);
                }
            }
        }

        private void AddReferenceClassSymbols()
        {
            foreach (var referenceClass in _referenceClasses)
            {
                var normalizedName = NormalizeRuntimeQualifiedName(
                    referenceClass.Name.Value);
                if (_classesByName.TryGetValue(normalizedName, out var existing))
                {
                    if (existing.Any(symbol => !_symbols[symbol.Value].Flags.HasFlag(
                            Avm1SourceProgramSymbolFlags.External)))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"External AVM1 class {normalizedName} is declared more than once.");
                }

                var index = new SourceProgramSymbolIndex(_symbols.Count);
                var qualifiedName = normalizedName == referenceClass.Name.Value
                    ? referenceClass.Name
                    : new Avm1SourceQualifiedName(normalizedName);
                _symbols.Add(new Avm1SourceProgramSymbol(
                    index,
                    referenceClass.Kind is Avm1SourceTypeDeclarationKind.Interface
                        ? Avm1SourceProgramSymbolKind.Interface
                        : Avm1SourceProgramSymbolKind.Class,
                    qualifiedName.SimpleName,
                    qualifiedName,
                    SourceProgramSymbolIndex.Invalid,
                    SourceProgramTypeIndex.Invalid,
                    SourceProgramTypeIndex.Invalid,
                    referenceClass.Modifiers,
                    Avm1SourceProgramSymbolFlags.External,
                    SourceProgramSignatureIndex.Invalid));
                _orderedClassSymbols.Add(index);
                _orderedReferenceClasses.Add(new ReferenceClassEntry(referenceClass, index));
                _classesByName.Add(normalizedName, [index]);
                if (referenceClass.BaseType is not null)
                    _baseTypeNames.Add(index, referenceClass.BaseType.Name.Value);
                if (referenceClass.Interfaces.Count != 0)
                {
                    _interfaceTypeNames.Add(
                        index,
                        referenceClass.Interfaces
                            .Select(type => type.Name.Value)
                            .ToList());
                }
            }
        }

        private void AddClassTypes()
        {
            foreach (var symbolIndex in _orderedClassSymbols)
            {
                var symbol = _symbols[symbolIndex.Value];
                var type = InternNominalType(symbol.QualifiedName.Value);
                _symbols[symbolIndex.Value] = symbol with
                {
                    DeclaredType = type,
                    InferredType = type
                };
            }
        }

        private void AddMemberSymbolsAndArenaContexts()
        {
            foreach (var file in _files)
            {
                foreach (var sourceClass in file.Classes)
                {
                    var classSymbol = _classSymbols[sourceClass];
                    var memberLookup = new Dictionary<string, List<SourceProgramSymbolIndex>>(
                        StringComparer.Ordinal);
                    _membersByClass.Add(classSymbol, memberLookup);

                    foreach (var member in sourceClass.Members)
                    {
                        var symbol = AddMemberSymbol(sourceClass, classSymbol, member);
                        _memberSymbols.Add(member, symbol);
                        AddMemberAlias(memberLookup, member.Name, symbol);
                        if (!string.IsNullOrWhiteSpace(member.Origin.RuntimeName) &&
                            !string.Equals(member.Name, member.Origin.RuntimeName, StringComparison.Ordinal))
                        {
                            AddMemberAlias(memberLookup, member.Origin.RuntimeName!, symbol);
                        }

                        switch (member)
                        {
                            case Avm1SourceField { Initializer: { } initializer }:
                                AddArenaContext(
                                    initializer.Arena,
                                    classSymbol,
                                    symbol,
                                     member.Modifiers.HasFlag(
                                         Avm1SourceDeclarationModifiers.Static),
                                     SourceStatementIndex.Invalid,
                                     initializer.Expression,
                                     []);
                                break;

                            case Avm1SourceMethodDeclaration method:
                                AddArenaContext(
                                    method.Body.Arena,
                                    classSymbol,
                                    symbol,
                                     method.Modifiers.HasFlag(
                                         Avm1SourceDeclarationModifiers.Static),
                                     method.Body.Body,
                                     SourceExpressionIndex.Invalid,
                                     method.Body.Parameters);
                                break;
                        }
                    }

                    if (sourceClass.Initializer is { } classInitializer)
                    {
                        AddArenaContext(
                            classInitializer.Body.Arena,
                            classSymbol,
                             SourceProgramSymbolIndex.Invalid,
                             isStaticContext: true,
                             classInitializer.Body.Body,
                             SourceExpressionIndex.Invalid,
                             classInitializer.Body.Parameters);
                    }
                }
            }
        }

        private SourceProgramSymbolIndex AddMemberSymbol(
            Avm1SourceClass sourceClass,
            SourceProgramSymbolIndex classSymbol,
            Avm1SourceClassMember member)
        {
            var kind = member switch
            {
                Avm1SourceField => Avm1SourceProgramSymbolKind.Field,
                Avm1SourceMethodDeclaration { Kind: Avm1SourceMethodKind.Constructor } =>
                    Avm1SourceProgramSymbolKind.Constructor,
                Avm1SourceMethodDeclaration { Kind: Avm1SourceMethodKind.Getter } =>
                    Avm1SourceProgramSymbolKind.Getter,
                Avm1SourceMethodDeclaration { Kind: Avm1SourceMethodKind.Setter } =>
                    Avm1SourceProgramSymbolKind.Setter,
                Avm1SourceMethodDeclaration => Avm1SourceProgramSymbolKind.Method,
                _ => throw new InvalidOperationException(
                    $"Unsupported Source member declaration {member.GetType().Name}.")
            };
            var unknownType = _builtInTypes[Avm1SourceTypeKind.Unknown];
            var declaredType = unknownType;
            var inferredType = unknownType;
            if (member is Avm1SourceField field)
            {
                declaredType = InternTypeReference(field.DeclaredType);
                inferredType = InternTypeReference(field.InferredType);
            }
            else if (member is Avm1SourceMethodDeclaration method)
            {
                if (method.Kind is Avm1SourceMethodKind.Constructor)
                {
                    declaredType = InternNominalType(sourceClass.Name.Value);
                    inferredType = declaredType;
                }
                else
                {
                    declaredType = InternTypeReference(method.DeclaredReturnType);
                    inferredType = InternTypeReference(method.InferredReturnType);
                }
            }

            var signature = member is Avm1SourceMethodDeclaration sourceMethod
                ? AddSourceSignature(sourceMethod.Body)
                : SourceProgramSignatureIndex.Invalid;

            var index = new SourceProgramSymbolIndex(_symbols.Count);
            _symbols.Add(new Avm1SourceProgramSymbol(
                index,
                kind,
                member.Name,
                new Avm1SourceQualifiedName(sourceClass.Name.Value + "." + member.Name),
                classSymbol,
                declaredType,
                inferredType,
                member.Modifiers,
                Avm1SourceProgramSymbolFlags.None,
                signature));
            if (kind is Avm1SourceProgramSymbolKind.Constructor)
            {
                _symbols[classSymbol.Value] = _symbols[classSymbol.Value] with
                {
                    Signature = signature
                };
            }
            return index;
        }

        private SourceProgramSignatureIndex AddSourceSignature(Avm1SourceMethod method)
        {
            var start = _parameters.Count;
            foreach (var parameterIndex in method.Parameters)
            {
                var parameter = method.Arena[parameterIndex];
                var type = parameter.DeclaredType.IsValid
                    ? InternArenaType(method.Arena, parameter.DeclaredType)
                    : _builtInTypes[Avm1SourceTypeKind.Unknown];
                _parameters.Add(new Avm1SourceProgramParameter(
                    method.Arena[parameter.Name],
                    type,
                    Avm1SourceProgramParameterFlags.None));
            }
            return AddSignature(start, method.Parameters.Count);
        }

        private void AddReferenceMembers()
        {
            foreach (var entry in _orderedReferenceClasses)
            {
                var memberLookup = new Dictionary<string, List<SourceProgramSymbolIndex>>(
                    StringComparer.Ordinal);
                _membersByClass.Add(entry.Symbol, memberLookup);
                foreach (var member in entry.ReferenceClass.Members)
                {
                    var kind = member.Kind switch
                    {
                        Avm1SourceReferenceMemberKind.Field =>
                            Avm1SourceProgramSymbolKind.Field,
                        Avm1SourceReferenceMemberKind.Constructor =>
                            Avm1SourceProgramSymbolKind.Constructor,
                        Avm1SourceReferenceMemberKind.Method =>
                            Avm1SourceProgramSymbolKind.Method,
                        Avm1SourceReferenceMemberKind.Getter =>
                            Avm1SourceProgramSymbolKind.Getter,
                        Avm1SourceReferenceMemberKind.Setter =>
                            Avm1SourceProgramSymbolKind.Setter,
                        _ => throw new InvalidOperationException(
                            $"Unsupported reference member kind {member.Kind}.")
                    };
                    var type = kind switch
                    {
                        Avm1SourceProgramSymbolKind.Constructor =>
                            _symbols[entry.Symbol.Value].DeclaredType,
                        Avm1SourceProgramSymbolKind.Setter when member.Type is null =>
                            _builtInTypes[Avm1SourceTypeKind.Void],
                        _ => InternTypeReference(member.Type)
                    };
                    var signature = kind is Avm1SourceProgramSymbolKind.Field
                        ? SourceProgramSignatureIndex.Invalid
                        : AddReferenceSignature(member.Parameters);
                    var index = new SourceProgramSymbolIndex(_symbols.Count);
                    var className = _symbols[entry.Symbol.Value].QualifiedName.Value;
                    _symbols.Add(new Avm1SourceProgramSymbol(
                        index,
                        kind,
                        member.Name,
                        new Avm1SourceQualifiedName(className + "." + member.Name),
                        entry.Symbol,
                        type,
                        type,
                        member.Modifiers,
                        Avm1SourceProgramSymbolFlags.External,
                        signature));
                    AddMemberAlias(memberLookup, member.Name, index);

                    if (kind is Avm1SourceProgramSymbolKind.Constructor)
                    {
                        if (_symbols[entry.Symbol.Value].Signature.IsValid)
                        {
                            throw new InvalidOperationException(
                                $"External AVM1 class {className} declares multiple constructors.");
                        }
                        _symbols[entry.Symbol.Value] = _symbols[entry.Symbol.Value] with
                        {
                            Signature = signature
                        };
                    }
                }
            }
        }

        private SourceProgramSignatureIndex AddReferenceSignature(
            IReadOnlyList<Avm1SourceReferenceParameter> parameters)
        {
            var start = _parameters.Count;
            foreach (var parameter in parameters)
            {
                var flags = Avm1SourceProgramParameterFlags.None;
                if (parameter.Flags.HasFlag(Avm1SourceReferenceParameterFlags.Optional))
                    flags |= Avm1SourceProgramParameterFlags.Optional;
                if (parameter.Flags.HasFlag(Avm1SourceReferenceParameterFlags.Rest))
                    flags |= Avm1SourceProgramParameterFlags.Rest;
                _parameters.Add(new Avm1SourceProgramParameter(
                    parameter.Name,
                    InternTypeReference(parameter.Type),
                    flags));
            }
            return AddSignature(start, parameters.Count);
        }

        private SourceProgramSignatureIndex AddSignature(int parameterStart, int parameterCount)
        {
            var index = new SourceProgramSignatureIndex(_signatures.Count);
            _signatures.Add(new Avm1SourceProgramSignature(
                index,
                new SourceProgramParameterList(parameterStart, parameterCount)));
            return index;
        }

        private void AddClassRelationships()
        {
            foreach (var pair in _baseTypeNames)
            {
                var baseType = InternNominalType(pair.Value);
                _baseTypes.Add(pair.Key, baseType);
                var baseClass = _types[baseType.Value].Symbol;
                if (baseClass.IsValid && baseClass != pair.Key)
                    _baseClasses.Add(pair.Key, baseClass);
            }

            foreach (var pair in _interfaceTypeNames)
            {
                var interfaces = new List<SourceProgramTypeIndex>(pair.Value.Count);
                foreach (var name in pair.Value)
                {
                    var type = InternNominalType(name);
                    if (!interfaces.Contains(type))
                        interfaces.Add(type);
                }
                _interfaceTypesByClass.Add(pair.Key, interfaces);
            }
        }

        private SourceProgramSymbolIndex[] BuildBaseClassTable()
        {
            var result = new SourceProgramSymbolIndex[_symbols.Count];
            Array.Fill(result, SourceProgramSymbolIndex.Invalid);
            foreach (var pair in _baseClasses)
                result[pair.Key.Value] = pair.Value;
            return result;
        }

        private SourceProgramTypeIndex[] BuildBaseTypeTable()
        {
            var result = new SourceProgramTypeIndex[_symbols.Count];
            Array.Fill(result, SourceProgramTypeIndex.Invalid);
            foreach (var pair in _baseTypes)
                result[pair.Key.Value] = pair.Value;
            return result;
        }

        private InterfaceTypeTables BuildInterfaceTypeTables()
        {
            var lists = new SourceProgramTypeList[_symbols.Count];
            Array.Fill(lists, SourceProgramTypeList.Empty);
            var types = new List<SourceProgramTypeIndex>();
            foreach (var classSymbol in _orderedClassSymbols)
            {
                if (!_interfaceTypesByClass.TryGetValue(
                        classSymbol,
                        out var classInterfaces))
                {
                    continue;
                }

                lists[classSymbol.Value] = new SourceProgramTypeList(
                    types.Count,
                    classInterfaces.Count);
                types.AddRange(classInterfaces);
            }
            return new InterfaceTypeTables(lists, types.ToArray());
        }

        private static void AddMemberAlias(
            Dictionary<string, List<SourceProgramSymbolIndex>> lookup,
            string name,
            SourceProgramSymbolIndex symbol)
        {
            if (!lookup.TryGetValue(name, out var candidates))
            {
                candidates = [];
                lookup.Add(name, candidates);
            }
            if (!candidates.Contains(symbol))
                candidates.Add(symbol);
        }

        private void AddArenaContext(
            Avm1SourceArena arena,
            SourceProgramSymbolIndex containingClass,
            SourceProgramSymbolIndex containingMember,
            bool isStaticContext,
            SourceStatementIndex statementRoot,
            SourceExpressionIndex expressionRoot,
            IReadOnlyList<SourceSymbolIndex> rootParameters)
        {
            if (_arenaContextIndices.TryGetValue(arena, out var existing))
            {
                var context = _arenaContexts[existing];
                if (context.ContainingClass != containingClass ||
                    context.ContainingMember != containingMember ||
                    context.IsStaticContext != isStaticContext)
                {
                    throw new InvalidOperationException(
                        "A Source HIR arena cannot be owned by multiple declarations.");
                }
                return;
            }

            _arenaContexts.Add(new ArenaContext(
                arena,
                containingClass,
                containingMember,
                isStaticContext,
                statementRoot,
                expressionRoot,
                rootParameters.ToArray()));
            _arenaContextIndices.Add(arena, _arenaContexts.Count - 1);
        }

        private void AddReferencedTypes()
        {
            foreach (var file in _files)
            {
                foreach (var sourceClass in file.Classes)
                {
                    if (sourceClass.BaseType is not null)
                        InternNominalType(sourceClass.BaseType.Value);
                    foreach (var interfaceName in sourceClass.Interfaces)
                        InternNominalType(interfaceName.Value);
                }
            }

            foreach (var entry in _orderedReferenceClasses)
            {
                var referenceClass = entry.ReferenceClass;
                if (referenceClass.BaseType is not null)
                    InternNominalType(referenceClass.BaseType.Name.Value);
                foreach (var interfaceType in referenceClass.Interfaces)
                    InternNominalType(interfaceType.Name.Value);
            }

            foreach (var context in _arenaContexts)
            {
                foreach (var type in context.Arena.Types)
                {
                    if (type.Kind is Avm1SourceTypeKind.Nominal && type.Name.IsValid)
                        InternNominalType(context.Arena[type.Name]);
                }
            }
        }

        private SourceProgramTypeIndex InternTypeReference(Avm1SourceTypeReference? reference)
        {
            if (reference is null)
                return _builtInTypes[Avm1SourceTypeKind.Unknown];
            if (TryGetBuiltInTypeKind(reference.Name.Value, out var builtInKind))
                return _builtInTypes[builtInKind];
            return InternNominalType(reference.Name.Value);
        }

        private SourceProgramTypeIndex InternArenaType(
            Avm1SourceArena arena,
            SourceTypeIndex sourceType)
        {
            var type = arena[sourceType];
            if (type.Kind is Avm1SourceTypeKind.Nominal && type.Name.IsValid)
                return InternNominalType(arena[type.Name]);
            if (type.Kind is not Avm1SourceTypeKind.Nominal)
                return _builtInTypes[type.Kind];
            return _builtInTypes[Avm1SourceTypeKind.Unknown];
        }

        private SourceProgramTypeIndex InternNominalType(string name)
        {
            var normalizedName = NormalizeRuntimeQualifiedName(name);
            if (_typesByName.TryGetValue(normalizedName, out var existing))
                return existing;

            var symbol = SourceProgramSymbolIndex.Invalid;
            if (_classesByName.TryGetValue(normalizedName, out var classSymbols) &&
                classSymbols.Count == 1)
            {
                symbol = classSymbols[0];
            }

            var index = new SourceProgramTypeIndex(_types.Count);
            _types.Add(new Avm1SourceProgramType(
                index,
                Avm1SourceTypeKind.Nominal,
                new Avm1SourceQualifiedName(normalizedName),
                symbol));
            _typesByName.Add(normalizedName, index);
            return index;
        }

        private void BindArenas()
        {
            foreach (var context in _arenaContexts)
            {
                var bindings = new ArenaBinder(this, context).Bind();
                _arenaBindings.Add(bindings);
            }
        }

        private List<SourceProgramSymbolIndex> FindClasses(string name)
        {
            var normalizedName = NormalizeRuntimeQualifiedName(name);
            return _classesByName.TryGetValue(normalizedName, out var candidates)
                ? candidates
                : EmptySymbolList;
        }

        private List<SourceProgramSymbolIndex> FindDirectMemberCandidates(
            SourceProgramSymbolIndex classSymbol,
            string name)
        {
            if (_membersByClass.TryGetValue(classSymbol, out var lookup) &&
                lookup.TryGetValue(name, out var candidates))
            {
                return candidates;
            }
            return EmptySymbolList;
        }

        private SourceProgramSymbolIndex GetBaseClass(
            SourceProgramSymbolIndex classSymbol) =>
            _baseClasses.TryGetValue(classSymbol, out var baseClass)
                ? baseClass
                : SourceProgramSymbolIndex.Invalid;

        private sealed class ArenaBinder
        {
            private readonly Builder _program;
            private readonly ArenaContext _context;
            private readonly Avm1SourceArena _arena;
            private readonly Avm1SourceExpressionBinding[] _bindings;
            private readonly byte[] _bindingState;
            private readonly List<SourceProgramSymbolIndex> _candidateSymbols = [];
            private readonly Avm1SourceScopeAnalysis _scopes;

            public ArenaBinder(Builder program, ArenaContext context)
            {
                _program = program;
                _context = context;
                _arena = context.Arena;
                _bindings = new Avm1SourceExpressionBinding[_arena.Expressions.Count];
                _bindingState = new byte[_arena.Expressions.Count];
                _scopes = Avm1SourceScopeAnalysis.Build(
                    context.Arena,
                    context.StatementRoot,
                    context.ExpressionRoot,
                    context.RootParameters);
            }

            public Avm1SourceArenaBindings Bind()
            {
                for (var i = 0; i < _arena.Expressions.Count; i++)
                    BindExpression(new SourceExpressionIndex(i));

                return new Avm1SourceArenaBindings(
                    _arena,
                    _context.ContainingClass,
                    _context.ContainingMember,
                    _context.IsStaticContext,
                    _bindings,
                    _scopes,
                    _candidateSymbols.ToArray());
            }

            private Avm1SourceExpressionBinding BindExpression(SourceExpressionIndex index)
            {
                if (_bindingState[index.Value] == 2)
                    return _bindings[index.Value];
                if (_bindingState[index.Value] == 1)
                    return Dynamic();

                _bindingState[index.Value] = 1;
                var expression = _arena[index];
                for (var i = 0; i < expression.Children.Count; i++)
                    BindExpression(_arena.GetChild(expression, i));

                var binding = expression.Kind switch
                {
                    Avm1SourceExpressionKind.DynamicName => BindDynamicName(expression),
                    Avm1SourceExpressionKind.ComputedDynamicName => Dynamic(),
                    Avm1SourceExpressionKind.QualifiedName => BindQualifiedName(expression),
                    Avm1SourceExpressionKind.MemberAccess => BindMemberAccess(expression),
                    _ => Avm1SourceExpressionBinding.None
                };
                _bindings[index.Value] = binding;
                _bindingState[index.Value] = 2;
                return binding;
            }

            private Avm1SourceExpressionBinding BindDynamicName(Avm1SourceExpression expression)
            {
                if (!HasKnownScope(expression.Index) ||
                    _scopes.IsInDynamicScope(expression.Index))
                    return Dynamic();

                var name = _arena[expression.Name];
                var use = _scopes.GetUse(expression.Index);
                var member = BindMembers(
                    _context.ContainingClass,
                    name,
                    isStatic: _context.IsStaticContext,
                    use);
                if (member.Kind is not Avm1SourceBindingKind.Dynamic)
                    return member;
                if (!_context.IsStaticContext)
                {
                    member = BindMembers(
                        _context.ContainingClass,
                        name,
                        isStatic: true,
                        use);
                    if (member.Kind is not Avm1SourceBindingKind.Dynamic)
                        return member;
                }

                var exact = _program.FindClasses(name);
                if (exact.Count != 0)
                    return FromCandidates(exact);

                var containingClass = _program._symbols[_context.ContainingClass.Value];
                return string.Equals(name, containingClass.Name, StringComparison.Ordinal)
                    ? Bound(_context.ContainingClass)
                    : Dynamic();
            }

            private Avm1SourceExpressionBinding BindQualifiedName(
                Avm1SourceExpression expression)
            {
                if (!HasKnownScope(expression.Index) ||
                    _scopes.IsInDynamicScope(expression.Index))
                    return Dynamic();
                return FromCandidates(_program.FindClasses(_arena[expression.Name]));
            }

            private Avm1SourceExpressionBinding BindMemberAccess(
                Avm1SourceExpression expression)
            {
                if (!TryGetMemberName(expression, out var memberName))
                    return Dynamic();
                if (!HasKnownScope(expression.Index))
                    return Dynamic();

                if (!_scopes.IsInDynamicScope(expression.Index) &&
                    TryFlattenQualifiedName(expression.Index, out var qualifiedName))
                {
                    var classCandidates = _program.FindClasses(qualifiedName);
                    if (classCandidates.Count != 0)
                        return FromCandidates(classCandidates);
                }

                var receiverIndex = _arena.GetChild(expression, 0);
                var use = _scopes.GetUse(expression.Index);
                if (IsCurrentClassThis(receiverIndex))
                {
                    return BindMembers(
                        _context.ContainingClass,
                        memberName,
                        isStatic: false,
                        use);
                }

                if (TryGetInstanceReceiverClass(receiverIndex, out var receiverClass))
                {
                    return BindMembers(
                        receiverClass,
                        memberName,
                        isStatic: false,
                        use);
                }

                var receiverBinding = BindExpression(receiverIndex);
                if (receiverBinding.Kind is Avm1SourceBindingKind.Bound &&
                    _program._symbols[receiverBinding.Symbol.Value].Kind is
                        Avm1SourceProgramSymbolKind.Class or
                        Avm1SourceProgramSymbolKind.Interface)
                {
                    return BindMembers(
                        receiverBinding.Symbol,
                        memberName,
                        isStatic: true,
                        use);
                }

                if (receiverBinding.Kind is Avm1SourceBindingKind.Ambiguous)
                {
                    return BindAmbiguousReceiverMembers(
                        receiverBinding,
                        memberName,
                        use);
                }

                return Dynamic();
            }

            private bool TryGetInstanceReceiverClass(
                SourceExpressionIndex receiverIndex,
                out SourceProgramSymbolIndex classSymbol)
            {
                var receiver = _arena[receiverIndex];
                if (receiver.Kind is Avm1SourceExpressionKind.New &&
                    receiver.Children.Count > 0)
                {
                    var callee = _arena.GetChild(receiver, 0);
                    var binding = BindExpression(callee);
                    if (binding.IsBound &&
                        _program._symbols[binding.Symbol.Value].Kind is
                            Avm1SourceProgramSymbolKind.Class or
                            Avm1SourceProgramSymbolKind.Interface)
                    {
                        classSymbol = binding.Symbol;
                        return true;
                    }
                }

                if (receiver.Kind is Avm1SourceExpressionKind.SymbolReference &&
                    receiver.Symbol.IsValid)
                {
                    var symbol = _arena[receiver.Symbol];
                    if (TryGetNominalClass(symbol.DeclaredType, out classSymbol) ||
                        TryGetNominalClass(symbol.InferredType, out classSymbol))
                    {
                        return true;
                    }
                }

                classSymbol = SourceProgramSymbolIndex.Invalid;
                return false;
            }

            private bool TryGetNominalClass(
                SourceTypeIndex typeIndex,
                out SourceProgramSymbolIndex classSymbol)
            {
                if (typeIndex.IsValid)
                {
                    var type = _arena[typeIndex];
                    if (type.Kind is Avm1SourceTypeKind.Nominal && type.Name.IsValid)
                    {
                        var candidates = _program.FindClasses(_arena[type.Name]);
                        if (candidates.Count == 1)
                        {
                            classSymbol = candidates[0];
                            return true;
                        }
                    }
                }

                classSymbol = SourceProgramSymbolIndex.Invalid;
                return false;
            }

            private bool IsCurrentClassThis(SourceExpressionIndex expressionIndex)
            {
                if (_context.IsStaticContext ||
                    !_scopes.IsRootCodeUnit(expressionIndex))
                {
                    return false;
                }
                var expression = _arena[expressionIndex];
                return expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
                    expression.Symbol.IsValid &&
                    _arena[expression.Symbol].Kind is Avm1SourceSymbolKind.This;
            }

            private bool TryFlattenQualifiedName(
                SourceExpressionIndex expressionIndex,
                [NotNullWhen(true)] out string? qualifiedName)
            {
                var expression = _arena[expressionIndex];
                switch (expression.Kind)
                {
                    case Avm1SourceExpressionKind.DynamicName:
                    case Avm1SourceExpressionKind.QualifiedName:
                        if (!HasKnownScope(expressionIndex) ||
                            _scopes.IsInDynamicScope(expressionIndex))
                            break;
                        qualifiedName = _arena[expression.Name];
                        return true;

                    case Avm1SourceExpressionKind.SymbolReference
                        when expression.Symbol.IsValid &&
                             _arena[expression.Symbol].Kind is Avm1SourceSymbolKind.Global:
                        qualifiedName = _arena[_arena[expression.Symbol].Name];
                        return true;

                    case Avm1SourceExpressionKind.MemberAccess:
                        if (!HasKnownScope(expressionIndex) ||
                            _scopes.IsInDynamicScope(expressionIndex) ||
                            !TryGetMemberName(expression, out var memberName) ||
                            !TryFlattenQualifiedName(
                                _arena.GetChild(expression, 0),
                                out var receiverName))
                        {
                            break;
                        }
                        qualifiedName = receiverName + "." + memberName;
                        return true;
                }

                qualifiedName = null;
                return false;
            }

            private bool HasKnownScope(SourceExpressionIndex expression) =>
                _scopes.GetScope(expression).IsValid;

            private bool TryGetMemberName(
                Avm1SourceExpression expression,
                [NotNullWhen(true)] out string? memberName)
            {
                memberName = null;
                if (expression.Flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember) ||
                    expression.Children.Count != 2)
                {
                    return false;
                }

                var nameExpression = _arena[_arena.GetChild(expression, 1)];
                if (nameExpression.Kind is not Avm1SourceExpressionKind.Literal ||
                    !nameExpression.Literal.IsValid)
                {
                    return false;
                }

                var literal = _arena[nameExpression.Literal];
                if (literal.Kind is not Avm1SourceLiteralKind.String ||
                    !literal.StringValue.IsValid)
                {
                    return false;
                }

                memberName = _arena[literal.StringValue];
                return true;
            }

            private Avm1SourceExpressionBinding FromCandidates(
                List<SourceProgramSymbolIndex> candidates)
            {
                return candidates.Count switch
                {
                    0 => Dynamic(),
                    1 => Bound(candidates[0]),
                    _ => Ambiguous(candidates)
                };
            }

            private Avm1SourceExpressionBinding BindMembers(
                SourceProgramSymbolIndex classSymbol,
                string name,
                bool isStatic,
                Avm1SourceExpressionUse use)
            {
                var first = SourceProgramSymbolIndex.Invalid;
                var candidateStart = -1;
                var candidateCount = 0;
                var current = classSymbol;
                for (var depth = 0;
                    current.IsValid && depth < _program._orderedClassSymbols.Count;
                    depth++)
                {
                    var before = candidateCount;
                    AppendCompatibleMembers(
                        current,
                        name,
                        isStatic,
                        use,
                        ref first,
                        ref candidateStart,
                        ref candidateCount);
                    if (candidateCount != before)
                        break;
                    current = _program.GetBaseClass(current);
                }

                return FinalizeMemberBinding(
                    first,
                    candidateStart,
                    candidateCount,
                    use);
            }

            private Avm1SourceExpressionBinding BindAmbiguousReceiverMembers(
                Avm1SourceExpressionBinding receiverBinding,
                string memberName,
                Avm1SourceExpressionUse use)
            {
                var first = SourceProgramSymbolIndex.Invalid;
                var candidateStart = -1;
                var candidateCount = 0;
                for (var i = 0; i < receiverBinding.Candidates.Count; i++)
                {
                    var receiver = _candidateSymbols[receiverBinding.Candidates.Start + i];
                    if (_program._symbols[receiver.Value].Kind is not (
                        Avm1SourceProgramSymbolKind.Class or
                        Avm1SourceProgramSymbolKind.Interface))
                    {
                        continue;
                    }

                    AppendNearestCompatibleMembers(
                        receiver,
                        memberName,
                        isStatic: true,
                        use,
                        ref first,
                        ref candidateStart,
                        ref candidateCount);
                }

                return FinalizeMemberBinding(
                    first,
                    candidateStart,
                    candidateCount,
                    use);
            }

            private void AppendNearestCompatibleMembers(
                SourceProgramSymbolIndex classSymbol,
                string name,
                bool isStatic,
                Avm1SourceExpressionUse use,
                ref SourceProgramSymbolIndex first,
                ref int candidateStart,
                ref int candidateCount)
            {
                var current = classSymbol;
                for (var depth = 0;
                    current.IsValid && depth < _program._orderedClassSymbols.Count;
                    depth++)
                {
                    var before = candidateCount;
                    AppendCompatibleMembers(
                        current,
                        name,
                        isStatic,
                        use,
                        ref first,
                        ref candidateStart,
                        ref candidateCount);
                    if (candidateCount != before)
                        return;
                    current = _program.GetBaseClass(current);
                }
            }

            private void AppendCompatibleMembers(
                SourceProgramSymbolIndex classSymbol,
                string name,
                bool isStatic,
                Avm1SourceExpressionUse use,
                ref SourceProgramSymbolIndex first,
                ref int candidateStart,
                ref int candidateCount)
            {
                foreach (var candidate in _program.FindDirectMemberCandidates(
                    classSymbol,
                    name))
                {
                    var symbol = _program._symbols[candidate.Value];
                    if (symbol.Kind is Avm1SourceProgramSymbolKind.Constructor ||
                        symbol.Modifiers.HasFlag(
                            Avm1SourceDeclarationModifiers.Static) != isStatic ||
                        !IsCompatibleMemberUse(symbol.Kind, use))
                    {
                        continue;
                    }

                    if (candidateCount == 0)
                    {
                        first = candidate;
                    }
                    else
                    {
                        if (candidateCount == 1)
                        {
                            candidateStart = _candidateSymbols.Count;
                            _candidateSymbols.Add(first);
                        }
                        _candidateSymbols.Add(candidate);
                    }
                    candidateCount++;
                }
            }

            private Avm1SourceExpressionBinding FinalizeMemberBinding(
                SourceProgramSymbolIndex first,
                int candidateStart,
                int candidateCount,
                Avm1SourceExpressionUse use)
            {
                if (IsReadWrite(use) && candidateCount > 0)
                {
                    var hasField = false;
                    var hasGetter = false;
                    var hasSetter = false;
                    for (var i = 0; i < candidateCount; i++)
                    {
                        var candidate = candidateCount == 1
                            ? first
                            : _candidateSymbols[candidateStart + i];
                        switch (_program._symbols[candidate.Value].Kind)
                        {
                            case Avm1SourceProgramSymbolKind.Field:
                                hasField = true;
                                break;
                            case Avm1SourceProgramSymbolKind.Getter:
                                hasGetter = true;
                                break;
                            case Avm1SourceProgramSymbolKind.Setter:
                                hasSetter = true;
                                break;
                        }
                    }

                    if (!hasField && !(hasGetter && hasSetter))
                        return Dynamic();
                }

                return candidateCount switch
                {
                    0 => Dynamic(),
                    1 => Bound(first),
                    _ => new Avm1SourceExpressionBinding(
                        Avm1SourceBindingKind.Ambiguous,
                        SourceProgramSymbolIndex.Invalid,
                        new SourceProgramSymbolList(candidateStart, candidateCount))
                };
            }

            private static bool IsCompatibleMemberUse(
                Avm1SourceProgramSymbolKind kind,
                Avm1SourceExpressionUse use)
            {
                if (use is Avm1SourceExpressionUse.None)
                    use = Avm1SourceExpressionUse.Read;
                if (use.HasFlag(Avm1SourceExpressionUse.Delete))
                    return false;

                var writes = use.HasFlag(Avm1SourceExpressionUse.Write);
                var reads = use.HasFlag(Avm1SourceExpressionUse.Read) ||
                    use.HasFlag(Avm1SourceExpressionUse.Invoke) ||
                    use.HasFlag(Avm1SourceExpressionUse.Construct);
                if (writes && reads)
                {
                    return kind is
                        Avm1SourceProgramSymbolKind.Field or
                        Avm1SourceProgramSymbolKind.Getter or
                        Avm1SourceProgramSymbolKind.Setter;
                }
                if (writes)
                {
                    return kind is
                        Avm1SourceProgramSymbolKind.Field or
                        Avm1SourceProgramSymbolKind.Setter;
                }
                return kind is
                    Avm1SourceProgramSymbolKind.Field or
                    Avm1SourceProgramSymbolKind.Getter or
                    Avm1SourceProgramSymbolKind.Method;
            }

            private static bool IsReadWrite(Avm1SourceExpressionUse use) =>
                use.HasFlag(Avm1SourceExpressionUse.Write) &&
                (use.HasFlag(Avm1SourceExpressionUse.Read) ||
                 use.HasFlag(Avm1SourceExpressionUse.Invoke) ||
                 use.HasFlag(Avm1SourceExpressionUse.Construct));

            private Avm1SourceExpressionBinding Ambiguous(
                List<SourceProgramSymbolIndex> candidates)
            {
                if (candidates.Count == 1)
                    return Bound(candidates[0]);
                if (candidates.Count == 0)
                    return Dynamic();

                var start = _candidateSymbols.Count;
                for (var i = 0; i < candidates.Count; i++)
                    _candidateSymbols.Add(candidates[i]);
                return new Avm1SourceExpressionBinding(
                    Avm1SourceBindingKind.Ambiguous,
                    SourceProgramSymbolIndex.Invalid,
                    new SourceProgramSymbolList(start, candidates.Count));
            }

            private static Avm1SourceExpressionBinding Bound(
                SourceProgramSymbolIndex symbol) =>
                new(
                    Avm1SourceBindingKind.Bound,
                    symbol,
                    SourceProgramSymbolList.Empty);

            private static Avm1SourceExpressionBinding Dynamic() =>
                new(
                    Avm1SourceBindingKind.Dynamic,
                    SourceProgramSymbolIndex.Invalid,
                    SourceProgramSymbolList.Empty);
        }

        private readonly record struct ArenaContext(
            Avm1SourceArena Arena,
            SourceProgramSymbolIndex ContainingClass,
            SourceProgramSymbolIndex ContainingMember,
            bool IsStaticContext,
            SourceStatementIndex StatementRoot,
            SourceExpressionIndex ExpressionRoot,
            SourceSymbolIndex[] RootParameters);

        private readonly record struct ReferenceClassEntry(
            Avm1SourceReferenceClass ReferenceClass,
            SourceProgramSymbolIndex Symbol);

        private readonly record struct InterfaceTypeTables(
            SourceProgramTypeList[] Lists,
            SourceProgramTypeIndex[] Types);
    }

    private sealed record BuildResult(
        Avm1SourceFile[] Files,
        Avm1SourceProgramSymbol[] Symbols,
        Avm1SourceProgramType[] Types,
        Avm1SourceProgramSignature[] Signatures,
        Avm1SourceProgramParameter[] Parameters,
        SourceProgramSymbolIndex[] BaseClasses,
        SourceProgramTypeIndex[] BaseTypes,
        SourceProgramTypeList[] InterfaceTypeLists,
        SourceProgramTypeIndex[] InterfaceTypes,
        Avm1SourceArenaBindings[] ArenaBindings,
        Dictionary<Avm1SourceClass, SourceProgramSymbolIndex> ClassSymbols,
        Dictionary<Avm1SourceClassMember, SourceProgramSymbolIndex> MemberSymbols,
        Dictionary<string, SourceProgramSymbolIndex[]> ClassesByName,
        Dictionary<string, SourceProgramTypeIndex> TypesByName,
        Dictionary<Avm1SourceTypeKind, SourceProgramTypeIndex> BuiltInTypes);
}
