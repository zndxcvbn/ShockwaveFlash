using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Binding;

public sealed class Avm1SyntaxLexicalBinding
{
    private readonly string[] _names;
    private readonly Avm1SyntaxCodeUnit[] _codeUnits;
    private readonly Avm1SyntaxScope[] _scopes;
    private readonly Avm1SyntaxLocalSymbol[] _symbols;
    private readonly Avm1SyntaxLocalDeclaration[] _declarations;
    private readonly Avm1SyntaxControlTarget[] _controlTargets;
    private readonly Avm1ExpressionIndex[] _expressionRoots;
    private readonly Avm1SyntaxLocalSymbolIndex[] _scopeSymbols;
    private readonly Avm1SyntaxLocalDeclarationIndex[] _symbolDeclarations;
    private readonly FileBindingData[] _fileBindings;
    private readonly Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SyntaxCodeUnitIndex>
        _memberCodeUnits;
    private readonly Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SyntaxCodeUnitIndex>
        _typeInitializerCodeUnits;
    private readonly Avm1SyntaxBindingDiagnostic[] _diagnostics;

    private Avm1SyntaxLexicalBinding(
        Avm1SyntaxProgram program,
        string[] names,
        Avm1SyntaxCodeUnit[] codeUnits,
        Avm1SyntaxScope[] scopes,
        Avm1SyntaxLocalSymbol[] symbols,
        Avm1SyntaxLocalDeclaration[] declarations,
        Avm1SyntaxControlTarget[] controlTargets,
        Avm1ExpressionIndex[] expressionRoots,
        Avm1SyntaxLocalSymbolIndex[] scopeSymbols,
        Avm1SyntaxLocalDeclarationIndex[] symbolDeclarations,
        FileBindingData[] fileBindings,
        Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SyntaxCodeUnitIndex>
            memberCodeUnits,
        Dictionary<Avm1SyntaxProgramSymbolIndex, Avm1SyntaxCodeUnitIndex>
            typeInitializerCodeUnits,
        Avm1SyntaxBindingDiagnostic[] diagnostics)
    {
        Program = program;
        _names = names;
        _codeUnits = codeUnits;
        _scopes = scopes;
        _symbols = symbols;
        _declarations = declarations;
        _controlTargets = controlTargets;
        _expressionRoots = expressionRoots;
        _scopeSymbols = scopeSymbols;
        _symbolDeclarations = symbolDeclarations;
        _fileBindings = fileBindings;
        _memberCodeUnits = memberCodeUnits;
        _typeInitializerCodeUnits = typeInitializerCodeUnits;
        _diagnostics = diagnostics;
    }

    public Avm1SyntaxProgram Program { get; }

    public IReadOnlyList<Avm1SyntaxCodeUnit> CodeUnits => _codeUnits;

    public IReadOnlyList<Avm1SyntaxScope> Scopes => _scopes;

    public IReadOnlyList<Avm1SyntaxLocalSymbol> Symbols => _symbols;

    public IReadOnlyList<Avm1SyntaxLocalDeclaration> Declarations => _declarations;

    public IReadOnlyList<Avm1SyntaxControlTarget> ControlTargets => _controlTargets;

    public IReadOnlyList<Avm1SyntaxBindingDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic =>
        diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    public static Avm1SyntaxLexicalBinding Bind(Avm1SyntaxProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new Builder(program).Build();
    }

    public string GetName(Avm1SyntaxLocalNameIndex name)
    {
        if (!name.IsValid || name.Value >= _names.Length)
            throw new ArgumentOutOfRangeException(nameof(name));
        return _names[name.Value];
    }

    public Avm1SyntaxCodeUnit GetCodeUnit(Avm1SyntaxCodeUnitIndex codeUnit)
    {
        ValidateCodeUnit(codeUnit);
        return _codeUnits[codeUnit.Value];
    }

    public Avm1SyntaxScope GetScope(Avm1SyntaxScopeIndex scope)
    {
        ValidateScope(scope);
        return _scopes[scope.Value];
    }

    public Avm1SyntaxLocalSymbol GetSymbol(Avm1SyntaxLocalSymbolIndex symbol)
    {
        ValidateSymbol(symbol);
        return _symbols[symbol.Value];
    }

    public Avm1SyntaxControlTarget GetControlTarget(
        Avm1SyntaxControlTargetIndex target)
    {
        if (!target.IsValid || target.Value >= _controlTargets.Length)
            throw new ArgumentOutOfRangeException(nameof(target));
        return _controlTargets[target.Value];
    }

    public ReadOnlySpan<Avm1ExpressionIndex> GetExpressionRoots(
        Avm1SyntaxCodeUnitIndex codeUnit)
    {
        ValidateCodeUnit(codeUnit);
        var range = _codeUnits[codeUnit.Value].ExpressionRoots;
        return _expressionRoots.AsSpan(range.Start, range.Count);
    }

    public ReadOnlySpan<Avm1SyntaxLocalSymbolIndex> GetSymbols(
        Avm1SyntaxScopeIndex scope)
    {
        ValidateScope(scope);
        var range = _scopes[scope.Value].Symbols;
        return _scopeSymbols.AsSpan(range.Start, range.Count);
    }

    public ReadOnlySpan<Avm1SyntaxLocalDeclarationIndex> GetDeclarations(
        Avm1SyntaxLocalSymbolIndex symbol)
    {
        ValidateSymbol(symbol);
        var range = _symbols[symbol.Value].Declarations;
        return _symbolDeclarations.AsSpan(range.Start, range.Count);
    }

    public Avm1SyntaxLocalDeclaration GetDeclaration(
        Avm1SyntaxLocalDeclarationIndex declaration)
    {
        if (!declaration.IsValid || declaration.Value >= _declarations.Length)
            throw new ArgumentOutOfRangeException(nameof(declaration));
        return _declarations[declaration.Value];
    }

    public Avm1SyntaxScopeIndex GetScope(
        Avm1SyntaxFileIndex file,
        Avm1ExpressionIndex expression)
    {
        var data = GetFileData(file);
        if (!expression.IsValid || expression.Value >= data.ExpressionScopes.Length)
            throw new ArgumentOutOfRangeException(nameof(expression));
        return data.ExpressionScopes[expression.Value];
    }

    public Avm1SyntaxScopeIndex GetScope(
        Avm1SyntaxFileIndex file,
        Avm1StatementIndex statement)
    {
        var data = GetFileData(file);
        if (!statement.IsValid || statement.Value >= data.StatementScopes.Length)
            throw new ArgumentOutOfRangeException(nameof(statement));
        return data.StatementScopes[statement.Value];
    }

    public Avm1SyntaxCodeUnitIndex GetCodeUnit(
        Avm1SyntaxFileIndex file,
        Avm1ExpressionIndex expression)
    {
        var scope = GetScope(file, expression);
        return scope.IsValid
            ? _scopes[scope.Value].CodeUnit
            : Avm1SyntaxCodeUnitIndex.Invalid;
    }

    public Avm1SyntaxCodeUnitIndex GetCodeUnit(
        Avm1SyntaxFileIndex file,
        Avm1StatementIndex statement)
    {
        var scope = GetScope(file, statement);
        return scope.IsValid
            ? _scopes[scope.Value].CodeUnit
            : Avm1SyntaxCodeUnitIndex.Invalid;
    }

    public Avm1SyntaxIdentifierBinding GetIdentifierBinding(
        Avm1SyntaxFileIndex file,
        Avm1ExpressionIndex expression)
    {
        var data = GetFileData(file);
        if (!expression.IsValid || expression.Value >= data.IdentifierBindings.Length)
            throw new ArgumentOutOfRangeException(nameof(expression));
        return data.IdentifierBindings[expression.Value];
    }

    public Avm1SyntaxLocalSymbolIndex GetDeclaredSymbol(
        Avm1SyntaxFileIndex file,
        Avm1SyntaxTokenIndex declarationToken)
    {
        var data = GetFileData(file);
        if (!declarationToken.IsValid ||
            declarationToken.Value >= data.DeclarationSymbols.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(declarationToken));
        }
        return data.DeclarationSymbols[declarationToken.Value];
    }

    public Avm1SyntaxControlTransferBinding GetControlTransfer(
        Avm1SyntaxFileIndex file,
        Avm1StatementIndex statement)
    {
        var data = GetFileData(file);
        if (!statement.IsValid || statement.Value >= data.ControlTransfers.Length)
            throw new ArgumentOutOfRangeException(nameof(statement));
        return data.ControlTransfers[statement.Value];
    }

    public Avm1SyntaxControlTargetIndex GetControlTarget(
        Avm1SyntaxFileIndex file,
        Avm1StatementIndex statement)
    {
        var data = GetFileData(file);
        if (!statement.IsValid || statement.Value >= data.StatementTargets.Length)
            throw new ArgumentOutOfRangeException(nameof(statement));
        return data.StatementTargets[statement.Value];
    }

    public Avm1SyntaxCodeUnitIndex GetFunctionCodeUnit(
        Avm1SyntaxFileIndex file,
        Avm1StatementIndex declaration)
    {
        var data = GetFileData(file);
        if (!declaration.IsValid ||
            declaration.Value >= data.StatementFunctionCodeUnits.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(declaration));
        }
        return data.StatementFunctionCodeUnits[declaration.Value];
    }

    public Avm1SyntaxCodeUnitIndex GetFunctionCodeUnit(
        Avm1SyntaxFileIndex file,
        Avm1ExpressionIndex expression)
    {
        var data = GetFileData(file);
        if (!expression.IsValid ||
            expression.Value >= data.ExpressionFunctionCodeUnits.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(expression));
        }
        return data.ExpressionFunctionCodeUnits[expression.Value];
    }

    public bool TryGetMemberCodeUnit(
        Avm1SyntaxProgramSymbolIndex member,
        out Avm1SyntaxCodeUnitIndex codeUnit) =>
        _memberCodeUnits.TryGetValue(member, out codeUnit);

    public bool TryGetTypeInitializerCodeUnit(
        Avm1SyntaxProgramSymbolIndex type,
        out Avm1SyntaxCodeUnitIndex codeUnit) =>
        _typeInitializerCodeUnits.TryGetValue(type, out codeUnit);

    public bool IsInDynamicScope(
        Avm1SyntaxFileIndex file,
        Avm1ExpressionIndex expression)
    {
        var scope = GetScope(file, expression);
        return scope.IsValid &&
            _scopes[scope.Value].Flags.HasFlag(Avm1SyntaxScopeFlags.Dynamic);
    }

    private FileBindingData GetFileData(Avm1SyntaxFileIndex file)
    {
        if (!file.IsValid || file.Value >= _fileBindings.Length)
            throw new ArgumentOutOfRangeException(nameof(file));
        return _fileBindings[file.Value];
    }

    private void ValidateCodeUnit(Avm1SyntaxCodeUnitIndex codeUnit)
    {
        if (!codeUnit.IsValid || codeUnit.Value >= _codeUnits.Length)
            throw new ArgumentOutOfRangeException(nameof(codeUnit));
    }

    private void ValidateScope(Avm1SyntaxScopeIndex scope)
    {
        if (!scope.IsValid || scope.Value >= _scopes.Length)
            throw new ArgumentOutOfRangeException(nameof(scope));
    }

    private void ValidateSymbol(Avm1SyntaxLocalSymbolIndex symbol)
    {
        if (!symbol.IsValid || symbol.Value >= _symbols.Length)
            throw new ArgumentOutOfRangeException(nameof(symbol));
    }

    private sealed class FileBindingData
    {
        public FileBindingData(Avm1SyntaxTree tree)
        {
            ExpressionScopes = CreateInvalidScopes(tree.Expressions.Count);
            StatementScopes = CreateInvalidScopes(tree.Statements.Count);
            IdentifierBindings = new Avm1SyntaxIdentifierBinding[
                tree.Expressions.Count];
            DeclarationSymbols = CreateInvalidSymbols(tree.TokenStream.Tokens.Count);
            ControlTransfers = new Avm1SyntaxControlTransferBinding[
                tree.Statements.Count];
            StatementTargets = CreateInvalidTargets(tree.Statements.Count);
            StatementFunctionCodeUnits = CreateInvalidCodeUnits(
                tree.Statements.Count);
            ExpressionFunctionCodeUnits = CreateInvalidCodeUnits(
                tree.Expressions.Count);
        }

        public Avm1SyntaxScopeIndex[] ExpressionScopes { get; }

        public Avm1SyntaxScopeIndex[] StatementScopes { get; }

        public Avm1SyntaxIdentifierBinding[] IdentifierBindings { get; }

        public Avm1SyntaxLocalSymbolIndex[] DeclarationSymbols { get; }

        public Avm1SyntaxControlTransferBinding[] ControlTransfers { get; }

        public Avm1SyntaxControlTargetIndex[] StatementTargets { get; }

        public Avm1SyntaxCodeUnitIndex[] StatementFunctionCodeUnits { get; }

        public Avm1SyntaxCodeUnitIndex[] ExpressionFunctionCodeUnits { get; }

        private static Avm1SyntaxScopeIndex[] CreateInvalidScopes(int count)
        {
            var result = new Avm1SyntaxScopeIndex[count];
            Array.Fill(result, Avm1SyntaxScopeIndex.Invalid);
            return result;
        }

        private static Avm1SyntaxLocalSymbolIndex[] CreateInvalidSymbols(int count)
        {
            var result = new Avm1SyntaxLocalSymbolIndex[count];
            Array.Fill(result, Avm1SyntaxLocalSymbolIndex.Invalid);
            return result;
        }

        private static Avm1SyntaxControlTargetIndex[] CreateInvalidTargets(int count)
        {
            var result = new Avm1SyntaxControlTargetIndex[count];
            Array.Fill(result, Avm1SyntaxControlTargetIndex.Invalid);
            return result;
        }

        private static Avm1SyntaxCodeUnitIndex[] CreateInvalidCodeUnits(int count)
        {
            var result = new Avm1SyntaxCodeUnitIndex[count];
            Array.Fill(result, Avm1SyntaxCodeUnitIndex.Invalid);
            return result;
        }
    }

    private sealed class Builder
    {
        private readonly Avm1SyntaxProgram _program;
        private readonly FileBindingData[] _fileBindings;
        private readonly List<string> _names = [];
        private readonly Dictionary<string, Avm1SyntaxLocalNameIndex> _nameIndexes =
            new(StringComparer.Ordinal);
        private readonly List<Avm1SyntaxCodeUnit> _codeUnits = [];
        private readonly List<Avm1SyntaxScope> _scopes = [];
        private readonly List<Avm1SyntaxLocalSymbol> _symbols = [];
        private readonly List<Avm1SyntaxLocalDeclaration> _declarations = [];
        private readonly List<Avm1SyntaxControlTarget> _controlTargets = [];
        private readonly List<Avm1ExpressionIndex> _expressionRoots = [];
        private readonly List<List<Avm1SyntaxLocalSymbolIndex>> _symbolsByScope = [];
        private readonly List<List<Avm1SyntaxLocalDeclarationIndex>>
            _declarationsBySymbol = [];
        private readonly Dictionary<ScopeNameKey, Avm1SyntaxLocalSymbolIndex>
            _symbolsByName = [];
        private readonly Dictionary<Avm1SyntaxProgramSymbolIndex,
            Avm1SyntaxCodeUnitIndex> _memberCodeUnits = [];
        private readonly Dictionary<Avm1SyntaxProgramSymbolIndex,
            Avm1SyntaxCodeUnitIndex> _typeInitializerCodeUnits = [];
        private readonly List<Avm1SyntaxBindingDiagnostic> _diagnostics = [];

        public Builder(Avm1SyntaxProgram program)
        {
            _program = program;
            _fileBindings = new FileBindingData[program.Files.Count];
            for (var i = 0; i < _fileBindings.Length; i++)
            {
                var tree = program.GetSource(new Avm1SyntaxFileIndex(i)).SyntaxTree;
                _fileBindings[i] = new FileBindingData(tree);
            }
        }

        public Avm1SyntaxLexicalBinding Build()
        {
            BuildProgramTopology();
            BindIdentifiers();
            DetectDirectEval();
            BindControlTransfers();
            var scopeSymbols = FreezeScopeSymbols();
            var symbolDeclarations = FreezeSymbolDeclarations();
            return new Avm1SyntaxLexicalBinding(
                _program,
                _names.ToArray(),
                _codeUnits.ToArray(),
                _scopes.ToArray(),
                _symbols.ToArray(),
                _declarations.ToArray(),
                _controlTargets.ToArray(),
                _expressionRoots.ToArray(),
                scopeSymbols,
                symbolDeclarations,
                _fileBindings,
                _memberCodeUnits,
                _typeInitializerCodeUnits,
                _diagnostics.ToArray());
        }

        private void BuildProgramTopology()
        {
            for (var fileValue = 0; fileValue < _program.Files.Count; fileValue++)
            {
                var file = new Avm1SyntaxFileIndex(fileValue);
                foreach (var type in _program.GetTypes(file))
                    BuildType(file, type);
            }
        }

        private void BuildType(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex type)
        {
            var tree = GetTree(file);
            var initializerRoots = new List<Avm1ExpressionIndex>();
            foreach (var member in _program.GetMembers(type))
            {
                if (_program.GetSymbol(member).Kind is not
                    Avm1SyntaxProgramSymbolKind.Field)
                {
                    continue;
                }

                var declarator = _program.GetFieldDeclarator(member);
                if (declarator.Initializer.IsValid)
                    initializerRoots.Add(declarator.Initializer);
            }

            if (initializerRoots.Count > 0)
            {
                var initializer = AddCodeUnit(
                    Avm1SyntaxCodeUnitKind.TypeInitializer,
                    Avm1SyntaxCodeUnitIndex.Invalid,
                    Avm1SyntaxScopeIndex.Invalid,
                    file,
                    type,
                    Avm1SyntaxProgramSymbolIndex.Invalid,
                    Avm1StatementIndex.Invalid,
                    Avm1ExpressionIndex.Invalid,
                    Avm1StatementIndex.Invalid,
                    initializerRoots,
                    Avm1SyntaxTokenIndex.Invalid);
                _typeInitializerCodeUnits.Add(type, initializer);
                var scope = _codeUnits[initializer.Value].RootScope;
                foreach (var expression in initializerRoots)
                    BuildExpression(file, expression, scope);
            }

            foreach (var member in _program.GetMembers(type))
            {
                var symbol = _program.GetSymbol(member);
                if (symbol.Kind is Avm1SyntaxProgramSymbolKind.Field)
                    continue;

                var declaration = _program.GetMemberDeclaration(member);
                if (!declaration.Body.IsValid)
                    continue;

                var parameters = tree.GetParameters(declaration);
                var defaults = CollectParameterDefaults(parameters);
                var codeUnit = AddCodeUnit(
                    Avm1SyntaxCodeUnitKind.Member,
                    Avm1SyntaxCodeUnitIndex.Invalid,
                    Avm1SyntaxScopeIndex.Invalid,
                    file,
                    type,
                    member,
                    Avm1StatementIndex.Invalid,
                    Avm1ExpressionIndex.Invalid,
                    declaration.Body,
                    defaults,
                    Avm1SyntaxTokenIndex.Invalid);
                _memberCodeUnits.Add(member, codeUnit);
                var rootScope = _codeUnits[codeUnit.Value].RootScope;
                DeclareArguments(rootScope);
                DeclareParameters(
                    file,
                    codeUnit,
                    rootScope,
                    parameters,
                    Avm1StatementIndex.Invalid,
                    Avm1ExpressionIndex.Invalid);
                foreach (var expression in defaults)
                    BuildExpression(file, expression, rootScope);
                BuildStatement(file, declaration.Body, rootScope);
            }
        }

        private void BuildStatement(
            Avm1SyntaxFileIndex file,
            Avm1StatementIndex index,
            Avm1SyntaxScopeIndex scope)
        {
            var tree = GetTree(file);
            var statement = tree.GetStatement(index);
            if (statement.Kind is Avm1StatementSyntaxKind.CatchClause)
            {
                BuildCatchClause(file, statement, scope);
                return;
            }
            if (!AssignStatementScope(file, index, scope))
                return;

            switch (statement.Kind)
            {
                case Avm1StatementSyntaxKind.FunctionDeclaration:
                    BuildFunctionDeclaration(file, statement, scope);
                    return;

                case Avm1StatementSyntaxKind.With:
                    BuildWithStatement(file, statement, scope);
                    return;

                case Avm1StatementSyntaxKind.TellTarget:
                    BuildTargetStatement(file, statement, scope);
                    return;
            }

            if (statement.Kind is
                Avm1StatementSyntaxKind.VariableDeclaration or
                Avm1StatementSyntaxKind.For or
                Avm1StatementSyntaxKind.ForIn)
            {
                DeclareVariables(file, statement, scope);
            }

            BuildStatementExpressions(file, statement, scope);
            foreach (var child in tree.GetChildren(statement))
                BuildStatement(file, child, scope);
        }

        private void BuildFunctionDeclaration(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            Avm1SyntaxScopeIndex declarationScope)
        {
            var parent = _scopes[declarationScope.Value].CodeUnit;
            var rootScope = _codeUnits[parent.Value].RootScope;
            Declare(
                file,
                rootScope,
                statement.NameToken,
                Avm1SyntaxLocalSymbolFlags.FunctionDeclaration,
                Avm1SyntaxLocalDeclarationKind.FunctionDeclaration,
                statement.Index,
                Avm1ExpressionIndex.Invalid,
                -1);

            var tree = GetTree(file);
            var children = tree.GetChildren(statement);
            var body = children.Length > 0
                ? children[0]
                : Avm1StatementIndex.Invalid;
            var parameters = tree.GetParameters(statement);
            var defaults = CollectParameterDefaults(parameters);
            var codeUnit = AddCodeUnit(
                Avm1SyntaxCodeUnitKind.FunctionDeclaration,
                parent,
                declarationScope,
                file,
                _codeUnits[parent.Value].ContainingType,
                _codeUnits[parent.Value].ContainingMember,
                statement.Index,
                Avm1ExpressionIndex.Invalid,
                body,
                defaults,
                Avm1SyntaxTokenIndex.Invalid);
            _fileBindings[file.Value]
                .StatementFunctionCodeUnits[statement.Index.Value] = codeUnit;
            var functionScope = _codeUnits[codeUnit.Value].RootScope;
            DeclareArguments(functionScope);
            DeclareParameters(
                file,
                codeUnit,
                functionScope,
                parameters,
                statement.Index,
                Avm1ExpressionIndex.Invalid);
            foreach (var expression in defaults)
                BuildExpression(file, expression, functionScope);
            if (body.IsValid)
                BuildStatement(file, body, functionScope);
        }

        private void BuildCatchClause(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            Avm1SyntaxScopeIndex parentScope)
        {
            var catchScope = AddScope(
                Avm1SyntaxScopeKind.Catch,
                parentScope,
                _scopes[parentScope.Value].CodeUnit,
                statement.Index,
                Avm1ExpressionIndex.Invalid,
                dynamic: false);
            if (!AssignStatementScope(file, statement.Index, catchScope))
                return;

            var tree = GetTree(file);
            var parameters = tree.GetParameters(statement);
            if (parameters.Length != 1)
            {
                AddDiagnostic(
                    "AVM1B2005",
                    file,
                    statement.Span,
                    "An AS2 catch clause must declare exactly one catch variable.");
            }
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                Declare(
                    file,
                    catchScope,
                    parameter.NameToken,
                    Avm1SyntaxLocalSymbolFlags.Catch,
                    Avm1SyntaxLocalDeclarationKind.Catch,
                    statement.Index,
                    Avm1ExpressionIndex.Invalid,
                    i);
                if (parameter.DefaultValue.IsValid)
                    BuildExpression(file, parameter.DefaultValue, catchScope);
            }

            foreach (var child in tree.GetChildren(statement))
                BuildStatement(file, child, catchScope);
        }

        private void BuildWithStatement(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            Avm1SyntaxScopeIndex outerScope)
        {
            BuildStatementExpressions(file, statement, outerScope);
            var codeUnit = _scopes[outerScope.Value].CodeUnit;
            AddCodeUnitFlags(codeUnit, Avm1SyntaxCodeUnitFlags.ContainsWith);
            var withScope = AddScope(
                Avm1SyntaxScopeKind.With,
                outerScope,
                codeUnit,
                statement.Index,
                Avm1ExpressionIndex.Invalid,
                dynamic: true);
            foreach (var child in GetTree(file).GetChildren(statement))
                BuildStatement(file, child, withScope);
        }

        private void BuildTargetStatement(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            Avm1SyntaxScopeIndex outerScope)
        {
            BuildStatementExpressions(file, statement, outerScope);
            var codeUnit = _scopes[outerScope.Value].CodeUnit;
            var targetScope = AddScope(
                Avm1SyntaxScopeKind.Target,
                outerScope,
                codeUnit,
                statement.Index,
                Avm1ExpressionIndex.Invalid,
                dynamic: true);
            foreach (var child in GetTree(file).GetChildren(statement))
                BuildStatement(file, child, targetScope);
        }

        private void BuildStatementExpressions(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            Avm1SyntaxScopeIndex scope)
        {
            var tree = GetTree(file);
            BuildExpressionIfValid(file, statement.Expression, scope);
            BuildExpressionIfValid(file, statement.SecondaryExpression, scope);
            foreach (var expression in tree.GetInitializers(statement))
                BuildExpression(file, expression, scope);
            foreach (var expression in tree.GetExpressions(statement))
                BuildExpression(file, expression, scope);
            foreach (var declarator in tree.GetDeclarators(statement))
                BuildExpressionIfValid(file, declarator.Initializer, scope);
        }

        private void BuildExpressionIfValid(
            Avm1SyntaxFileIndex file,
            Avm1ExpressionIndex expression,
            Avm1SyntaxScopeIndex scope)
        {
            if (expression.IsValid)
                BuildExpression(file, expression, scope);
        }

        private void BuildExpression(
            Avm1SyntaxFileIndex file,
            Avm1ExpressionIndex index,
            Avm1SyntaxScopeIndex scope)
        {
            if (!AssignExpressionScope(file, index, scope))
                return;

            var tree = GetTree(file);
            var expression = tree.GetExpression(index);
            foreach (var child in tree.GetChildren(expression))
                BuildExpression(file, child, scope);
            if (expression.Kind is not Avm1ExpressionSyntaxKind.FunctionExpression)
                return;

            var parent = _scopes[scope.Value].CodeUnit;
            var parameters = tree.GetParameters(expression);
            var defaults = CollectParameterDefaults(parameters);
            var codeUnit = AddCodeUnit(
                Avm1SyntaxCodeUnitKind.FunctionExpression,
                parent,
                scope,
                file,
                _codeUnits[parent.Value].ContainingType,
                _codeUnits[parent.Value].ContainingMember,
                Avm1StatementIndex.Invalid,
                expression.Index,
                expression.Body,
                defaults,
                expression.Token);
            _fileBindings[file.Value]
                .ExpressionFunctionCodeUnits[expression.Index.Value] = codeUnit;
            var functionScope = _codeUnits[codeUnit.Value].RootScope;
            DeclareArguments(functionScope);
            DeclareParameters(
                file,
                codeUnit,
                functionScope,
                parameters,
                Avm1StatementIndex.Invalid,
                expression.Index);
            foreach (var defaultValue in defaults)
                BuildExpression(file, defaultValue, functionScope);
            if (expression.Body.IsValid)
                BuildStatement(file, expression.Body, functionScope);
        }

        private void DeclareVariables(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            Avm1SyntaxScopeIndex currentScope)
        {
            var codeUnit = _scopes[currentScope.Value].CodeUnit;
            var rootScope = _codeUnits[codeUnit.Value].RootScope;
            var declarators = GetTree(file).GetDeclarators(statement);
            for (var i = 0; i < declarators.Length; i++)
            {
                Declare(
                    file,
                    rootScope,
                    declarators[i].NameToken,
                    Avm1SyntaxLocalSymbolFlags.Variable,
                    Avm1SyntaxLocalDeclarationKind.Variable,
                    statement.Index,
                    Avm1ExpressionIndex.Invalid,
                    i);
            }
        }

        private void DeclareParameters(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1SyntaxScopeIndex scope,
            ReadOnlySpan<Avm1ParameterSyntax> parameters,
            Avm1StatementIndex ownerStatement,
            Avm1ExpressionIndex ownerExpression)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                Declare(
                    file,
                    scope,
                    parameters[i].NameToken,
                    Avm1SyntaxLocalSymbolFlags.Parameter,
                    Avm1SyntaxLocalDeclarationKind.Parameter,
                    ownerStatement,
                    ownerExpression,
                    i);
            }
        }

        private void DeclareArguments(Avm1SyntaxScopeIndex scope) =>
            DeclareName(
                scope,
                "arguments",
                Avm1SyntaxLocalSymbolFlags.Arguments,
                Avm1TextSpan.Invalid);

        private Avm1SyntaxLocalSymbolIndex Declare(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxScopeIndex scope,
            Avm1SyntaxTokenIndex nameToken,
            Avm1SyntaxLocalSymbolFlags flags,
            Avm1SyntaxLocalDeclarationKind declarationKind,
            Avm1StatementIndex ownerStatement,
            Avm1ExpressionIndex ownerExpression,
            int ordinal)
        {
            if (!nameToken.IsValid)
                return Avm1SyntaxLocalSymbolIndex.Invalid;

            var tree = GetTree(file);
            var name = tree.GetTokenText(nameToken).ToString();
            var span = tree.GetToken(nameToken).Span;
            var symbol = DeclareName(scope, name, flags, span);
            var declaration = new Avm1SyntaxLocalDeclarationIndex(
                _declarations.Count);
            _declarations.Add(new Avm1SyntaxLocalDeclaration(
                declaration,
                symbol,
                declarationKind,
                file,
                _scopes[scope.Value].CodeUnit,
                nameToken,
                ownerStatement,
                ownerExpression,
                ordinal,
                span));
            _declarationsBySymbol[symbol.Value].Add(declaration);

            var existing = _fileBindings[file.Value]
                .DeclarationSymbols[nameToken.Value];
            if (existing.IsValid && existing != symbol)
            {
                AddDiagnostic(
                    "AVM1B2099",
                    file,
                    span,
                    "A declaration token was assigned to multiple local symbols.");
            }
            else
            {
                _fileBindings[file.Value]
                    .DeclarationSymbols[nameToken.Value] = symbol;
            }
            return symbol;
        }

        private Avm1SyntaxLocalSymbolIndex DeclareName(
            Avm1SyntaxScopeIndex scope,
            string name,
            Avm1SyntaxLocalSymbolFlags flags,
            Avm1TextSpan declarationSpan)
        {
            var nameIndex = Intern(name);
            var key = new ScopeNameKey(scope.Value, nameIndex.Value);
            if (_symbolsByName.TryGetValue(key, out var existing))
            {
                var symbol = _symbols[existing.Value];
                _symbols[existing.Value] = symbol with
                {
                    Flags = symbol.Flags | flags,
                    FirstDeclarationSpan = symbol.FirstDeclarationSpan.IsValid
                        ? symbol.FirstDeclarationSpan
                        : declarationSpan
                };
                return existing;
            }

            var index = new Avm1SyntaxLocalSymbolIndex(_symbols.Count);
            _symbols.Add(new Avm1SyntaxLocalSymbol(
                index,
                nameIndex,
                flags,
                scope,
                _scopes[scope.Value].CodeUnit,
                declarationSpan,
                Avm1SyntaxLocalDeclarationList.Empty));
            _symbolsByName.Add(key, index);
            _symbolsByScope[scope.Value].Add(index);
            _declarationsBySymbol.Add([]);
            return index;
        }

        private Avm1SyntaxCodeUnitIndex AddCodeUnit(
            Avm1SyntaxCodeUnitKind kind,
            Avm1SyntaxCodeUnitIndex parent,
            Avm1SyntaxScopeIndex declarationScope,
            Avm1SyntaxFileIndex file,
            Avm1SyntaxProgramSymbolIndex containingType,
            Avm1SyntaxProgramSymbolIndex containingMember,
            Avm1StatementIndex ownerStatement,
            Avm1ExpressionIndex ownerExpression,
            Avm1StatementIndex rootStatement,
            List<Avm1ExpressionIndex> expressionRoots,
            Avm1SyntaxTokenIndex functionNameToken)
        {
            var index = new Avm1SyntaxCodeUnitIndex(_codeUnits.Count);
            _codeUnits.Add(default);
            var nameScope = Avm1SyntaxScopeIndex.Invalid;
            if (functionNameToken.IsValid)
            {
                nameScope = AddScope(
                    Avm1SyntaxScopeKind.FunctionName,
                    declarationScope,
                    index,
                    Avm1StatementIndex.Invalid,
                    ownerExpression,
                    dynamic: false);
            }
            var rootScope = AddScope(
                Avm1SyntaxScopeKind.CodeUnit,
                nameScope.IsValid ? nameScope : declarationScope,
                index,
                rootStatement,
                ownerExpression,
                dynamic: false);
            var rootStart = _expressionRoots.Count;
            _expressionRoots.AddRange(expressionRoots);
            var flags = declarationScope.IsValid &&
                _scopes[declarationScope.Value].Flags.HasFlag(
                    Avm1SyntaxScopeFlags.Dynamic)
                ? Avm1SyntaxCodeUnitFlags.DeclaredInDynamicScope
                : Avm1SyntaxCodeUnitFlags.None;
            _codeUnits[index.Value] = new Avm1SyntaxCodeUnit(
                index,
                kind,
                parent,
                declarationScope,
                rootScope,
                file,
                containingType,
                containingMember,
                ownerStatement,
                ownerExpression,
                rootStatement,
                new Avm1SyntaxExpressionRootList(
                    rootStart,
                    expressionRoots.Count),
                flags);

            if (functionNameToken.IsValid)
            {
                Declare(
                    file,
                    nameScope,
                    functionNameToken,
                    Avm1SyntaxLocalSymbolFlags.FunctionExpressionName,
                    Avm1SyntaxLocalDeclarationKind.FunctionExpressionName,
                    ownerStatement,
                    ownerExpression,
                    -1);
            }
            return index;
        }

        private Avm1SyntaxScopeIndex AddScope(
            Avm1SyntaxScopeKind kind,
            Avm1SyntaxScopeIndex parent,
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1StatementIndex ownerStatement,
            Avm1ExpressionIndex ownerExpression,
            bool dynamic)
        {
            var inheritedDynamic = parent.IsValid &&
                _scopes[parent.Value].Flags.HasFlag(Avm1SyntaxScopeFlags.Dynamic);
            var flags = dynamic || inheritedDynamic
                ? Avm1SyntaxScopeFlags.Dynamic
                : Avm1SyntaxScopeFlags.None;
            var index = new Avm1SyntaxScopeIndex(_scopes.Count);
            _scopes.Add(new Avm1SyntaxScope(
                index,
                kind,
                parent,
                codeUnit,
                ownerStatement,
                ownerExpression,
                flags,
                Avm1SyntaxLocalSymbolList.Empty));
            _symbolsByScope.Add([]);
            return index;
        }

        private bool AssignStatementScope(
            Avm1SyntaxFileIndex file,
            Avm1StatementIndex statement,
            Avm1SyntaxScopeIndex scope)
        {
            var assignments = _fileBindings[file.Value].StatementScopes;
            var existing = assignments[statement.Value];
            if (!existing.IsValid)
            {
                assignments[statement.Value] = scope;
                return true;
            }
            if (existing != scope)
            {
                AddDiagnostic(
                    "AVM1B2099",
                    file,
                    GetTree(file).GetStatement(statement).Span,
                    "A statement belongs to multiple lexical scopes.");
            }
            return false;
        }

        private bool AssignExpressionScope(
            Avm1SyntaxFileIndex file,
            Avm1ExpressionIndex expression,
            Avm1SyntaxScopeIndex scope)
        {
            var assignments = _fileBindings[file.Value].ExpressionScopes;
            var existing = assignments[expression.Value];
            if (!existing.IsValid)
            {
                assignments[expression.Value] = scope;
                return true;
            }
            if (existing != scope)
            {
                AddDiagnostic(
                    "AVM1B2099",
                    file,
                    GetTree(file).GetExpression(expression).Span,
                    "An expression belongs to multiple lexical scopes.");
            }
            return false;
        }

        private void BindIdentifiers()
        {
            for (var fileValue = 0; fileValue < _fileBindings.Length; fileValue++)
            {
                var file = new Avm1SyntaxFileIndex(fileValue);
                var tree = GetTree(file);
                var data = _fileBindings[fileValue];
                for (var i = 0; i < tree.Expressions.Count; i++)
                {
                    var expression = tree.Expressions[i];
                    if (expression.Kind is not
                        Avm1ExpressionSyntaxKind.IdentifierName)
                    {
                        continue;
                    }

                    var scope = data.ExpressionScopes[i];
                    if (!scope.IsValid)
                        continue;
                    var name = tree.GetTokenText(expression.Token).ToString();
                    var symbol = Lookup(scope, name, out var dynamic);
                    data.IdentifierBindings[i] = dynamic
                        ? Avm1SyntaxIdentifierBinding.Dynamic(symbol)
                        : symbol.IsValid
                            ? Avm1SyntaxIdentifierBinding.Lexical(symbol)
                            : Avm1SyntaxIdentifierBinding.Unresolved;
                }
            }
        }

        private Avm1SyntaxLocalSymbolIndex Lookup(
            Avm1SyntaxScopeIndex scope,
            string name,
            out bool crossesDynamicScope)
        {
            var nameIndex = Intern(name);
            crossesDynamicScope = false;
            for (var depth = 0;
                 scope.IsValid && depth < _scopes.Count;
                 depth++, scope = _scopes[scope.Value].Parent)
            {
                if (_symbolsByName.TryGetValue(
                    new ScopeNameKey(scope.Value, nameIndex.Value),
                    out var symbol))
                {
                    return symbol;
                }
                if (_scopes[scope.Value].Kind is Avm1SyntaxScopeKind.With)
                    crossesDynamicScope = true;
            }
            return Avm1SyntaxLocalSymbolIndex.Invalid;
        }

        private void DetectDirectEval()
        {
            for (var fileValue = 0; fileValue < _fileBindings.Length; fileValue++)
            {
                var file = new Avm1SyntaxFileIndex(fileValue);
                var tree = GetTree(file);
                for (var i = 0; i < tree.Expressions.Count; i++)
                {
                    var invocation = tree.Expressions[i];
                    if (invocation.Kind is not
                        Avm1ExpressionSyntaxKind.InvocationExpression)
                    {
                        continue;
                    }

                    var children = tree.GetChildren(invocation);
                    if (children.Length == 0)
                        continue;
                    var callee = UnwrapParentheses(tree, children[0]);
                    var expression = tree.GetExpression(callee);
                    if (expression.Kind is not
                            Avm1ExpressionSyntaxKind.IdentifierName ||
                        tree.GetTokenText(expression.Token).ToString() != "eval")
                    {
                        continue;
                    }

                    var binding = _fileBindings[fileValue]
                        .IdentifierBindings[callee.Value];
                    if (binding.Kind is Avm1SyntaxIdentifierBindingKind.Lexical)
                        continue;

                    var scope = _fileBindings[fileValue].ExpressionScopes[i];
                    if (!scope.IsValid)
                        continue;
                    var codeUnit = _scopes[scope.Value].CodeUnit;
                    AddCodeUnitFlags(
                        codeUnit,
                        Avm1SyntaxCodeUnitFlags.ContainsDirectEval);
                    for (var parent = _codeUnits[codeUnit.Value].Parent;
                         parent.IsValid;
                         parent = _codeUnits[parent.Value].Parent)
                    {
                        AddCodeUnitFlags(
                            parent,
                            Avm1SyntaxCodeUnitFlags.HasDescendantDirectEval);
                    }
                }
            }
        }

        private static Avm1ExpressionIndex UnwrapParentheses(
            Avm1SyntaxTree tree,
            Avm1ExpressionIndex expression)
        {
            for (var depth = 0; depth < tree.Expressions.Count; depth++)
            {
                var node = tree.GetExpression(expression);
                if (node.Kind is not
                    Avm1ExpressionSyntaxKind.ParenthesizedExpression)
                {
                    return expression;
                }
                var children = tree.GetChildren(node);
                if (children.Length != 1)
                    return expression;
                expression = children[0];
            }
            return expression;
        }

        private void BindControlTransfers()
        {
            foreach (var codeUnit in _codeUnits)
            {
                if (!codeUnit.RootStatement.IsValid)
                    continue;
                var active = new List<Avm1SyntaxControlTargetIndex>();
                BindControlStatement(
                    codeUnit.File,
                    codeUnit.Index,
                    codeUnit.RootStatement,
                    active);
            }
        }

        private void BindControlStatement(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1StatementIndex index,
            List<Avm1SyntaxControlTargetIndex> active)
        {
            var tree = GetTree(file);
            var statement = tree.GetStatement(index);
            switch (statement.Kind)
            {
                case Avm1StatementSyntaxKind.FunctionDeclaration:
                    return;

                case Avm1StatementSyntaxKind.Labeled:
                    BindLabeledStatement(file, codeUnit, statement, active);
                    return;

                case Avm1StatementSyntaxKind.While:
                case Avm1StatementSyntaxKind.DoWhile:
                case Avm1StatementSyntaxKind.For:
                case Avm1StatementSyntaxKind.ForIn:
                    BindImplicitTarget(
                        file,
                        codeUnit,
                        statement,
                        Avm1SyntaxControlTargetKind.Loop,
                        Avm1SyntaxControlTargetFlags.Breakable |
                        Avm1SyntaxControlTargetFlags.Continuable,
                        active);
                    return;

                case Avm1StatementSyntaxKind.Switch:
                    BindImplicitTarget(
                        file,
                        codeUnit,
                        statement,
                        Avm1SyntaxControlTargetKind.Switch,
                        Avm1SyntaxControlTargetFlags.Breakable,
                        active);
                    return;

                case Avm1StatementSyntaxKind.Break:
                case Avm1StatementSyntaxKind.Continue:
                    BindControlTransfer(file, statement, active);
                    return;
            }

            foreach (var child in tree.GetChildren(statement))
                BindControlStatement(file, codeUnit, child, active);
        }

        private void BindLabeledStatement(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1StatementSyntax statement,
            List<Avm1SyntaxControlTargetIndex> active)
        {
            var tree = GetTree(file);
            var name = statement.NameToken.IsValid
                ? tree.GetTokenText(statement.NameToken).ToString()
                : string.Empty;
            if (active.Any(target =>
                    _controlTargets[target.Value].Kind is
                        Avm1SyntaxControlTargetKind.Label &&
                    GetName(_controlTargets[target.Value].LabelName) == name))
            {
                AddDiagnostic(
                    "AVM1B2004",
                    file,
                    statement.NameToken.IsValid
                        ? tree.GetToken(statement.NameToken).Span
                        : statement.Span,
                    $"Label '{name}' is already active in this function.");
            }

            var children = tree.GetChildren(statement);
            var targetStatement = children.Length > 0
                ? GetUltimateLabeledBody(tree, children[0])
                : Avm1StatementIndex.Invalid;
            var flags = Avm1SyntaxControlTargetFlags.Breakable;
            if (targetStatement.IsValid && IsLoop(
                    tree.GetStatement(targetStatement).Kind))
            {
                flags |= Avm1SyntaxControlTargetFlags.Continuable;
            }
            var target = AddControlTarget(
                file,
                codeUnit,
                Avm1SyntaxControlTargetKind.Label,
                flags,
                statement.Index,
                targetStatement,
                name.Length == 0
                    ? Avm1SyntaxLocalNameIndex.Invalid
                    : Intern(name));
            active.Add(target);
            foreach (var child in children)
                BindControlStatement(file, codeUnit, child, active);
            active.RemoveAt(active.Count - 1);
        }

        private void BindImplicitTarget(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1StatementSyntax statement,
            Avm1SyntaxControlTargetKind kind,
            Avm1SyntaxControlTargetFlags flags,
            List<Avm1SyntaxControlTargetIndex> active)
        {
            var target = AddControlTarget(
                file,
                codeUnit,
                kind,
                flags,
                statement.Index,
                statement.Index,
                Avm1SyntaxLocalNameIndex.Invalid);
            active.Add(target);
            foreach (var child in GetTree(file).GetChildren(statement))
                BindControlStatement(file, codeUnit, child, active);
            active.RemoveAt(active.Count - 1);
        }

        private void BindControlTransfer(
            Avm1SyntaxFileIndex file,
            Avm1StatementSyntax statement,
            List<Avm1SyntaxControlTargetIndex> active)
        {
            var isContinue = statement.Kind is
                Avm1StatementSyntaxKind.Continue;
            Avm1SyntaxControlTargetIndex target;
            if (statement.NameToken.IsValid)
            {
                var name = GetTree(file)
                    .GetTokenText(statement.NameToken).ToString();
                target = FindLabeledTarget(active, name);
                if (!target.IsValid)
                {
                    _fileBindings[file.Value]
                        .ControlTransfers[statement.Index.Value] =
                        Avm1SyntaxControlTransferBinding.Unresolved;
                    AddDiagnostic(
                        "AVM1B2002",
                        file,
                        GetTree(file).GetToken(statement.NameToken).Span,
                        $"Label '{name}' is not active in this function.");
                    return;
                }
                if (isContinue && !_controlTargets[target.Value].Flags.HasFlag(
                        Avm1SyntaxControlTargetFlags.Continuable))
                {
                    _fileBindings[file.Value]
                        .ControlTransfers[statement.Index.Value] =
                        Avm1SyntaxControlTransferBinding.Invalid;
                    AddDiagnostic(
                        "AVM1B2003",
                        file,
                        statement.Span,
                        $"Continue label '{name}' does not target an iteration statement.");
                    return;
                }
            }
            else
            {
                target = FindImplicitTarget(active, isContinue);
                if (!target.IsValid)
                {
                    _fileBindings[file.Value]
                        .ControlTransfers[statement.Index.Value] =
                        Avm1SyntaxControlTransferBinding.Unresolved;
                    AddDiagnostic(
                        "AVM1B2001",
                        file,
                        statement.Span,
                        isContinue
                            ? "Continue is not enclosed by an iteration statement."
                            : "Break is not enclosed by an iteration or switch statement.");
                    return;
                }
            }

            _fileBindings[file.Value].ControlTransfers[statement.Index.Value] =
                Avm1SyntaxControlTransferBinding.Bound(target);
        }

        private Avm1SyntaxControlTargetIndex FindLabeledTarget(
            List<Avm1SyntaxControlTargetIndex> active,
            string name)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var target = _controlTargets[active[i].Value];
                if (target.Kind is Avm1SyntaxControlTargetKind.Label &&
                    target.LabelName.IsValid &&
                    GetName(target.LabelName) == name)
                {
                    return target.Index;
                }
            }
            return Avm1SyntaxControlTargetIndex.Invalid;
        }

        private Avm1SyntaxControlTargetIndex FindImplicitTarget(
            List<Avm1SyntaxControlTargetIndex> active,
            bool isContinue)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var target = _controlTargets[active[i].Value];
                if (target.Kind is Avm1SyntaxControlTargetKind.Label)
                    continue;
                var required = isContinue
                    ? Avm1SyntaxControlTargetFlags.Continuable
                    : Avm1SyntaxControlTargetFlags.Breakable;
                if (target.Flags.HasFlag(required))
                    return target.Index;
            }
            return Avm1SyntaxControlTargetIndex.Invalid;
        }

        private Avm1SyntaxControlTargetIndex AddControlTarget(
            Avm1SyntaxFileIndex file,
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1SyntaxControlTargetKind kind,
            Avm1SyntaxControlTargetFlags flags,
            Avm1StatementIndex owner,
            Avm1StatementIndex targetStatement,
            Avm1SyntaxLocalNameIndex labelName)
        {
            var index = new Avm1SyntaxControlTargetIndex(_controlTargets.Count);
            _controlTargets.Add(new Avm1SyntaxControlTarget(
                index,
                kind,
                flags,
                file,
                codeUnit,
                owner,
                targetStatement,
                labelName));
            var existing = _fileBindings[file.Value].StatementTargets[owner.Value];
            if (existing.IsValid)
            {
                AddDiagnostic(
                    "AVM1B2099",
                    file,
                    GetTree(file).GetStatement(owner).Span,
                    "A statement owns multiple control targets.");
            }
            else
            {
                _fileBindings[file.Value].StatementTargets[owner.Value] = index;
            }
            return index;
        }

        private static Avm1StatementIndex GetUltimateLabeledBody(
            Avm1SyntaxTree tree,
            Avm1StatementIndex statement)
        {
            for (var depth = 0; depth < tree.Statements.Count; depth++)
            {
                var node = tree.GetStatement(statement);
                if (node.Kind is not Avm1StatementSyntaxKind.Labeled)
                    return statement;
                var children = tree.GetChildren(node);
                if (children.Length != 1)
                    return statement;
                statement = children[0];
            }
            return statement;
        }

        private static bool IsLoop(Avm1StatementSyntaxKind kind) =>
            kind is
                Avm1StatementSyntaxKind.While or
                Avm1StatementSyntaxKind.DoWhile or
                Avm1StatementSyntaxKind.For or
                Avm1StatementSyntaxKind.ForIn;

        private void AddCodeUnitFlags(
            Avm1SyntaxCodeUnitIndex codeUnit,
            Avm1SyntaxCodeUnitFlags flags)
        {
            var value = _codeUnits[codeUnit.Value];
            _codeUnits[codeUnit.Value] = value with
            {
                Flags = value.Flags | flags
            };
        }

        private Avm1SyntaxLocalSymbolIndex[] FreezeScopeSymbols()
        {
            var result = new List<Avm1SyntaxLocalSymbolIndex>();
            for (var i = 0; i < _scopes.Count; i++)
            {
                var start = result.Count;
                result.AddRange(_symbolsByScope[i]);
                _scopes[i] = _scopes[i] with
                {
                    Symbols = new Avm1SyntaxLocalSymbolList(
                        start,
                        result.Count - start)
                };
            }
            return result.ToArray();
        }

        private Avm1SyntaxLocalDeclarationIndex[] FreezeSymbolDeclarations()
        {
            var result = new List<Avm1SyntaxLocalDeclarationIndex>();
            for (var i = 0; i < _symbols.Count; i++)
            {
                var start = result.Count;
                result.AddRange(_declarationsBySymbol[i]);
                _symbols[i] = _symbols[i] with
                {
                    Declarations = new Avm1SyntaxLocalDeclarationList(
                        start,
                        result.Count - start)
                };
            }
            return result.ToArray();
        }

        private Avm1SyntaxLocalNameIndex Intern(string value)
        {
            if (_nameIndexes.TryGetValue(value, out var existing))
                return existing;
            var index = new Avm1SyntaxLocalNameIndex(_names.Count);
            _names.Add(value);
            _nameIndexes.Add(value, index);
            return index;
        }

        private string GetName(Avm1SyntaxLocalNameIndex name) =>
            _names[name.Value];

        private Avm1SyntaxTree GetTree(Avm1SyntaxFileIndex file) =>
            _program.GetSource(file).SyntaxTree;

        private void AddDiagnostic(
            string code,
            Avm1SyntaxFileIndex file,
            Avm1TextSpan span,
            string message) =>
            _diagnostics.Add(new Avm1SyntaxBindingDiagnostic(
                code,
                Avm1CompilationDiagnosticSeverity.Error,
                file,
                span,
                message));

        private static List<Avm1ExpressionIndex> CollectParameterDefaults(
            ReadOnlySpan<Avm1ParameterSyntax> parameters)
        {
            var result = new List<Avm1ExpressionIndex>();
            foreach (var parameter in parameters)
            {
                if (parameter.DefaultValue.IsValid)
                    result.Add(parameter.DefaultValue);
            }
            return result;
        }

        private readonly record struct ScopeNameKey(int Scope, int Name);
    }
}
