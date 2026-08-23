namespace ShockwaveFlash.Avm1.Compilation.Syntax;

public static class Avm1Lexer
{
    public static Avm1SyntaxTokenStream Lex(
        string text,
        Avm1ParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Lexer(text, options ?? Avm1ParseOptions.Default).Lex();
    }

    private sealed class Lexer
    {
        private readonly string _text;
        private readonly Avm1ParseOptions _options;
        private readonly List<Avm1SyntaxToken> _tokens;
        private readonly List<Avm1SyntaxTrivia> _trivia;
        private readonly List<Avm1SyntaxDiagnostic> _diagnostics;
        private int _position;

        public Lexer(string text, Avm1ParseOptions options)
        {
            _text = text;
            _options = options;
            _tokens = new List<Avm1SyntaxToken>(Math.Max(16, text.Length / 5));
            _trivia = new List<Avm1SyntaxTrivia>(Math.Max(8, text.Length / 16));
            _diagnostics = [];
        }

        public Avm1SyntaxTokenStream Lex()
        {
            while (true)
            {
                var triviaStart = _trivia.Count;
                ScanTrivia();
                var leadingTrivia = new Avm1SyntaxTriviaList(
                    triviaStart,
                    _trivia.Count - triviaStart);
                var tokenStart = _position;
                var kind = ScanToken();
                _tokens.Add(new Avm1SyntaxToken(
                    kind,
                    Avm1TextSpan.FromBounds(tokenStart, _position),
                    leadingTrivia));
                if (kind is Avm1SyntaxKind.EndOfFileToken)
                    break;
            }

            return new Avm1SyntaxTokenStream(
                _text,
                _options,
                _tokens.ToArray(),
                _trivia.ToArray(),
                _diagnostics.ToArray());
        }

        private char Current => Peek(0);

        private char Peek(int offset)
        {
            var index = _position + offset;
            return (uint)index < (uint)_text.Length ? _text[index] : '\0';
        }

        private void ScanTrivia()
        {
            while (_position < _text.Length)
            {
                var start = _position;
                if (Current is '\r' or '\n')
                {
                    if (Current is '\r' && Peek(1) is '\n')
                        _position += 2;
                    else
                        _position++;
                    AddTrivia(Avm1SyntaxTriviaKind.EndOfLine, start);
                    continue;
                }

                if (char.IsWhiteSpace(Current))
                {
                    do
                    {
                        _position++;
                    }
                    while (_position < _text.Length &&
                           Current is not '\r' and not '\n' &&
                           char.IsWhiteSpace(Current));
                    AddTrivia(Avm1SyntaxTriviaKind.Whitespace, start);
                    continue;
                }

                if (Current is '/' && Peek(1) is '/')
                {
                    _position += 2;
                    while (_position < _text.Length && Current is not '\r' and not '\n')
                        _position++;
                    AddTrivia(Avm1SyntaxTriviaKind.SingleLineComment, start);
                    continue;
                }

                if (Current is '/' && Peek(1) is '*')
                {
                    _position += 2;
                    while (_position < _text.Length &&
                           !(Current is '*' && Peek(1) is '/'))
                    {
                        _position++;
                    }

                    if (_position < _text.Length)
                    {
                        _position += 2;
                    }
                    else
                    {
                        AddDiagnostic(
                            "AVM1S0002",
                            start,
                            _position,
                            "Unterminated multi-line comment.");
                    }

                    AddTrivia(Avm1SyntaxTriviaKind.MultiLineComment, start);
                    continue;
                }

                break;
            }
        }

        private void AddTrivia(Avm1SyntaxTriviaKind kind, int start) =>
            _trivia.Add(new Avm1SyntaxTrivia(
                kind,
                Avm1TextSpan.FromBounds(start, _position)));

        private Avm1SyntaxKind ScanToken()
        {
            if (_position >= _text.Length)
                return Avm1SyntaxKind.EndOfFileToken;

            if (IsIdentifierStart(Current))
                return ScanIdentifierOrKeyword();
            if (char.IsDigit(Current) ||
                Current is '.' && char.IsDigit(Peek(1)))
            {
                return ScanNumericLiteral();
            }
            if (Current is '\'' or '"')
                return ScanStringLiteral();

            return Current switch
            {
                '{' => Single(Avm1SyntaxKind.OpenBraceToken),
                '}' => Single(Avm1SyntaxKind.CloseBraceToken),
                '(' => Single(Avm1SyntaxKind.OpenParenthesisToken),
                ')' => Single(Avm1SyntaxKind.CloseParenthesisToken),
                '[' => Single(Avm1SyntaxKind.OpenBracketToken),
                ']' => Single(Avm1SyntaxKind.CloseBracketToken),
                '.' => Single(Avm1SyntaxKind.DotToken),
                ',' => Single(Avm1SyntaxKind.CommaToken),
                ';' => Single(Avm1SyntaxKind.SemicolonToken),
                '?' => Single(Avm1SyntaxKind.QuestionToken),
                '#' => Single(Avm1SyntaxKind.HashToken),
                ':' => Match(':', Avm1SyntaxKind.DoubleColonToken, Avm1SyntaxKind.ColonToken),
                '+' => MatchEither(
                    '+', Avm1SyntaxKind.PlusPlusToken,
                    '=', Avm1SyntaxKind.PlusEqualsToken,
                    Avm1SyntaxKind.PlusToken),
                '-' => MatchEither(
                    '-', Avm1SyntaxKind.MinusMinusToken,
                    '=', Avm1SyntaxKind.MinusEqualsToken,
                    Avm1SyntaxKind.MinusToken),
                '*' => Match('=', Avm1SyntaxKind.AsteriskEqualsToken, Avm1SyntaxKind.AsteriskToken),
                '/' => Match('=', Avm1SyntaxKind.SlashEqualsToken, Avm1SyntaxKind.SlashToken),
                '%' => Match('=', Avm1SyntaxKind.PercentEqualsToken, Avm1SyntaxKind.PercentToken),
                '=' => Match(
                    '=', Avm1SyntaxKind.EqualsEqualsToken,
                    '=', Avm1SyntaxKind.EqualsEqualsEqualsToken,
                    Avm1SyntaxKind.EqualsToken),
                '!' => Match(
                    '=', Avm1SyntaxKind.ExclamationEqualsToken,
                    '=', Avm1SyntaxKind.ExclamationEqualsEqualsToken,
                    Avm1SyntaxKind.ExclamationToken),
                '<' => ScanLessThan(),
                '>' => ScanGreaterThan(),
                '&' => MatchEither(
                    '&', Avm1SyntaxKind.AmpersandAmpersandToken,
                    '=', Avm1SyntaxKind.AmpersandEqualsToken,
                    Avm1SyntaxKind.AmpersandToken),
                '|' => MatchEither(
                    '|', Avm1SyntaxKind.PipePipeToken,
                    '=', Avm1SyntaxKind.PipeEqualsToken,
                    Avm1SyntaxKind.PipeToken),
                '^' => Match('=', Avm1SyntaxKind.CaretEqualsToken, Avm1SyntaxKind.CaretToken),
                '~' => Single(Avm1SyntaxKind.TildeToken),
                _ => ScanBadToken()
            };
        }

        private Avm1SyntaxKind ScanIdentifierOrKeyword()
        {
            var start = _position++;
            while (IsIdentifierPart(Current))
                _position++;
            return GetKeywordKind(_text.AsSpan(start, _position - start));
        }

        private Avm1SyntaxKind ScanNumericLiteral()
        {
            if (Current is '0' && Peek(1) is 'x' or 'X')
            {
                _position += 2;
                var digits = _position;
                while (IsHexDigit(Current))
                    _position++;
                if (_position == digits)
                {
                    AddDiagnostic(
                        "AVM1S0003",
                        digits - 2,
                        _position,
                        "A hexadecimal literal requires at least one digit.");
                }
                return Avm1SyntaxKind.NumericLiteralToken;
            }

            if (Current is '.')
            {
                _position++;
                while (char.IsDigit(Current))
                    _position++;
            }
            else
            {
                while (char.IsDigit(Current))
                    _position++;
                if (Current is '.')
                {
                    _position++;
                    while (char.IsDigit(Current))
                        _position++;
                }
            }

            if (Current is 'e' or 'E')
            {
                var exponent = _position++;
                if (Current is '+' or '-')
                    _position++;
                var digits = _position;
                while (char.IsDigit(Current))
                    _position++;
                if (_position == digits)
                {
                    AddDiagnostic(
                        "AVM1S0003",
                        exponent,
                        _position,
                        "A numeric exponent requires at least one digit.");
                }
            }

            return Avm1SyntaxKind.NumericLiteralToken;
        }

        private Avm1SyntaxKind ScanStringLiteral()
        {
            var start = _position;
            var quote = Current;
            _position++;
            var terminated = false;
            while (_position < _text.Length)
            {
                if (Current == quote)
                {
                    _position++;
                    terminated = true;
                    break;
                }

                if (Current is '\r' or '\n')
                    break;
                if (Current is '\\')
                {
                    _position++;
                    if (_position < _text.Length)
                    {
                        if (Current is '\r' && Peek(1) is '\n')
                            _position += 2;
                        else
                            _position++;
                    }
                    continue;
                }

                _position++;
            }

            if (!terminated)
            {
                AddDiagnostic(
                    "AVM1S0004",
                    start,
                    _position,
                    "Unterminated string literal.");
            }

            return Avm1SyntaxKind.StringLiteralToken;
        }

        private Avm1SyntaxKind ScanLessThan()
        {
            _position++;
            if (Current is '=')
            {
                _position++;
                return Avm1SyntaxKind.LessThanEqualsToken;
            }
            if (Current is not '<')
                return Avm1SyntaxKind.LessThanToken;
            _position++;
            if (Current is '=')
            {
                _position++;
                return Avm1SyntaxKind.LessThanLessThanEqualsToken;
            }
            return Avm1SyntaxKind.LessThanLessThanToken;
        }

        private Avm1SyntaxKind ScanGreaterThan()
        {
            _position++;
            if (Current is '=')
            {
                _position++;
                return Avm1SyntaxKind.GreaterThanEqualsToken;
            }
            if (Current is not '>')
                return Avm1SyntaxKind.GreaterThanToken;

            _position++;
            if (Current is '=')
            {
                _position++;
                return Avm1SyntaxKind.GreaterThanGreaterThanEqualsToken;
            }
            if (Current is not '>')
                return Avm1SyntaxKind.GreaterThanGreaterThanToken;

            _position++;
            if (Current is '=')
            {
                _position++;
                return Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken;
            }
            return Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanToken;
        }

        private Avm1SyntaxKind Single(Avm1SyntaxKind kind)
        {
            _position++;
            return kind;
        }

        private Avm1SyntaxKind Match(
            char second,
            Avm1SyntaxKind combined,
            Avm1SyntaxKind single)
        {
            _position++;
            if (Current != second)
                return single;
            _position++;
            return combined;
        }

        private Avm1SyntaxKind Match(
            char second,
            Avm1SyntaxKind combined,
            char third,
            Avm1SyntaxKind triple,
            Avm1SyntaxKind single)
        {
            _position++;
            if (Current != second)
                return single;
            _position++;
            if (Current != third)
                return combined;
            _position++;
            return triple;
        }

        private Avm1SyntaxKind MatchEither(
            char second,
            Avm1SyntaxKind secondKind,
            char alternative,
            Avm1SyntaxKind alternativeKind,
            Avm1SyntaxKind single)
        {
            _position++;
            if (Current == second)
            {
                _position++;
                return secondKind;
            }
            if (Current == alternative)
            {
                _position++;
                return alternativeKind;
            }
            return single;
        }

        private Avm1SyntaxKind ScanBadToken()
        {
            var start = _position++;
            AddDiagnostic(
                "AVM1S0001",
                start,
                _position,
                "Unexpected character in ActionScript source.");
            return Avm1SyntaxKind.BadToken;
        }

        private void AddDiagnostic(
            string code,
            int start,
            int end,
            string message) =>
            _diagnostics.Add(new Avm1SyntaxDiagnostic(
                code,
                Avm1SyntaxDiagnosticSeverity.Error,
                Avm1TextSpan.FromBounds(start, end),
                message));

        private static bool IsIdentifierStart(char value) =>
            value is '_' or '$' || char.IsLetter(value);

        private static bool IsIdentifierPart(char value) =>
            IsIdentifierStart(value) || char.IsDigit(value);

        private static bool IsHexDigit(char value) =>
            value is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F';

        private static Avm1SyntaxKind GetKeywordKind(ReadOnlySpan<char> text) =>
            text switch
            {
                "break" => Avm1SyntaxKind.BreakKeyword,
                "case" => Avm1SyntaxKind.CaseKeyword,
                "catch" => Avm1SyntaxKind.CatchKeyword,
                "class" => Avm1SyntaxKind.ClassKeyword,
                "const" => Avm1SyntaxKind.ConstKeyword,
                "continue" => Avm1SyntaxKind.ContinueKeyword,
                "default" => Avm1SyntaxKind.DefaultKeyword,
                "delete" => Avm1SyntaxKind.DeleteKeyword,
                "do" => Avm1SyntaxKind.DoKeyword,
                "dynamic" => Avm1SyntaxKind.DynamicKeyword,
                "else" => Avm1SyntaxKind.ElseKeyword,
                "extends" => Avm1SyntaxKind.ExtendsKeyword,
                "false" => Avm1SyntaxKind.FalseKeyword,
                "final" => Avm1SyntaxKind.FinalKeyword,
                "finally" => Avm1SyntaxKind.FinallyKeyword,
                "for" => Avm1SyntaxKind.ForKeyword,
                "function" => Avm1SyntaxKind.FunctionKeyword,
                "get" => Avm1SyntaxKind.GetKeyword,
                "if" => Avm1SyntaxKind.IfKeyword,
                "implements" => Avm1SyntaxKind.ImplementsKeyword,
                "import" => Avm1SyntaxKind.ImportKeyword,
                "in" => Avm1SyntaxKind.InKeyword,
                "instanceof" => Avm1SyntaxKind.InstanceOfKeyword,
                "interface" => Avm1SyntaxKind.InterfaceKeyword,
                "intrinsic" => Avm1SyntaxKind.IntrinsicKeyword,
                "new" => Avm1SyntaxKind.NewKeyword,
                "null" => Avm1SyntaxKind.NullKeyword,
                "override" => Avm1SyntaxKind.OverrideKeyword,
                "private" => Avm1SyntaxKind.PrivateKeyword,
                "protected" => Avm1SyntaxKind.ProtectedKeyword,
                "public" => Avm1SyntaxKind.PublicKeyword,
                "return" => Avm1SyntaxKind.ReturnKeyword,
                "set" => Avm1SyntaxKind.SetKeyword,
                "static" => Avm1SyntaxKind.StaticKeyword,
                "super" => Avm1SyntaxKind.SuperKeyword,
                "switch" => Avm1SyntaxKind.SwitchKeyword,
                "this" => Avm1SyntaxKind.ThisKeyword,
                "tellTarget" => Avm1SyntaxKind.TellTargetKeyword,
                "throw" => Avm1SyntaxKind.ThrowKeyword,
                "true" => Avm1SyntaxKind.TrueKeyword,
                "try" => Avm1SyntaxKind.TryKeyword,
                "typeof" => Avm1SyntaxKind.TypeOfKeyword,
                "undefined" => Avm1SyntaxKind.UndefinedKeyword,
                "var" => Avm1SyntaxKind.VarKeyword,
                "void" => Avm1SyntaxKind.VoidKeyword,
                "while" => Avm1SyntaxKind.WhileKeyword,
                "with" => Avm1SyntaxKind.WithKeyword,
                _ => Avm1SyntaxKind.IdentifierToken
            };
    }
}
