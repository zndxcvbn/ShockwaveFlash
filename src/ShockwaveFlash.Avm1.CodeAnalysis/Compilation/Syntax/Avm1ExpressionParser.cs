namespace ShockwaveFlash.Avm1.Compilation.Syntax;

internal sealed class Avm1ExpressionParser
{
    private readonly Avm1SyntaxTokenStream _tokenStream;
    private readonly IReadOnlyList<Avm1SyntaxToken> _tokens;
    private readonly int _end;
    private readonly List<Avm1ExpressionSyntax> _expressions;
    private readonly List<Avm1ExpressionIndex> _children;
    private readonly List<Avm1StatementSyntax> _statements;
    private readonly List<Avm1StatementIndex> _statementChildren;
    private readonly List<Avm1VariableDeclaratorSyntax> _declarators;
    private readonly List<Avm1ParameterSyntax> _parameters;
    private readonly List<Avm1SyntaxDiagnostic> _diagnostics;
    private int _position;

    private Avm1ExpressionParser(
        Avm1SyntaxTokenStream tokenStream,
        int start,
        int end,
        List<Avm1ExpressionSyntax> expressions,
        List<Avm1ExpressionIndex> children,
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
        _children = children;
        _statements = statements;
        _statementChildren = statementChildren;
        _declarators = declarators;
        _parameters = parameters;
        _diagnostics = diagnostics;
    }

    public static Avm1ExpressionIndex Parse(
        Avm1SyntaxTokenStream tokenStream,
        int start,
        int end,
        List<Avm1ExpressionSyntax> expressions,
        List<Avm1ExpressionIndex> children,
        List<Avm1StatementSyntax> statements,
        List<Avm1StatementIndex> statementChildren,
        List<Avm1VariableDeclaratorSyntax> declarators,
        List<Avm1ParameterSyntax> parameters,
        List<Avm1SyntaxDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(tokenStream);
        ArgumentNullException.ThrowIfNull(expressions);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            end,
            tokenStream.Tokens.Count - 1);

        var parser = new Avm1ExpressionParser(
            tokenStream,
            start,
            end,
            expressions,
            children,
            statements,
            statementChildren,
            declarators,
            parameters,
            diagnostics);
        return parser.ParseRoot();
    }

    private bool AtEnd => _position >= _end;

    private Avm1SyntaxToken Current => _tokens[Math.Min(_position, _end)];

    private Avm1SyntaxKind CurrentKind => AtEnd
        ? Avm1SyntaxKind.EndOfFileToken
        : Current.Kind;

    private Avm1ExpressionIndex ParseRoot()
    {
        if (AtEnd)
        {
            AddDiagnostic(
                "AVM1S2001",
                EmptySpanAtCurrent(),
                "Expected an expression.");
            return AddExpression(
                Avm1ExpressionSyntaxKind.Missing,
                span: EmptySpanAtCurrent());
        }

        var expression = ParseCommaExpression();
        if (!AtEnd)
        {
            var start = Current.Span.Start;
            while (!AtEnd)
                Take();
            AddDiagnostic(
                "AVM1S2002",
                Avm1TextSpan.FromBounds(start, PreviousEnd(start)),
                "Unexpected tokens after the expression.");
        }
        return expression;
    }

    private Avm1ExpressionIndex ParseCommaExpression()
    {
        var left = ParseAssignmentExpression();
        while (CurrentKind is Avm1SyntaxKind.CommaToken)
        {
            var operatorToken = TakeToken();
            var right = ParseAssignmentExpression();
            left = AddExpression(
                Avm1ExpressionSyntaxKind.CommaExpression,
                operatorToken: operatorToken,
                children: [left, right],
                span: SpanFrom(left, right));
        }
        return left;
    }

    private Avm1ExpressionIndex ParseAssignmentExpression()
    {
        var left = ParseConditionalExpression();
        if (!IsAssignmentOperator(CurrentKind))
            return left;

        var operatorToken = TakeToken();
        var right = ParseAssignmentExpression();
        return AddExpression(
            Avm1ExpressionSyntaxKind.AssignmentExpression,
            operatorToken: operatorToken,
            children: [left, right],
            span: SpanFrom(left, right));
    }

    private Avm1ExpressionIndex ParseConditionalExpression()
    {
        var condition = ParseBinaryExpression(1);
        if (CurrentKind is not Avm1SyntaxKind.QuestionToken)
            return condition;

        var operatorToken = TakeToken();
        var whenTrue = ParseAssignmentExpression();
        if (CurrentKind is Avm1SyntaxKind.ColonToken)
            Take();
        else
            AddExpectedDiagnostic("':' in the conditional expression");
        var whenFalse = ParseAssignmentExpression();
        return AddExpression(
            Avm1ExpressionSyntaxKind.ConditionalExpression,
            operatorToken: operatorToken,
            children: [condition, whenTrue, whenFalse],
            span: SpanFrom(condition, whenFalse));
    }

    private Avm1ExpressionIndex ParseBinaryExpression(int minimumPrecedence)
    {
        var left = ParseUnaryExpression();
        while (true)
        {
            var precedence = GetBinaryPrecedence(CurrentKind);
            if (precedence < minimumPrecedence)
                break;
            var operatorToken = TakeToken();
            var right = ParseBinaryExpression(precedence + 1);
            left = AddExpression(
                Avm1ExpressionSyntaxKind.BinaryExpression,
                operatorToken: operatorToken,
                children: [left, right],
                span: SpanFrom(left, right));
        }
        return left;
    }

    private Avm1ExpressionIndex ParseUnaryExpression()
    {
        if (IsPrefixUnaryOperator(CurrentKind))
        {
            var operatorToken = TakeToken();
            var operand = ParseUnaryExpression();
            return AddExpression(
                Avm1ExpressionSyntaxKind.UnaryExpression,
                operatorToken: operatorToken,
                children: [operand],
                span: Avm1TextSpan.FromBounds(
                    GetToken(operatorToken).Span.Start,
                    GetExpression(operand).Span.End));
        }

        return ParsePostfixExpression();
    }

    private Avm1ExpressionIndex ParsePostfixExpression()
    {
        var expression = CurrentKind is Avm1SyntaxKind.NewKeyword
            ? ParseNewExpression()
            : ParsePrimaryExpression();
        return ParsePostfixSuffixes(expression);
    }

    private Avm1ExpressionIndex ParsePostfixSuffixes(
        Avm1ExpressionIndex expression,
        bool allowInvocation = true)
    {
        while (!AtEnd)
        {
            if (CurrentKind is Avm1SyntaxKind.DotToken)
            {
                Take();
                var name = IsPropertyName(CurrentKind)
                    ? TakeToken()
                    : Avm1SyntaxTokenIndex.Invalid;
                if (!name.IsValid)
                {
                    AddExpectedDiagnostic("a property name after '.'");
                    return expression;
                }
                expression = AddExpression(
                    Avm1ExpressionSyntaxKind.MemberAccessExpression,
                    token: name,
                    children: [expression],
                    span: Avm1TextSpan.FromBounds(
                        GetExpression(expression).Span.Start,
                        GetToken(name).Span.End));
                continue;
            }

            if (CurrentKind is Avm1SyntaxKind.OpenBracketToken)
            {
                var start = GetExpression(expression).Span.Start;
                Take();
                var index = ParseCommaExpression();
                var end = ExpectClosingToken(
                    Avm1SyntaxKind.CloseBracketToken,
                    "']' after the element index");
                expression = AddExpression(
                    Avm1ExpressionSyntaxKind.ElementAccessExpression,
                    children: [expression, index],
                    span: Avm1TextSpan.FromBounds(start, end));
                continue;
            }

            if (allowInvocation &&
                CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
            {
                var start = GetExpression(expression).Span.Start;
                var open = TakeToken();
                var arguments = ParseDelimitedExpressions(
                    Avm1SyntaxKind.CloseParenthesisToken,
                    allowOmitted: false);
                var end = ExpectClosingToken(
                    Avm1SyntaxKind.CloseParenthesisToken,
                    "')' after the argument list");
                var invocationChildren = new List<Avm1ExpressionIndex>(
                    arguments.Count + 1)
                {
                    expression
                };
                invocationChildren.AddRange(arguments);
                expression = AddExpression(
                    Avm1ExpressionSyntaxKind.InvocationExpression,
                    token: open,
                    children: invocationChildren,
                    span: Avm1TextSpan.FromBounds(start, end));
                continue;
            }

            if ((CurrentKind is Avm1SyntaxKind.PlusPlusToken or
                 Avm1SyntaxKind.MinusMinusToken) &&
                !HasLineBreakBeforeCurrent())
            {
                var operatorToken = TakeToken();
                expression = AddExpression(
                    Avm1ExpressionSyntaxKind.PostfixUnaryExpression,
                    operatorToken: operatorToken,
                    children: [expression],
                    span: Avm1TextSpan.FromBounds(
                        GetExpression(expression).Span.Start,
                        GetToken(operatorToken).Span.End));
                continue;
            }

            break;
        }
        return expression;
    }

    private Avm1ExpressionIndex ParseNewExpression()
    {
        var operatorToken = TakeToken();
        Avm1ExpressionIndex constructor;
        if (CurrentKind is Avm1SyntaxKind.NewKeyword)
        {
            constructor = ParseNewExpression();
        }
        else
        {
            constructor = ParsePrimaryExpression();
            constructor = ParsePostfixSuffixes(
                constructor,
                allowInvocation: false);
        }

        var children = new List<Avm1ExpressionIndex> { constructor };
        var end = GetExpression(constructor).Span.End;
        if (CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
        {
            Take();
            children.AddRange(ParseDelimitedExpressions(
                Avm1SyntaxKind.CloseParenthesisToken,
                allowOmitted: false));
            end = ExpectClosingToken(
                Avm1SyntaxKind.CloseParenthesisToken,
                "')' after the constructor arguments");
        }

        return AddExpression(
            Avm1ExpressionSyntaxKind.NewExpression,
            operatorToken: operatorToken,
            children: children,
            span: Avm1TextSpan.FromBounds(
                GetToken(operatorToken).Span.Start,
                end));
    }

    private Avm1ExpressionIndex ParsePrimaryExpression()
    {
        if (AtEnd)
        {
            var span = EmptySpanAtCurrent();
            AddDiagnostic(
                "AVM1S2001",
                span,
                "Expected an expression.");
            return AddExpression(
                Avm1ExpressionSyntaxKind.Missing,
                span: span);
        }

        switch (CurrentKind)
        {
            case Avm1SyntaxKind.IdentifierToken:
            case Avm1SyntaxKind.GetKeyword:
            case Avm1SyntaxKind.SetKeyword:
                return AddTokenExpression(
                    Avm1ExpressionSyntaxKind.IdentifierName,
                    TakeToken());
            case Avm1SyntaxKind.ThisKeyword:
                return AddTokenExpression(
                    Avm1ExpressionSyntaxKind.ThisExpression,
                    TakeToken());
            case Avm1SyntaxKind.SuperKeyword:
                return AddTokenExpression(
                    Avm1ExpressionSyntaxKind.SuperExpression,
                    TakeToken());
            case Avm1SyntaxKind.NumericLiteralToken:
            case Avm1SyntaxKind.StringLiteralToken:
            case Avm1SyntaxKind.TrueKeyword:
            case Avm1SyntaxKind.FalseKeyword:
            case Avm1SyntaxKind.NullKeyword:
            case Avm1SyntaxKind.UndefinedKeyword:
                return AddTokenExpression(
                    Avm1ExpressionSyntaxKind.LiteralExpression,
                    TakeToken());
            case Avm1SyntaxKind.OpenParenthesisToken:
                return ParseParenthesizedExpression();
            case Avm1SyntaxKind.OpenBracketToken:
                return ParseArrayLiteral();
            case Avm1SyntaxKind.OpenBraceToken:
                return ParseObjectLiteral();
            case Avm1SyntaxKind.FunctionKeyword:
                return ParseFunctionExpression();
            default:
                var token = TakeToken();
                AddDiagnostic(
                    "AVM1S2003",
                    GetToken(token).Span,
                    "Expected a primary expression.");
                return AddTokenExpression(
                    Avm1ExpressionSyntaxKind.Skipped,
                    token);
        }
    }

    private Avm1ExpressionIndex ParseParenthesizedExpression()
    {
        var open = TakeToken();
        var expression = ParseCommaExpression();
        var end = ExpectClosingToken(
            Avm1SyntaxKind.CloseParenthesisToken,
            "')' after the expression");
        return AddExpression(
            Avm1ExpressionSyntaxKind.ParenthesizedExpression,
            token: open,
            children: [expression],
            span: Avm1TextSpan.FromBounds(GetToken(open).Span.Start, end));
    }

    private Avm1ExpressionIndex ParseArrayLiteral()
    {
        var open = TakeToken();
        var elements = ParseDelimitedExpressions(
            Avm1SyntaxKind.CloseBracketToken,
            allowOmitted: true);
        var end = ExpectClosingToken(
            Avm1SyntaxKind.CloseBracketToken,
            "']' after the array literal");
        return AddExpression(
            Avm1ExpressionSyntaxKind.ArrayLiteralExpression,
            token: open,
            children: elements,
            span: Avm1TextSpan.FromBounds(GetToken(open).Span.Start, end));
    }

    private Avm1ExpressionIndex ParseObjectLiteral()
    {
        var open = TakeToken();
        var properties = new List<Avm1ExpressionIndex>();
        while (!AtEnd && CurrentKind is not Avm1SyntaxKind.CloseBraceToken)
        {
            var propertyStart = Current.Span.Start;
            var name = IsObjectPropertyName(CurrentKind)
                ? TakeToken()
                : Avm1SyntaxTokenIndex.Invalid;
            if (!name.IsValid)
            {
                AddExpectedDiagnostic("an object property name");
                RecoverTo(
                    Avm1SyntaxKind.CommaToken,
                    Avm1SyntaxKind.CloseBraceToken);
            }
            if (CurrentKind is Avm1SyntaxKind.ColonToken)
                Take();
            else
                AddExpectedDiagnostic("':' after the object property name");

            var value = ParseAssignmentExpression();
            properties.Add(AddExpression(
                Avm1ExpressionSyntaxKind.ObjectProperty,
                token: name,
                children: [value],
                span: Avm1TextSpan.FromBounds(
                    propertyStart,
                    GetExpression(value).Span.End)));
            if (CurrentKind is Avm1SyntaxKind.CommaToken)
            {
                Take();
                continue;
            }
            break;
        }

        var end = ExpectClosingToken(
            Avm1SyntaxKind.CloseBraceToken,
            "'}' after the object literal");
        return AddExpression(
            Avm1ExpressionSyntaxKind.ObjectLiteralExpression,
            token: open,
            children: properties,
            span: Avm1TextSpan.FromBounds(GetToken(open).Span.Start, end));
    }

    private Avm1ExpressionIndex ParseFunctionExpression()
    {
        var function = TakeToken();
        var name = IsIdentifierName(CurrentKind)
            ? TakeToken()
            : Avm1SyntaxTokenIndex.Invalid;
        var parameterTokens = Avm1SyntaxTokenList.Empty;
        var parameters = Avm1ParameterList.Empty;
        if (CurrentKind is Avm1SyntaxKind.OpenParenthesisToken)
        {
            Take();
            var parameterStart = _position;
            SkipBalancedContent(
                Avm1SyntaxKind.OpenParenthesisToken,
                Avm1SyntaxKind.CloseParenthesisToken,
                "Unterminated function-expression parameter list.");
            parameterTokens = new Avm1SyntaxTokenList(
                parameterStart,
                Math.Max(0, _position - parameterStart - 1));
            parameters = ParseParametersRange(
                parameterStart,
                parameterStart + parameterTokens.Count);
        }
        else
        {
            AddExpectedDiagnostic("'(' after 'function'");
        }

        var returnType = Avm1QualifiedNameSyntax.Missing;
        if (CurrentKind is Avm1SyntaxKind.ColonToken)
        {
            Take();
            returnType = ParseQualifiedTypeName();
        }

        var bodyRoot = Avm1StatementIndex.Invalid;
        var body = Avm1TextSpan.Invalid;
        if (CurrentKind is Avm1SyntaxKind.OpenBraceToken)
        {
            var bodyTokenStart = _position;
            body = ConsumeBalancedBlock();
            bodyRoot = Avm1StatementParser.ParseBody(
                _tokenStream,
                bodyTokenStart,
                _position,
                _expressions,
                _children,
                _statements,
                _statementChildren,
                _declarators,
                _parameters,
                _diagnostics);
        }
        else
            AddExpectedDiagnostic("a function-expression body");
        var end = body.IsValid
            ? body.End
            : PreviousEnd(GetToken(function).Span.End);
        return AddExpression(
            Avm1ExpressionSyntaxKind.FunctionExpression,
            token: name,
            operatorToken: function,
            auxiliaryTokens: parameterTokens,
            parameters: parameters,
            declaredType: returnType,
            body: bodyRoot,
            bodySpan: body,
            span: Avm1TextSpan.FromBounds(
                GetToken(function).Span.Start,
                end));
    }

    private List<Avm1ExpressionIndex> ParseDelimitedExpressions(
        Avm1SyntaxKind closeKind,
        bool allowOmitted)
    {
        var result = new List<Avm1ExpressionIndex>();
        while (!AtEnd && CurrentKind != closeKind)
        {
            if (allowOmitted && CurrentKind is Avm1SyntaxKind.CommaToken)
            {
                result.Add(AddExpression(
                    Avm1ExpressionSyntaxKind.Omitted,
                    span: EmptySpanAtCurrent()));
                Take();
                continue;
            }

            result.Add(ParseAssignmentExpression());
            if (CurrentKind is not Avm1SyntaxKind.CommaToken)
                break;
            Take();
            if (CurrentKind == closeKind)
                break;
        }
        return result;
    }

    private void SkipBalancedContent(
        Avm1SyntaxKind openKind,
        Avm1SyntaxKind closeKind,
        string errorMessage)
    {
        var depth = 1;
        while (!AtEnd)
        {
            var token = Take();
            if (token.Kind == openKind)
                depth++;
            else if (token.Kind == closeKind && --depth == 0)
                return;
        }
        AddDiagnostic("AVM1S2004", EmptySpanAtCurrent(), errorMessage);
    }

    private Avm1TextSpan ConsumeBalancedBlock()
    {
        var start = Current.Span.Start;
        var depth = 0;
        var end = start;
        while (!AtEnd)
        {
            var token = Take();
            end = token.Span.End;
            if (token.Kind is Avm1SyntaxKind.OpenBraceToken)
                depth++;
            else if (token.Kind is Avm1SyntaxKind.CloseBraceToken && --depth == 0)
                return Avm1TextSpan.FromBounds(start, end);
        }
        AddDiagnostic(
            "AVM1S2005",
            Avm1TextSpan.FromBounds(start, end),
            "Unterminated function-expression body.");
        return Avm1TextSpan.FromBounds(start, end);
    }

    private void RecoverTo(
        Avm1SyntaxKind first,
        Avm1SyntaxKind second)
    {
        while (!AtEnd && CurrentKind != first && CurrentKind != second)
            Take();
    }

    private int ExpectClosingToken(Avm1SyntaxKind kind, string expectation)
    {
        if (CurrentKind == kind)
            return Take().Span.End;
        AddExpectedDiagnostic(expectation);
        return PreviousEnd(Current.Span.Start);
    }

    private Avm1ExpressionIndex AddTokenExpression(
        Avm1ExpressionSyntaxKind kind,
        Avm1SyntaxTokenIndex token) =>
        AddExpression(kind, token: token, span: GetToken(token).Span);

    private Avm1ExpressionIndex AddExpression(
        Avm1ExpressionSyntaxKind kind,
        Avm1SyntaxTokenIndex? token = null,
        Avm1SyntaxTokenIndex? operatorToken = null,
        IReadOnlyList<Avm1ExpressionIndex>? children = null,
        Avm1SyntaxTokenList auxiliaryTokens = default,
        Avm1ParameterList parameters = default,
        Avm1QualifiedNameSyntax declaredType = default,
        Avm1StatementIndex? body = null,
        Avm1TextSpan? bodySpan = null,
        Avm1TextSpan span = default)
    {
        var childStart = _children.Count;
        if (children is not null)
            _children.AddRange(children);
        var index = new Avm1ExpressionIndex(_expressions.Count);
        _expressions.Add(new Avm1ExpressionSyntax(
            index,
            kind,
            token ?? Avm1SyntaxTokenIndex.Invalid,
            operatorToken ?? Avm1SyntaxTokenIndex.Invalid,
            new Avm1ExpressionList(
                childStart,
                _children.Count - childStart),
            auxiliaryTokens,
            parameters,
            declaredType.IsValid
                ? declaredType
                : Avm1QualifiedNameSyntax.Missing,
            body ?? Avm1StatementIndex.Invalid,
            bodySpan ?? Avm1TextSpan.Invalid,
            span));
        return index;
    }

    private Avm1ExpressionSyntax GetExpression(Avm1ExpressionIndex index) =>
        _expressions[index.Value];

    private Avm1QualifiedNameSyntax ParseQualifiedTypeName()
    {
        var tokenStart = _position;
        if (!IsIdentifierName(CurrentKind) &&
            CurrentKind is not Avm1SyntaxKind.AsteriskToken)
        {
            AddExpectedDiagnostic("a function return type");
            return Avm1QualifiedNameSyntax.Missing;
        }
        var start = Take().Span.Start;
        while (CurrentKind is Avm1SyntaxKind.DotToken &&
               _position + 1 < _end &&
               (IsIdentifierName(_tokens[_position + 1].Kind) ||
                _tokens[_position + 1].Kind is Avm1SyntaxKind.AsteriskToken))
        {
            Take();
            Take();
        }
        return new Avm1QualifiedNameSyntax(
            new Avm1SyntaxTokenList(tokenStart, _position - tokenStart),
            Avm1TextSpan.FromBounds(start, PreviousEnd(start)));
    }

    private Avm1ParameterList ParseParametersRange(int start, int end)
    {
        var parameterStart = _parameters.Count;
        var position = start;
        while (position < end)
        {
            var segmentEnd = FindTopLevelComma(position, end);
            if (segmentEnd < 0)
                segmentEnd = end;
            var itemStart = position;
            var name = IsIdentifierName(_tokens[position].Kind)
                ? new Avm1SyntaxTokenIndex(position++)
                : Avm1SyntaxTokenIndex.Invalid;
            if (!name.IsValid)
            {
                AddDiagnostic(
                    "AVM1S2007",
                    _tokens[position].Span,
                    "Expected a function-expression parameter name.");
                position = Math.Min(position + 1, segmentEnd);
            }

            var declaredType = Avm1QualifiedNameSyntax.Missing;
            if (position < segmentEnd &&
                _tokens[position].Kind is Avm1SyntaxKind.ColonToken)
            {
                position++;
                var typeStart = position;
                if (position < segmentEnd &&
                    (IsIdentifierName(_tokens[position].Kind) ||
                     _tokens[position].Kind is Avm1SyntaxKind.AsteriskToken))
                {
                    position++;
                    while (position + 1 < segmentEnd &&
                           _tokens[position].Kind is Avm1SyntaxKind.DotToken &&
                           (IsIdentifierName(_tokens[position + 1].Kind) ||
                            _tokens[position + 1].Kind is Avm1SyntaxKind.AsteriskToken))
                    {
                        position += 2;
                    }
                    declaredType = new Avm1QualifiedNameSyntax(
                        new Avm1SyntaxTokenList(typeStart, position - typeStart),
                        Avm1TextSpan.FromBounds(
                            _tokens[typeStart].Span.Start,
                            _tokens[position - 1].Span.End));
                }
                else
                {
                    AddDiagnostic(
                        "AVM1S2008",
                        _tokens[Math.Min(position, _tokens.Count - 1)].Span,
                        "Expected a function-expression parameter type.");
                }
            }

            var defaultValue = Avm1ExpressionIndex.Invalid;
            var defaultSpan = Avm1TextSpan.Invalid;
            if (position < segmentEnd &&
                _tokens[position].Kind is Avm1SyntaxKind.EqualsToken)
            {
                var expressionStart = ++position;
                defaultValue = Parse(
                    _tokenStream,
                    expressionStart,
                    segmentEnd,
                    _expressions,
                    _children,
                    _statements,
                    _statementChildren,
                    _declarators,
                    _parameters,
                    _diagnostics);
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

    private int FindTopLevelComma(int start, int end)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = start; index < end; index++)
        {
            var kind = _tokens[index].Kind;
            if (parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0 &&
                kind is Avm1SyntaxKind.CommaToken)
            {
                return index;
            }
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
        return -1;
    }

    private Avm1TextSpan SpanForRange(int start, int end)
    {
        if (start >= end)
            return new Avm1TextSpan(_tokens[Math.Min(start, _tokens.Count - 1)].Span.Start, 0);
        return Avm1TextSpan.FromBounds(
            _tokens[start].Span.Start,
            _tokens[end - 1].Span.End);
    }

    private Avm1SyntaxToken GetToken(Avm1SyntaxTokenIndex index) =>
        _tokens[index.Value];

    private Avm1TextSpan SpanFrom(
        Avm1ExpressionIndex first,
        Avm1ExpressionIndex last) =>
        Avm1TextSpan.FromBounds(
            GetExpression(first).Span.Start,
            GetExpression(last).Span.End);

    private Avm1TextSpan EmptySpanAtCurrent() =>
        new(Current.Span.Start, 0);

    private int PreviousEnd(int fallback) =>
        _position > 0 ? _tokens[_position - 1].Span.End : fallback;

    private bool HasLineBreakBeforeCurrent()
    {
        if (AtEnd)
            return false;
        foreach (var trivia in _tokenStream.GetLeadingTrivia(Current))
        {
            if (trivia.Kind is Avm1SyntaxTriviaKind.EndOfLine)
                return true;
        }
        return false;
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
        if (!AtEnd)
            _position++;
        return token;
    }

    private void AddExpectedDiagnostic(string expected) =>
        AddDiagnostic(
            "AVM1S2006",
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

    private static bool IsIdentifierName(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.IdentifierToken or
            Avm1SyntaxKind.GetKeyword or
            Avm1SyntaxKind.SetKeyword;

    private static bool IsPropertyName(Avm1SyntaxKind kind) =>
        IsIdentifierName(kind) || IsKeyword(kind);

    private static bool IsObjectPropertyName(Avm1SyntaxKind kind) =>
        IsPropertyName(kind) ||
        kind is Avm1SyntaxKind.StringLiteralToken or
            Avm1SyntaxKind.NumericLiteralToken;

    private static bool IsKeyword(Avm1SyntaxKind kind) =>
        kind >= Avm1SyntaxKind.BreakKeyword;

    private static bool IsPrefixUnaryOperator(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.PlusToken or
            Avm1SyntaxKind.MinusToken or
            Avm1SyntaxKind.PlusPlusToken or
            Avm1SyntaxKind.MinusMinusToken or
            Avm1SyntaxKind.ExclamationToken or
            Avm1SyntaxKind.TildeToken or
            Avm1SyntaxKind.DeleteKeyword or
            Avm1SyntaxKind.TypeOfKeyword or
            Avm1SyntaxKind.VoidKeyword;

    private static bool IsAssignmentOperator(Avm1SyntaxKind kind) =>
        kind is Avm1SyntaxKind.EqualsToken or
            Avm1SyntaxKind.PlusEqualsToken or
            Avm1SyntaxKind.MinusEqualsToken or
            Avm1SyntaxKind.AsteriskEqualsToken or
            Avm1SyntaxKind.SlashEqualsToken or
            Avm1SyntaxKind.PercentEqualsToken or
            Avm1SyntaxKind.AmpersandEqualsToken or
            Avm1SyntaxKind.PipeEqualsToken or
            Avm1SyntaxKind.CaretEqualsToken or
            Avm1SyntaxKind.LessThanLessThanEqualsToken or
            Avm1SyntaxKind.GreaterThanGreaterThanEqualsToken or
            Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken;

    private static int GetBinaryPrecedence(Avm1SyntaxKind kind) =>
        kind switch
        {
            Avm1SyntaxKind.PipePipeToken => 1,
            Avm1SyntaxKind.AmpersandAmpersandToken => 2,
            Avm1SyntaxKind.PipeToken => 3,
            Avm1SyntaxKind.CaretToken => 4,
            Avm1SyntaxKind.AmpersandToken => 5,
            Avm1SyntaxKind.EqualsEqualsToken or
                Avm1SyntaxKind.ExclamationEqualsToken or
                Avm1SyntaxKind.EqualsEqualsEqualsToken or
                Avm1SyntaxKind.ExclamationEqualsEqualsToken => 6,
            Avm1SyntaxKind.LessThanToken or
                Avm1SyntaxKind.LessThanEqualsToken or
                Avm1SyntaxKind.GreaterThanToken or
                Avm1SyntaxKind.GreaterThanEqualsToken or
                Avm1SyntaxKind.InKeyword or
                Avm1SyntaxKind.InstanceOfKeyword => 7,
            Avm1SyntaxKind.LessThanLessThanToken or
                Avm1SyntaxKind.GreaterThanGreaterThanToken or
                Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanToken => 8,
            Avm1SyntaxKind.PlusToken or Avm1SyntaxKind.MinusToken => 9,
            Avm1SyntaxKind.AsteriskToken or
                Avm1SyntaxKind.SlashToken or
                Avm1SyntaxKind.PercentToken => 10,
            _ => 0
        };
}
