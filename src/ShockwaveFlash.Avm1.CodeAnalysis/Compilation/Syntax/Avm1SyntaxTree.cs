namespace ShockwaveFlash.Avm1.Compilation.Syntax;

public sealed class Avm1SyntaxTree
{
    private readonly Avm1ImportDirectiveSyntax[] _imports;
    private readonly Avm1DirectiveSyntax[] _directives;
    private readonly Avm1TypeDeclarationSyntax[] _types;
    private readonly Avm1QualifiedNameSyntax[] _qualifiedNames;
    private readonly Avm1MemberDeclarationSyntax[] _members;
    private readonly Avm1VariableDeclaratorSyntax[] _declarators;
    private readonly Avm1ParameterSyntax[] _parameters;
    private readonly Avm1ExpressionSyntax[] _expressions;
    private readonly Avm1ExpressionIndex[] _expressionChildren;
    private readonly Avm1StatementSyntax[] _statements;
    private readonly Avm1StatementIndex[] _statementChildren;
    private readonly Avm1SyntaxDiagnostic[] _diagnostics;

    internal Avm1SyntaxTree(
        Avm1SyntaxTokenStream tokenStream,
        Avm1ImportDirectiveSyntax[] imports,
        Avm1DirectiveSyntax[] directives,
        Avm1TypeDeclarationSyntax[] types,
        Avm1QualifiedNameSyntax[] qualifiedNames,
        Avm1MemberDeclarationSyntax[] members,
        Avm1VariableDeclaratorSyntax[] declarators,
        Avm1ParameterSyntax[] parameters,
        Avm1ExpressionSyntax[] expressions,
        Avm1ExpressionIndex[] expressionChildren,
        Avm1StatementSyntax[] statements,
        Avm1StatementIndex[] statementChildren,
        Avm1SyntaxDiagnostic[] diagnostics)
    {
        TokenStream = tokenStream;
        _imports = imports;
        _directives = directives;
        _types = types;
        _qualifiedNames = qualifiedNames;
        _members = members;
        _declarators = declarators;
        _parameters = parameters;
        _expressions = expressions;
        _expressionChildren = expressionChildren;
        _statements = statements;
        _statementChildren = statementChildren;
        _diagnostics = diagnostics;
    }

    public Avm1SyntaxTokenStream TokenStream { get; }

    public string Text => TokenStream.Text;

    public Avm1ParseOptions Options => TokenStream.Options;

    public IReadOnlyList<Avm1ImportDirectiveSyntax> Imports => _imports;

    public IReadOnlyList<Avm1DirectiveSyntax> Directives => _directives;

    public IReadOnlyList<Avm1TypeDeclarationSyntax> Types => _types;

    public IReadOnlyList<Avm1ExpressionSyntax> Expressions => _expressions;

    public IReadOnlyList<Avm1StatementSyntax> Statements => _statements;

    public IReadOnlyList<Avm1SyntaxDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic =>
        diagnostic.Severity is Avm1SyntaxDiagnosticSeverity.Error);

    public static Avm1SyntaxTree Parse(
        string text,
        Avm1ParseOptions? options = null) =>
        Avm1SyntaxParser.Parse(Avm1Lexer.Lex(text, options));

    public Avm1SyntaxToken GetToken(Avm1SyntaxTokenIndex index) =>
        TokenStream.Tokens[index.Value];

    public ReadOnlySpan<char> GetText(Avm1TextSpan span) =>
        TokenStream.GetText(span);

    public ReadOnlySpan<char> GetTokenText(Avm1SyntaxTokenIndex index) =>
        GetText(GetToken(index).Span);

    public ReadOnlySpan<char> GetNameText(Avm1QualifiedNameSyntax name) =>
        GetText(name.Span);

    public ReadOnlySpan<Avm1QualifiedNameSyntax> GetBaseTypes(
        Avm1TypeDeclarationSyntax declaration) =>
        _qualifiedNames.AsSpan(
            declaration.BaseTypes.Start,
            declaration.BaseTypes.Count);

    public ReadOnlySpan<Avm1QualifiedNameSyntax> GetImplementedTypes(
        Avm1TypeDeclarationSyntax declaration) =>
        _qualifiedNames.AsSpan(
            declaration.ImplementedTypes.Start,
            declaration.ImplementedTypes.Count);

    public ReadOnlySpan<Avm1MemberDeclarationSyntax> GetMembers(
        Avm1TypeDeclarationSyntax declaration) =>
        _members.AsSpan(declaration.Members.Start, declaration.Members.Count);

    public ReadOnlySpan<Avm1VariableDeclaratorSyntax> GetDeclarators(
        Avm1MemberDeclarationSyntax declaration) =>
        _declarators.AsSpan(
            declaration.Declarators.Start,
            declaration.Declarators.Count);

    public ReadOnlySpan<Avm1ParameterSyntax> GetParameters(
        Avm1MemberDeclarationSyntax declaration) =>
        _parameters.AsSpan(
            declaration.Parameters.Start,
            declaration.Parameters.Count);

    public ReadOnlySpan<Avm1VariableDeclaratorSyntax> GetDeclarators(
        Avm1StatementSyntax statement) =>
        _declarators.AsSpan(
            statement.Declarators.Start,
            statement.Declarators.Count);

    public ReadOnlySpan<Avm1ParameterSyntax> GetParameters(
        Avm1StatementSyntax statement) =>
        _parameters.AsSpan(
            statement.Parameters.Start,
            statement.Parameters.Count);

    public ReadOnlySpan<Avm1ParameterSyntax> GetParameters(
        Avm1ExpressionSyntax expression) =>
        _parameters.AsSpan(
            expression.Parameters.Start,
            expression.Parameters.Count);

    public Avm1DirectiveSyntax GetDirective(Avm1DirectiveIndex index) =>
        _directives[index.Value];

    public Avm1ExpressionSyntax GetExpression(Avm1ExpressionIndex index) =>
        _expressions[index.Value];

    public ReadOnlySpan<Avm1ExpressionIndex> GetChildren(
        Avm1ExpressionSyntax expression) =>
        _expressionChildren.AsSpan(
            expression.Children.Start,
            expression.Children.Count);

    public Avm1StatementSyntax GetStatement(Avm1StatementIndex index) =>
        _statements[index.Value];

    public ReadOnlySpan<Avm1StatementIndex> GetChildren(
        Avm1StatementSyntax statement) =>
        _statementChildren.AsSpan(
            statement.Children.Start,
            statement.Children.Count);

    public ReadOnlySpan<Avm1ExpressionIndex> GetInitializers(
        Avm1StatementSyntax statement) =>
        _expressionChildren.AsSpan(
            statement.Initializers.Start,
            statement.Initializers.Count);

    public ReadOnlySpan<Avm1ExpressionIndex> GetExpressions(
        Avm1StatementSyntax statement) =>
        _expressionChildren.AsSpan(
            statement.Expressions.Start,
            statement.Expressions.Count);
}
