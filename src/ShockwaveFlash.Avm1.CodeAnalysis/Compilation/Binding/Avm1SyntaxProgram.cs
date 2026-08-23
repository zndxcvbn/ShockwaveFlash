using System.Text;
using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Binding;

public sealed class Avm1SyntaxProgram
{
    private static readonly Avm1SyntaxProgramSymbolIndex[] NoSymbols = [];

    private readonly Avm1SyntaxSourceFile[] _sources;
    private readonly Avm1SyntaxProgramFile[] _files;
    private readonly string[] _names;
    private readonly Avm1SyntaxProgramSymbol[] _symbols;
    private readonly Avm1SyntaxProgramSymbolIndex[] _symbolChildren;
    private readonly Avm1SyntaxImport[] _imports;
    private readonly Avm1SyntaxTypeUse[] _typeUses;
    private readonly Avm1SyntaxBindingDiagnostic[] _diagnostics;
    private readonly Dictionary<string, Avm1SyntaxProgramSymbolIndex[]>
        _typesByQualifiedName;
    private readonly Dictionary<MemberKey, Avm1SyntaxProgramSymbolIndex[]>
        _membersByName;

    private Avm1SyntaxProgram(
        Avm1SyntaxSourceFile[] sources,
        Avm1SyntaxProgramFile[] files,
        string[] names,
        Avm1SyntaxProgramSymbol[] symbols,
        Avm1SyntaxProgramSymbolIndex[] symbolChildren,
        Avm1SyntaxImport[] imports,
        Avm1SyntaxTypeUse[] typeUses,
        Avm1SyntaxBindingDiagnostic[] diagnostics,
        Dictionary<string, Avm1SyntaxProgramSymbolIndex[]> typesByQualifiedName,
        Dictionary<MemberKey, Avm1SyntaxProgramSymbolIndex[]> membersByName)
    {
        _sources = sources;
        _files = files;
        _names = names;
        _symbols = symbols;
        _symbolChildren = symbolChildren;
        _imports = imports;
        _typeUses = typeUses;
        _diagnostics = diagnostics;
        _typesByQualifiedName = typesByQualifiedName;
        _membersByName = membersByName;
    }

    public IReadOnlyList<Avm1SyntaxSourceFile> Sources => _sources;

    public IReadOnlyList<Avm1SyntaxProgramFile> Files => _files;

    public IReadOnlyList<Avm1SyntaxProgramSymbol> Symbols => _symbols;

    public IReadOnlyList<Avm1SyntaxImport> Imports => _imports;

    public IReadOnlyList<Avm1SyntaxTypeUse> TypeUses => _typeUses;

    public IReadOnlyList<Avm1SyntaxBindingDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic =>
        diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    public static Avm1SyntaxProgram Create(
        IEnumerable<Avm1SyntaxSourceFile> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        return new Builder(sourceFiles).Build();
    }

    public string GetName(Avm1SyntaxNameIndex name)
    {
        ValidateName(name);
        return _names[name.Value];
    }

    public Avm1SyntaxSourceFile GetSource(Avm1SyntaxFileIndex file)
    {
        ValidateFile(file);
        return _sources[file.Value];
    }

    public string GetPath(Avm1SyntaxFileIndex file)
    {
        ValidateFile(file);
        return GetName(_files[file.Value].Path);
    }

    public Avm1SyntaxProgramSymbol GetSymbol(
        Avm1SyntaxProgramSymbolIndex symbol)
    {
        ValidateSymbol(symbol);
        return _symbols[symbol.Value];
    }

    public ReadOnlySpan<Avm1SyntaxProgramSymbolIndex> GetTypes(
        Avm1SyntaxFileIndex file)
    {
        ValidateFile(file);
        var range = _files[file.Value].Types;
        return _symbolChildren.AsSpan(range.Start, range.Count);
    }

    public ReadOnlySpan<Avm1SyntaxProgramSymbolIndex> GetMembers(
        Avm1SyntaxProgramSymbolIndex type)
    {
        ValidateType(type);
        var range = _symbols[type.Value].Members;
        return _symbolChildren.AsSpan(range.Start, range.Count);
    }

    public ReadOnlySpan<Avm1SyntaxImport> GetImports(Avm1SyntaxFileIndex file)
    {
        ValidateFile(file);
        var range = _files[file.Value].Imports;
        return _imports.AsSpan(range.Start, range.Count);
    }

    public ReadOnlySpan<Avm1SyntaxTypeUse> GetTypeUses(
        Avm1SyntaxFileIndex file)
    {
        ValidateFile(file);
        var range = _files[file.Value].TypeUses;
        return _typeUses.AsSpan(range.Start, range.Count);
    }

    public IReadOnlyList<Avm1SyntaxProgramSymbolIndex> GetDeclaredTypes(
        string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        return _typesByQualifiedName.TryGetValue(
            qualifiedName.Trim(),
            out var symbols)
            ? symbols
            : NoSymbols;
    }

    public bool TryGetType(
        string qualifiedName,
        out Avm1SyntaxProgramSymbolIndex symbol)
    {
        var candidates = GetDeclaredTypes(qualifiedName);
        if (candidates.Count == 1)
        {
            symbol = candidates[0];
            return true;
        }

        symbol = Avm1SyntaxProgramSymbolIndex.Invalid;
        return false;
    }

    public IReadOnlyList<Avm1SyntaxProgramSymbolIndex> GetMemberCandidates(
        Avm1SyntaxProgramSymbolIndex containingType,
        string name)
    {
        ValidateType(containingType);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _membersByName.TryGetValue(
            new MemberKey(containingType.Value, name),
            out var symbols)
            ? symbols
            : NoSymbols;
    }

    public Avm1SyntaxSymbolResolution ResolveType(
        Avm1SyntaxFileIndex file,
        Avm1SyntaxProgramSymbolIndex containingType,
        string name)
    {
        var candidates = GetTypeCandidates(file, containingType, name);
        return candidates.Count switch
        {
            0 => Avm1SyntaxSymbolResolution.Unresolved,
            1 => Avm1SyntaxSymbolResolution.Bound(candidates[0]),
            _ => Avm1SyntaxSymbolResolution.Ambiguous
        };
    }

    public IReadOnlyList<Avm1SyntaxProgramSymbolIndex> GetTypeCandidates(
        Avm1SyntaxFileIndex file,
        Avm1SyntaxProgramSymbolIndex containingType,
        string name)
    {
        ValidateFile(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (containingType.IsValid)
        {
            ValidateType(containingType);
            if (_symbols[containingType.Value].Origin.File != file)
            {
                throw new ArgumentException(
                    "The containing type does not belong to the supplied file.",
                    nameof(containingType));
            }
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length == 0 || normalizedName.Contains('*'))
            return NoSymbols;

        var candidates = new List<Avm1SyntaxProgramSymbolIndex>(4);
        if (normalizedName.Contains('.'))
        {
            AddDeclaredTypes(normalizedName, candidates);
            return candidates.Count == 0 ? NoSymbols : candidates.ToArray();
        }

        foreach (var import in GetImports(file))
        {
            if (import.Kind is not Avm1SyntaxImportKind.Explicit ||
                !StringComparer.Ordinal.Equals(
                    GetName(import.SimpleName),
                    normalizedName))
            {
                continue;
            }

            AddDeclaredTypes(GetName(import.Name), candidates);
        }

        if (containingType.IsValid)
        {
            var containingName = GetName(
                _symbols[containingType.Value].QualifiedName);
            var namespaceName = GetNamespaceName(containingName);
            AddDeclaredTypes(
                Qualify(namespaceName, normalizedName),
                candidates);
        }

        foreach (var import in GetImports(file))
        {
            if (import.Kind is Avm1SyntaxImportKind.Wildcard)
            {
                AddDeclaredTypes(
                    Qualify(GetName(import.NamespaceName), normalizedName),
                    candidates);
            }
        }

        AddDeclaredTypes(normalizedName, candidates);
        return candidates.Count == 0 ? NoSymbols : candidates.ToArray();
    }

    public Avm1TypeDeclarationSyntax GetTypeDeclaration(
        Avm1SyntaxProgramSymbolIndex type)
    {
        ValidateType(type);
        var origin = _symbols[type.Value].Origin;
        return _sources[origin.File.Value].SyntaxTree.Types[origin.TypeOrdinal];
    }

    public Avm1MemberDeclarationSyntax GetMemberDeclaration(
        Avm1SyntaxProgramSymbolIndex member)
    {
        ValidateMember(member);
        var origin = _symbols[member.Value].Origin;
        var tree = _sources[origin.File.Value].SyntaxTree;
        var type = tree.Types[origin.TypeOrdinal];
        return tree.GetMembers(type)[origin.MemberOrdinal];
    }

    public Avm1VariableDeclaratorSyntax GetFieldDeclarator(
        Avm1SyntaxProgramSymbolIndex field)
    {
        ValidateSymbol(field);
        if (_symbols[field.Value].Kind is not Avm1SyntaxProgramSymbolKind.Field)
            throw new ArgumentException("The symbol is not a field.", nameof(field));

        var origin = _symbols[field.Value].Origin;
        var tree = _sources[origin.File.Value].SyntaxTree;
        var type = tree.Types[origin.TypeOrdinal];
        var member = tree.GetMembers(type)[origin.MemberOrdinal];
        return tree.GetDeclarators(member)[origin.DeclaratorOrdinal];
    }

    private void AddDeclaredTypes(
        string qualifiedName,
        List<Avm1SyntaxProgramSymbolIndex> destination)
    {
        if (!_typesByQualifiedName.TryGetValue(qualifiedName, out var symbols))
            return;

        foreach (var symbol in symbols)
        {
            if (!destination.Contains(symbol))
                destination.Add(symbol);
        }
    }

    private void ValidateFile(Avm1SyntaxFileIndex file)
    {
        if (!file.IsValid || file.Value >= _files.Length)
            throw new ArgumentOutOfRangeException(nameof(file));
    }

    private void ValidateName(Avm1SyntaxNameIndex name)
    {
        if (!name.IsValid || name.Value >= _names.Length)
            throw new ArgumentOutOfRangeException(nameof(name));
    }

    private void ValidateSymbol(Avm1SyntaxProgramSymbolIndex symbol)
    {
        if (!symbol.IsValid || symbol.Value >= _symbols.Length)
            throw new ArgumentOutOfRangeException(nameof(symbol));
    }

    private void ValidateType(Avm1SyntaxProgramSymbolIndex symbol)
    {
        ValidateSymbol(symbol);
        if (_symbols[symbol.Value].Kind is not (
            Avm1SyntaxProgramSymbolKind.Class or
            Avm1SyntaxProgramSymbolKind.Interface))
        {
            throw new ArgumentException("The symbol is not a type.", nameof(symbol));
        }
    }

    private void ValidateMember(Avm1SyntaxProgramSymbolIndex symbol)
    {
        ValidateSymbol(symbol);
        if (_symbols[symbol.Value].ContainingType.IsValid is false)
            throw new ArgumentException("The symbol is not a member.", nameof(symbol));
    }

    private static string GetNamespaceName(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0 ? string.Empty : qualifiedName[..separator];
    }

    private static string GetSimpleName(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0 ? qualifiedName : qualifiedName[(separator + 1)..];
    }

    private static string Qualify(string namespaceName, string simpleName) =>
        namespaceName.Length == 0
            ? simpleName
            : namespaceName + "." + simpleName;

    private readonly record struct MemberKey(int ContainingType, string Name);

    private sealed class Builder
    {
        private readonly Avm1SyntaxSourceFile[] _sources;
        private readonly List<Avm1SyntaxProgramFile> _files = [];
        private readonly List<string> _names = [];
        private readonly Dictionary<string, Avm1SyntaxNameIndex> _nameIndexes =
            new(StringComparer.Ordinal);
        private readonly List<Avm1SyntaxProgramSymbol> _symbols = [];
        private readonly List<Avm1SyntaxProgramSymbolIndex> _symbolChildren = [];
        private readonly List<Avm1SyntaxImport> _imports = [];
        private readonly List<Avm1SyntaxTypeUse> _typeUses = [];
        private readonly List<Avm1SyntaxBindingDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, List<Avm1SyntaxProgramSymbolIndex>>
            _typesByQualifiedName = new(StringComparer.Ordinal);
        private readonly Dictionary<MemberKey, List<Avm1SyntaxProgramSymbolIndex>>
            _membersByName = [];
        private readonly Dictionary<string, Avm1SyntaxFileIndex> _filesByPath =
            new(StringComparer.OrdinalIgnoreCase);

        public Builder(IEnumerable<Avm1SyntaxSourceFile> sourceFiles)
        {
            _sources = sourceFiles.ToArray();
            if (_sources.Any(source => source is null))
            {
                throw new ArgumentException(
                    "A syntax program cannot contain a null source file.",
                    nameof(sourceFiles));
            }
        }

        public Avm1SyntaxProgram Build()
        {
            AddDeclarations();
            var typesByName = Freeze(_typesByQualifiedName);
            var membersByName = Freeze(_membersByName);
            var lookupProgram = CreateProgram(
                typesByName,
                membersByName,
                []);
            ResolveImports(lookupProgram);
            BuildTypeUses(lookupProgram);
            return CreateProgram(
                typesByName,
                membersByName,
                _diagnostics.ToArray());
        }

        private void AddDeclarations()
        {
            for (var fileOrdinal = 0; fileOrdinal < _sources.Length; fileOrdinal++)
            {
                var source = _sources[fileOrdinal];
                var file = new Avm1SyntaxFileIndex(fileOrdinal);
                if (!_filesByPath.TryAdd(source.Path, file))
                {
                    AddDiagnostic(
                        "AVM1B1001",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        Avm1TextSpan.Empty,
                        $"Source path '{source.Path}' is included more than once.");
                }

                var importStart = _imports.Count;
                AddImports(file, source.SyntaxTree);

                var fileTypes = new List<Avm1SyntaxProgramSymbolIndex>(
                    source.SyntaxTree.Types.Count);
                for (var typeOrdinal = 0;
                     typeOrdinal < source.SyntaxTree.Types.Count;
                     typeOrdinal++)
                {
                    var type = source.SyntaxTree.Types[typeOrdinal];
                    var qualifiedName = GetCanonicalName(source.SyntaxTree, type.Name);
                    if (qualifiedName.Length == 0)
                        continue;

                    var typeIndex = AddType(
                        file,
                        typeOrdinal,
                        source.SyntaxTree,
                        type,
                        qualifiedName);
                    fileTypes.Add(typeIndex);
                }

                var typeStart = _symbolChildren.Count;
                _symbolChildren.AddRange(fileTypes);
                _files.Add(new Avm1SyntaxProgramFile(
                    file,
                    Intern(source.Path),
                    new Avm1SyntaxImportList(
                        importStart,
                        _imports.Count - importStart),
                    new Avm1SyntaxSymbolList(
                        typeStart,
                        fileTypes.Count),
                    Avm1SyntaxTypeUseList.Empty));
            }
        }

        private void AddImports(Avm1SyntaxFileIndex file, Avm1SyntaxTree tree)
        {
            var explicitImports = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (var directive in tree.Imports)
            {
                var name = GetCanonicalName(tree, directive.Name);
                var wildcard = name.EndsWith(".*", StringComparison.Ordinal);
                var namespaceName = wildcard
                    ? name[..^2]
                    : GetNamespaceName(name);
                var simpleName = wildcard ? "*" : GetSimpleName(name);
                _imports.Add(new Avm1SyntaxImport(
                    file,
                    wildcard
                        ? Avm1SyntaxImportKind.Wildcard
                        : Avm1SyntaxImportKind.Explicit,
                    Intern(name),
                    Intern(namespaceName),
                    Intern(simpleName),
                    directive.Span,
                    Avm1SyntaxSymbolResolution.NotApplicable));

                if (wildcard || name.Length == 0)
                    continue;

                if (explicitImports.TryGetValue(simpleName, out var previous) &&
                    !StringComparer.Ordinal.Equals(previous, name))
                {
                    AddDiagnostic(
                        "AVM1B1004",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        directive.Span,
                        $"Imports '{previous}' and '{name}' introduce the same " +
                        $"simple name '{simpleName}'.");
                }
                else
                {
                    explicitImports.TryAdd(simpleName, name);
                }
            }
        }

        private Avm1SyntaxProgramSymbolIndex AddType(
            Avm1SyntaxFileIndex file,
            int typeOrdinal,
            Avm1SyntaxTree tree,
            Avm1TypeDeclarationSyntax declaration,
            string qualifiedName)
        {
            var index = new Avm1SyntaxProgramSymbolIndex(_symbols.Count);
            var simpleName = GetSimpleName(qualifiedName);
            _symbols.Add(new Avm1SyntaxProgramSymbol(
                index,
                declaration.Kind is Avm1TypeDeclarationKind.Class
                    ? Avm1SyntaxProgramSymbolKind.Class
                    : Avm1SyntaxProgramSymbolKind.Interface,
                Intern(simpleName),
                Intern(qualifiedName),
                Avm1SyntaxProgramSymbolIndex.Invalid,
                declaration.Modifiers,
                new Avm1SyntaxDeclarationOrigin(file, typeOrdinal, -1, -1),
                declaration.Span,
                Avm1SyntaxSymbolList.Empty));

            if (!_typesByQualifiedName.TryGetValue(qualifiedName, out var matches))
            {
                matches = [];
                _typesByQualifiedName.Add(qualifiedName, matches);
            }
            else if (matches.Count > 0)
            {
                AddDiagnostic(
                    "AVM1B1002",
                    Avm1CompilationDiagnosticSeverity.Error,
                    file,
                    declaration.Name.Span,
                    $"Type '{qualifiedName}' is declared more than once.");
            }
            matches.Add(index);

            var memberStart = _symbolChildren.Count;
            var members = tree.GetMembers(declaration);
            for (var memberOrdinal = 0;
                 memberOrdinal < members.Length;
                 memberOrdinal++)
            {
                AddMember(
                    file,
                    typeOrdinal,
                    memberOrdinal,
                    tree,
                    members[memberOrdinal],
                    index,
                    qualifiedName);
            }

            _symbols[index.Value] = _symbols[index.Value] with
            {
                Members = new Avm1SyntaxSymbolList(
                    memberStart,
                    _symbolChildren.Count - memberStart)
            };
            return index;
        }

        private void AddMember(
            Avm1SyntaxFileIndex file,
            int typeOrdinal,
            int memberOrdinal,
            Avm1SyntaxTree tree,
            Avm1MemberDeclarationSyntax declaration,
            Avm1SyntaxProgramSymbolIndex containingType,
            string containingName)
        {
            if (declaration.Kind is Avm1MemberDeclarationKind.Field)
            {
                var declarators = tree.GetDeclarators(declaration);
                for (var declaratorOrdinal = 0;
                     declaratorOrdinal < declarators.Length;
                     declaratorOrdinal++)
                {
                    var declarator = declarators[declaratorOrdinal];
                    if (!declarator.NameToken.IsValid)
                        continue;
                    AddMemberSymbol(
                        file,
                        typeOrdinal,
                        memberOrdinal,
                        declaratorOrdinal,
                        tree.GetTokenText(declarator.NameToken).ToString(),
                        Avm1SyntaxProgramSymbolKind.Field,
                        declaration.Modifiers,
                        declarator.Span,
                        containingType,
                        containingName);
                }
                return;
            }

            var kind = declaration.Kind switch
            {
                Avm1MemberDeclarationKind.Constructor =>
                    Avm1SyntaxProgramSymbolKind.Constructor,
                Avm1MemberDeclarationKind.Method =>
                    Avm1SyntaxProgramSymbolKind.Method,
                Avm1MemberDeclarationKind.Getter =>
                    Avm1SyntaxProgramSymbolKind.Getter,
                Avm1MemberDeclarationKind.Setter =>
                    Avm1SyntaxProgramSymbolKind.Setter,
                _ => (Avm1SyntaxProgramSymbolKind?)null
            };
            if (!kind.HasValue || !declaration.NameToken.IsValid)
                return;

            AddMemberSymbol(
                file,
                typeOrdinal,
                memberOrdinal,
                -1,
                tree.GetTokenText(declaration.NameToken).ToString(),
                kind.Value,
                declaration.Modifiers,
                declaration.Span,
                containingType,
                containingName);
        }

        private void AddMemberSymbol(
            Avm1SyntaxFileIndex file,
            int typeOrdinal,
            int memberOrdinal,
            int declaratorOrdinal,
            string name,
            Avm1SyntaxProgramSymbolKind kind,
            Avm1SyntaxModifiers modifiers,
            Avm1TextSpan span,
            Avm1SyntaxProgramSymbolIndex containingType,
            string containingName)
        {
            var index = new Avm1SyntaxProgramSymbolIndex(_symbols.Count);
            _symbols.Add(new Avm1SyntaxProgramSymbol(
                index,
                kind,
                Intern(name),
                Intern(Qualify(containingName, name)),
                containingType,
                modifiers,
                new Avm1SyntaxDeclarationOrigin(
                    file,
                    typeOrdinal,
                    memberOrdinal,
                    declaratorOrdinal),
                span,
                Avm1SyntaxSymbolList.Empty));
            _symbolChildren.Add(index);

            var key = new MemberKey(containingType.Value, name);
            if (!_membersByName.TryGetValue(key, out var matches))
            {
                matches = [];
                _membersByName.Add(key, matches);
            }
            else if (matches.Any(existing => MembersConflict(
                _symbols[existing.Value].Kind,
                kind)))
            {
                AddDiagnostic(
                    "AVM1B1003",
                    Avm1CompilationDiagnosticSeverity.Error,
                    file,
                    span,
                    $"Member '{containingName}.{name}' is declared more than once.");
            }
            matches.Add(index);
        }

        private void ResolveImports(Avm1SyntaxProgram lookupProgram)
        {
            for (var i = 0; i < _imports.Count; i++)
            {
                var import = _imports[i];
                if (import.Kind is Avm1SyntaxImportKind.Wildcard)
                    continue;

                var name = lookupProgram.GetName(import.Name);
                var matches = lookupProgram.GetDeclaredTypes(name);
                var target = matches.Count switch
                {
                    0 => Avm1SyntaxSymbolResolution.Unresolved,
                    1 => Avm1SyntaxSymbolResolution.Bound(matches[0]),
                    _ => Avm1SyntaxSymbolResolution.Ambiguous
                };
                _imports[i] = import with { Target = target };
                if (target.Kind is Avm1SyntaxSymbolResolutionKind.Ambiguous)
                {
                    AddDiagnostic(
                        "AVM1B1005",
                        Avm1CompilationDiagnosticSeverity.Error,
                        import.File,
                        import.Span,
                        $"Imported type '{name}' is ambiguous.");
                }
            }
        }

        private void BuildTypeUses(Avm1SyntaxProgram lookupProgram)
        {
            for (var fileOrdinal = 0; fileOrdinal < _files.Count; fileOrdinal++)
            {
                var file = new Avm1SyntaxFileIndex(fileOrdinal);
                var start = _typeUses.Count;
                foreach (var typeIndex in lookupProgram.GetTypes(file))
                {
                    var symbol = lookupProgram.GetSymbol(typeIndex);
                    var declaration = lookupProgram.GetTypeDeclaration(typeIndex);
                    var tree = lookupProgram.GetSource(file).SyntaxTree;
                    foreach (var baseType in tree.GetBaseTypes(declaration))
                    {
                        AddTypeUse(
                            lookupProgram,
                            file,
                            typeIndex,
                            declaration.Kind is Avm1TypeDeclarationKind.Class
                                ? Avm1SyntaxTypeUseKind.BaseClass
                                : Avm1SyntaxTypeUseKind.ExtendedInterface,
                            tree,
                            baseType);
                    }
                    foreach (var implementedType in
                             tree.GetImplementedTypes(declaration))
                    {
                        AddTypeUse(
                            lookupProgram,
                            file,
                            typeIndex,
                            Avm1SyntaxTypeUseKind.ImplementedInterface,
                            tree,
                            implementedType);
                    }
                }

                _files[fileOrdinal] = _files[fileOrdinal] with
                {
                    TypeUses = new Avm1SyntaxTypeUseList(
                        start,
                        _typeUses.Count - start)
                };
            }
        }

        private void AddTypeUse(
            Avm1SyntaxProgram lookupProgram,
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1SyntaxTypeUseKind kind,
            Avm1SyntaxTree tree,
            Avm1QualifiedNameSyntax syntax)
        {
            var name = GetCanonicalName(tree, syntax);
            var resolution = lookupProgram.ResolveType(file, containingType, name);
            _typeUses.Add(new Avm1SyntaxTypeUse(
                containingType,
                kind,
                Intern(name),
                syntax.Span,
                resolution));
            if (resolution.Kind is Avm1SyntaxSymbolResolutionKind.Ambiguous)
            {
                AddDiagnostic(
                    "AVM1B1006",
                    Avm1CompilationDiagnosticSeverity.Error,
                    file,
                    syntax.Span,
                    $"Type reference '{name}' is ambiguous.");
            }
        }

        private Avm1SyntaxProgram CreateProgram(
            Dictionary<string, Avm1SyntaxProgramSymbolIndex[]> typesByName,
            Dictionary<MemberKey, Avm1SyntaxProgramSymbolIndex[]> membersByName,
            Avm1SyntaxBindingDiagnostic[] diagnostics) =>
            new(
                _sources,
                _files.ToArray(),
                _names.ToArray(),
                _symbols.ToArray(),
                _symbolChildren.ToArray(),
                _imports.ToArray(),
                _typeUses.ToArray(),
                diagnostics,
                typesByName,
                membersByName);

        private Avm1SyntaxNameIndex Intern(string value)
        {
            if (_nameIndexes.TryGetValue(value, out var existing))
                return existing;

            var index = new Avm1SyntaxNameIndex(_names.Count);
            _names.Add(value);
            _nameIndexes.Add(value, index);
            return index;
        }

        private void AddDiagnostic(
            string code,
            Avm1CompilationDiagnosticSeverity severity,
            Avm1SyntaxFileIndex file,
            Avm1TextSpan span,
            string message) =>
            _diagnostics.Add(new Avm1SyntaxBindingDiagnostic(
                code,
                severity,
                file,
                span,
                message));

        private static bool MembersConflict(
            Avm1SyntaxProgramSymbolKind first,
            Avm1SyntaxProgramSymbolKind second) =>
            (first, second) is not (
                Avm1SyntaxProgramSymbolKind.Getter,
                Avm1SyntaxProgramSymbolKind.Setter) and not (
                Avm1SyntaxProgramSymbolKind.Setter,
                Avm1SyntaxProgramSymbolKind.Getter);

        private static Dictionary<TKey, Avm1SyntaxProgramSymbolIndex[]> Freeze<TKey>(
            Dictionary<TKey, List<Avm1SyntaxProgramSymbolIndex>> source)
            where TKey : notnull
        {
            var result = new Dictionary<TKey, Avm1SyntaxProgramSymbolIndex[]>(
                source.Count,
                source.Comparer);
            foreach (var (key, value) in source)
                result.Add(key, value.ToArray());
            return result;
        }

        private static string GetCanonicalName(
            Avm1SyntaxTree tree,
            Avm1QualifiedNameSyntax syntax)
        {
            if (!syntax.IsValid)
                return string.Empty;

            var builder = new StringBuilder(syntax.Span.Length);
            var end = syntax.Tokens.Start + syntax.Tokens.Count;
            for (var i = syntax.Tokens.Start; i < end; i++)
            {
                var token = tree.TokenStream.Tokens[i];
                if (token.Kind is Avm1SyntaxKind.DotToken)
                    builder.Append('.');
                else
                    builder.Append(tree.GetText(token.Span));
            }
            return builder.ToString();
        }
    }
}
