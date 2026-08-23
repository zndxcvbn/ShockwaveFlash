namespace ShockwaveFlash.Avm1.Compilation.Syntax;

internal sealed class Avm1SyntaxParser
{
    private readonly Avm1SyntaxTokenStream _tokenStream;
    private readonly IReadOnlyList<Avm1SyntaxToken> _tokens;
    private readonly List<Avm1ImportDirectiveSyntax> _imports = [];
    private readonly List<Avm1DirectiveSyntax> _directives = [];
    private readonly List<Avm1TypeDeclarationSyntax> _types = [];
    private readonly List<Avm1QualifiedNameSyntax> _qualifiedNames = [];
    private readonly List<Avm1MemberDeclarationSyntax> _members = [];
    private readonly List<Avm1VariableDeclaratorSyntax> _declarators = [];
    private readonly List<Avm1ParameterSyntax> _parameters = [];
    private readonly List<Avm1ExpressionSyntax> _expressions = [];
    private readonly List<Avm1ExpressionIndex> _expressionChildren = [];
    private readonly List<Avm1StatementSyntax> _statements = [];
    private readonly List<Avm1StatementIndex> _statementChildren = [];
    private readonly List<Avm1SyntaxDiagnostic> _diagnostics;
    private int _position;

    private Avm1SyntaxParser(Avm1SyntaxTokenStream tokenStream)
    {
        _tokenStream = tokenStream;
        _tokens = tokenStream.Tokens;
        _diagnostics = [.. tokenStream.Diagnostics];
    }

    public static Avm1SyntaxTree Parse(Avm1SyntaxTokenStream tokenStream)
    {
        ArgumentNullException.ThrowIfNull(tokenStream);
        return new Avm1SyntaxParser(tokenStream).ParseCompilationUnit();
    }

    private Avm1SyntaxToken Current => Peek(0);

    private Avm1SyntaxKind CurrentKind => Current.Kind;

    private Avm1SyntaxToken Peek(int offset)
    {
        var index = Math.Clamp(_position + offset, 0, _tokens.Count - 1);
        return _tokens[index];
    }

    private Avm1SyntaxTree ParseCompilationUnit()
    {
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken)
        {
            if (CurrentKind is Avm1SyntaxKind.ImportKeyword)
            {
                ParseImport();
                continue;
            }
            if (CurrentKind is Avm1SyntaxKind.HashToken)
            {
                ParseDirective();
                continue;
            }

            var start = _position;
            var modifiers = ParseModifiers();
            if (CurrentKind is Avm1SyntaxKind.ClassKeyword or
                Avm1SyntaxKind.InterfaceKeyword)
            {
                ParseType(start, modifiers);
                continue;
            }

            AddDiagnostic(
                "AVM1S1001",
                Current.Span,
                "Expected an import, directive, class, or interface declaration.");
            RecoverTopLevel();
            EnsureProgress(start);
        }

        return new Avm1SyntaxTree(
            _tokenStream,
            _imports.ToArray(),
            _directives.ToArray(),
            _types.ToArray(),
            _qualifiedNames.ToArray(),
            _members.ToArray(),
            _declarators.ToArray(),
            _parameters.ToArray(),
            _expressions.ToArray(),
            _expressionChildren.ToArray(),
            _statements.ToArray(),
            _statementChildren.ToArray(),
            _diagnostics.ToArray());
    }

    private void ParseImport()
    {
        var start = Current.Span.Start;
        var keyword = TakeToken();
        var name = ParseQualifiedName(allowWildcard: true);
        var end = name.IsValid ? name.Span.End : Current.Span.Start;
        if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
            end = Take().Span.End;
        else if (!CanInsertSemicolon())
            AddExpectedDiagnostic("';' after the import declaration");
        _imports.Add(new Avm1ImportDirectiveSyntax(
            keyword,
            name,
            Avm1TextSpan.FromBounds(start, end)));
    }

    private Avm1DirectiveIndex ParseDirective()
    {
        var start = Current.Span.Start;
        var hash = TakeToken();
        var name = CurrentKind is Avm1SyntaxKind.IdentifierToken
            ? TakeToken()
            : Avm1SyntaxTokenIndex.Invalid;
        if (!name.IsValid)
            AddExpectedDiagnostic("a directive name");

        var argumentStart = Current.Span.Start;
        var end = name.IsValid ? GetToken(name).Span.End : GetToken(hash).Span.End;
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken &&
               !HasLineBreakBeforeCurrent())
        {
            end = Take().Span.End;
        }

        var argumentSpan = argumentStart <= end
            ? Avm1TextSpan.FromBounds(argumentStart, end)
            : Avm1TextSpan.Invalid;
        var directive = new Avm1DirectiveSyntax(
            hash,
            name,
            argumentSpan,
            Avm1TextSpan.FromBounds(start, end));
        var index = new Avm1DirectiveIndex(_directives.Count);
        _directives.Add(directive);

        if ((_tokenStream.Options.Features &
             Avm1SyntaxFeatures.PreprocessorDirectives) == 0)
        {
            AddDiagnostic(
                "AVM1S1008",
                directive.Span,
                "Preprocessor directives are disabled for this parse.");
        }

        return index;
    }

    private void ParseType(int declarationStart, Avm1SyntaxModifiers modifiers)
    {
        var kind = CurrentKind is Avm1SyntaxKind.ClassKeyword
            ? Avm1TypeDeclarationKind.Class
            : Avm1TypeDeclarationKind.Interface;
        var keyword = TakeToken();
        var name = ParseQualifiedName(allowWildcard: false);

        var baseStart = _qualifiedNames.Count;
        if (CurrentKind is Avm1SyntaxKind.ExtendsKeyword)
        {
            Take();
            ParseQualifiedNameList(
                kind is Avm1TypeDeclarationKind.Interface,
                Avm1SyntaxKind.ImplementsKeyword,
                Avm1SyntaxKind.OpenBraceToken);
        }
        var baseTypes = new Avm1QualifiedNameList(
            baseStart,
            _qualifiedNames.Count - baseStart);

        var implementedStart = _qualifiedNames.Count;
        if (CurrentKind is Avm1SyntaxKind.ImplementsKeyword)
        {
            Take();
            ParseQualifiedNameList(
                allowMultiple: true,
                Avm1SyntaxKind.OpenBraceToken,
                Avm1SyntaxKind.EndOfFileToken);
        }
        var implementedTypes = new Avm1QualifiedNameList(
            implementedStart,
            _qualifiedNames.Count - implementedStart);

        if (CurrentKind is not Avm1SyntaxKind.OpenBraceToken)
        {
            AddExpectedDiagnostic("'{' to begin the type body");
            RecoverTopLevel();
            var failedEnd = PreviousEnd(declarationStart);
            _types.Add(new Avm1TypeDeclarationSyntax(
                kind,
                modifiers,
                keyword,
                name,
                baseTypes,
                implementedTypes,
                Avm1MemberDeclarationList.Empty,
                Avm1TextSpan.Invalid,
                Avm1TextSpan.FromBounds(declarationStart, failedEnd)));
            return;
        }

        var openBrace = Take();
        var memberStart = _members.Count;
        var simpleNameToken = LastNameToken(name);
        while (CurrentKind is not Avm1SyntaxKind.CloseBraceToken and
               not Avm1SyntaxKind.EndOfFileToken)
        {
            ParseMember(simpleNameToken);
        }

        var bodyEnd = Current.Span.Start;
        var declarationEnd = bodyEnd;
        if (CurrentKind is Avm1SyntaxKind.CloseBraceToken)
        {
            var closeBrace = Take();
            bodyEnd = closeBrace.Span.End;
            declarationEnd = bodyEnd;
        }
        else
        {
            AddDiagnostic(
                "AVM1S1002",
                Avm1TextSpan.FromBounds(openBrace.Span.Start, bodyEnd),
                "Unterminated type body.");
        }

        _types.Add(new Avm1TypeDeclarationSyntax(
            kind,
            modifiers,
            keyword,
            name,
            baseTypes,
            implementedTypes,
            new Avm1MemberDeclarationList(
                memberStart,
                _members.Count - memberStart),
            Avm1TextSpan.FromBounds(openBrace.Span.Start, bodyEnd),
            Avm1TextSpan.FromBounds(declarationStart, declarationEnd)));
    }

    private void ParseMember(Avm1SyntaxTokenIndex typeNameToken)
    {
        var startPosition = _position;
        if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
        {
            Take();
            return;
        }
        if (CurrentKind is Avm1SyntaxKind.HashToken)
        {
            var start = Current.Span.Start;
            var directive = ParseDirective();
            var value = _directives[directive.Value];
            _members.Add(new Avm1MemberDeclarationSyntax(
                Avm1MemberDeclarationKind.Directive,
                Avm1SyntaxModifiers.None,
                value.HashToken,
                value.NameToken,
                Avm1VariableDeclaratorList.Empty,
                Avm1ParameterList.Empty,
                Avm1QualifiedNameSyntax.Missing,
                directive,
                Avm1StatementIndex.Invalid,
                Avm1TextSpan.Invalid,
                Avm1TextSpan.FromBounds(start, value.Span.End)));
            return;
        }

        var declarationStart = Current.Span.Start;
        var modifiers = ParseModifiers();
        switch (CurrentKind)
        {
            case Avm1SyntaxKind.VarKeyword:
            case Avm1SyntaxKind.ConstKeyword:
                ParseField(declarationStart, modifiers);
                return;
            case Avm1SyntaxKind.FunctionKeyword:
                ParseMethod(declarationStart, modifiers, typeNameToken);
                return;
            default:
                AddDiagnostic(
                    "AVM1S1003",
                    Current.Span,
                    "Expected a field, method, or directive declaration.");
                RecoverMember(declarationStart, modifiers);
                EnsureProgress(startPosition);
                return;
        }
    }

    private void ParseField(int declarationStart, Avm1SyntaxModifiers modifiers)
    {
        var keyword = TakeToken();
        var declaratorStart = _declarators.Count;
        var end = GetToken(keyword).Span.End;

        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken and
               not Avm1SyntaxKind.CloseBraceToken)
        {
            var declaratorPosition = _position;
            var declaratorStartOffset = Current.Span.Start;
            var name = TakeIdentifier("a variable name");
            var type = Avm1QualifiedNameSyntax.Missing;
            if (CurrentKind is Avm1SyntaxKind.ColonToken)
            {
                Take();
                type = ParseQualifiedName(allowWildcard: true);
            }

            var initializer = ParsedExpression.Missing;
            if (CurrentKind is Avm1SyntaxKind.EqualsToken)
            {
                Take();
                initializer = ConsumeExpressionUntil(
                    Avm1SyntaxKind.CommaToken,
                    Avm1SyntaxKind.SemicolonToken);
            }

            var declaratorEnd = initializer.Span.IsValid
                ? initializer.Span.End
                : PreviousEnd(declaratorStartOffset);
            if (name.IsValid)
            {
                _declarators.Add(new Avm1VariableDeclaratorSyntax(
                    name,
                    type,
                    initializer.Root,
                    initializer.Span,
                    Avm1TextSpan.FromBounds(
                        declaratorStartOffset,
                        declaratorEnd)));
            }
            EnsureProgress(declaratorPosition);
            end = Math.Max(end, declaratorEnd);

            if (CurrentKind is Avm1SyntaxKind.CommaToken)
            {
                end = Take().Span.End;
                continue;
            }
            break;
        }

        if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
            end = Take().Span.End;
        else if (!CanInsertSemicolon())
            AddExpectedDiagnostic("';' after the field declaration");

        _members.Add(new Avm1MemberDeclarationSyntax(
            Avm1MemberDeclarationKind.Field,
            modifiers,
            keyword,
            Avm1SyntaxTokenIndex.Invalid,
            new Avm1VariableDeclaratorList(
                declaratorStart,
                _declarators.Count - declaratorStart),
            Avm1ParameterList.Empty,
            Avm1QualifiedNameSyntax.Missing,
            Avm1DirectiveIndex.Invalid,
            Avm1StatementIndex.Invalid,
            Avm1TextSpan.Invalid,
            Avm1TextSpan.FromBounds(declarationStart, end)));
    }

    private void ParseMethod(
        int declarationStart,
        Avm1SyntaxModifiers modifiers,
        Avm1SyntaxTokenIndex typeNameToken)
    {
        var keyword = TakeToken();
        var kind = Avm1MemberDeclarationKind.Method;
        if (CurrentKind is Avm1SyntaxKind.GetKeyword &&
            Peek(1).Kind is Avm1SyntaxKind.IdentifierToken)
        {
            Take();
            kind = Avm1MemberDeclarationKind.Getter;
        }
        else if (CurrentKind is Avm1SyntaxKind.SetKeyword &&
                 Peek(1).Kind is Avm1SyntaxKind.IdentifierToken)
        {
            Take();
            kind = Avm1MemberDeclarationKind.Setter;
        }

        var name = TakeIdentifier("a function name");
        if (kind is Avm1MemberDeclarationKind.Method &&
            name.IsValid && typeNameToken.IsValid &&
            TokenTextEquals(name, typeNameToken))
        {
            kind = Avm1MemberDeclarationKind.Constructor;
        }

        var parameterStart = _parameters.Count;
        if (CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
        {
            Take();
            ParseParameters();
        }
        else
        {
            AddExpectedDiagnostic("'(' after the function name");
        }
        var parameters = new Avm1ParameterList(
            parameterStart,
            _parameters.Count - parameterStart);

        var returnType = Avm1QualifiedNameSyntax.Missing;
        if (CurrentKind is Avm1SyntaxKind.ColonToken)
        {
            Take();
            returnType = ParseQualifiedName(allowWildcard: true);
        }

        var bodyRoot = Avm1StatementIndex.Invalid;
        var body = Avm1TextSpan.Invalid;
        var end = PreviousEnd(declarationStart);
        if (CurrentKind is Avm1SyntaxKind.OpenBraceToken)
        {
            var bodyTokenStart = _position;
            body = ConsumeBalancedBlock();
            bodyRoot = Avm1StatementParser.ParseBody(
                _tokenStream,
                bodyTokenStart,
                _position,
                _expressions,
                _expressionChildren,
                _statements,
                _statementChildren,
                _declarators,
                _parameters,
                _diagnostics);
            end = body.End;
        }
        else if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
        {
            end = Take().Span.End;
        }
        else
        {
            AddExpectedDiagnostic("a function body or ';'");
            RecoverToMemberBoundary();
            end = PreviousEnd(declarationStart);
        }

        _members.Add(new Avm1MemberDeclarationSyntax(
            kind,
            modifiers,
            keyword,
            name,
            Avm1VariableDeclaratorList.Empty,
            parameters,
            returnType,
            Avm1DirectiveIndex.Invalid,
            bodyRoot,
            body,
            Avm1TextSpan.FromBounds(declarationStart, end)));
    }

    private void ParseParameters()
    {
        while (CurrentKind is not Avm1SyntaxKind.CloseParenthesisToken and
               not Avm1SyntaxKind.EndOfFileToken)
        {
            var startPosition = _position;
            var start = Current.Span.Start;
            var name = TakeIdentifier("a parameter name");
            var type = Avm1QualifiedNameSyntax.Missing;
            if (CurrentKind is Avm1SyntaxKind.ColonToken)
            {
                Take();
                type = ParseQualifiedName(allowWildcard: true);
            }

            var defaultValue = ParsedExpression.Missing;
            if (CurrentKind is Avm1SyntaxKind.EqualsToken)
            {
                Take();
                defaultValue = ConsumeExpressionUntil(
                    Avm1SyntaxKind.CommaToken,
                    Avm1SyntaxKind.CloseParenthesisToken);
            }

            var end = defaultValue.Span.IsValid
                ? defaultValue.Span.End
                : PreviousEnd(start);
            if (name.IsValid)
            {
                _parameters.Add(new Avm1ParameterSyntax(
                    name,
                    type,
                    defaultValue.Root,
                    defaultValue.Span,
                    Avm1TextSpan.FromBounds(start, end)));
            }
            EnsureProgress(startPosition);

            if (CurrentKind is Avm1SyntaxKind.CommaToken)
            {
                Take();
                continue;
            }
            break;
        }

        if (CurrentKind is Avm1SyntaxKind.CloseParenthesisToken)
            Take();
        else
            AddExpectedDiagnostic("')' after the parameter list");
    }

    private ParsedExpression ConsumeExpressionUntil(
        Avm1SyntaxKind firstTerminator,
        Avm1SyntaxKind secondTerminator)
    {
        var tokenStart = _position;
        var start = Current.Span.Start;
        var end = start;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken)
        {
            if (parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0 &&
                (CurrentKind == firstTerminator || CurrentKind == secondTerminator ||
                 CurrentKind is Avm1SyntaxKind.CloseBraceToken ||
                 HasLineBreakBeforeCurrent() && IsMemberStart(CurrentKind)))
            {
                break;
            }

            switch (CurrentKind)
            {
                case Avm1SyntaxKind.OpenParenthesisToken:
                    parenthesisDepth++;
                    break;
                case Avm1SyntaxKind.CloseParenthesisToken when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case Avm1SyntaxKind.OpenBracketToken:
                    bracketDepth++;
                    break;
                case Avm1SyntaxKind.CloseBracketToken when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case Avm1SyntaxKind.OpenBraceToken:
                    braceDepth++;
                    break;
                case Avm1SyntaxKind.CloseBraceToken when braceDepth > 0:
                    braceDepth--;
                    break;
            }
            end = Take().Span.End;
        }

        var span = Avm1TextSpan.FromBounds(start, end);
        var root = Avm1ExpressionParser.Parse(
            _tokenStream,
            tokenStart,
            _position,
            _expressions,
            _expressionChildren,
            _statements,
            _statementChildren,
            _declarators,
            _parameters,
            _diagnostics);
        return new ParsedExpression(root, span);
    }

    private Avm1TextSpan ConsumeBalancedBlock()
    {
        var start = Current.Span.Start;
        var depth = 0;
        var end = start;
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken)
        {
            var token = Take();
            end = token.Span.End;
            if (token.Kind is Avm1SyntaxKind.OpenBraceToken)
                depth++;
            else if (token.Kind is Avm1SyntaxKind.CloseBraceToken && --depth == 0)
                return Avm1TextSpan.FromBounds(start, end);
        }

        AddDiagnostic(
            "AVM1S1004",
            Avm1TextSpan.FromBounds(start, end),
            "Unterminated function or statement block.");
        return Avm1TextSpan.FromBounds(start, end);
    }

    private void ParseQualifiedNameList(
        bool allowMultiple,
        Avm1SyntaxKind firstTerminator,
        Avm1SyntaxKind secondTerminator)
    {
        while (CurrentKind != firstTerminator &&
               CurrentKind != secondTerminator &&
               CurrentKind is not Avm1SyntaxKind.EndOfFileToken)
        {
            var start = _position;
            var name = ParseQualifiedName(allowWildcard: false);
            if (name.IsValid)
                _qualifiedNames.Add(name);
            EnsureProgress(start);
            if (!allowMultiple || CurrentKind is not Avm1SyntaxKind.CommaToken)
                break;
            Take();
        }
    }

    private Avm1QualifiedNameSyntax ParseQualifiedName(bool allowWildcard)
    {
        var tokenStart = _position;
        var start = Current.Span.Start;
        if (!IsNameToken(CurrentKind) &&
            !(allowWildcard && CurrentKind is Avm1SyntaxKind.AsteriskToken))
        {
            AddExpectedDiagnostic("a qualified name");
            return Avm1QualifiedNameSyntax.Missing;
        }

        Take();
        while (CurrentKind is Avm1SyntaxKind.DotToken)
        {
            var dotPosition = _position;
            Take();
            if (!IsNameToken(CurrentKind) &&
                !(allowWildcard && CurrentKind is Avm1SyntaxKind.AsteriskToken))
            {
                AddExpectedDiagnostic("an identifier after '.'");
                _position = dotPosition;
                break;
            }
            Take();
            if (Peek(-1).Kind is Avm1SyntaxKind.AsteriskToken)
                break;
        }

        var end = PreviousEnd(start);
        return new Avm1QualifiedNameSyntax(
            new Avm1SyntaxTokenList(tokenStart, _position - tokenStart),
            Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1SyntaxModifiers ParseModifiers()
    {
        var modifiers = Avm1SyntaxModifiers.None;
        while (TryGetModifier(CurrentKind, out var modifier))
        {
            if ((modifiers & modifier) != 0)
            {
                AddDiagnostic(
                    "AVM1S1005",
                    Current.Span,
                    "Duplicate declaration modifier.");
            }
            modifiers |= modifier;
            Take();
        }
        return modifiers;
    }

    private void RecoverTopLevel()
    {
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken)
        {
            if (CurrentKind is Avm1SyntaxKind.ImportKeyword or
                Avm1SyntaxKind.ClassKeyword or
                Avm1SyntaxKind.InterfaceKeyword or
                Avm1SyntaxKind.HashToken ||
                TryGetModifier(CurrentKind, out _))
            {
                return;
            }
            if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
            {
                Take();
                return;
            }
            if (CurrentKind is Avm1SyntaxKind.OpenBraceToken)
            {
                ConsumeBalancedBlock();
                return;
            }
            Take();
        }
    }

    private void RecoverMember(int start, Avm1SyntaxModifiers modifiers)
    {
        var end = Current.Span.Start;
        var body = Avm1TextSpan.Invalid;
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken and
               not Avm1SyntaxKind.CloseBraceToken)
        {
            if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
            {
                end = Take().Span.End;
                break;
            }
            if (CurrentKind is Avm1SyntaxKind.OpenBraceToken)
            {
                body = ConsumeBalancedBlock();
                end = body.End;
                break;
            }
            end = Take().Span.End;
        }

        _members.Add(new Avm1MemberDeclarationSyntax(
            Avm1MemberDeclarationKind.Unknown,
            modifiers,
            Avm1SyntaxTokenIndex.Invalid,
            Avm1SyntaxTokenIndex.Invalid,
            Avm1VariableDeclaratorList.Empty,
            Avm1ParameterList.Empty,
            Avm1QualifiedNameSyntax.Missing,
            Avm1DirectiveIndex.Invalid,
            Avm1StatementIndex.Invalid,
            body,
            Avm1TextSpan.FromBounds(start, end)));
    }

    private void RecoverToMemberBoundary()
    {
        while (CurrentKind is not Avm1SyntaxKind.EndOfFileToken and
               not Avm1SyntaxKind.CloseBraceToken)
        {
            if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
            {
                Take();
                return;
            }
            if (CurrentKind is Avm1SyntaxKind.OpenBraceToken)
            {
                ConsumeBalancedBlock();
                return;
            }
            Take();
        }
    }

    private Avm1SyntaxTokenIndex TakeIdentifier(string expectation)
    {
        if (!IsNameToken(CurrentKind))
        {
            AddExpectedDiagnostic(expectation);
            return Avm1SyntaxTokenIndex.Invalid;
        }
        return TakeToken();
    }

    private Avm1SyntaxTokenIndex TakeToken()
    {
        var result = new Avm1SyntaxTokenIndex(_position);
        Take();
        return result;
    }

    private Avm1SyntaxToken Take()
    {
        var token = Current;
        if (token.Kind is not Avm1SyntaxKind.EndOfFileToken)
            _position++;
        return token;
    }

    private Avm1SyntaxToken GetToken(Avm1SyntaxTokenIndex index) =>
        _tokens[index.Value];

    private Avm1SyntaxTokenIndex LastNameToken(Avm1QualifiedNameSyntax name)
    {
        if (!name.IsValid)
            return Avm1SyntaxTokenIndex.Invalid;
        for (var index = name.Tokens.Start + name.Tokens.Count - 1;
             index >= name.Tokens.Start;
             index--)
        {
            if (IsNameToken(_tokens[index].Kind))
                return new Avm1SyntaxTokenIndex(index);
        }
        return Avm1SyntaxTokenIndex.Invalid;
    }

    private bool TokenTextEquals(
        Avm1SyntaxTokenIndex left,
        Avm1SyntaxTokenIndex right) =>
        _tokenStream.GetText(GetToken(left).Span)
            .SequenceEqual(_tokenStream.GetText(GetToken(right).Span));

    private bool HasLineBreakBeforeCurrent()
    {
        foreach (var trivia in _tokenStream.GetLeadingTrivia(Current))
        {
            if (trivia.Kind is Avm1SyntaxTriviaKind.EndOfLine)
                return true;
        }
        return false;
    }

    private bool CanInsertSemicolon() =>
        CurrentKind is Avm1SyntaxKind.CloseBraceToken or
            Avm1SyntaxKind.EndOfFileToken ||
        HasLineBreakBeforeCurrent();

    private int PreviousEnd(int fallback)
    {
        if (_position == 0)
            return fallback;
        return _tokens[_position - 1].Span.End;
    }

    private void EnsureProgress(int startPosition)
    {
        if (_position == startPosition &&
            CurrentKind is not Avm1SyntaxKind.EndOfFileToken)
        {
            Take();
        }
    }

    private void AddExpectedDiagnostic(string expected) =>
        AddDiagnostic(
            "AVM1S1006",
            Current.Span,
            "Expected " + expected + ".");

    private void AddDiagnostic(
        string code,
        Avm1TextSpan span,
        string message) =>
        _diagnostics.Add(new Avm1SyntaxDiagnostic(
            code,
            Avm1SyntaxDiagnosticSeverity.Error,
            span,
            message));

    private static bool IsNameToken(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.IdentifierToken or
            Avm1SyntaxKind.GetKeyword or
            Avm1SyntaxKind.SetKeyword;

    private static bool IsMemberStart(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.VarKeyword or
            Avm1SyntaxKind.ConstKeyword or
            Avm1SyntaxKind.FunctionKeyword or
            Avm1SyntaxKind.HashToken ||
        TryGetModifier(kind, out _);

    private static bool TryGetModifier(
        Avm1SyntaxKind kind,
        out Avm1SyntaxModifiers modifier)
    {
        modifier = kind switch
        {
            Avm1SyntaxKind.PublicKeyword => Avm1SyntaxModifiers.Public,
            Avm1SyntaxKind.PrivateKeyword => Avm1SyntaxModifiers.Private,
            Avm1SyntaxKind.ProtectedKeyword => Avm1SyntaxModifiers.Protected,
            Avm1SyntaxKind.StaticKeyword => Avm1SyntaxModifiers.Static,
            Avm1SyntaxKind.FinalKeyword => Avm1SyntaxModifiers.Final,
            Avm1SyntaxKind.OverrideKeyword => Avm1SyntaxModifiers.Override,
            Avm1SyntaxKind.DynamicKeyword => Avm1SyntaxModifiers.Dynamic,
            Avm1SyntaxKind.IntrinsicKeyword => Avm1SyntaxModifiers.Intrinsic,
            _ => Avm1SyntaxModifiers.None
        };
        return modifier is not Avm1SyntaxModifiers.None;
    }

    private readonly record struct ParsedExpression(
        Avm1ExpressionIndex Root,
        Avm1TextSpan Span)
    {
        public static readonly ParsedExpression Missing = new(
            Avm1ExpressionIndex.Invalid,
            Avm1TextSpan.Invalid);
    }
}
