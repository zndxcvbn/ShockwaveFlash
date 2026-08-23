using System.Globalization;
using System.Text;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Binding;

public static class Avm1SyntaxSourceProjector
{
    public static Avm1SyntaxSourceProjection Project(
        Avm1SyntaxProgram syntaxProgram,
        Avm1SyntaxLexicalBinding? lexicalBinding = null,
        IAvm1ReferenceProvider? referenceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxProgram);
        lexicalBinding ??= Avm1SyntaxLexicalBinding.Bind(syntaxProgram);
        if (!ReferenceEquals(lexicalBinding.Program, syntaxProgram))
        {
            throw new ArgumentException(
                "The lexical binding belongs to a different syntax program.",
                nameof(lexicalBinding));
        }

        if (referenceProvider is not null &&
            referenceProvider is not Avm1ReferenceCatalog)
        {
            referenceProvider = new Avm1ReferenceCatalog(
                referenceProvider.GetClasses() ?? throw new InvalidOperationException(
                    "The AVM1 reference provider returned a null class sequence."));
        }

        return new Projector(
            syntaxProgram,
            lexicalBinding,
            referenceProvider).Project();
    }

    private sealed class Projector
    {
        private readonly Avm1SyntaxProgram _program;
        private readonly Avm1SyntaxLexicalBinding _lexical;
        private readonly IAvm1ReferenceProvider? _referenceProvider;
        private readonly HashSet<string> _referenceTypes;
        private readonly List<Avm1SyntaxSourceDiagnostic> _diagnostics = [];
        private readonly Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SourceClass>
            _types = [];
        private readonly Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SourceClassMember>
            _members = [];

        public Projector(
            Avm1SyntaxProgram program,
            Avm1SyntaxLexicalBinding lexical,
            IAvm1ReferenceProvider? referenceProvider)
        {
            _program = program;
            _lexical = lexical;
            _referenceProvider = referenceProvider;
            _referenceTypes = referenceProvider?.GetClasses()
                .Select(sourceClass => Avm1SourceProgram.NormalizeRuntimeQualifiedName(
                    sourceClass.Name.Value))
                .ToHashSet(StringComparer.Ordinal) ?? [];
        }

        public Avm1SyntaxSourceProjection Project()
        {
            var files = new Avm1SourceFile[_program.Files.Count];
            for (var fileOrdinal = 0; fileOrdinal < files.Length; fileOrdinal++)
            {
                files[fileOrdinal] = ProjectFile(
                    new Avm1SyntaxFileIndex(fileOrdinal));
            }

            var sourceProgram = Avm1SourceProgram.Create(
                files,
                _referenceProvider);
            return new Avm1SyntaxSourceProjection(
                _program,
                _lexical,
                sourceProgram,
                _diagnostics.ToArray(),
                _types,
                _members);
        }

        private Avm1SourceFile ProjectFile(Avm1SyntaxFileIndex file)
        {
            var source = _program.GetSource(file);
            var imports = _program.GetImports(file)
                .ToArray()
                .Select(import => new Avm1SourceQualifiedName(
                    _program.GetName(import.Name)))
                .ToArray();
            var types = new List<Avm1SourceClass>();
            foreach (var type in _program.GetTypes(file))
                types.Add(ProjectType(file, type));

            var memberDirectives = new HashSet<int>();
            foreach (var type in source.SyntaxTree.Types)
            {
                foreach (var member in source.SyntaxTree.GetMembers(type))
                {
                    if (member.Kind is Avm1MemberDeclarationKind.Directive &&
                        member.Directive.IsValid)
                    {
                        memberDirectives.Add(member.Directive.Value);
                    }
                }
            }

            for (var directiveOrdinal = 0;
                 directiveOrdinal < source.SyntaxTree.Directives.Count;
                 directiveOrdinal++)
            {
                if (memberDirectives.Contains(directiveOrdinal))
                    continue;
                var directive = source.SyntaxTree.Directives[directiveOrdinal];
                AddDiagnostic(
                    "AVM1P1001",
                    Avm1CompilationDiagnosticSeverity.Error,
                    file,
                    directive.Span,
                    "A top-level preprocessor directive must be expanded before " +
                    "Source HIR projection.");
            }

            return new Avm1SourceFile(types, imports);
        }

        private Avm1SourceClass ProjectType(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex typeIndex)
        {
            var symbol = _program.GetSymbol(typeIndex);
            var declaration = _program.GetTypeDeclaration(typeIndex);
            var tree = _program.GetSource(file).SyntaxTree;
            var sourceKind = declaration.Kind is Avm1TypeDeclarationKind.Interface
                ? Avm1SourceTypeDeclarationKind.Interface
                : Avm1SourceTypeDeclarationKind.Class;

            Avm1SourceQualifiedName? baseType = null;
            var interfaces = new List<Avm1SourceQualifiedName>();
            var declaredBases = tree.GetBaseTypes(declaration);
            if (sourceKind is Avm1SourceTypeDeclarationKind.Interface)
            {
                foreach (var syntaxType in declaredBases)
                {
                    interfaces.Add(ResolveTypeReference(
                        file,
                        typeIndex,
                        syntaxType).Name);
                }
            }
            else if (declaredBases.Length > 0)
            {
                baseType = ResolveTypeReference(
                    file,
                    typeIndex,
                    declaredBases[0]).Name;
                if (declaredBases.Length > 1)
                {
                    AddDiagnostic(
                        "AVM1P1002",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        declaration.Span,
                        "An AS2 class cannot extend more than one base class.");
                }
            }

            foreach (var syntaxType in tree.GetImplementedTypes(declaration))
            {
                interfaces.Add(ResolveTypeReference(
                    file,
                    typeIndex,
                    syntaxType).Name);
            }

            var members = new List<Avm1SourceClassMember>();
            foreach (var memberIndex in _program.GetMembers(typeIndex))
            {
                var member = ProjectMember(file, typeIndex, memberIndex);
                members.Add(member);
                _members.Add(memberIndex, member);
            }

            foreach (var member in tree.GetMembers(declaration))
            {
                if (member.Kind is Avm1MemberDeclarationKind.Directive)
                {
                    AddDiagnostic(
                        "AVM1P1003",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        member.Span,
                        "A class preprocessor directive must be expanded before " +
                        "Source HIR projection.");
                }
                else if (member.Kind is Avm1MemberDeclarationKind.Unknown)
                {
                    AddDiagnostic(
                        "AVM1P1004",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        member.Span,
                        "The class contains an unsupported member declaration.");
                }
            }

            var result = new Avm1SourceClass(
                new Avm1SourceQualifiedName(_program.GetName(symbol.QualifiedName)),
                baseType,
                interfaces,
                members,
                ConvertModifiers(symbol.Modifiers),
                kind: sourceKind);
            _types.Add(typeIndex, result);
            return result;
        }

        private Avm1SourceClassMember ProjectMember(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1SyntaxProgramSymbolIndex memberIndex)
        {
            var symbol = _program.GetSymbol(memberIndex);
            return symbol.Kind switch
            {
                Avm1SyntaxProgramSymbolKind.Field => ProjectField(
                    file,
                    containingType,
                    memberIndex),
                Avm1SyntaxProgramSymbolKind.Constructor or
                Avm1SyntaxProgramSymbolKind.Method or
                Avm1SyntaxProgramSymbolKind.Getter or
                Avm1SyntaxProgramSymbolKind.Setter => ProjectMethod(
                    file,
                    containingType,
                    memberIndex),
                _ => throw new InvalidOperationException(
                    $"Syntax symbol {memberIndex} is not a class member.")
            };
        }

        private Avm1SourceField ProjectField(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1SyntaxProgramSymbolIndex fieldIndex)
        {
            var symbol = _program.GetSymbol(fieldIndex);
            var declarator = _program.GetFieldDeclarator(fieldIndex);
            Avm1SourceExpressionFragment? initializer = null;
            if (declarator.Initializer.IsValid)
            {
                if (!_lexical.TryGetTypeInitializerCodeUnit(
                        containingType,
                        out var codeUnit))
                {
                    AddDiagnostic(
                        "AVM1P1005",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        declarator.Span,
                        "The field initializer has no lexical code unit.");
                }
                else
                {
                    initializer = new CodeUnitProjector(
                        this,
                        file,
                        containingType,
                        codeUnit).ProjectExpressionFragment(
                            declarator.Initializer);
                }
            }

            var declaration = _program.GetMemberDeclaration(fieldIndex);
            if (_program.GetSource(file).SyntaxTree
                    .GetToken(declaration.Keyword).Kind is
                Avm1SyntaxKind.ConstKeyword)
            {
                AddDiagnostic(
                    "AVM1P1006",
                    Avm1CompilationDiagnosticSeverity.Error,
                    file,
                    declaration.Span,
                    "Source HIR does not represent const fields yet.");
            }

            return new Avm1SourceField(
                _program.GetName(symbol.Name),
                ConvertModifiers(symbol.Modifiers),
                ResolveOptionalTypeReference(
                    file,
                    containingType,
                    declarator.DeclaredType),
                initializer: initializer);
        }

        private Avm1SourceMethodDeclaration ProjectMethod(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1SyntaxProgramSymbolIndex memberIndex)
        {
            var symbol = _program.GetSymbol(memberIndex);
            var declaration = _program.GetMemberDeclaration(memberIndex);
            if (!_lexical.TryGetMemberCodeUnit(memberIndex, out var codeUnit))
            {
                if (declaration.Body.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Member {memberIndex} has no lexical code unit.");
                }

                var bodylessMethod = new CodeUnitProjector(
                    this,
                    file,
                    containingType,
                    Avm1SyntaxCodeUnitIndex.Invalid).ProjectBodylessMethod(
                        declaration);
                return CreateMethodDeclaration(
                    file,
                    containingType,
                    symbol,
                    declaration,
                    bodylessMethod);
            }

            var body = new CodeUnitProjector(
                this,
                file,
                containingType,
                codeUnit).ProjectMethod(declaration);
            return CreateMethodDeclaration(
                file,
                containingType,
                symbol,
                declaration,
                body);
        }

        private Avm1SourceMethodDeclaration CreateMethodDeclaration(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1SyntaxProgramSymbol symbol,
            Avm1MemberDeclarationSyntax declaration,
            Avm1SourceMethod body)
        {
            var kind = symbol.Kind switch
            {
                Avm1SyntaxProgramSymbolKind.Constructor =>
                    Avm1SourceMethodKind.Constructor,
                Avm1SyntaxProgramSymbolKind.Getter => Avm1SourceMethodKind.Getter,
                Avm1SyntaxProgramSymbolKind.Setter => Avm1SourceMethodKind.Setter,
                _ => Avm1SourceMethodKind.Method
            };
            return new Avm1SourceMethodDeclaration(
                _program.GetName(symbol.Name),
                kind,
                body,
                ConvertModifiers(symbol.Modifiers),
                kind is Avm1SourceMethodKind.Constructor
                    ? null
                    : ResolveOptionalTypeReference(
                        file,
                        containingType,
                        declaration.DeclaredType),
                hasBody: declaration.Body.IsValid);
        }

        internal Avm1SourceTypeReference ResolveTypeReference(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1QualifiedNameSyntax syntax)
        {
            var name = GetQualifiedName(_program.GetSource(file).SyntaxTree, syntax);
            var resolution = _program.ResolveType(file, containingType, name);
            if (resolution.Kind is Avm1SyntaxSymbolResolutionKind.Bound)
            {
                name = _program.GetName(
                    _program.GetSymbol(resolution.Symbol).QualifiedName);
            }
            else if (resolution.Kind is Avm1SyntaxSymbolResolutionKind.Ambiguous)
            {
                AddDiagnostic(
                    "AVM1P1007",
                    Avm1CompilationDiagnosticSeverity.Error,
                    file,
                    syntax.Span,
                    $"Type name '{name}' is ambiguous.");
            }
            else
            {
                var referenceCandidates = GetReferenceTypeCandidates(
                    file,
                    containingType,
                    name);
                if (referenceCandidates.Count == 1)
                {
                    name = referenceCandidates[0];
                }
                else if (referenceCandidates.Count > 1)
                {
                    AddDiagnostic(
                        "AVM1P1010",
                        Avm1CompilationDiagnosticSeverity.Error,
                        file,
                        syntax.Span,
                        $"External type name '{name}' is ambiguous between " +
                        string.Join(", ", referenceCandidates) + ".");
                }
                else if (!name.Contains('.') && TryExpandExplicitImport(
                             file,
                             name,
                             out var importedName,
                             out var importIsAmbiguous))
                {
                    if (importIsAmbiguous)
                    {
                        AddDiagnostic(
                            "AVM1P1008",
                            Avm1CompilationDiagnosticSeverity.Error,
                            file,
                            syntax.Span,
                            $"Imported type name '{name}' is ambiguous.");
                    }
                    else
                        name = importedName;
                }
            }

            return new Avm1SourceTypeReference(
                new Avm1SourceQualifiedName(name));
        }

        internal Avm1SourceTypeReference? ResolveOptionalTypeReference(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1QualifiedNameSyntax syntax) =>
            syntax.IsValid
                ? ResolveTypeReference(file, containingType, syntax)
                : null;

        internal bool TryResolveClassExpression(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            string name,
            out string qualifiedName)
        {
            var resolution = _program.ResolveType(file, containingType, name);
            if (resolution.Kind is Avm1SyntaxSymbolResolutionKind.Bound)
            {
                qualifiedName = _program.GetName(
                    _program.GetSymbol(resolution.Symbol).QualifiedName);
                return true;
            }

            var referenceCandidates = GetReferenceTypeCandidates(
                file,
                containingType,
                name);
            if (referenceCandidates.Count == 1)
            {
                qualifiedName = referenceCandidates[0];
                return true;
            }

            if (resolution.Kind is Avm1SyntaxSymbolResolutionKind.Unresolved &&
                !name.Contains('.') &&
                TryExpandExplicitImport(
                    file,
                    name,
                    out qualifiedName,
                    out var ambiguous) &&
                !ambiguous)
            {
                return true;
            }

            qualifiedName = string.Empty;
            return false;
        }

        private List<string> GetReferenceTypeCandidates(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            string name)
        {
            if (_referenceTypes.Count == 0 || string.IsNullOrWhiteSpace(name))
                return [];

            var normalizedName = Avm1SourceProgram.NormalizeRuntimeQualifiedName(
                name.Trim());
            var candidates = new List<string>(4);
            if (normalizedName.Contains('.'))
            {
                AddReferenceCandidate(normalizedName, candidates);
                return candidates;
            }

            foreach (var import in _program.GetImports(file))
            {
                if (import.Kind is Avm1SyntaxImportKind.Explicit &&
                    string.Equals(
                        _program.GetName(import.SimpleName),
                        normalizedName,
                        StringComparison.Ordinal))
                {
                    AddReferenceCandidate(
                        _program.GetName(import.Name),
                        candidates);
                }
            }

            if (containingType.IsValid)
            {
                var containingName = _program.GetName(
                    _program.GetSymbol(containingType).QualifiedName);
                var separator = containingName.LastIndexOf('.');
                if (separator >= 0)
                {
                    AddReferenceCandidate(
                        containingName[..separator] + "." + normalizedName,
                        candidates);
                }
            }

            foreach (var import in _program.GetImports(file))
            {
                if (import.Kind is Avm1SyntaxImportKind.Wildcard)
                {
                    AddReferenceCandidate(
                        _program.GetName(import.NamespaceName) + "." +
                        normalizedName,
                        candidates);
                }
            }

            AddReferenceCandidate(normalizedName, candidates);
            return candidates;
        }

        private void AddReferenceCandidate(
            string name,
            List<string> candidates)
        {
            var normalized = Avm1SourceProgram.NormalizeRuntimeQualifiedName(name);
            if (_referenceTypes.Contains(normalized) &&
                !candidates.Contains(normalized, StringComparer.Ordinal))
            {
                candidates.Add(normalized);
            }
        }

        internal bool HasCurrentMember(
            Avm1SyntaxProgramSymbolIndex containingType,
            string name) =>
            _program.GetMemberCandidates(containingType, name).Count != 0;

        internal void AddDiagnostic(
            string code,
            Avm1CompilationDiagnosticSeverity severity,
            Avm1SyntaxFileIndex file,
            Avm1TextSpan span,
            string message) =>
            _diagnostics.Add(new Avm1SyntaxSourceDiagnostic(
                code,
                severity,
                file,
                span,
                message));

        private bool TryExpandExplicitImport(
            Avm1SyntaxFileIndex file,
            string simpleName,
            out string qualifiedName,
            out bool ambiguous)
        {
            qualifiedName = string.Empty;
            ambiguous = false;
            foreach (var import in _program.GetImports(file))
            {
                if (import.Kind is not Avm1SyntaxImportKind.Explicit ||
                    !string.Equals(
                        _program.GetName(import.SimpleName),
                        simpleName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = _program.GetName(import.Name);
                if (qualifiedName.Length == 0)
                    qualifiedName = candidate;
                else if (!string.Equals(
                             qualifiedName,
                             candidate,
                             StringComparison.Ordinal))
                    ambiguous = true;
            }
            return qualifiedName.Length != 0;
        }

        private static string GetQualifiedName(
            Avm1SyntaxTree tree,
            Avm1QualifiedNameSyntax syntax)
        {
            if (!syntax.IsValid)
                throw new ArgumentException("The qualified name is missing.", nameof(syntax));

            var result = new StringBuilder(syntax.Span.Length);
            for (var i = 0; i < syntax.Tokens.Count; i++)
            {
                result.Append(tree.GetTokenText(
                    new Avm1SyntaxTokenIndex(syntax.Tokens.Start + i)));
            }
            return result.ToString();
        }

        private static Avm1SourceDeclarationModifiers ConvertModifiers(
            Avm1SyntaxModifiers modifiers)
        {
            var result = Avm1SourceDeclarationModifiers.None;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Static))
                result |= Avm1SourceDeclarationModifiers.Static;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Public))
                result |= Avm1SourceDeclarationModifiers.Public;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Private))
                result |= Avm1SourceDeclarationModifiers.Private;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Protected))
                result |= Avm1SourceDeclarationModifiers.Protected;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Final))
                result |= Avm1SourceDeclarationModifiers.Final;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Override))
                result |= Avm1SourceDeclarationModifiers.Override;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Dynamic))
                result |= Avm1SourceDeclarationModifiers.Dynamic;
            if (modifiers.HasFlag(Avm1SyntaxModifiers.Intrinsic))
                result |= Avm1SourceDeclarationModifiers.Intrinsic;
            return result;
        }

        private sealed class CodeUnitProjector
        {
            private readonly Projector _owner;
            private readonly Avm1SyntaxProgram _program;
            private readonly Avm1SyntaxLexicalBinding _lexical;
            private readonly Avm1SyntaxFileIndex _file;
            private readonly Avm1SyntaxProgramSymbolIndex _containingType;
            private readonly Avm1SyntaxCodeUnitIndex _rootCodeUnit;
            private readonly Avm1SyntaxTree _tree;
            private readonly Avm1SourceArena.Builder _builder = new();
            private readonly List<Avm1SourceDiagnostic> _diagnostics = [];
            private readonly Dictionary<Avm1SyntaxLocalSymbolIndex, SourceSymbolIndex>
                _symbols = [];
            private readonly Dictionary<(int CodeUnit, Avm1SourceSymbolKind Kind),
                SourceSymbolIndex> _specialSymbols = [];
            private readonly Dictionary<Avm1SyntaxControlTargetIndex, SourceLabelIndex>
                _labels = [];
            private readonly Dictionary<Avm1ExpressionIndex, SourceExpressionIndex>
                _expressions = [];
            private readonly Dictionary<Avm1SyntaxCodeUnitIndex, List<SourceSymbolIndex>>
                _captures = [];
            private readonly Dictionary<Avm1SyntaxCodeUnitIndex, HashSet<SourceSymbolIndex>>
                _captureSets = [];
            private readonly Dictionary<Avm1SyntaxTokenIndex, SourceSymbolIndex>
                _fallbackDeclarationSymbols = [];
            private readonly SourceTypeIndex _unknownType;
            private readonly SourceTypeIndex _functionType;

            public CodeUnitProjector(
                Projector owner,
                Avm1SyntaxFileIndex file,
                Avm1SyntaxProgramSymbolIndex containingType,
                Avm1SyntaxCodeUnitIndex rootCodeUnit)
            {
                _owner = owner;
                _program = owner._program;
                _lexical = owner._lexical;
                _file = file;
                _containingType = containingType;
                _rootCodeUnit = rootCodeUnit;
                _tree = _program.GetSource(file).SyntaxTree;
                _unknownType = _builder.GetBuiltInType(
                    Avm1SourceTypeKind.Unknown);
                _functionType = _builder.GetBuiltInType(
                    Avm1SourceTypeKind.Function);
            }

            public Avm1SourceMethod ProjectMethod(
                Avm1MemberDeclarationSyntax declaration)
            {
                var parameters = ProjectParameters(
                    _tree.GetParameters(declaration),
                    _rootCodeUnit);
                var body = declaration.Body.IsValid
                    ? ProjectStatement(declaration.Body, _rootCodeUnit)
                    : AddEmptyBlock(declaration.Span);
                body = AddParameterDefaultGuards(
                    body,
                    _tree.GetParameters(declaration),
                    parameters,
                    _rootCodeUnit,
                    declaration.Span);
                return new Avm1SourceMethod(
                    _builder.ToArena(),
                    body,
                    parameters,
                    _diagnostics.ToArray());
            }

            public Avm1SourceMethod ProjectBodylessMethod(
                Avm1MemberDeclarationSyntax declaration)
            {
                var syntaxParameters = _tree.GetParameters(declaration);
                var parameters = new SourceSymbolIndex[syntaxParameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = syntaxParameters[i];
                    var declaredType = parameter.DeclaredType.IsValid
                        ? GetArenaType(_owner.ResolveTypeReference(
                            _file,
                            _containingType,
                            parameter.DeclaredType))
                        : SourceTypeIndex.Invalid;
                    parameters[i] = _builder.AddSymbol(
                        Avm1SourceSymbolKind.Parameter,
                        GetTokenText(parameter.NameToken),
                        declaredType,
                        _unknownType,
                        Avm1SourceSymbolFlags.DeclarationProvided);
                }

                var body = AddEmptyBlock(declaration.Span);
                return new Avm1SourceMethod(
                    _builder.ToArena(),
                    body,
                    parameters,
                    _diagnostics.ToArray());
            }

            public Avm1SourceExpressionFragment ProjectExpressionFragment(
                Avm1ExpressionIndex syntaxExpression)
            {
                var expression = ProjectExpression(
                    syntaxExpression,
                    _rootCodeUnit);
                return new Avm1SourceExpressionFragment(
                    _builder.ToArena(),
                    expression,
                    _diagnostics.ToArray());
            }

            private SourceStatementIndex ProjectStatement(
                Avm1StatementIndex index,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceLabelIndex? label = null)
            {
                var statement = _tree.GetStatement(index);
                var origin = AddOrigin(index, statement.Span);
                switch (statement.Kind)
                {
                    case Avm1StatementSyntaxKind.Block:
                        return ProjectBlock(statement, codeUnit, origin, label);
                    case Avm1StatementSyntaxKind.Empty:
                        return _builder.AddStatement(
                            Avm1SourceStatementKind.Block,
                            label: NormalizeLabel(label),
                            origin: origin);
                    case Avm1StatementSyntaxKind.Expression:
                        return _builder.AddStatement(
                            Avm1SourceStatementKind.Expression,
                            expression: ProjectOptionalExpression(
                                statement.Expression,
                                codeUnit),
                            origin: origin);
                    case Avm1StatementSyntaxKind.VariableDeclaration:
                        return ProjectVariableDeclarations(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.FunctionDeclaration:
                        return ProjectFunctionDeclaration(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.Return:
                        return _builder.AddStatement(
                            Avm1SourceStatementKind.Return,
                            expression: ProjectOptionalExpression(
                                statement.Expression,
                                codeUnit),
                            origin: origin);
                    case Avm1StatementSyntaxKind.Throw:
                        return _builder.AddStatement(
                            Avm1SourceStatementKind.Throw,
                            expression: ProjectExpression(statement.Expression, codeUnit),
                            origin: origin);
                    case Avm1StatementSyntaxKind.If:
                        return ProjectIf(statement, codeUnit, origin, label);
                    case Avm1StatementSyntaxKind.IfFrameLoaded:
                        return ProjectIfFrameLoaded(
                            statement,
                            codeUnit,
                            origin,
                            label);
                    case Avm1StatementSyntaxKind.While:
                        return ProjectLoop(
                            statement,
                            codeUnit,
                            origin,
                            Avm1SourceStatementKind.While,
                            label);
                    case Avm1StatementSyntaxKind.DoWhile:
                        return ProjectLoop(
                            statement,
                            codeUnit,
                            origin,
                            Avm1SourceStatementKind.DoWhile,
                            label);
                    case Avm1StatementSyntaxKind.For:
                        return ProjectFor(statement, codeUnit, origin, label);
                    case Avm1StatementSyntaxKind.ForIn:
                        return ProjectForIn(statement, codeUnit, origin, label);
                    case Avm1StatementSyntaxKind.Switch:
                        return ProjectSwitch(statement, codeUnit, origin, label);
                    case Avm1StatementSyntaxKind.SwitchSection:
                        return ProjectSwitchSection(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.Break:
                        return ProjectControlTransfer(
                            statement,
                            Avm1SourceStatementKind.Break,
                            origin);
                    case Avm1StatementSyntaxKind.Continue:
                        return ProjectControlTransfer(
                            statement,
                            Avm1SourceStatementKind.Continue,
                            origin);
                    case Avm1StatementSyntaxKind.With:
                        return ProjectWith(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.TellTarget:
                        return ProjectTellTarget(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.Try:
                        return ProjectTry(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.CatchClause:
                        return ProjectCatch(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.FinallyClause:
                        return ProjectFinally(statement, codeUnit, origin);
                    case Avm1StatementSyntaxKind.Labeled:
                        return ProjectLabeled(statement, codeUnit, origin, label);
                    case Avm1StatementSyntaxKind.Directive:
                    case Avm1StatementSyntaxKind.Missing:
                    case Avm1StatementSyntaxKind.Skipped:
                        return AddUnsupportedStatement(statement);
                    default:
                        return AddUnsupportedStatement(statement);
                }
            }

            private SourceStatementIndex ProjectBlock(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? label)
            {
                var children = new List<SourceStatementIndex>();
                foreach (var childIndex in _tree.GetChildren(statement))
                {
                    var syntaxChild = _tree.GetStatement(childIndex);
                    var child = ProjectStatement(childIndex, codeUnit);
                    if (syntaxChild.Kind is
                            Avm1StatementSyntaxKind.VariableDeclaration &&
                        _builder.GetStatement(child) is
                        { Kind: Avm1SourceStatementKind.Block } declarationBlock)
                    {
                        for (var i = 0; i < declarationBlock.Children.Count; i++)
                            children.Add(_builder.GetStatementChild(declarationBlock, i));
                    }
                    else
                        children.Add(child);
                }

                if (label is { IsValid: true })
                    AddUnsupportedLabel(statement.Span);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    origin: origin,
                    children: children);
            }

            private SourceStatementIndex ProjectVariableDeclarations(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                if (_tree.GetToken(statement.Keyword).Kind is
                    Avm1SyntaxKind.ConstKeyword)
                {
                    AddSourceDiagnostic(
                        "AVM1P2001",
                        statement.Span,
                        origin,
                        "Source HIR does not represent const locals yet.");
                }

                var declarations = new List<SourceStatementIndex>();
                foreach (var declarator in _tree.GetDeclarators(statement))
                {
                    var symbol = GetDeclaredSymbol(
                        declarator.NameToken,
                        codeUnit,
                        Avm1SourceSymbolKind.Local);
                    declarations.Add(_builder.AddStatement(
                        Avm1SourceStatementKind.VariableDeclaration,
                        expression: ProjectOptionalExpression(
                            declarator.Initializer,
                            codeUnit),
                        symbol: symbol,
                        origin: AddOrigin(index: statement.Index, declarator.Span)));
                }

                if (declarations.Count == 1)
                    return declarations[0];
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    origin: origin,
                    children: declarations);
            }

            private SourceStatementIndex ProjectFunctionDeclaration(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex declarationCodeUnit,
                SourceOriginIndex origin)
            {
                var symbol = GetDeclaredSymbol(
                    statement.NameToken,
                    declarationCodeUnit,
                    Avm1SourceSymbolKind.Function);
                var bodyChildren = _tree.GetChildren(statement);
                var expression = ProjectFunction(
                    statement.NameToken,
                    statement.Parameters,
                    bodyChildren.Length == 1
                        ? bodyChildren[0]
                        : Avm1StatementIndex.Invalid,
                    statement.Span,
                    _lexical.GetFunctionCodeUnit(_file, statement.Index),
                    symbol,
                    isDeclaration: true);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.FunctionDeclaration,
                    expression: expression,
                    symbol: symbol,
                    origin: origin);
            }

            private SourceStatementIndex ProjectIf(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? label)
            {
                if (label is { IsValid: true })
                    AddUnsupportedLabel(statement.Span);
                var children = ProjectStatementChildren(statement, codeUnit);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: ProjectExpression(statement.Expression, codeUnit),
                    origin: origin,
                    children: children);
            }

            private SourceStatementIndex ProjectLoop(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                Avm1SourceStatementKind kind,
                SourceLabelIndex? label)
            {
                return _builder.AddStatement(
                    kind,
                    expression: ProjectExpression(statement.Expression, codeUnit),
                    label: NormalizeLabel(label),
                    origin: origin,
                    children: ProjectBodyChildren(statement, codeUnit));
            }

            private SourceStatementIndex ProjectFor(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? label)
            {
                var initializers = new List<SourceStatementIndex>();
                foreach (var declarator in _tree.GetDeclarators(statement))
                {
                    initializers.Add(_builder.AddStatement(
                        Avm1SourceStatementKind.VariableDeclaration,
                        expression: ProjectOptionalExpression(
                            declarator.Initializer,
                            codeUnit),
                        symbol: GetDeclaredSymbol(
                            declarator.NameToken,
                            codeUnit,
                            Avm1SourceSymbolKind.Local),
                        origin: AddOrigin(statement.Index, declarator.Span)));
                }
                foreach (var initializer in _tree.GetInitializers(statement))
                {
                    initializers.Add(_builder.AddStatement(
                        Avm1SourceStatementKind.Expression,
                        expression: ProjectExpression(initializer, codeUnit),
                        origin: AddOrigin(initializer, _tree.GetExpression(initializer).Span)));
                }

                var updates = _tree.GetExpressions(statement)
                    .ToArray()
                    .Select(expression => ProjectExpression(expression, codeUnit))
                    .ToArray();
                return _builder.AddStatement(
                    Avm1SourceStatementKind.For,
                    expression: ProjectOptionalExpression(
                        statement.Expression,
                        codeUnit),
                    label: NormalizeLabel(label),
                    origin: origin,
                    children: ProjectBodyChildren(statement, codeUnit),
                    initializers: initializers,
                    expressions: updates);
            }

            private SourceStatementIndex ProjectForIn(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? label)
            {
                SourceExpressionIndex key;
                var flags = Avm1SourceStatementFlags.None;
                var declarators = _tree.GetDeclarators(statement);
                if (declarators.Length != 0)
                {
                    if (declarators.Length != 1)
                    {
                        AddSourceDiagnostic(
                            "AVM1P2002",
                            statement.Span,
                            origin,
                            "A for-in statement must declare exactly one key.");
                    }
                    key = AddSymbolReference(GetDeclaredSymbol(
                        declarators[0].NameToken,
                        codeUnit,
                        Avm1SourceSymbolKind.Local),
                        origin);
                    flags |= Avm1SourceStatementFlags.ForInDeclaresKey;
                }
                else
                {
                    key = ProjectExpression(statement.SecondaryExpression, codeUnit);
                }

                return _builder.AddStatement(
                    Avm1SourceStatementKind.ForIn,
                    expression: key,
                    secondaryExpression: ProjectExpression(
                        statement.Expression,
                        codeUnit),
                    label: NormalizeLabel(label),
                    flags: flags,
                    origin: origin,
                    children: ProjectBodyChildren(statement, codeUnit));
            }

            private SourceStatementIndex ProjectSwitch(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? label) =>
                _builder.AddStatement(
                    Avm1SourceStatementKind.Switch,
                    expression: ProjectExpression(statement.Expression, codeUnit),
                    label: NormalizeLabel(label),
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));

            private SourceStatementIndex ProjectSwitchSection(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin) =>
                _builder.AddStatement(
                    statement.Flags.HasFlag(Avm1StatementSyntaxFlags.HasDefaultLabel)
                        ? Avm1SourceStatementKind.SwitchDefault
                        : Avm1SourceStatementKind.SwitchCase,
                    expression: ProjectOptionalExpression(
                        statement.Expression,
                        codeUnit),
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));

            private SourceStatementIndex ProjectControlTransfer(
                Avm1StatementSyntax statement,
                Avm1SourceStatementKind kind,
                SourceOriginIndex origin)
            {
                var binding = _lexical.GetControlTransfer(_file, statement.Index);
                var label = SourceLabelIndex.Invalid;
                if (statement.NameToken.IsValid &&
                    binding.Kind is Avm1SyntaxControlResolutionKind.Bound)
                {
                    label = GetControlLabel(binding.Target);
                }
                else if (binding.Kind is not Avm1SyntaxControlResolutionKind.Bound)
                {
                    AddSourceDiagnostic(
                        "AVM1P2003",
                        statement.Span,
                        origin,
                        $"{kind} has no valid lexical control target.");
                }

                return _builder.AddStatement(
                    kind,
                    label: label,
                    origin: origin);
            }

            private SourceStatementIndex ProjectWith(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin) =>
                _builder.AddStatement(
                    Avm1SourceStatementKind.With,
                    expression: ProjectExpression(statement.Expression, codeUnit),
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));

            private SourceStatementIndex ProjectTellTarget(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin) =>
                _builder.AddStatement(
                    Avm1SourceStatementKind.TellTarget,
                    expression: ProjectExpression(statement.Expression, codeUnit),
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));

            private SourceStatementIndex ProjectTry(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin) =>
                _builder.AddStatement(
                    Avm1SourceStatementKind.Try,
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));

            private SourceStatementIndex ProjectCatch(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var parameters = _tree.GetParameters(statement);
                var symbol = parameters.Length == 1
                    ? GetDeclaredSymbol(
                        parameters[0].NameToken,
                        codeUnit,
                        Avm1SourceSymbolKind.Catch)
                    : SourceSymbolIndex.Invalid;
                if (!symbol.IsValid)
                {
                    AddSourceDiagnostic(
                        "AVM1P2004",
                        statement.Span,
                        origin,
                        "A catch clause must have one bound parameter.");
                }
                return _builder.AddStatement(
                    Avm1SourceStatementKind.CatchClause,
                    symbol: symbol,
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));
            }

            private SourceStatementIndex ProjectFinally(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin) =>
                _builder.AddStatement(
                    Avm1SourceStatementKind.FinallyClause,
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));

            private SourceStatementIndex ProjectLabeled(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? enclosingLabel)
            {
                if (enclosingLabel is { IsValid: true })
                {
                    AddSourceDiagnostic(
                        "AVM1P2005",
                        statement.Span,
                        origin,
                        "Source HIR cannot attach multiple labels to one statement.");
                }
                var target = _lexical.GetControlTarget(_file, statement.Index);
                var label = target.IsValid
                    ? GetControlLabel(target)
                    : SourceLabelIndex.Invalid;
                var children = _tree.GetChildren(statement);
                if (children.Length != 1)
                    return AddUnsupportedStatement(statement);
                return ProjectStatement(children[0], codeUnit, label);
            }

            private SourceStatementIndex[] ProjectStatementChildren(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit)
            {
                var syntaxChildren = _tree.GetChildren(statement);
                var result = new SourceStatementIndex[syntaxChildren.Length];
                for (var i = 0; i < result.Length; i++)
                    result[i] = ProjectStatement(syntaxChildren[i], codeUnit);
                return result;
            }

            private SourceStatementIndex[] ProjectBodyChildren(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit)
            {
                var syntaxChildren = _tree.GetChildren(statement);
                if (syntaxChildren.Length == 0)
                    return [];
                var body = ProjectStatement(syntaxChildren[0], codeUnit);
                var projected = _builder.GetStatement(body);
                if (projected.Kind is not Avm1SourceStatementKind.Block)
                    return [body];

                var result = new SourceStatementIndex[projected.Children.Count];
                for (var i = 0; i < result.Length; i++)
                    result[i] = _builder.GetStatementChild(projected, i);
                return result;
            }

            private SourceExpressionIndex ProjectExpression(
                Avm1ExpressionIndex index,
                Avm1SyntaxCodeUnitIndex codeUnit)
            {
                if (!index.IsValid)
                    return AddUndefined(SourceOriginIndex.Invalid);
                if (_expressions.TryGetValue(index, out var existing))
                    return existing;

                var expression = _tree.GetExpression(index);
                var origin = AddOrigin(index, expression.Span);
                var result = expression.Kind switch
                {
                    Avm1ExpressionSyntaxKind.IdentifierName =>
                        ProjectIdentifier(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.ThisExpression =>
                        AddSymbolReference(GetSpecialSymbol(
                            codeUnit,
                            Avm1SourceSymbolKind.This), origin),
                    Avm1ExpressionSyntaxKind.SuperExpression =>
                        AddSymbolReference(GetSpecialSymbol(
                            codeUnit,
                            Avm1SourceSymbolKind.Super), origin),
                    Avm1ExpressionSyntaxKind.LiteralExpression =>
                        ProjectLiteral(expression, origin),
                    Avm1ExpressionSyntaxKind.ParenthesizedExpression =>
                        ProjectOnlyChild(expression, codeUnit),
                    Avm1ExpressionSyntaxKind.ArrayLiteralExpression =>
                        ProjectArray(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.ObjectLiteralExpression =>
                        ProjectObject(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.FunctionExpression =>
                        ProjectFunctionExpression(expression),
                    Avm1ExpressionSyntaxKind.UnaryExpression =>
                        ProjectUnary(expression, codeUnit, origin, postfix: false),
                    Avm1ExpressionSyntaxKind.PostfixUnaryExpression =>
                        ProjectUnary(expression, codeUnit, origin, postfix: true),
                    Avm1ExpressionSyntaxKind.BinaryExpression =>
                        ProjectBinary(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.CommaExpression =>
                        ProjectSequence(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.ConditionalExpression =>
                        ProjectChildrenExpression(
                            expression,
                            codeUnit,
                            origin,
                            Avm1SourceExpressionKind.Conditional),
                    Avm1ExpressionSyntaxKind.AssignmentExpression =>
                        ProjectAssignment(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.MemberAccessExpression =>
                        ProjectMemberAccess(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.ElementAccessExpression =>
                        ProjectElementAccess(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.InvocationExpression =>
                        ProjectInvocation(expression, codeUnit, origin),
                    Avm1ExpressionSyntaxKind.NewExpression =>
                        ProjectChildrenExpression(
                            expression,
                            codeUnit,
                            origin,
                            Avm1SourceExpressionKind.New),
                    Avm1ExpressionSyntaxKind.Omitted => AddUndefined(origin),
                    Avm1ExpressionSyntaxKind.ObjectProperty or
                    Avm1ExpressionSyntaxKind.Missing or
                    Avm1ExpressionSyntaxKind.Skipped =>
                        AddUnsupportedExpression(expression, origin),
                    _ => AddUnsupportedExpression(expression, origin)
                };
                _expressions.Add(index, result);
                return result;
            }

            private SourceExpressionIndex ProjectOptionalExpression(
                Avm1ExpressionIndex expression,
                Avm1SyntaxCodeUnitIndex codeUnit) =>
                expression.IsValid
                    ? ProjectExpression(expression, codeUnit)
                    : SourceExpressionIndex.Invalid;

            private SourceExpressionIndex ProjectIdentifier(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var name = GetTokenText(expression.Token);
                var binding = _lexical.GetIdentifierBinding(_file, expression.Index);
                if (binding.Kind is Avm1SyntaxIdentifierBindingKind.Lexical)
                {
                    var symbol = GetSourceSymbol(binding.Symbol);
                    RecordCapture(codeUnit, binding.Symbol, symbol);
                    return AddSymbolReference(symbol, origin);
                }

                if (binding.Kind is Avm1SyntaxIdentifierBindingKind.Unresolved &&
                    !_owner.HasCurrentMember(_containingType, name) &&
                    _owner.TryResolveClassExpression(
                        _file,
                        _containingType,
                        name,
                        out var qualifiedName))
                {
                    return _builder.AddExpression(
                        Avm1SourceExpressionKind.QualifiedName,
                        name: _builder.InternString(qualifiedName),
                        origin: origin);
                }

                return _builder.AddExpression(
                    Avm1SourceExpressionKind.DynamicName,
                    name: _builder.InternString(name),
                    origin: origin);
            }

            private SourceStatementIndex ProjectIfFrameLoaded(
                Avm1StatementSyntax statement,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                SourceLabelIndex? label)
            {
                if (label is { IsValid: true })
                    AddUnsupportedLabel(statement.Span);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.IfFrameLoaded,
                    expression: ProjectExpression(statement.Expression, codeUnit),
                    secondaryExpression: ProjectOptionalExpression(
                        statement.SecondaryExpression,
                        codeUnit),
                    origin: origin,
                    children: ProjectStatementChildren(statement, codeUnit));
            }

            private SourceExpressionIndex ProjectInvocation(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var syntaxChildren = _tree.GetChildren(expression);
                if (syntaxChildren.Length == 0)
                {
                    return AddUnsupportedExpression(expression, origin);
                }

                var target = _tree.GetExpression(syntaxChildren[0]);
                if (target.Kind is Avm1ExpressionSyntaxKind.IdentifierName &&
                    IsIntrinsicInvocation(target, out var intrinsicName))
                {
                    var arguments = new SourceExpressionIndex[
                        syntaxChildren.Length - 1];
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        arguments[i] = ProjectExpression(
                            syntaxChildren[i + 1],
                            codeUnit);
                    }
                    return _builder.AddExpression(
                        Avm1SourceExpressionKind.IntrinsicCall,
                        name: _builder.InternString(intrinsicName),
                        origin: origin,
                        children: arguments);
                }

                return ProjectChildrenExpression(
                    expression,
                    codeUnit,
                    origin,
                    Avm1SourceExpressionKind.Call);
            }

            private bool IsIntrinsicInvocation(
                Avm1ExpressionSyntax target,
                out string name)
            {
                name = GetTokenText(target.Token);
                if (name is not (
                    "Number" or
                    "String" or
                    "int" or
                    "random" or
                    "length" or
                    "mblength" or
                    "chr" or
                    "ord" or
                    "mbchr" or
                    "mbord" or
                    "substring" or
                    "mbsubstring" or
                    "getProperty" or
                    "setProperty" or
                    "duplicateMovieClip" or
                    "removeMovieClip" or
                    "startDrag" or
                    "stopDrag" or
                    "targetPath" or
                    "getTimer" or
                    "trace" or
                    "nextFrame" or
                    "prevFrame" or
                    "play" or
                    "stop" or
                    "stopAllSounds" or
                    "toggleHighQuality" or
                    "call" or
                    "gotoAndPlay" or
                    "gotoAndStop" or
                    "getURL" or
                    "fscommand" or
                    "loadMovie" or
                    "loadMovieNum" or
                    "loadVariables" or
                    "loadVariablesNum"))
                {
                    return false;
                }

                var binding = _lexical.GetIdentifierBinding(
                    _file,
                    target.Index);
                return binding.Kind is not Avm1SyntaxIdentifierBindingKind.Lexical &&
                    !_owner.HasCurrentMember(_containingType, name);
            }

            private SourceExpressionIndex ProjectLiteral(
                Avm1ExpressionSyntax expression,
                SourceOriginIndex origin)
            {
                var token = _tree.GetToken(expression.Token);
                var text = GetTokenText(expression.Token);
                var literal = token.Kind switch
                {
                    Avm1SyntaxKind.TrueKeyword => _builder.AddLiteral(
                        Avm1SourceLiteralKind.Boolean,
                        booleanValue: true),
                    Avm1SyntaxKind.FalseKeyword => _builder.AddLiteral(
                        Avm1SourceLiteralKind.Boolean),
                    Avm1SyntaxKind.NullKeyword => _builder.AddLiteral(
                        Avm1SourceLiteralKind.Null),
                    Avm1SyntaxKind.UndefinedKeyword => _builder.AddLiteral(
                        Avm1SourceLiteralKind.Undefined),
                    Avm1SyntaxKind.StringLiteralToken => _builder.AddLiteral(
                        Avm1SourceLiteralKind.String,
                        stringValue: _builder.InternString(ParseStringLiteral(text))),
                    Avm1SyntaxKind.NumericLiteralToken => AddNumericLiteral(text),
                    _ => _builder.AddLiteral(Avm1SourceLiteralKind.Undefined)
                };
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Literal,
                    literal: literal,
                    origin: origin);
            }

            private SourceExpressionIndex ProjectOnlyChild(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit)
            {
                var children = _tree.GetChildren(expression);
                return children.Length == 1
                    ? ProjectExpression(children[0], codeUnit)
                    : AddUnsupportedExpression(expression, AddOrigin(
                        expression.Index,
                        expression.Span));
            }

            private SourceExpressionIndex ProjectArray(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin) =>
                ProjectChildrenExpression(
                    expression,
                    codeUnit,
                    origin,
                    Avm1SourceExpressionKind.ArrayLiteral);

            private SourceExpressionIndex ProjectObject(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var children = new List<SourceExpressionIndex>();
                foreach (var propertyIndex in _tree.GetChildren(expression))
                {
                    var property = _tree.GetExpression(propertyIndex);
                    if (property.Kind is not Avm1ExpressionSyntaxKind.ObjectProperty ||
                        !property.Token.IsValid ||
                        _tree.GetChildren(property).Length != 1)
                    {
                        children.Add(AddUnsupportedExpression(
                            property,
                            AddOrigin(property.Index, property.Span)));
                        children.Add(AddUndefined(origin));
                        continue;
                    }

                    children.Add(ProjectObjectKey(property));
                    children.Add(ProjectExpression(
                        _tree.GetChildren(property)[0],
                        codeUnit));
                }
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.ObjectLiteral,
                    origin: origin,
                    children: children);
            }

            private SourceExpressionIndex ProjectObjectKey(
                Avm1ExpressionSyntax property)
            {
                var token = _tree.GetToken(property.Token);
                var origin = AddOrigin(property.Index, token.Span);
                if (token.Kind is Avm1SyntaxKind.NumericLiteralToken)
                {
                    return _builder.AddExpression(
                        Avm1SourceExpressionKind.Literal,
                        literal: AddNumericLiteral(GetTokenText(property.Token)),
                        origin: origin);
                }
                var value = token.Kind is Avm1SyntaxKind.StringLiteralToken
                    ? ParseStringLiteral(GetTokenText(property.Token))
                    : GetTokenText(property.Token);
                return AddStringLiteral(value, origin);
            }

            private SourceExpressionIndex ProjectFunctionExpression(
                Avm1ExpressionSyntax expression)
            {
                var codeUnit = _lexical.GetFunctionCodeUnit(
                    _file,
                    expression.Index);
                var nameSymbol = expression.Token.IsValid
                    ? GetDeclaredSymbol(
                        expression.Token,
                        codeUnit,
                        Avm1SourceSymbolKind.Function)
                    : SourceSymbolIndex.Invalid;
                return ProjectFunction(
                    expression.Token,
                    expression.Parameters,
                    expression.Body,
                    expression.Span,
                    codeUnit,
                    nameSymbol,
                    isDeclaration: false);
            }

            private SourceExpressionIndex ProjectFunction(
                Avm1SyntaxTokenIndex nameToken,
                Avm1ParameterList parameterList,
                Avm1StatementIndex bodyIndex,
                Avm1TextSpan span,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceSymbolIndex nameSymbol,
                bool isDeclaration)
            {
                if (!codeUnit.IsValid)
                    return AddUnsupportedExpression(span, "Function has no code unit.");

                EnsureCaptureSet(codeUnit);
                var parametersSyntax = GetParameters(parameterList);
                var parameters = ProjectParameters(parametersSyntax, codeUnit);
                var body = bodyIndex.IsValid
                    ? ProjectStatement(bodyIndex, codeUnit)
                    : AddEmptyBlock(span);
                body = AddParameterDefaultGuards(
                    body,
                    parametersSyntax,
                    parameters,
                    codeUnit,
                    span);
                var codeUnitFacts = _lexical.GetCodeUnit(codeUnit);
                var flags = isDeclaration
                    ? Avm1SourceFunctionFlags.Declaration
                    : Avm1SourceFunctionFlags.None;
                if (codeUnitFacts.Flags.HasFlag(
                        Avm1SyntaxCodeUnitFlags.DeclaredInDynamicScope))
                {
                    flags |= Avm1SourceFunctionFlags.CapturesDynamicScope;
                }

                var function = _builder.AddFunction(
                    nameSymbol,
                    body,
                    flags,
                    AddOrigin(codeUnitFacts.OwnerExpression, span),
                    parameters,
                    GetCaptures(codeUnit));
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.FunctionLiteral,
                    function: function,
                    origin: AddOrigin(codeUnitFacts.OwnerExpression, span));
            }

            private SourceExpressionIndex ProjectUnary(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                bool postfix)
            {
                var children = _tree.GetChildren(expression);
                if (children.Length != 1)
                    return AddUnsupportedExpression(expression, origin);
                var tokenKind = _tree.GetToken(expression.OperatorToken).Kind;
                if (tokenKind is Avm1SyntaxKind.DeleteKeyword)
                {
                    return _builder.AddExpression(
                        Avm1SourceExpressionKind.Delete,
                        origin: origin,
                        children: [ProjectExpression(children[0], codeUnit)]);
                }

                var @operator = tokenKind switch
                {
                    Avm1SyntaxKind.PlusPlusToken => postfix
                        ? Avm1SourceOperator.PostfixIncrement
                        : Avm1SourceOperator.PrefixIncrement,
                    Avm1SyntaxKind.MinusMinusToken => postfix
                        ? Avm1SourceOperator.PostfixDecrement
                        : Avm1SourceOperator.PrefixDecrement,
                    Avm1SyntaxKind.ExclamationToken => Avm1SourceOperator.LogicalNot,
                    Avm1SyntaxKind.TildeToken => Avm1SourceOperator.BitwiseNot,
                    Avm1SyntaxKind.TypeOfKeyword => Avm1SourceOperator.TypeOf,
                    Avm1SyntaxKind.PlusToken => Avm1SourceOperator.UnaryPlus,
                    Avm1SyntaxKind.MinusToken => Avm1SourceOperator.UnaryMinus,
                    Avm1SyntaxKind.VoidKeyword => Avm1SourceOperator.Void,
                    _ => Avm1SourceOperator.None
                };
                if (@operator is Avm1SourceOperator.None)
                    return AddUnsupportedExpression(expression, origin);
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Unary,
                    @operator,
                    origin: origin,
                    children: [ProjectExpression(children[0], codeUnit)]);
            }

            private SourceExpressionIndex ProjectBinary(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var @operator = MapBinaryOperator(
                    _tree.GetToken(expression.OperatorToken).Kind);
                return @operator is Avm1SourceOperator.None
                    ? AddUnsupportedExpression(expression, origin)
                    : ProjectChildrenExpression(
                        expression,
                        codeUnit,
                        origin,
                        Avm1SourceExpressionKind.Binary,
                        @operator);
            }

            private SourceExpressionIndex ProjectAssignment(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var @operator = MapAssignmentOperator(
                    _tree.GetToken(expression.OperatorToken).Kind);
                return @operator is Avm1SourceOperator.None
                    ? AddUnsupportedExpression(expression, origin)
                    : ProjectChildrenExpression(
                        expression,
                        codeUnit,
                        origin,
                        Avm1SourceExpressionKind.Assignment,
                        @operator);
            }

            private SourceExpressionIndex ProjectSequence(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var children = new List<SourceExpressionIndex>();
                AddSequenceChildren(expression, codeUnit, children);
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Sequence,
                    origin: origin,
                    children: children);
            }

            private void AddSequenceChildren(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                List<SourceExpressionIndex> destination)
            {
                foreach (var child in _tree.GetChildren(expression))
                {
                    var childExpression = _tree.GetExpression(child);
                    if (childExpression.Kind is Avm1ExpressionSyntaxKind.CommaExpression)
                        AddSequenceChildren(childExpression, codeUnit, destination);
                    else
                        destination.Add(ProjectExpression(child, codeUnit));
                }
            }

            private SourceExpressionIndex ProjectMemberAccess(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var children = _tree.GetChildren(expression);
                if (children.Length != 1 || !expression.Token.IsValid)
                    return AddUnsupportedExpression(expression, origin);
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.MemberAccess,
                    origin: origin,
                    children:
                    [
                        ProjectExpression(children[0], codeUnit),
                        AddStringLiteral(GetTokenText(expression.Token), origin)
                    ]);
            }

            private SourceExpressionIndex ProjectElementAccess(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin)
            {
                var children = _tree.GetChildren(expression);
                if (children.Length != 2)
                    return AddUnsupportedExpression(expression, origin);
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.MemberAccess,
                    flags: Avm1SourceExpressionFlags.ComputedMember,
                    origin: origin,
                    children:
                    [
                        ProjectExpression(children[0], codeUnit),
                        ProjectExpression(children[1], codeUnit)
                    ]);
            }

            private SourceExpressionIndex ProjectChildrenExpression(
                Avm1ExpressionSyntax expression,
                Avm1SyntaxCodeUnitIndex codeUnit,
                SourceOriginIndex origin,
                Avm1SourceExpressionKind kind,
                Avm1SourceOperator @operator = Avm1SourceOperator.None)
            {
                var children = _tree.GetChildren(expression)
                    .ToArray()
                    .Select(child => ProjectExpression(child, codeUnit))
                    .ToArray();
                return _builder.AddExpression(
                    kind,
                    @operator,
                    origin: origin,
                    children: children);
            }

            private SourceStatementIndex AddParameterDefaultGuards(
                SourceStatementIndex body,
                ReadOnlySpan<Avm1ParameterSyntax> parameters,
                SourceSymbolIndex[] sourceParameters,
                Avm1SyntaxCodeUnitIndex codeUnit,
                Avm1TextSpan span)
            {
                var guards = new List<SourceStatementIndex>();
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (!parameters[i].DefaultValue.IsValid)
                        continue;
                    var origin = AddOrigin(parameters[i].DefaultValue, parameters[i].Span);
                    var condition = _builder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.StrictEqual,
                        origin: origin,
                        children:
                        [
                            AddSymbolReference(sourceParameters[i], origin),
                            AddUndefined(origin)
                        ]);
                    var assignment = _builder.AddExpression(
                        Avm1SourceExpressionKind.Assignment,
                        Avm1SourceOperator.Assign,
                        origin: origin,
                        children:
                        [
                            AddSymbolReference(sourceParameters[i], origin),
                            ProjectExpression(parameters[i].DefaultValue, codeUnit)
                        ]);
                    var assignmentStatement = _builder.AddStatement(
                        Avm1SourceStatementKind.Expression,
                        expression: assignment,
                        origin: origin);
                    var branch = _builder.AddStatement(
                        Avm1SourceStatementKind.Block,
                        origin: origin,
                        children: [assignmentStatement]);
                    guards.Add(_builder.AddStatement(
                        Avm1SourceStatementKind.If,
                        expression: condition,
                        origin: origin,
                        children: [branch]));
                }
                if (guards.Count == 0)
                    return body;

                var bodyStatement = _builder.GetStatement(body);
                if (bodyStatement.Kind is Avm1SourceStatementKind.Block)
                {
                    for (var i = 0; i < bodyStatement.Children.Count; i++)
                        guards.Add(_builder.GetStatementChild(bodyStatement, i));
                }
                else
                    guards.Add(body);
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    origin: AddOrigin(Avm1StatementIndex.Invalid, span),
                    children: guards);
            }

            private SourceSymbolIndex[] ProjectParameters(
                ReadOnlySpan<Avm1ParameterSyntax> parameters,
                Avm1SyntaxCodeUnitIndex codeUnit)
            {
                var result = new SourceSymbolIndex[parameters.Length];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = GetDeclaredSymbol(
                        parameters[i].NameToken,
                        codeUnit,
                        Avm1SourceSymbolKind.Parameter);
                }
                return result;
            }

            private ReadOnlySpan<Avm1ParameterSyntax> GetParameters(
                Avm1ParameterList list)
            {
                if (list.Count == 0)
                    return [];
                var owner = _lexical.GetCodeUnit(_rootCodeUnit);
                if (owner.ContainingMember.IsValid)
                {
                    var declaration = _program.GetMemberDeclaration(
                        owner.ContainingMember);
                    if (declaration.Parameters == list)
                        return _tree.GetParameters(declaration);
                }

                foreach (var statement in _tree.Statements)
                {
                    if (statement.Parameters == list)
                        return _tree.GetParameters(statement);
                }
                foreach (var expression in _tree.Expressions)
                {
                    if (expression.Parameters == list)
                        return _tree.GetParameters(expression);
                }
                return [];
            }

            private SourceSymbolIndex GetDeclaredSymbol(
                Avm1SyntaxTokenIndex token,
                Avm1SyntaxCodeUnitIndex codeUnit,
                Avm1SourceSymbolKind fallbackKind)
            {
                var syntaxSymbol = _lexical.GetDeclaredSymbol(_file, token);
                if (syntaxSymbol.IsValid)
                    return GetSourceSymbol(syntaxSymbol);
                if (_fallbackDeclarationSymbols.TryGetValue(token, out var existing))
                    return existing;

                var origin = token.IsValid
                    ? _tree.GetToken(token).Span
                    : Avm1TextSpan.Invalid;
                AddSourceDiagnostic(
                    "AVM1P2006",
                    origin,
                    SourceOriginIndex.Invalid,
                    "A source declaration has no lexical symbol.");
                var result = _builder.AddSymbol(
                    fallbackKind,
                    token.IsValid ? GetTokenText(token) : "missing",
                    SourceTypeIndex.Invalid,
                    fallbackKind is Avm1SourceSymbolKind.Function
                        ? _functionType
                        : _unknownType,
                    Avm1SourceSymbolFlags.DeclarationProvided);
                _fallbackDeclarationSymbols.Add(token, result);
                return result;
            }

            private SourceSymbolIndex GetSourceSymbol(
                Avm1SyntaxLocalSymbolIndex syntaxSymbol)
            {
                if (_symbols.TryGetValue(syntaxSymbol, out var existing))
                    return existing;
                var symbol = _lexical.GetSymbol(syntaxSymbol);
                var kind = GetSourceSymbolKind(symbol.Flags);
                var declaredType = GetDeclaredType(syntaxSymbol);
                var inferredType = kind is Avm1SourceSymbolKind.Function
                    ? _functionType
                    : _unknownType;
                var result = _builder.AddSymbol(
                    kind,
                    _lexical.GetName(symbol.Name),
                    declaredType,
                    inferredType,
                    Avm1SourceSymbolFlags.DeclarationProvided);
                _symbols.Add(syntaxSymbol, result);
                return result;
            }

            private SourceTypeIndex GetDeclaredType(
                Avm1SyntaxLocalSymbolIndex syntaxSymbol)
            {
                Avm1SourceTypeReference? result = null;
                foreach (var declarationIndex in _lexical.GetDeclarations(syntaxSymbol))
                {
                    var declaration = _lexical.GetDeclaration(declarationIndex);
                    var syntaxType = GetDeclarationType(declaration);
                    if (!syntaxType.IsValid)
                        continue;
                    var candidate = _owner.ResolveTypeReference(
                        _file,
                        _containingType,
                        syntaxType);
                    if (result is null)
                    {
                        result = candidate;
                        continue;
                    }
                    if (!string.Equals(
                            result.Name.Value,
                            candidate.Name.Value,
                            StringComparison.Ordinal))
                    {
                        _owner.AddDiagnostic(
                            "AVM1P1009",
                            Avm1CompilationDiagnosticSeverity.Warning,
                            _file,
                            declaration.Span,
                            $"Local '{_lexical.GetName(
                                _lexical.GetSymbol(syntaxSymbol).Name)}' has " +
                            "conflicting declared types.");
                        return SourceTypeIndex.Invalid;
                    }
                }
                return result is null
                    ? SourceTypeIndex.Invalid
                    : GetArenaType(result);
            }

            private Avm1QualifiedNameSyntax GetDeclarationType(
                Avm1SyntaxLocalDeclaration declaration)
            {
                if (declaration.Kind is
                    Avm1SyntaxLocalDeclarationKind.FunctionDeclaration or
                    Avm1SyntaxLocalDeclarationKind.FunctionExpressionName)
                {
                    return Avm1QualifiedNameSyntax.Missing;
                }

                if (declaration.OwnerStatement.IsValid)
                {
                    var statement = _tree.GetStatement(declaration.OwnerStatement);
                    if (declaration.Kind is Avm1SyntaxLocalDeclarationKind.Variable)
                    {
                        var declarators = _tree.GetDeclarators(statement);
                        return declaration.Ordinal < declarators.Length
                            ? declarators[declaration.Ordinal].DeclaredType
                            : Avm1QualifiedNameSyntax.Missing;
                    }

                    if (declaration.Kind is not (
                        Avm1SyntaxLocalDeclarationKind.Parameter or
                        Avm1SyntaxLocalDeclarationKind.Catch))
                    {
                        return Avm1QualifiedNameSyntax.Missing;
                    }

                    var parameters = _tree.GetParameters(statement);
                    return declaration.Ordinal < parameters.Length
                        ? parameters[declaration.Ordinal].DeclaredType
                        : Avm1QualifiedNameSyntax.Missing;
                }

                if (declaration.OwnerExpression.IsValid)
                {
                    if (declaration.Kind is not Avm1SyntaxLocalDeclarationKind.Parameter)
                        return Avm1QualifiedNameSyntax.Missing;

                    var parameters = _tree.GetParameters(
                        _tree.GetExpression(declaration.OwnerExpression));
                    return declaration.Ordinal < parameters.Length
                        ? parameters[declaration.Ordinal].DeclaredType
                        : Avm1QualifiedNameSyntax.Missing;
                }

                var codeUnit = _lexical.GetCodeUnit(declaration.CodeUnit);
                if (declaration.Kind is Avm1SyntaxLocalDeclarationKind.Parameter &&
                    codeUnit.ContainingMember.IsValid)
                {
                    var parameters = _tree.GetParameters(
                        _program.GetMemberDeclaration(codeUnit.ContainingMember));
                    return declaration.Ordinal < parameters.Length
                        ? parameters[declaration.Ordinal].DeclaredType
                        : Avm1QualifiedNameSyntax.Missing;
                }
                return Avm1QualifiedNameSyntax.Missing;
            }

            private SourceTypeIndex GetArenaType(Avm1SourceTypeReference type) =>
                type.Name.Value switch
                {
                    "Boolean" => _builder.GetBuiltInType(Avm1SourceTypeKind.Boolean),
                    "Number" => _builder.GetBuiltInType(Avm1SourceTypeKind.Number),
                    "String" => _builder.GetBuiltInType(Avm1SourceTypeKind.String),
                    "Object" => _builder.GetBuiltInType(Avm1SourceTypeKind.Object),
                    "Array" => _builder.GetBuiltInType(Avm1SourceTypeKind.Array),
                    "Function" => _functionType,
                    "Void" or "void" => _builder.GetBuiltInType(
                        Avm1SourceTypeKind.Void),
                    "*" => SourceTypeIndex.Invalid,
                    _ => _builder.GetNominalType(type.Name.Value)
                };

            private SourceSymbolIndex GetSpecialSymbol(
                Avm1SyntaxCodeUnitIndex codeUnit,
                Avm1SourceSymbolKind kind)
            {
                var key = (codeUnit.Value, kind);
                if (_specialSymbols.TryGetValue(key, out var existing))
                    return existing;
                var name = kind switch
                {
                    Avm1SourceSymbolKind.This => "this",
                    Avm1SourceSymbolKind.Super => "super",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };
                var declaredType = kind is Avm1SourceSymbolKind.This
                    ? _builder.GetNominalType(_program.GetName(
                        _program.GetSymbol(_containingType).QualifiedName))
                    : GetBaseType();
                var result = _builder.AddSymbol(
                    kind,
                    name,
                    declaredType,
                    declaredType.IsValid ? declaredType : _unknownType,
                    Avm1SourceSymbolFlags.DeclarationProvided);
                _specialSymbols.Add(key, result);
                return result;
            }

            private SourceTypeIndex GetBaseType()
            {
                foreach (var typeUse in _program.GetTypeUses(_file))
                {
                    if (typeUse.ContainingType != _containingType ||
                        typeUse.Kind is not Avm1SyntaxTypeUseKind.BaseClass)
                    {
                        continue;
                    }
                    var name = typeUse.Resolution.Kind is
                        Avm1SyntaxSymbolResolutionKind.Bound
                            ? _program.GetName(_program.GetSymbol(
                                typeUse.Resolution.Symbol).QualifiedName)
                            : _program.GetName(typeUse.Name);
                    return _builder.GetNominalType(name);
                }
                return SourceTypeIndex.Invalid;
            }

            private void RecordCapture(
                Avm1SyntaxCodeUnitIndex codeUnit,
                Avm1SyntaxLocalSymbolIndex syntaxSymbol,
                SourceSymbolIndex sourceSymbol)
            {
                if (_lexical.GetSymbol(syntaxSymbol).CodeUnit == codeUnit)
                    return;
                EnsureCaptureSet(codeUnit);
                if (_captureSets[codeUnit].Add(sourceSymbol))
                    _captures[codeUnit].Add(sourceSymbol);
            }

            private void EnsureCaptureSet(Avm1SyntaxCodeUnitIndex codeUnit)
            {
                if (_captureSets.ContainsKey(codeUnit))
                    return;
                _captureSets.Add(codeUnit, []);
                _captures.Add(codeUnit, []);
            }

            private List<SourceSymbolIndex> GetCaptures(
                Avm1SyntaxCodeUnitIndex codeUnit) =>
                _captures.TryGetValue(codeUnit, out var captures)
                    ? captures
                    : [];

            private SourceLabelIndex GetControlLabel(
                Avm1SyntaxControlTargetIndex target)
            {
                if (_labels.TryGetValue(target, out var existing))
                    return existing;
                var control = _lexical.GetControlTarget(target);
                var name = control.LabelName.IsValid
                    ? _lexical.GetName(control.LabelName)
                    : "label" + target.Value.ToString(CultureInfo.InvariantCulture);
                var result = _builder.AddLabel(name);
                _labels.Add(target, result);
                return result;
            }

            private SourceStatementIndex AddEmptyBlock(Avm1TextSpan span) =>
                _builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    origin: AddOrigin(Avm1StatementIndex.Invalid, span));

            private SourceStatementIndex AddUnsupportedStatement(
                Avm1StatementSyntax statement)
            {
                var origin = AddOrigin(statement.Index, statement.Span);
                var description = $"Unsupported syntax statement {statement.Kind}";
                AddSourceDiagnostic(
                    "AVM1P2007",
                    statement.Span,
                    origin,
                    description + ".");
                return _builder.AddStatement(
                    Avm1SourceStatementKind.Opaque,
                    opaque: _builder.AddOpaque(
                        Avm1SourceOpaqueKind.RecoveryStatement,
                        description,
                        origin),
                    origin: origin);
            }

            private SourceExpressionIndex AddUnsupportedExpression(
                Avm1ExpressionSyntax expression,
                SourceOriginIndex origin) =>
                AddUnsupportedExpression(
                    expression.Span,
                    $"Unsupported syntax expression {expression.Kind}",
                    origin);

            private SourceExpressionIndex AddUnsupportedExpression(
                Avm1TextSpan span,
                string description,
                SourceOriginIndex? origin = null)
            {
                var resolvedOrigin = origin is { IsValid: true } value
                    ? value
                    : AddOrigin(Avm1ExpressionIndex.Invalid, span);
                AddSourceDiagnostic(
                    "AVM1P2008",
                    span,
                    resolvedOrigin,
                    description + ".");
                return _builder.AddExpression(
                    Avm1SourceExpressionKind.Opaque,
                    opaque: _builder.AddOpaque(
                        Avm1SourceOpaqueKind.RecoveryExpression,
                        description,
                        resolvedOrigin),
                    origin: resolvedOrigin);
            }

            private void AddUnsupportedLabel(Avm1TextSpan span) =>
                AddSourceDiagnostic(
                    "AVM1P2009",
                    span,
                    SourceOriginIndex.Invalid,
                    "Only loop and switch labels are representable in Source HIR.");

            private void AddSourceDiagnostic(
                string code,
                Avm1TextSpan span,
                SourceOriginIndex origin,
                string message)
            {
                _diagnostics.Add(new Avm1SourceDiagnostic(
                    code,
                    Avm1SourceDiagnosticSeverity.Error,
                    origin,
                    message));
                _owner.AddDiagnostic(
                    code,
                    Avm1CompilationDiagnosticSeverity.Error,
                    _file,
                    span,
                    message);
            }

            private SourceExpressionIndex AddSymbolReference(
                SourceSymbolIndex symbol,
                SourceOriginIndex origin) =>
                _builder.AddExpression(
                    Avm1SourceExpressionKind.SymbolReference,
                    symbol: symbol,
                    origin: origin);

            private SourceExpressionIndex AddUndefined(SourceOriginIndex origin) =>
                _builder.AddExpression(
                    Avm1SourceExpressionKind.Literal,
                    literal: _builder.AddLiteral(Avm1SourceLiteralKind.Undefined),
                    origin: origin);

            private SourceExpressionIndex AddStringLiteral(
                string value,
                SourceOriginIndex origin) =>
                _builder.AddExpression(
                    Avm1SourceExpressionKind.Literal,
                    literal: _builder.AddLiteral(
                        Avm1SourceLiteralKind.String,
                        stringValue: _builder.InternString(value)),
                    origin: origin);

            private SourceLiteralIndex AddNumericLiteral(string text)
            {
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(
                        text.AsSpan(2),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out var hexadecimal))
                {
                    if (hexadecimal <= int.MaxValue)
                    {
                        return _builder.AddLiteral(
                            Avm1SourceLiteralKind.Integer,
                            integerValue: (int)hexadecimal);
                    }
                    return _builder.AddLiteral(
                        Avm1SourceLiteralKind.Number,
                        numberValue: hexadecimal);
                }
                if (int.TryParse(
                        text,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var integer))
                {
                    return _builder.AddLiteral(
                        Avm1SourceLiteralKind.Integer,
                        integerValue: integer);
                }
                if (double.TryParse(
                        text,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number))
                {
                    return _builder.AddLiteral(
                        Avm1SourceLiteralKind.Number,
                        numberValue: number);
                }
                return _builder.AddLiteral(Avm1SourceLiteralKind.Undefined);
            }

            private SourceOriginIndex AddOrigin(
                Avm1ExpressionIndex index,
                Avm1TextSpan span) =>
                _builder.AddOrigin(
                    _file.Value,
                    index.IsValid ? index.Value : -1,
                    span.IsValid ? span.Start : -1,
                    span.IsValid ? span.End : -1);

            private SourceOriginIndex AddOrigin(
                Avm1StatementIndex index,
                Avm1TextSpan span) =>
                _builder.AddOrigin(
                    _file.Value,
                    index.IsValid ? index.Value : -1,
                    span.IsValid ? span.Start : -1,
                    span.IsValid ? span.End : -1);

            private string GetTokenText(Avm1SyntaxTokenIndex token) =>
                _tree.GetTokenText(token).ToString();

            private static SourceLabelIndex NormalizeLabel(SourceLabelIndex? label) =>
                label is { IsValid: true } value
                    ? value
                    : SourceLabelIndex.Invalid;

            private static Avm1SourceSymbolKind GetSourceSymbolKind(
                Avm1SyntaxLocalSymbolFlags flags)
            {
                if (flags.HasFlag(Avm1SyntaxLocalSymbolFlags.Arguments))
                    return Avm1SourceSymbolKind.Arguments;
                if (flags.HasFlag(Avm1SyntaxLocalSymbolFlags.Parameter))
                    return Avm1SourceSymbolKind.Parameter;
                if (flags.HasFlag(Avm1SyntaxLocalSymbolFlags.Catch))
                    return Avm1SourceSymbolKind.Catch;
                if (flags.HasFlag(Avm1SyntaxLocalSymbolFlags.FunctionDeclaration) ||
                    flags.HasFlag(Avm1SyntaxLocalSymbolFlags.FunctionExpressionName))
                {
                    return Avm1SourceSymbolKind.Function;
                }
                return Avm1SourceSymbolKind.Local;
            }

            private static Avm1SourceOperator MapAssignmentOperator(
                Avm1SyntaxKind kind) => kind switch
                {
                    Avm1SyntaxKind.EqualsToken => Avm1SourceOperator.Assign,
                    Avm1SyntaxKind.PlusEqualsToken => Avm1SourceOperator.AddAssign,
                    Avm1SyntaxKind.MinusEqualsToken =>
                        Avm1SourceOperator.SubtractAssign,
                    Avm1SyntaxKind.AsteriskEqualsToken =>
                        Avm1SourceOperator.MultiplyAssign,
                    Avm1SyntaxKind.SlashEqualsToken =>
                        Avm1SourceOperator.DivideAssign,
                    Avm1SyntaxKind.PercentEqualsToken =>
                        Avm1SourceOperator.ModuloAssign,
                    Avm1SyntaxKind.AmpersandEqualsToken =>
                        Avm1SourceOperator.BitAndAssign,
                    Avm1SyntaxKind.PipeEqualsToken =>
                        Avm1SourceOperator.BitOrAssign,
                    Avm1SyntaxKind.CaretEqualsToken =>
                        Avm1SourceOperator.BitXorAssign,
                    Avm1SyntaxKind.LessThanLessThanEqualsToken =>
                        Avm1SourceOperator.ShiftLeftAssign,
                    Avm1SyntaxKind.GreaterThanGreaterThanEqualsToken =>
                        Avm1SourceOperator.ShiftRightAssign,
                    Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken =>
                        Avm1SourceOperator.ShiftRightUnsignedAssign,
                    _ => Avm1SourceOperator.None
                };

            private static Avm1SourceOperator MapBinaryOperator(
                Avm1SyntaxKind kind) => kind switch
                {
                    Avm1SyntaxKind.PlusToken => Avm1SourceOperator.Add,
                    Avm1SyntaxKind.MinusToken => Avm1SourceOperator.Subtract,
                    Avm1SyntaxKind.AsteriskToken => Avm1SourceOperator.Multiply,
                    Avm1SyntaxKind.SlashToken => Avm1SourceOperator.Divide,
                    Avm1SyntaxKind.PercentToken => Avm1SourceOperator.Modulo,
                    Avm1SyntaxKind.EqualsEqualsToken => Avm1SourceOperator.Equal,
                    Avm1SyntaxKind.ExclamationEqualsToken =>
                        Avm1SourceOperator.NotEqual,
                    Avm1SyntaxKind.EqualsEqualsEqualsToken =>
                        Avm1SourceOperator.StrictEqual,
                    Avm1SyntaxKind.ExclamationEqualsEqualsToken =>
                        Avm1SourceOperator.StrictNotEqual,
                    Avm1SyntaxKind.LessThanToken => Avm1SourceOperator.Less,
                    Avm1SyntaxKind.LessThanEqualsToken =>
                        Avm1SourceOperator.LessOrEqual,
                    Avm1SyntaxKind.GreaterThanToken => Avm1SourceOperator.Greater,
                    Avm1SyntaxKind.GreaterThanEqualsToken =>
                        Avm1SourceOperator.GreaterOrEqual,
                    Avm1SyntaxKind.AmpersandAmpersandToken =>
                        Avm1SourceOperator.LogicalAnd,
                    Avm1SyntaxKind.PipePipeToken => Avm1SourceOperator.LogicalOr,
                    Avm1SyntaxKind.AmpersandToken => Avm1SourceOperator.BitAnd,
                    Avm1SyntaxKind.PipeToken => Avm1SourceOperator.BitOr,
                    Avm1SyntaxKind.CaretToken => Avm1SourceOperator.BitXor,
                    Avm1SyntaxKind.LessThanLessThanToken =>
                        Avm1SourceOperator.ShiftLeft,
                    Avm1SyntaxKind.GreaterThanGreaterThanToken =>
                        Avm1SourceOperator.ShiftRight,
                    Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanToken =>
                        Avm1SourceOperator.ShiftRightUnsigned,
                    Avm1SyntaxKind.InKeyword => Avm1SourceOperator.In,
                    Avm1SyntaxKind.InstanceOfKeyword =>
                        Avm1SourceOperator.InstanceOf,
                    _ => Avm1SourceOperator.None
                };

            private static string ParseStringLiteral(string text)
            {
                if (text.Length < 2)
                    return text;
                var result = new StringBuilder(text.Length - 2);
                for (var i = 1; i + 1 < text.Length; i++)
                {
                    var character = text[i];
                    if (character != '\\' || i + 2 >= text.Length)
                    {
                        result.Append(character);
                        continue;
                    }

                    var escape = text[++i];
                    switch (escape)
                    {
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'v': result.Append('\v'); break;
                        case '\r':
                            if (i + 1 < text.Length && text[i + 1] == '\n')
                                i++;
                            break;
                        case '\n':
                            break;
                        case 'x' when TryParseHexEscape(text, i + 1, 2, out var x):
                            result.Append((char)x);
                            i += 2;
                            break;
                        case 'u' when TryParseHexEscape(text, i + 1, 4, out var u):
                            result.Append((char)u);
                            i += 4;
                            break;
                        default:
                            result.Append(escape);
                            break;
                    }
                }
                return result.ToString();
            }

            private static bool TryParseHexEscape(
                string text,
                int start,
                int length,
                out int value)
            {
                value = 0;
                if (start + length > text.Length - 1)
                    return false;
                for (var i = 0; i < length; i++)
                {
                    var digit = text[start + i] switch
                    {
                        >= '0' and <= '9' => text[start + i] - '0',
                        >= 'a' and <= 'f' => text[start + i] - 'a' + 10,
                        >= 'A' and <= 'F' => text[start + i] - 'A' + 10,
                        _ => -1
                    };
                    if (digit < 0)
                        return false;
                    value = (value << 4) | digit;
                }
                return true;
            }
        }
    }
}
