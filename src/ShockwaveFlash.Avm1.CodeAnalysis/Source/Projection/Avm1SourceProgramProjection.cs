namespace ShockwaveFlash.Avm1.Source;

public sealed record Avm1SourceProjectionOptions
{
    public bool SynthesizeImports { get; init; } = true;

    public bool ShortenClassReferences { get; init; } = true;

    public bool ElideThisReferences { get; init; }

    public bool EmitInferredTypes { get; init; }
}

public sealed class Avm1SourceProgramProjection
{
    private readonly Avm1SourceFileProjection[] _files;
    private readonly Dictionary<Avm1SourceFile, Avm1SourceFileProjection> _filesBySource;

    private Avm1SourceProgramProjection(
        Avm1SourceProgram program,
        Avm1SourceProjectionOptions options,
        Avm1SourceFileProjection[] files)
    {
        Program = program;
        Options = options;
        _files = files;
        _filesBySource = new Dictionary<Avm1SourceFile, Avm1SourceFileProjection>(
            ReferenceEqualityComparer.Instance);
        foreach (var file in files)
            _filesBySource.Add(file.SourceFile, file);
    }

    public Avm1SourceProgram Program { get; }

    public Avm1SourceProjectionOptions Options { get; }

    public IReadOnlyList<Avm1SourceFileProjection> Files => _files;

    public static Avm1SourceProgramProjection Create(
        Avm1SourceProgram program,
        Avm1SourceProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        var effectiveOptions = options ?? new Avm1SourceProjectionOptions();
        var files = new Avm1SourceFileProjection[program.Files.Count];
        for (var i = 0; i < files.Length; i++)
        {
            files[i] = FileProjectionBuilder.Build(
                program,
                program.Files[i],
                effectiveOptions);
        }
        return new Avm1SourceProgramProjection(program, effectiveOptions, files);
    }

    public Avm1SourceFileProjection GetFile(Avm1SourceFile sourceFile)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        if (!_filesBySource.TryGetValue(sourceFile, out var projection))
        {
            throw new ArgumentException(
                "The file does not belong to this Source program projection.",
                nameof(sourceFile));
        }
        return projection;
    }

    private static class FileProjectionBuilder
    {
        private static readonly string[] ReservedNames =
        [
            "Array",
            "Boolean",
            "Date",
            "Error",
            "Function",
            "Math",
            "MovieClip",
            "Number",
            "Object",
            "String",
            "Void",
            "XML",
            "XMLNode",
            "break",
            "case",
            "catch",
            "class",
            "continue",
            "default",
            "delete",
            "do",
            "else",
            "extends",
            "false",
            "finally",
            "for",
            "function",
            "get",
            "if",
            "implements",
            "import",
            "in",
            "instanceof",
            "interface",
            "intrinsic",
            "new",
            "null",
            "private",
            "public",
            "return",
            "set",
            "static",
            "super",
            "switch",
            "this",
            "throw",
            "true",
            "try",
            "typeof",
            "undefined",
            "var",
            "while",
            "with"
        ];

        public static Avm1SourceFileProjection Build(
            Avm1SourceProgram program,
            Avm1SourceFile sourceFile,
            Avm1SourceProjectionOptions options)
        {
            var classSymbols = new HashSet<SourceProgramSymbolIndex>();
            var currentClasses = new HashSet<string>(StringComparer.Ordinal);
            var explicitImports = new HashSet<string>(StringComparer.Ordinal);
            var referencedClasses = new HashSet<string>(StringComparer.Ordinal);
            var lexicalBlockers = new HashSet<string>(ReservedNames, StringComparer.Ordinal);
            var ownersBySimpleName = new Dictionary<string, HashSet<string>>(
                StringComparer.Ordinal);
            var typeAnalysis = options.EmitInferredTypes &&
                program.SemanticAnalysis.IsComplete
                ? program.TypeAnalysis
                : null;

            foreach (var sourceClass in sourceFile.Classes)
            {
                if (!program.TryGetSymbol(sourceClass, out var classSymbol))
                {
                    throw new InvalidOperationException(
                        $"Source class {sourceClass.Name.Value} has no program symbol.");
                }

                classSymbols.Add(classSymbol);
                var className = Normalize(sourceClass.Name.Value);
                currentClasses.Add(className);
                AddOwner(ownersBySimpleName, className);
                foreach (var member in sourceClass.Members)
                    lexicalBlockers.Add(member.Name);

                if (sourceClass.BaseType is not null)
                    AddTypeName(sourceClass.BaseType.Value);
                foreach (var interfaceName in sourceClass.Interfaces)
                    AddTypeName(interfaceName.Value);
                foreach (var field in sourceClass.Fields)
                {
                    AddTypeReference(field.DeclaredType);
                    AddInferredMemberType(field, field.DeclaredType);
                }
                foreach (var method in sourceClass.Methods)
                {
                    AddTypeReference(method.DeclaredReturnType);
                    AddInferredMemberType(method, method.DeclaredReturnType);
                }
                if (sourceClass.Constructor is not null)
                    AddTypeReference(sourceClass.Constructor.DeclaredReturnType);
            }

            foreach (var import in sourceFile.Imports)
            {
                var name = Normalize(import.Value);
                explicitImports.Add(name);
                AddOwner(ownersBySimpleName, name);
            }

            var fileArenaBindings = program.ArenaBindings
                .Where(bindings =>
                    classSymbols.Contains(bindings.ContainingClass) &&
                    bindings.ContainingMember.IsValid)
                .ToArray();
            foreach (var arenaBindings in fileArenaBindings)
            {
                var arena = arenaBindings.Arena;
                foreach (var symbol in arena.Symbols)
                {
                    lexicalBlockers.Add(arena[symbol.Name]);
                    AddArenaType(arena, symbol.DeclaredType);
                    if (!symbol.DeclaredType.IsValid &&
                        typeAnalysis is not null &&
                        typeAnalysis.TryGetAnnotation(
                            typeAnalysis.GetSymbolFact(arena, symbol.Index),
                            out var inferredType))
                    {
                        AddTypeReference(inferredType);
                    }
                }

                if (typeAnalysis is not null)
                {
                    foreach (var function in arena.Functions)
                    {
                        if (typeAnalysis.TryGetAnnotation(
                                typeAnalysis.GetFunctionReturnFact(arena, function.Index),
                                out var inferredReturnType))
                        {
                            AddTypeReference(inferredReturnType);
                        }
                    }
                }

                foreach (var expression in arena.Expressions)
                {
                    var binding = arenaBindings[expression.Index];
                    if (expression.Kind is Avm1SourceExpressionKind.DynamicName &&
                        expression.Name.IsValid &&
                        binding.Kind is not Avm1SourceBindingKind.Bound)
                    {
                        lexicalBlockers.Add(arena[expression.Name]);
                    }

                    if (binding.Kind is not Avm1SourceBindingKind.Bound)
                        continue;
                    var symbol = program[binding.Symbol];
                    if (symbol.Kind is not (
                            Avm1SourceProgramSymbolKind.Class or
                            Avm1SourceProgramSymbolKind.Interface) ||
                        !IsSafeClassSpellingUse(arenaBindings.GetUse(expression.Index)))
                        continue;
                    AddReferencedClass(symbol.QualifiedName.Value);
                }
            }

            var simpleNames = new HashSet<string>(StringComparer.Ordinal);
            if (options.ShortenClassReferences)
            {
                foreach (var (simpleName, owners) in ownersBySimpleName)
                {
                    if (owners.Count != 1 || lexicalBlockers.Contains(simpleName))
                        continue;

                    var owner = owners.Single();
                    if (currentClasses.Contains(owner) ||
                        explicitImports.Contains(owner) ||
                        (options.SynthesizeImports && referencedClasses.Contains(owner)))
                    {
                        simpleNames.Add(owner);
                    }
                }
            }

            var imports = new List<Avm1SourceQualifiedName>(sourceFile.Imports.Count + 8);
            imports.AddRange(sourceFile.Imports);
            if (options.ShortenClassReferences && options.SynthesizeImports)
            {
                var existingImportNames = new HashSet<string>(explicitImports, StringComparer.Ordinal);
                foreach (var name in simpleNames
                             .Where(name =>
                                 referencedClasses.Contains(name) &&
                                 !currentClasses.Contains(name) &&
                                 GetNamespaceName(name).Length != 0 &&
                                 !existingImportNames.Contains(name))
                             .OrderBy(name => name, StringComparer.Ordinal))
                {
                    imports.Add(new Avm1SourceQualifiedName(name));
                    existingImportNames.Add(name);
                }
            }

            var thisElisionFileBlockers = new HashSet<string>(
                ReservedNames,
                StringComparer.Ordinal);
            foreach (var currentClass in currentClasses)
                thisElisionFileBlockers.Add(GetSimpleName(currentClass));
            foreach (var import in imports)
                thisElisionFileBlockers.Add(import.SimpleName);

            var arenaProjections = new Dictionary<Avm1SourceArena, string?[]>(
                ReferenceEqualityComparer.Instance);
            var thisElisionCount = 0;
            if (options.ShortenClassReferences || options.ElideThisReferences)
            {
                foreach (var arenaBindings in fileArenaBindings)
                {
                    string?[]? spellings = null;
                    var localBlockers = BuildLocalBlockers(arenaBindings.Arena);
                    var evalCodeUnits = FindEvalCodeUnits(
                        arenaBindings.Arena,
                        arenaBindings.ScopeAnalysis);

                    for (var i = 0; i < arenaBindings.Bindings.Count; i++)
                    {
                        var expressionIndex = new SourceExpressionIndex(i);
                        var binding = arenaBindings.Bindings[i];
                        if (binding.Kind is not Avm1SourceBindingKind.Bound)
                            continue;
                        var symbol = program[binding.Symbol];
                        if (options.ShortenClassReferences &&
                            (symbol.Kind is
                                Avm1SourceProgramSymbolKind.Class or
                                Avm1SourceProgramSymbolKind.Interface) &&
                            IsSafeClassSpellingUse(arenaBindings.GetUse(
                                expressionIndex)))
                        {
                            var qualifiedName = Normalize(symbol.QualifiedName.Value);
                            var spelling = simpleNames.Contains(qualifiedName)
                                ? GetSimpleName(qualifiedName)
                                : qualifiedName;
                            spellings ??= new string?[arenaBindings.Bindings.Count];
                            spellings[i] = spelling;
                            continue;
                        }

                        if (!options.ElideThisReferences ||
                            !arenaBindings.ScopeAnalysis.IsComplete ||
                            evalCodeUnits.Contains(
                                arenaBindings.ScopeAnalysis.GetCodeUnit(expressionIndex)) ||
                            arenaBindings.IsInDynamicScope(expressionIndex) ||
                            !IsSafeThisElision(
                                symbol.Kind,
                                arenaBindings.GetUse(expressionIndex)) ||
                            thisElisionFileBlockers.Contains(symbol.Name) ||
                            HasVisibleLocalBlocker(
                                arenaBindings,
                                localBlockers,
                                expressionIndex,
                                symbol.Name) ||
                            !TryGetThisMemberName(
                                arenaBindings.Arena,
                                expressionIndex,
                                out var memberName) ||
                            !string.Equals(memberName, symbol.Name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        spellings ??= new string?[arenaBindings.Bindings.Count];
                        spellings[i] = memberName;
                        thisElisionCount++;
                    }

                    if (spellings is not null)
                        arenaProjections.Add(arenaBindings.Arena, spellings);
                }
            }

            return new Avm1SourceFileProjection(
                sourceFile,
                options,
                imports.ToArray(),
                simpleNames,
                arenaProjections,
                thisElisionCount,
                typeAnalysis);

            void AddInferredMemberType(
                Avm1SourceClassMember member,
                Avm1SourceTypeReference? declaredType)
            {
                if (declaredType is not null ||
                    typeAnalysis is null ||
                    !program.TryGetSymbol(member, out var memberSymbol) ||
                    !typeAnalysis.TryGetAnnotation(
                        typeAnalysis[memberSymbol],
                        out var inferredType))
                {
                    return;
                }
                AddTypeReference(inferredType);
            }

            void AddTypeReference(Avm1SourceTypeReference? type)
            {
                if (type is not null)
                    AddTypeName(type.Name.Value);
            }

            void AddArenaType(Avm1SourceArena arena, SourceTypeIndex typeIndex)
            {
                if (!typeIndex.IsValid)
                    return;
                var type = arena[typeIndex];
                if (type.Kind is Avm1SourceTypeKind.Nominal && type.Name.IsValid)
                    AddTypeName(arena[type.Name]);
            }

            void AddTypeName(string name)
            {
                var normalized = Normalize(name);
                if (IsBuiltInTypeName(normalized))
                    return;
                AddReferencedClass(normalized);
            }

            void AddReferencedClass(string name)
            {
                var normalized = Normalize(name);
                referencedClasses.Add(normalized);
                AddOwner(ownersBySimpleName, normalized);
            }
        }

        private static void AddOwner(
            Dictionary<string, HashSet<string>> ownersBySimpleName,
            string qualifiedName)
        {
            var simpleName = GetSimpleName(qualifiedName);
            if (!ownersBySimpleName.TryGetValue(simpleName, out var owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                ownersBySimpleName.Add(simpleName, owners);
            }
            owners.Add(qualifiedName);
        }

        private static bool IsBuiltInTypeName(string name) => name is
            "Boolean" or
            "Number" or
            "String" or
            "Object" or
            "Array" or
            "Function" or
            "Void" or
            "void";

        private static Dictionary<string, List<SourceSymbolIndex>> BuildLocalBlockers(
            Avm1SourceArena arena)
        {
            var result = new Dictionary<string, List<SourceSymbolIndex>>(
                StringComparer.Ordinal);
            foreach (var symbol in arena.Symbols)
            {
                if (symbol.Kind is
                    Avm1SourceSymbolKind.This or Avm1SourceSymbolKind.Super)
                {
                    continue;
                }
                var name = arena[symbol.Name];
                if (!result.TryGetValue(name, out var symbols))
                {
                    symbols = [];
                    result.Add(name, symbols);
                }
                symbols.Add(symbol.Index);
            }
            return result;
        }

        private static bool HasVisibleLocalBlocker(
            Avm1SourceArenaBindings bindings,
            Dictionary<string, List<SourceSymbolIndex>> localBlockers,
            SourceExpressionIndex expression,
            string name)
        {
            if (!localBlockers.TryGetValue(name, out var symbols))
                return false;
            foreach (var symbol in symbols)
            {
                if (bindings.ScopeAnalysis.IsSymbolVisibleAt(symbol, expression))
                    return true;
            }
            return false;
        }

        private static HashSet<SourceCodeUnitIndex> FindEvalCodeUnits(
            Avm1SourceArena arena,
            Avm1SourceScopeAnalysis scopes)
        {
            var result = new HashSet<SourceCodeUnitIndex>();
            foreach (var expression in arena.Expressions)
            {
                if (!Avm1SourceEvalAnalysis.IsEvalExpression(
                    arena,
                    expression.Index))
                    continue;

                var codeUnit = scopes.GetCodeUnit(expression.Index);
                if (codeUnit.IsValid)
                    result.Add(codeUnit);
            }
            return result;
        }

        private static bool IsSafeThisElision(
            Avm1SourceProgramSymbolKind symbolKind,
            Avm1SourceExpressionUse use)
        {
            if (use.HasFlag(Avm1SourceExpressionUse.Delete) ||
                use.HasFlag(Avm1SourceExpressionUse.Construct))
            {
                return false;
            }
            if (use.HasFlag(Avm1SourceExpressionUse.Invoke))
                return symbolKind is Avm1SourceProgramSymbolKind.Method;
            return symbolKind is
                Avm1SourceProgramSymbolKind.Field or
                Avm1SourceProgramSymbolKind.Method or
                Avm1SourceProgramSymbolKind.Getter or
                Avm1SourceProgramSymbolKind.Setter;
        }

        private static bool IsSafeClassSpellingUse(Avm1SourceExpressionUse use) =>
            !use.HasFlag(Avm1SourceExpressionUse.Write) &&
            !use.HasFlag(Avm1SourceExpressionUse.Delete);

        private static bool TryGetThisMemberName(
            Avm1SourceArena arena,
            SourceExpressionIndex expressionIndex,
            out string memberName)
        {
            memberName = string.Empty;
            var expression = arena[expressionIndex];
            if (expression.Kind is not Avm1SourceExpressionKind.MemberAccess ||
                expression.Flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember) ||
                expression.Children.Count != 2)
            {
                return false;
            }

            var receiver = arena[arena.GetChild(expression, 0)];
            if (receiver.Kind is not Avm1SourceExpressionKind.SymbolReference ||
                !receiver.Symbol.IsValid ||
                arena[receiver.Symbol].Kind is not Avm1SourceSymbolKind.This)
            {
                return false;
            }

            var nameExpression = arena[arena.GetChild(expression, 1)];
            if (nameExpression.Kind is not Avm1SourceExpressionKind.Literal ||
                !nameExpression.Literal.IsValid)
            {
                return false;
            }

            var literal = arena[nameExpression.Literal];
            if (literal.Kind is not Avm1SourceLiteralKind.String ||
                !literal.StringValue.IsValid)
            {
                return false;
            }

            memberName = arena[literal.StringValue];
            return true;
        }

        private static string Normalize(string name) =>
            Avm1SourceProgram.NormalizeRuntimeQualifiedName(name);

        private static string GetSimpleName(string name)
        {
            var separator = name.LastIndexOf('.');
            return separator >= 0 ? name[(separator + 1)..] : name;
        }

        private static string GetNamespaceName(string name)
        {
            var separator = name.LastIndexOf('.');
            return separator >= 0 ? name[..separator] : string.Empty;
        }
    }
}

public sealed class Avm1SourceFileProjection
{
    private readonly Avm1SourceQualifiedName[] _imports;
    private readonly HashSet<string> _simpleNames;
    private readonly Dictionary<Avm1SourceArena, string?[]> _expressionSpellings;
    private readonly Avm1SourceTypeAnalysis? _typeAnalysis;

    internal Avm1SourceFileProjection(
        Avm1SourceFile sourceFile,
        Avm1SourceProjectionOptions options,
        Avm1SourceQualifiedName[] imports,
        HashSet<string> simpleNames,
        Dictionary<Avm1SourceArena, string?[]> expressionSpellings,
        int thisElisionCount,
        Avm1SourceTypeAnalysis? typeAnalysis)
    {
        SourceFile = sourceFile;
        Options = options;
        _imports = imports;
        _simpleNames = simpleNames;
        _expressionSpellings = expressionSpellings;
        _typeAnalysis = typeAnalysis;
        ThisElisionCount = thisElisionCount;
    }

    public Avm1SourceFile SourceFile { get; }

    public Avm1SourceProjectionOptions Options { get; }

    public IReadOnlyList<Avm1SourceQualifiedName> Imports => _imports;

    public int ExpressionSpellingCount => _expressionSpellings.Values.Sum(spellings =>
        spellings.Count(spelling => spelling is not null));

    public int ThisElisionCount { get; }

    public bool TryGetInferredType(
        Avm1SourceClassMember member,
        out Avm1SourceTypeReference? type)
    {
        ArgumentNullException.ThrowIfNull(member);
        type = null;
        if (_typeAnalysis is null ||
            !_typeAnalysis.Program.TryGetSymbol(member, out var symbol))
        {
            return false;
        }

        var fact = _typeAnalysis[symbol];
        return (member is not Avm1SourceField || !IsVoid(fact)) &&
            _typeAnalysis.TryGetAnnotation(fact, out type);
    }

    public bool TryGetInferredType(
        Avm1SourceArena arena,
        SourceSymbolIndex symbol,
        out Avm1SourceTypeReference? type)
    {
        ArgumentNullException.ThrowIfNull(arena);
        type = null;
        if (_typeAnalysis is null)
            return false;

        var fact = _typeAnalysis.GetSymbolFact(arena, symbol);
        return !IsVoid(fact) &&
            _typeAnalysis.TryGetAnnotation(fact, out type);
    }

    public bool TryGetInferredReturnType(
        Avm1SourceArena arena,
        SourceFunctionIndex function,
        out Avm1SourceTypeReference? type)
    {
        ArgumentNullException.ThrowIfNull(arena);
        type = null;
        return _typeAnalysis is not null &&
            _typeAnalysis.TryGetAnnotation(
                _typeAnalysis.GetFunctionReturnFact(arena, function),
                out type);
    }

    private bool IsVoid(Avm1SourceTypeFact fact) =>
        _typeAnalysis is not null &&
        fact.Type.IsValid &&
        _typeAnalysis.Program[fact.Type].Kind is Avm1SourceTypeKind.Void;

    public bool TryGetExpressionSpelling(
        Avm1SourceArena arena,
        SourceExpressionIndex expression,
        out string spelling)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (_expressionSpellings.TryGetValue(arena, out var spellings) &&
            expression.Value >= 0 &&
            expression.Value < spellings.Length &&
            spellings[expression.Value] is { } selected)
        {
            spelling = selected;
            return true;
        }

        spelling = string.Empty;
        return false;
    }

    public string GetTypeSpelling(Avm1SourceQualifiedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = Avm1SourceProgram.NormalizeRuntimeQualifiedName(name.Value);
        if (_simpleNames.Contains(normalized))
            return new Avm1SourceQualifiedName(normalized).SimpleName;
        return normalized;
    }

    public void WriteAs2(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new Emit.Avm1SourceFileAs2Emitter(writer, this).Write(SourceFile);
    }

    public string GetAs2Text()
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        WriteAs2(writer);
        return writer.ToString();
    }
}
