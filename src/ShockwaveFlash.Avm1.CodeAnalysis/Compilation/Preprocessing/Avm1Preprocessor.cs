using System.Text;
using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Preprocessing;

public static class Avm1Preprocessor
{
    public static Avm1PreprocessorResult Expand(
        string path,
        string text,
        IAvm1IncludeResolver? includeResolver = null,
        Avm1PreprocessorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        options ??= Avm1PreprocessorOptions.Default;
        if (options.MaximumIncludeDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The maximum include depth cannot be negative.");
        }

        var expander = new Expander(
            includeResolver,
            options,
            cancellationToken);
        var expanded = expander.ExpandRoot(path, text);
        return new Avm1PreprocessorResult(
            expanded,
            expander.Diagnostics.ToArray());
    }

    private sealed class Expander
    {
        private readonly IAvm1IncludeResolver? _resolver;
        private readonly Avm1PreprocessorOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly List<Avm1PreprocessorDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, string> _dependencies = new(
            PathComparer);
        private readonly HashSet<string> _activePaths = new(PathComparer);

        public Expander(
            IAvm1IncludeResolver? resolver,
            Avm1PreprocessorOptions options,
            CancellationToken cancellationToken)
        {
            _resolver = resolver;
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public IReadOnlyList<Avm1PreprocessorDiagnostic> Diagnostics =>
            _diagnostics;

        public Avm1ExpandedSource ExpandRoot(string path, string text)
        {
            var normalizedPath = NormalizePath(path);
            _activePaths.Add(normalizedPath);
            var content = ExpandFile(normalizedPath, text, depth: 0);
            _activePaths.Remove(normalizedPath);
            var dependencies = _dependencies
                .OrderBy(pair => pair.Key, PathComparer)
                .Select(pair => new Avm1IncludeDependency(pair.Key, pair.Value))
                .ToArray();
            return new Avm1ExpandedSource(
                normalizedPath,
                content.Text,
                content.Map.ToArray(),
                dependencies,
                [
                    new Avm1SourceDocument(normalizedPath, text),
                    .. dependencies.Select(dependency =>
                        new Avm1SourceDocument(dependency.Path, dependency.Text))
                ]);
        }

        private ExpandedContent ExpandFile(
            string path,
            string text,
            int depth)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var directives = FindIncludeDirectives(text);
            if (directives.Count == 0)
                return ExpandedContent.Direct(path, text);

            var builder = new ContentBuilder(text.Length);
            var position = 0;
            foreach (var directive in directives)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                builder.AppendDirect(
                    path,
                    text,
                    position,
                    directive.Span.Start - position);

                if (!directive.IsValid)
                {
                    AddDiagnostic(
                        "AVM1PP001",
                        path,
                        directive.Span,
                        directive.Error ?? "Malformed include directive.");
                    builder.AppendDirect(
                        path,
                        CreateWhitespace(text, directive.Span),
                        0,
                        directive.Span.Length,
                        sourceStart: directive.Span.Start);
                    position = directive.Span.End;
                    continue;
                }

                if (_resolver is null)
                {
                    AddDiagnostic(
                        "AVM1PP002",
                        path,
                        directive.Span,
                        $"Include '{directive.IncludePath}' requires an include resolver.");
                    builder.AppendDirect(
                        path,
                        CreateWhitespace(text, directive.Span),
                        0,
                        directive.Span.Length,
                        sourceStart: directive.Span.Start);
                    position = directive.Span.End;
                    continue;
                }

                Avm1IncludeResolution resolution;
                try
                {
                    resolution = _resolver.Resolve(
                        path,
                        directive.IncludePath!,
                        _cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    resolution = Avm1IncludeResolution.Failure(
                        $"Include resolver failed: {exception.Message}");
                }

                if (!resolution.Succeeded)
                {
                    AddDiagnostic(
                        "AVM1PP002",
                        path,
                        directive.Span,
                        resolution.Error ?? "The include could not be resolved.");
                    builder.AppendDirect(
                        path,
                        CreateWhitespace(text, directive.Span),
                        0,
                        directive.Span.Length,
                        sourceStart: directive.Span.Start);
                    position = directive.Span.End;
                    continue;
                }

                var includedPath = NormalizePath(resolution.Path);
                if (depth >= _options.MaximumIncludeDepth)
                {
                    AddDiagnostic(
                        "AVM1PP003",
                        path,
                        directive.Span,
                        $"Include '{includedPath}' exceeds the maximum depth " +
                        $"of {_options.MaximumIncludeDepth}.");
                    position = directive.Span.End;
                    continue;
                }
                if (!_activePaths.Add(includedPath))
                {
                    AddDiagnostic(
                        "AVM1PP004",
                        path,
                        directive.Span,
                        $"Include cycle detected at '{includedPath}'.");
                    position = directive.Span.End;
                    continue;
                }

                if (_dependencies.TryGetValue(includedPath, out var previous) &&
                    !string.Equals(previous, resolution.Text, StringComparison.Ordinal))
                {
                    AddDiagnostic(
                        "AVM1PP005",
                        path,
                        directive.Span,
                        $"Include resolver returned different content for '{includedPath}'.");
                    _activePaths.Remove(includedPath);
                    position = directive.Span.End;
                    continue;
                }

                _dependencies[includedPath] = resolution.Text;
                var included = ExpandFile(
                    includedPath,
                    resolution.Text,
                    depth + 1);
                _activePaths.Remove(includedPath);
                builder.Append(included);
                position = directive.Span.End;
            }

            builder.AppendDirect(path, text, position, text.Length - position);
            return builder.Build();
        }

        private void AddDiagnostic(
            string code,
            string path,
            Avm1TextSpan span,
            string message) =>
            _diagnostics.Add(new Avm1PreprocessorDiagnostic(
                code,
                Avm1CompilationDiagnosticSeverity.Error,
                new Avm1SourceLocation(path, span),
                message));

        private static List<IncludeDirective> FindIncludeDirectives(string text)
        {
            var tokens = Avm1Lexer.Lex(text).Tokens;
            var result = new List<IncludeDirective>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind is not Avm1SyntaxKind.HashToken)
                    continue;

                var lineEnd = FindLineEnd(text, tokens[i].Span.Start);
                var span = Avm1TextSpan.FromBounds(tokens[i].Span.Start, lineEnd);
                if (i + 1 >= tokens.Count ||
                    tokens[i + 1].Kind is not Avm1SyntaxKind.IdentifierToken ||
                    !string.Equals(
                        tokens[i + 1].GetText(text).ToString(),
                        "include",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (i + 2 >= tokens.Count ||
                    tokens[i + 2].Kind is not Avm1SyntaxKind.StringLiteralToken ||
                    tokens[i + 2].Span.End > lineEnd)
                {
                    result.Add(IncludeDirective.Invalid(
                        span,
                        "Expected a quoted path after #include."));
                    continue;
                }

                var literal = tokens[i + 2].GetText(text).ToString();
                if (!TryParseString(literal, out var includePath) ||
                    string.IsNullOrWhiteSpace(includePath))
                {
                    result.Add(IncludeDirective.Invalid(
                        span,
                        "The #include path is not a valid nonempty string."));
                    continue;
                }

                result.Add(IncludeDirective.Valid(span, includePath));
            }
            return result;
        }

        private static int FindLineEnd(string text, int start)
        {
            var position = start;
            while (position < text.Length && text[position] is not '\r' and not '\n')
                position++;
            return position;
        }

        private static bool TryParseString(string literal, out string value)
        {
            value = string.Empty;
            if (literal.Length < 2 ||
                literal[0] is not ('\'' or '"') ||
                literal[^1] != literal[0])
            {
                return false;
            }

            var builder = new StringBuilder(literal.Length - 2);
            for (var i = 1; i + 1 < literal.Length; i++)
            {
                var character = literal[i];
                if (character != '\\' || i + 2 >= literal.Length)
                {
                    builder.Append(character);
                    continue;
                }

                var escaped = literal[++i];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped
                });
            }
            value = builder.ToString();
            return true;
        }

        private static string CreateWhitespace(string text, Avm1TextSpan span)
        {
            var characters = text.AsSpan(span.Start, span.Length).ToArray();
            for (var i = 0; i < characters.Length; i++)
            {
                if (characters[i] is not '\r' and not '\n')
                    characters[i] = ' ';
            }
            return new string(characters);
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or
                NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        private static StringComparer PathComparer => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private readonly record struct IncludeDirective(
        Avm1TextSpan Span,
        string? IncludePath,
        string? Error)
    {
        public bool IsValid => IncludePath is not null;

        public static IncludeDirective Valid(
            Avm1TextSpan span,
            string includePath) =>
            new(span, includePath, null);

        public static IncludeDirective Invalid(
            Avm1TextSpan span,
            string error) =>
            new(span, null, error);
    }

    private sealed class ExpandedContent
    {
        public ExpandedContent(
            string text,
            List<Avm1ExpandedSourceMapEntry> map)
        {
            Text = text;
            Map = map;
        }

        public string Text { get; }

        public List<Avm1ExpandedSourceMapEntry> Map { get; }

        public static ExpandedContent Direct(string path, string text)
        {
            var map = text.Length == 0
                ? []
                : new List<Avm1ExpandedSourceMapEntry>
                {
                    new(
                        new Avm1TextSpan(0, text.Length),
                        path,
                        new Avm1TextSpan(0, text.Length))
                };
            return new ExpandedContent(text, map);
        }
    }

    private sealed class ContentBuilder
    {
        private readonly StringBuilder _text;
        private readonly List<Avm1ExpandedSourceMapEntry> _map = [];

        public ContentBuilder(int capacity)
        {
            _text = new StringBuilder(capacity);
        }

        public void Append(ExpandedContent content)
        {
            var offset = _text.Length;
            _text.Append(content.Text);
            foreach (var entry in content.Map)
            {
                AddMap(new Avm1ExpandedSourceMapEntry(
                    new Avm1TextSpan(
                        offset + entry.ExpandedSpan.Start,
                        entry.ExpandedSpan.Length),
                    entry.SourcePath,
                    entry.SourceSpan));
            }
        }

        public void AppendDirect(
            string path,
            string text,
            int start,
            int length,
            int? sourceStart = null)
        {
            if (length == 0)
                return;
            var expandedStart = _text.Length;
            _text.Append(text, start, length);
            AddMap(new Avm1ExpandedSourceMapEntry(
                new Avm1TextSpan(expandedStart, length),
                path,
                new Avm1TextSpan(sourceStart ?? start, length)));
        }

        public ExpandedContent Build() => new(_text.ToString(), _map);

        private void AddMap(Avm1ExpandedSourceMapEntry entry)
        {
            if (_map.Count != 0)
            {
                var previous = _map[^1];
                if (string.Equals(
                        previous.SourcePath,
                        entry.SourcePath,
                        PathComparison) &&
                    previous.ExpandedSpan.End == entry.ExpandedSpan.Start &&
                    previous.SourceSpan.End == entry.SourceSpan.Start)
                {
                    _map[^1] = new Avm1ExpandedSourceMapEntry(
                        Avm1TextSpan.FromBounds(
                            previous.ExpandedSpan.Start,
                            entry.ExpandedSpan.End),
                        previous.SourcePath,
                        Avm1TextSpan.FromBounds(
                            previous.SourceSpan.Start,
                            entry.SourceSpan.End));
                    return;
                }
            }
            _map.Add(entry);
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
    }
}
