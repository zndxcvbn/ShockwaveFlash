namespace ShockwaveFlash.Avm1.Compilation.Syntax;

internal sealed class Avm1StatementParser
{
    private readonly Avm1SyntaxTokenStream _tokenStream;
    private readonly IReadOnlyList<Avm1SyntaxToken> _tokens;
    private readonly int _end;
    private readonly List<Avm1ExpressionSyntax> _expressions;
    private readonly List<Avm1ExpressionIndex> _expressionChildren;
    private readonly List<Avm1StatementSyntax> _statements;
    private readonly List<Avm1StatementIndex> _statementChildren;
    private readonly List<Avm1VariableDeclaratorSyntax> _declarators;
    private readonly List<Avm1ParameterSyntax> _parameters;
    private readonly List<Avm1SyntaxDiagnostic> _diagnostics;
    private int _position;

    private Avm1StatementParser(
        Avm1SyntaxTokenStream tokenStream,
        int start,
        int end,
        List<Avm1ExpressionSyntax> expressions,
        List<Avm1ExpressionIndex> expressionChildren,
        List<Avm1StatementSyntax> statements,
        List<Avm1StatementIndex> statementChildren,
        List<Avm1VariableDeclaratorSyntax> declarators,
        List<Avm1ParameterSyntax> parameters,
        List<Avm1SyntaxDiagnostic> diagnostics)
    {
        _tokenStream = tokenStream;
        _tokens = tokenStream.Tokens;
        _position = start;
        _end = end;
        _expressions = expressions;
        _expressionChildren = expressionChildren;
        _statements = statements;
        _statementChildren = statementChildren;
        _declarators = declarators;
        _parameters = parameters;
        _diagnostics = diagnostics;
    }

    public static Avm1StatementIndex ParseBody(
        Avm1SyntaxTokenStream tokenStream,
        int start,
        int end,
        List<Avm1ExpressionSyntax> expressions,
        List<Avm1ExpressionIndex> expressionChildren,
        List<Avm1StatementSyntax> statements,
        List<Avm1StatementIndex> statementChildren,
        List<Avm1VariableDeclaratorSyntax> declarators,
        List<Avm1ParameterSyntax> parameters,
        List<Avm1SyntaxDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(tokenStream);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            end,
            tokenStream.Tokens.Count - 1);
        var parser = new Avm1StatementParser(
            tokenStream,
            start,
            end,
            expressions,
            expressionChildren,
            statements,
            statementChildren,
            declarators,
            parameters,
            diagnostics);
        var root = parser.ParseStatement();
        if (!parser.AtEnd)
        {
            parser.AddDiagnostic(
                "AVM1S3001",
                parser.Current.Span,
                "Unexpected tokens after the method body.");
        }
        return root;
    }

    private bool AtEnd => _position >= _end;

    private Avm1SyntaxToken Current =>
        _tokens[Math.Clamp(_position, 0, _tokens.Count - 1)];

    private Avm1SyntaxKind CurrentKind => AtEnd
        ? Avm1SyntaxKind.EndOfFileToken
        : Current.Kind;

    private Avm1SyntaxKind PeekKind(int offset)
    {
        var index = _position + offset;
        return index >= _end
            ? Avm1SyntaxKind.EndOfFileToken
            : _tokens[index].Kind;
    }

    private Avm1StatementIndex ParseStatement()
    {
        if (AtEnd)
        {
            AddDiagnostic(
                "AVM1S3002",
                EmptySpanAtCurrent(),
                "Expected a statement.");
            return AddStatement(
                Avm1StatementSyntaxKind.Missing,
                span: EmptySpanAtCurrent());
        }

        return CurrentKind switch
        {
            Avm1SyntaxKind.OpenBraceToken => ParseBlock(),
            Avm1SyntaxKind.SemicolonToken => ParseEmptyStatement(),
            Avm1SyntaxKind.VarKeyword or Avm1SyntaxKind.ConstKeyword =>
                ParseVariableStatement(),
            Avm1SyntaxKind.FunctionKeyword => ParseFunctionDeclaration(),
            Avm1SyntaxKind.ReturnKeyword => ParseReturnOrThrow(isThrow: false),
            Avm1SyntaxKind.ThrowKeyword => ParseReturnOrThrow(isThrow: true),
            Avm1SyntaxKind.IfKeyword => ParseIfStatement(),
            _ when IsIfFrameLoadedStart() => ParseIfFrameLoadedStatement(),
            Avm1SyntaxKind.WhileKeyword => ParseWhileStatement(),
            Avm1SyntaxKind.DoKeyword => ParseDoWhileStatement(),
            Avm1SyntaxKind.ForKeyword => ParseForStatement(),
            Avm1SyntaxKind.SwitchKeyword => ParseSwitchStatement(),
            Avm1SyntaxKind.BreakKeyword => ParseBreakOrContinue(isContinue: false),
            Avm1SyntaxKind.ContinueKeyword => ParseBreakOrContinue(isContinue: true),
            Avm1SyntaxKind.WithKeyword => ParseWithStatement(),
            Avm1SyntaxKind.TellTargetKeyword => ParseTellTargetStatement(),
            Avm1SyntaxKind.TryKeyword => ParseTryStatement(),
            Avm1SyntaxKind.HashToken => ParseDirectiveStatement(),
            _ when IsIdentifierName(CurrentKind) &&
                   PeekKind(1) is Avm1SyntaxKind.ColonToken =>
                ParseLabeledStatement(),
            _ => ParseExpressionStatement()
        };
    }

    private Avm1StatementIndex ParseBlock()
    {
        var open = TakeToken();
        var children = new List<Avm1StatementIndex>();
        while (!AtEnd && CurrentKind is not Avm1SyntaxKind.CloseBraceToken)
        {
            var start = _position;
            children.Add(ParseStatement());
            EnsureProgress(start);
        }

        var end = PreviousEnd(GetToken(open).Span.End);
        if (CurrentKind is Avm1SyntaxKind.CloseBraceToken)
            end = Take().Span.End;
        else
            AddExpectedDiagnostic("'}' after the block");
        return AddStatement(
            Avm1StatementSyntaxKind.Block,
            keyword: open,
            children: children,
            span: Avm1TextSpan.FromBounds(GetToken(open).Span.Start, end));
    }

    private Avm1StatementIndex ParseEmptyStatement()
    {
        var token = TakeToken();
        return AddStatement(
            Avm1StatementSyntaxKind.Empty,
            keyword: token,
            span: GetToken(token).Span);
    }

    private Avm1StatementIndex ParseVariableStatement()
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        var terminator = FindStatementEnd(_position);
        var declarators = ParseVariableDeclaratorsRange(_position, terminator);
        _position = terminator;
        var end = ConsumeStatementTerminator(start);
        return AddStatement(
            Avm1StatementSyntaxKind.VariableDeclaration,
            keyword: keyword,
            declarators: declarators,
            span: Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1StatementIndex ParseFunctionDeclaration()
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        var name = IsIdentifierName(CurrentKind)
            ? TakeToken()
            : Avm1SyntaxTokenIndex.Invalid;
        if (!name.IsValid)
            AddExpectedDiagnostic("a nested function name");

        var parameters = Avm1ParameterList.Empty;
        if (CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
        {
            Take();
            var close = FindMatchingClose(
                _position,
                Avm1SyntaxKind.OpenParenthesisToken,
                Avm1SyntaxKind.CloseParenthesisToken);
            parameters = ParseParametersRange(_position, close);
            _position = close;
            if (CurrentKind is Avm1SyntaxKind.CloseParenthesisToken)
                Take();
            else
                AddExpectedDiagnostic("')' after nested function parameters");
        }
        else
        {
            AddExpectedDiagnostic("'(' after the nested function name");
        }

        var returnType = Avm1QualifiedNameSyntax.Missing;
        if (CurrentKind is Avm1SyntaxKind.ColonToken)
        {
            Take();
            var typePosition = _position;
            returnType = ParseQualifiedName(ref typePosition, _end);
            _position = typePosition;
        }

        var body = ParseStatement();
        var end = GetStatement(body).Span.End;
        return AddStatement(
            Avm1StatementSyntaxKind.FunctionDeclaration,
            keyword: keyword,
            nameToken: name,
            parameters: parameters,
            declaredType: returnType,
            children: [body],
            span: Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1StatementIndex ParseReturnOrThrow(bool isThrow)
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        var expression = Avm1ExpressionIndex.Invalid;
        if (!AtEnd &&
            CurrentKind is not Avm1SyntaxKind.SemicolonToken and
            not Avm1SyntaxKind.CloseBraceToken &&
            (isThrow || !HasLineBreakBefore(_position)))
        {
            var terminator = FindStatementEnd(_position);
            expression = ParseExpressionRange(_position, terminator, required: true);
            _position = terminator;
        }
        else if (isThrow)
        {
            AddExpectedDiagnostic("an expression after 'throw'");
        }

        var end = ConsumeStatementTerminator(start);
        return AddStatement(
            isThrow
                ? Avm1StatementSyntaxKind.Throw
                : Avm1StatementSyntaxKind.Return,
            keyword: keyword,
            expression: expression,
            span: Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1StatementIndex ParseIfStatement()
    {
        var keyword = TakeToken();
        var condition = ParseParenthesizedExpression("an if condition");
        var thenStatement = ParseStatement();
        var children = new List<Avm1StatementIndex> { thenStatement };
        if (CurrentKind is Avm1SyntaxKind.ElseKeyword)
        {
            Take();
            children.Add(ParseStatement());
        }
        return AddStatement(
            Avm1StatementSyntaxKind.If,
            keyword: keyword,
            expression: condition,
            children: children,
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(children[^1]).Span.End));
    }

    private Avm1StatementIndex ParseIfFrameLoadedStatement()
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        var arguments = new List<Avm1ExpressionIndex>();
        if (CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
        {
            Take();
            var close = FindMatchingClose(
                _position,
                Avm1SyntaxKind.OpenParenthesisToken,
                Avm1SyntaxKind.CloseParenthesisToken);
            arguments = ParseExpressionListRange(_position, close);
            _position = close;
            if (CurrentKind is Avm1SyntaxKind.CloseParenthesisToken)
                Take();
            else
                AddExpectedDiagnostic("')' after ifFrameLoaded arguments");
        }
        else
        {
            AddExpectedDiagnostic("'(' after 'ifFrameLoaded'");
        }

        if (arguments.Count is < 1 or > 2)
        {
            AddDiagnostic(
                "AVM1S3010",
                Avm1TextSpan.FromBounds(start, PreviousEnd(start)),
                "ifFrameLoaded requires one frame argument or a scene and frame argument.");
        }

        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.IfFrameLoaded,
            keyword: keyword,
            expression: arguments.Count > 0
                ? arguments[0]
                : Avm1ExpressionIndex.Invalid,
            secondaryExpression: arguments.Count > 1
                ? arguments[1]
                : Avm1ExpressionIndex.Invalid,
            children: [body],
            span: Avm1TextSpan.FromBounds(start, GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseWhileStatement()
    {
        var keyword = TakeToken();
        var condition = ParseParenthesizedExpression("a while condition");
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.While,
            keyword: keyword,
            expression: condition,
            children: [body],
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseDoWhileStatement()
    {
        var keyword = TakeToken();
        var body = ParseStatement();
        var condition = Avm1ExpressionIndex.Invalid;
        if (CurrentKind is Avm1SyntaxKind.WhileKeyword)
        {
            Take();
            condition = ParseParenthesizedExpression("a do-while condition");
        }
        else
        {
            AddExpectedDiagnostic("'while' after the do body");
        }
        var end = ConsumeStatementTerminator(GetStatement(body).Span.End);
        return AddStatement(
            Avm1StatementSyntaxKind.DoWhile,
            keyword: keyword,
            expression: condition,
            children: [body],
            span: Avm1TextSpan.FromBounds(GetToken(keyword).Span.Start, end));
    }

    private Avm1StatementIndex ParseForStatement()
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        if (CurrentKind is not Avm1SyntaxKind.OpenParenthesisToken)
        {
            AddExpectedDiagnostic("'(' after 'for'");
            var missingBody = ParseStatement();
            return AddStatement(
                Avm1StatementSyntaxKind.For,
                keyword: keyword,
                children: [missingBody],
                span: Avm1TextSpan.FromBounds(
                    start,
                    GetStatement(missingBody).Span.End));
        }

        Take();
        var headerStart = _position;
        var close = FindMatchingClose(
            headerStart,
            Avm1SyntaxKind.OpenParenthesisToken,
            Avm1SyntaxKind.CloseParenthesisToken);
        var semicolons = FindTopLevelTokens(
            headerStart,
            close,
            Avm1SyntaxKind.SemicolonToken);
        var inToken = FindTopLevelToken(
            headerStart,
            close,
            Avm1SyntaxKind.InKeyword);
        _position = close;
        if (CurrentKind is Avm1SyntaxKind.CloseParenthesisToken)
            Take();
        else
            AddExpectedDiagnostic("')' after the for header");

        if (semicolons.Count == 0 && inToken >= 0)
        {
            return ParseForInStatement(
                keyword,
                start,
                headerStart,
                inToken,
                close);
        }

        if (semicolons.Count != 2)
        {
            AddDiagnostic(
                "AVM1S3003",
                SpanForRange(headerStart, close),
                "A for statement requires two top-level semicolons.");
        }
        var firstSemicolon = semicolons.Count > 0 ? semicolons[0] : close;
        var secondSemicolon = semicolons.Count > 1 ? semicolons[1] : close;
        var declarators = Avm1VariableDeclaratorList.Empty;
        var initializerExpressions = new List<Avm1ExpressionIndex>();
        if (headerStart < firstSemicolon)
        {
            if (_tokens[headerStart].Kind is
                Avm1SyntaxKind.VarKeyword or Avm1SyntaxKind.ConstKeyword)
            {
                declarators = ParseVariableDeclaratorsRange(
                    headerStart + 1,
                    firstSemicolon);
            }
            else
            {
                initializerExpressions.AddRange(ParseExpressionListRange(
                    headerStart,
                    firstSemicolon));
            }
        }
        var condition = ParseExpressionRange(
            Math.Min(firstSemicolon + 1, close),
            secondSemicolon,
            required: false);
        var updates = ParseExpressionListRange(
            Math.Min(secondSemicolon + 1, close),
            close);
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.For,
            keyword: keyword,
            expression: condition,
            initializers: initializerExpressions,
            expressions: updates,
            declarators: declarators,
            children: [body],
            span: Avm1TextSpan.FromBounds(start, GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseForInStatement(
        Avm1SyntaxTokenIndex keyword,
        int start,
        int headerStart,
        int inToken,
        int close)
    {
        var declarators = Avm1VariableDeclaratorList.Empty;
        var target = Avm1ExpressionIndex.Invalid;
        if (headerStart < inToken &&
            _tokens[headerStart].Kind is
                Avm1SyntaxKind.VarKeyword or Avm1SyntaxKind.ConstKeyword)
        {
            declarators = ParseVariableDeclaratorsRange(headerStart + 1, inToken);
        }
        else
        {
            target = ParseExpressionRange(headerStart, inToken, required: true);
        }
        var collection = ParseExpressionRange(inToken + 1, close, required: true);
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.ForIn,
            keyword: keyword,
            expression: collection,
            secondaryExpression: target,
            declarators: declarators,
            children: [body],
            span: Avm1TextSpan.FromBounds(start, GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseSwitchStatement()
    {
        var keyword = TakeToken();
        var selector = ParseParenthesizedExpression("a switch selector");
        var sections = new List<Avm1StatementIndex>();
        if (CurrentKind is not Avm1SyntaxKind.OpenBraceToken)
        {
            AddExpectedDiagnostic("'{' after the switch selector");
            return AddStatement(
                Avm1StatementSyntaxKind.Switch,
                keyword: keyword,
                expression: selector,
                span: Avm1TextSpan.FromBounds(
                    GetToken(keyword).Span.Start,
                    PreviousEnd(GetToken(keyword).Span.End)));
        }

        Take();
        while (!AtEnd && CurrentKind is not Avm1SyntaxKind.CloseBraceToken)
        {
            if (CurrentKind is not Avm1SyntaxKind.CaseKeyword and
                not Avm1SyntaxKind.DefaultKeyword)
            {
                AddDiagnostic(
                    "AVM1S3004",
                    Current.Span,
                    "Expected 'case' or 'default' in the switch body.");
                var skipped = ParseStatement();
                sections.Add(AddStatement(
                    Avm1StatementSyntaxKind.SwitchSection,
                    children: [skipped],
                    span: GetStatement(skipped).Span));
                continue;
            }

            sections.Add(ParseSwitchSection());
        }

        var end = PreviousEnd(GetToken(keyword).Span.End);
        if (CurrentKind is Avm1SyntaxKind.CloseBraceToken)
            end = Take().Span.End;
        else
            AddExpectedDiagnostic("'}' after the switch body");
        return AddStatement(
            Avm1StatementSyntaxKind.Switch,
            keyword: keyword,
            expression: selector,
            children: sections,
            span: Avm1TextSpan.FromBounds(GetToken(keyword).Span.Start, end));
    }

    private Avm1StatementIndex ParseSwitchSection()
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        var flags = Avm1StatementSyntaxFlags.None;
        var expression = Avm1ExpressionIndex.Invalid;
        if (GetToken(keyword).Kind is Avm1SyntaxKind.DefaultKeyword)
        {
            flags |= Avm1StatementSyntaxFlags.HasDefaultLabel;
        }
        else
        {
            var colon = FindTopLevelToken(
                _position,
                _end,
                Avm1SyntaxKind.ColonToken,
                stopAtSwitchBoundary: true);
            expression = ParseExpressionRange(_position, colon, required: true);
            _position = colon;
        }

        if (CurrentKind is Avm1SyntaxKind.ColonToken)
            Take();
        else
            AddExpectedDiagnostic("':' after the switch label");

        var statements = new List<Avm1StatementIndex>();
        while (!AtEnd &&
               CurrentKind is not Avm1SyntaxKind.CaseKeyword and
               not Avm1SyntaxKind.DefaultKeyword and
               not Avm1SyntaxKind.CloseBraceToken)
        {
            var position = _position;
            statements.Add(ParseStatement());
            EnsureProgress(position);
        }
        var end = statements.Count > 0
            ? GetStatement(statements[^1]).Span.End
            : PreviousEnd(GetToken(keyword).Span.End);
        return AddStatement(
            Avm1StatementSyntaxKind.SwitchSection,
            flags: flags,
            keyword: keyword,
            expression: expression,
            children: statements,
            span: Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1StatementIndex ParseBreakOrContinue(bool isContinue)
    {
        var keyword = TakeToken();
        var start = GetToken(keyword).Span.Start;
        var label = Avm1SyntaxTokenIndex.Invalid;
        if (!AtEnd &&
            !HasLineBreakBefore(_position) &&
            IsIdentifierName(CurrentKind))
        {
            label = TakeToken();
        }
        var end = ConsumeStatementTerminator(start);
        return AddStatement(
            isContinue
                ? Avm1StatementSyntaxKind.Continue
                : Avm1StatementSyntaxKind.Break,
            keyword: keyword,
            nameToken: label,
            span: Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1StatementIndex ParseWithStatement()
    {
        var keyword = TakeToken();
        var expression = ParseParenthesizedExpression("a with expression");
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.With,
            keyword: keyword,
            expression: expression,
            children: [body],
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseTellTargetStatement()
    {
        var keyword = TakeToken();
        var expression = ParseParenthesizedExpression("a tellTarget expression");
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.TellTarget,
            keyword: keyword,
            expression: expression,
            children: [body],
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseTryStatement()
    {
        var keyword = TakeToken();
        var children = new List<Avm1StatementIndex> { ParseStatement() };
        if (CurrentKind is Avm1SyntaxKind.CatchKeyword)
            children.Add(ParseCatchClause());
        if (CurrentKind is Avm1SyntaxKind.FinallyKeyword)
            children.Add(ParseFinallyClause());
        if (children.Count == 1)
            AddExpectedDiagnostic("'catch' or 'finally' after the try body");
        return AddStatement(
            Avm1StatementSyntaxKind.Try,
            keyword: keyword,
            children: children,
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(children[^1]).Span.End));
    }

    private Avm1StatementIndex ParseCatchClause()
    {
        var keyword = TakeToken();
        var parameters = Avm1ParameterList.Empty;
        if (CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
        {
            Take();
            var close = FindMatchingClose(
                _position,
                Avm1SyntaxKind.OpenParenthesisToken,
                Avm1SyntaxKind.CloseParenthesisToken);
            parameters = ParseParametersRange(_position, close);
            _position = close;
            if (CurrentKind is Avm1SyntaxKind.CloseParenthesisToken)
                Take();
            else
                AddExpectedDiagnostic("')' after the catch variable");
        }
        else
        {
            AddExpectedDiagnostic("'(' after 'catch'");
        }
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.CatchClause,
            keyword: keyword,
            parameters: parameters,
            children: [body],
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseFinallyClause()
    {
        var keyword = TakeToken();
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.FinallyClause,
            keyword: keyword,
            children: [body],
            span: Avm1TextSpan.FromBounds(
                GetToken(keyword).Span.Start,
                GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseLabeledStatement()
    {
        var label = TakeToken();
        Take();
        var body = ParseStatement();
        return AddStatement(
            Avm1StatementSyntaxKind.Labeled,
            nameToken: label,
            children: [body],
            span: Avm1TextSpan.FromBounds(
                GetToken(label).Span.Start,
                GetStatement(body).Span.End));
    }

    private Avm1StatementIndex ParseDirectiveStatement()
    {
        var hash = TakeToken();
        var start = GetToken(hash).Span.Start;
        while (!AtEnd && !HasLineBreakBefore(_position))
            Take();
        return AddStatement(
            Avm1StatementSyntaxKind.Directive,
            keyword: hash,
            span: Avm1TextSpan.FromBounds(start, PreviousEnd(start)));
    }

    private Avm1StatementIndex ParseExpressionStatement()
    {
        var start = Current.Span.Start;
        var terminator = FindStatementEnd(_position);
        var expression = ParseExpressionRange(_position, terminator, required: true);
        _position = terminator;
        var end = ConsumeStatementTerminator(start);
        return AddStatement(
            Avm1StatementSyntaxKind.Expression,
            expression: expression,
            span: Avm1TextSpan.FromBounds(start, end));
    }

    private Avm1ExpressionIndex ParseParenthesizedExpression(string expectation)
    {
        if (CurrentKind is not Avm1SyntaxKind.OpenParenthesisToken)
        {
            AddExpectedDiagnostic("'(' before " + expectation);
            return Avm1ExpressionIndex.Invalid;
        }
        Take();
        var close = FindMatchingClose(
            _position,
            Avm1SyntaxKind.OpenParenthesisToken,
            Avm1SyntaxKind.CloseParenthesisToken);
        var expression = ParseExpressionRange(_position, close, required: true);
        _position = close;
        if (CurrentKind is Avm1SyntaxKind.CloseParenthesisToken)
            Take();
        else
            AddExpectedDiagnostic("')' after " + expectation);
        return expression;
    }

    private Avm1VariableDeclaratorList ParseVariableDeclaratorsRange(
        int start,
        int end)
    {
        var declaratorStart = _declarators.Count;
        var position = start;
        while (position < end)
        {
            var segmentEnd = FindTopLevelToken(
                position,
                end,
                Avm1SyntaxKind.CommaToken);
            if (segmentEnd < 0)
                segmentEnd = end;
            var itemStart = position;
            var name = IsIdentifierName(_tokens[position].Kind)
                ? new Avm1SyntaxTokenIndex(position++)
                : Avm1SyntaxTokenIndex.Invalid;
            if (!name.IsValid)
            {
                AddDiagnostic(
                    "AVM1S3005",
                    _tokens[position].Span,
                    "Expected a variable name.");
                position = Math.Min(position + 1, segmentEnd);
            }

            var declaredType = Avm1QualifiedNameSyntax.Missing;
            if (position < segmentEnd &&
                _tokens[position].Kind is Avm1SyntaxKind.ColonToken)
            {
                position++;
                declaredType = ParseQualifiedName(ref position, segmentEnd);
            }

            var initializer = Avm1ExpressionIndex.Invalid;
            var initializerSpan = Avm1TextSpan.Invalid;
            if (position < segmentEnd &&
                _tokens[position].Kind is Avm1SyntaxKind.EqualsToken)
            {
                var expressionStart = ++position;
                initializer = ParseExpressionRange(
                    expressionStart,
                    segmentEnd,
                    required: true);
                initializerSpan = SpanForRange(expressionStart, segmentEnd);
                position = segmentEnd;
            }
            else if (position < segmentEnd)
            {
                AddDiagnostic(
                    "AVM1S3006",
                    _tokens[position].Span,
                    "Unexpected token in a variable declaration.");
                position = segmentEnd;
            }

            if (name.IsValid)
            {
                var itemEnd = initializerSpan.IsValid
                    ? initializerSpan.End
                    : declaredType.IsValid
                        ? declaredType.Span.End
                        : GetToken(name).Span.End;
                _declarators.Add(new Avm1VariableDeclaratorSyntax(
                    name,
                    declaredType,
                    initializer,
                    initializerSpan,
                    Avm1TextSpan.FromBounds(
                        _tokens[itemStart].Span.Start,
                        itemEnd)));
            }
            position = segmentEnd < end ? segmentEnd + 1 : end;
        }
        return new Avm1VariableDeclaratorList(
            declaratorStart,
            _declarators.Count - declaratorStart);
    }

    private Avm1ParameterList ParseParametersRange(int start, int end)
    {
        var parameterStart = _parameters.Count;
        var position = start;
        while (position < end)
        {
            var segmentEnd = FindTopLevelToken(
                position,
                end,
                Avm1SyntaxKind.CommaToken);
            if (segmentEnd < 0)
                segmentEnd = end;
            var itemStart = position;
            var name = IsIdentifierName(_tokens[position].Kind)
                ? new Avm1SyntaxTokenIndex(position++)
                : Avm1SyntaxTokenIndex.Invalid;
            if (!name.IsValid)
            {
                AddDiagnostic(
                    "AVM1S3007",
                    _tokens[position].Span,
                    "Expected a parameter name.");
                position = Math.Min(position + 1, segmentEnd);
            }
            var declaredType = Avm1QualifiedNameSyntax.Missing;
            if (position < segmentEnd &&
                _tokens[position].Kind is Avm1SyntaxKind.ColonToken)
            {
                position++;
                declaredType = ParseQualifiedName(ref position, segmentEnd);
            }
            var defaultValue = Avm1ExpressionIndex.Invalid;
            var defaultSpan = Avm1TextSpan.Invalid;
            if (position < segmentEnd &&
                _tokens[position].Kind is Avm1SyntaxKind.EqualsToken)
            {
                var expressionStart = ++position;
                defaultValue = ParseExpressionRange(
                    expressionStart,
                    segmentEnd,
                    required: true);
                defaultSpan = SpanForRange(expressionStart, segmentEnd);
            }
            if (name.IsValid)
            {
                var itemEnd = defaultSpan.IsValid
                    ? defaultSpan.End
                    : declaredType.IsValid
                        ? declaredType.Span.End
                        : GetToken(name).Span.End;
                _parameters.Add(new Avm1ParameterSyntax(
                    name,
                    declaredType,
                    defaultValue,
                    defaultSpan,
                    Avm1TextSpan.FromBounds(
                        _tokens[itemStart].Span.Start,
                        itemEnd)));
            }
            position = segmentEnd < end ? segmentEnd + 1 : end;
        }
        return new Avm1ParameterList(
            parameterStart,
            _parameters.Count - parameterStart);
    }

    private Avm1QualifiedNameSyntax ParseQualifiedName(
        ref int position,
        int end)
    {
        var tokenStart = position;
        if (position >= end || !IsTypeName(_tokens[position].Kind))
        {
            AddDiagnostic(
                "AVM1S3008",
                position < _tokens.Count
                    ? _tokens[position].Span
                    : EmptySpanAtCurrent(),
                "Expected a type name.");
            return Avm1QualifiedNameSyntax.Missing;
        }
        var start = _tokens[position++].Span.Start;
        while (position + 1 < end &&
               _tokens[position].Kind is Avm1SyntaxKind.DotToken &&
               IsTypeName(_tokens[position + 1].Kind))
        {
            position += 2;
        }
        return new Avm1QualifiedNameSyntax(
            new Avm1SyntaxTokenList(tokenStart, position - tokenStart),
            Avm1TextSpan.FromBounds(start, _tokens[position - 1].Span.End));
    }

    private Avm1ExpressionIndex ParseExpressionRange(
        int start,
        int end,
        bool required)
    {
        if (start >= end && !required)
            return Avm1ExpressionIndex.Invalid;
        return Avm1ExpressionParser.Parse(
            _tokenStream,
            start,
            end,
            _expressions,
            _expressionChildren,
            _statements,
            _statementChildren,
            _declarators,
            _parameters,
            _diagnostics);
    }

    private List<Avm1ExpressionIndex> ParseExpressionListRange(
        int start,
        int end)
    {
        var result = new List<Avm1ExpressionIndex>();
        var position = start;
        while (position < end)
        {
            var separator = FindTopLevelToken(
                position,
                end,
                Avm1SyntaxKind.CommaToken);
            if (separator < 0)
                separator = end;
            result.Add(ParseExpressionRange(position, separator, required: true));
            position = separator < end ? separator + 1 : end;
        }
        return result;
    }

    private int FindStatementEnd(int start)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = start; index < _end; index++)
        {
            var kind = _tokens[index].Kind;
            if (parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                if (kind is Avm1SyntaxKind.SemicolonToken or
                    Avm1SyntaxKind.CloseBraceToken)
                {
                    return index;
                }
                if (kind is Avm1SyntaxKind.ElseKeyword or
                    Avm1SyntaxKind.CaseKeyword or
                    Avm1SyntaxKind.DefaultKeyword)
                {
                    return index;
                }
                if (index > start &&
                    HasLineBreakBefore(index) &&
                    IsAutomaticSemicolonBoundary(kind, _tokens[index - 1].Kind))
                {
                    return index;
                }
            }
            UpdateDepths(
                kind,
                ref parenthesisDepth,
                ref bracketDepth,
                ref braceDepth);
        }
        return _end;
    }

    private int FindMatchingClose(
        int start,
        Avm1SyntaxKind openKind,
        Avm1SyntaxKind closeKind)
    {
        var depth = 1;
        for (var index = start; index < _end; index++)
        {
            var kind = _tokens[index].Kind;
            if (kind == openKind)
                depth++;
            else if (kind == closeKind && --depth == 0)
                return index;
        }
        return _end;
    }

    private List<int> FindTopLevelTokens(
        int start,
        int end,
        Avm1SyntaxKind kind)
    {
        var result = new List<int>();
        var position = start;
        while (position < end)
        {
            var found = FindTopLevelToken(position, end, kind);
            if (found < 0)
                break;
            result.Add(found);
            position = found + 1;
        }
        return result;
    }

    private int FindTopLevelToken(
        int start,
        int end,
        Avm1SyntaxKind kind,
        bool stopAtSwitchBoundary = false)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = start; index < end; index++)
        {
            var current = _tokens[index].Kind;
            if (parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                if (current == kind)
                    return index;
                if (stopAtSwitchBoundary &&
                    current is Avm1SyntaxKind.CaseKeyword or
                        Avm1SyntaxKind.DefaultKeyword or
                        Avm1SyntaxKind.CloseBraceToken)
                {
                    return index;
                }
            }
            UpdateDepths(
                current,
                ref parenthesisDepth,
                ref bracketDepth,
                ref braceDepth);
        }
        return -1;
    }

    private int ConsumeStatementTerminator(int fallback)
    {
        if (CurrentKind is Avm1SyntaxKind.SemicolonToken)
            return Take().Span.End;
        return PreviousEnd(fallback);
    }

    private Avm1TextSpan SpanForRange(int start, int end)
    {
        if (start >= end)
            return new Avm1TextSpan(_tokens[Math.Min(start, _tokens.Count - 1)].Span.Start, 0);
        return Avm1TextSpan.FromBounds(
            _tokens[start].Span.Start,
            _tokens[end - 1].Span.End);
    }

    private Avm1StatementIndex AddStatement(
        Avm1StatementSyntaxKind kind,
        Avm1StatementSyntaxFlags flags = Avm1StatementSyntaxFlags.None,
        Avm1SyntaxTokenIndex? keyword = null,
        Avm1SyntaxTokenIndex? nameToken = null,
        Avm1ExpressionIndex? expression = null,
        Avm1ExpressionIndex? secondaryExpression = null,
        IReadOnlyList<Avm1ExpressionIndex>? initializers = null,
        IReadOnlyList<Avm1ExpressionIndex>? expressions = null,
        Avm1VariableDeclaratorList declarators = default,
        Avm1ParameterList parameters = default,
        Avm1QualifiedNameSyntax declaredType = default,
        IReadOnlyList<Avm1StatementIndex>? children = null,
        Avm1TextSpan span = default)
    {
        var initializerStart = _expressionChildren.Count;
        if (initializers is not null)
            _expressionChildren.AddRange(initializers);
        var initializerList = new Avm1ExpressionList(
            initializerStart,
            _expressionChildren.Count - initializerStart);
        var expressionStart = _expressionChildren.Count;
        if (expressions is not null)
            _expressionChildren.AddRange(expressions);
        var expressionList = new Avm1ExpressionList(
            expressionStart,
            _expressionChildren.Count - expressionStart);
        var childStart = _statementChildren.Count;
        if (children is not null)
            _statementChildren.AddRange(children);
        var index = new Avm1StatementIndex(_statements.Count);
        _statements.Add(new Avm1StatementSyntax(
            index,
            kind,
            flags,
            keyword ?? Avm1SyntaxTokenIndex.Invalid,
            nameToken ?? Avm1SyntaxTokenIndex.Invalid,
            expression ?? Avm1ExpressionIndex.Invalid,
            secondaryExpression ?? Avm1ExpressionIndex.Invalid,
            initializerList,
            expressionList,
            declarators,
            parameters,
            declaredType.IsValid
                ? declaredType
                : Avm1QualifiedNameSyntax.Missing,
            new Avm1StatementList(
                childStart,
                _statementChildren.Count - childStart),
            span));
        return index;
    }

    private Avm1StatementSyntax GetStatement(Avm1StatementIndex index) =>
        _statements[index.Value];

    private Avm1SyntaxToken GetToken(Avm1SyntaxTokenIndex index) =>
        _tokens[index.Value];

    private Avm1SyntaxTokenIndex TakeToken()
    {
        var result = new Avm1SyntaxTokenIndex(_position);
        Take();
        return result;
    }

    private Avm1SyntaxToken Take()
    {
        var token = Current;
        if (!AtEnd)
            _position++;
        return token;
    }

    private int PreviousEnd(int fallback) =>
        _position > 0 ? _tokens[_position - 1].Span.End : fallback;

    private Avm1TextSpan EmptySpanAtCurrent() =>
        new(Current.Span.Start, 0);

    private bool HasLineBreakBefore(int tokenIndex)
    {
        foreach (var trivia in _tokenStream.GetLeadingTrivia(_tokens[tokenIndex]))
        {
            if (trivia.Kind is Avm1SyntaxTriviaKind.EndOfLine)
                return true;
        }
        return false;
    }

    private void EnsureProgress(int start)
    {
        if (_position == start && !AtEnd)
            Take();
    }

    private void AddExpectedDiagnostic(string expected) =>
        AddDiagnostic(
            "AVM1S3009",
            EmptySpanAtCurrent(),
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

    private static void UpdateDepths(
        Avm1SyntaxKind kind,
        ref int parenthesisDepth,
        ref int bracketDepth,
        ref int braceDepth)
    {
        switch (kind)
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
    }

    private static bool IsIdentifierName(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.IdentifierToken or
            Avm1SyntaxKind.GetKeyword or
            Avm1SyntaxKind.SetKeyword;

    private bool IsIfFrameLoadedStart() =>
        CurrentKind is Avm1SyntaxKind.IdentifierToken &&
        PeekKind(1) is Avm1SyntaxKind.OpenParenthesisToken &&
        _tokenStream.GetText(Current.Span).SequenceEqual("ifFrameLoaded");

    private static bool IsTypeName(Avm1SyntaxKind kind) =>
        IsIdentifierName(kind) || kind is Avm1SyntaxKind.AsteriskToken;

    private static bool IsDefiniteStatementStart(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.VarKeyword or
            Avm1SyntaxKind.ConstKeyword or
            Avm1SyntaxKind.FunctionKeyword or
            Avm1SyntaxKind.ReturnKeyword or
            Avm1SyntaxKind.ThrowKeyword or
            Avm1SyntaxKind.IfKeyword or
            Avm1SyntaxKind.WhileKeyword or
            Avm1SyntaxKind.DoKeyword or
            Avm1SyntaxKind.ForKeyword or
            Avm1SyntaxKind.SwitchKeyword or
            Avm1SyntaxKind.BreakKeyword or
            Avm1SyntaxKind.ContinueKeyword or
            Avm1SyntaxKind.WithKeyword or
            Avm1SyntaxKind.TellTargetKeyword or
            Avm1SyntaxKind.TryKeyword or
            Avm1SyntaxKind.HashToken or
            Avm1SyntaxKind.PlusPlusToken or
            Avm1SyntaxKind.MinusMinusToken;

    private static bool IsAutomaticSemicolonBoundary(
        Avm1SyntaxKind current,
        Avm1SyntaxKind previous) =>
        CanEndExpression(previous) &&
        (IsDefiniteStatementStart(current) ||
         IsIdentifierName(current) ||
         current is Avm1SyntaxKind.ThisKeyword or
             Avm1SyntaxKind.SuperKeyword or
             Avm1SyntaxKind.NewKeyword or
             Avm1SyntaxKind.NumericLiteralToken or
             Avm1SyntaxKind.StringLiteralToken);

    private static bool CanEndExpression(Avm1SyntaxKind kind) =>
        IsIdentifierName(kind) ||
        kind is Avm1SyntaxKind.ThisKeyword or
            Avm1SyntaxKind.SuperKeyword or
            Avm1SyntaxKind.TrueKeyword or
            Avm1SyntaxKind.FalseKeyword or
            Avm1SyntaxKind.NullKeyword or
            Avm1SyntaxKind.UndefinedKeyword or
            Avm1SyntaxKind.NumericLiteralToken or
            Avm1SyntaxKind.StringLiteralToken or
            Avm1SyntaxKind.CloseParenthesisToken or
            Avm1SyntaxKind.CloseBracketToken or
            Avm1SyntaxKind.CloseBraceToken or
            Avm1SyntaxKind.PlusPlusToken or
            Avm1SyntaxKind.MinusMinusToken;
}
