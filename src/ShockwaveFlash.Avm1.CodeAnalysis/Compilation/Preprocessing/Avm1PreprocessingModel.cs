using ShockwaveFlash.Avm1.Compilation.Syntax;

namespace ShockwaveFlash.Avm1.Compilation.Preprocessing;

public readonly record struct Avm1SourceLocation(
    string Path,
    Avm1TextSpan Span);

public readonly record struct Avm1SourceLinePosition(int Line, int Column)
{
    public bool IsValid => Line > 0 && Column > 0;
}

public readonly record struct Avm1SourceLineSpan(
    Avm1SourceLinePosition Start,
    Avm1SourceLinePosition End)
{
    public bool IsValid => Start.IsValid && End.IsValid;
}

public sealed class Avm1SourceDocument
{
    private readonly int[] _lineStarts;

    internal Avm1SourceDocument(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        Path = path;
        Text = text;
        _lineStarts = BuildLineStarts(text);
    }

    public string Path { get; }

    public string Text { get; }

    public int LineCount => _lineStarts.Length;

    public Avm1SourceLinePosition GetLinePosition(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Text.Length);

        var line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        return new Avm1SourceLinePosition(
            line + 1,
            offset - _lineStarts[line] + 1);
    }

    public Avm1SourceLineSpan GetLineSpan(Avm1TextSpan span)
    {
        if (!span.IsValid || span.End > Text.Length)
            throw new ArgumentOutOfRangeException(nameof(span));
        return new Avm1SourceLineSpan(
            GetLinePosition(span.Start),
            GetLinePosition(span.End));
    }

    private static int[] BuildLineStarts(string text)
    {
        var result = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r')
            {
                if (i + 1 < text.Length && text[i + 1] is '\n')
                    i++;
                result.Add(i + 1);
            }
            else if (text[i] is '\n')
            {
                result.Add(i + 1);
            }
        }
        return result.ToArray();
    }
}

public readonly record struct Avm1ExpandedSourceMapEntry(
    Avm1TextSpan ExpandedSpan,
    string SourcePath,
    Avm1TextSpan SourceSpan);

public readonly record struct Avm1IncludeDependency(
    string Path,
    string Text);

public readonly record struct Avm1PreprocessorDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    Avm1SourceLocation Location,
    string Message);

public sealed record Avm1PreprocessorOptions
{
    public static Avm1PreprocessorOptions Default { get; } = new();

    public int MaximumIncludeDepth { get; init; } = 64;
}

public sealed class Avm1ExpandedSource
{
    private readonly Avm1ExpandedSourceMapEntry[] _sourceMap;
    private readonly Avm1IncludeDependency[] _dependencies;
    private readonly Avm1SourceDocument[] _documents;

    internal Avm1ExpandedSource(
        string path,
        string text,
        Avm1ExpandedSourceMapEntry[] sourceMap,
        Avm1IncludeDependency[] dependencies,
        Avm1SourceDocument[] documents)
    {
        Path = path;
        Text = text;
        _sourceMap = sourceMap;
        _dependencies = dependencies;
        _documents = documents;
    }

    public string Path { get; }

    public string Text { get; }

    public IReadOnlyList<Avm1ExpandedSourceMapEntry> SourceMap => _sourceMap;

    public IReadOnlyList<Avm1IncludeDependency> Dependencies => _dependencies;

    public IReadOnlyList<Avm1SourceDocument> Documents => _documents;

    public bool TryGetDocument(string path, out Avm1SourceDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (var candidate in _documents)
        {
            if (string.Equals(candidate.Path, path, PathComparison))
            {
                document = candidate;
                return true;
            }
        }

        document = null!;
        return false;
    }

    public Avm1SourceLocation MapLocation(Avm1TextSpan expandedSpan)
    {
        if (!expandedSpan.IsValid)
            return new Avm1SourceLocation(Path, Avm1TextSpan.Invalid);
        if (_sourceMap.Length == 0)
            return new Avm1SourceLocation(Path, expandedSpan);

        var position = expandedSpan.Start;
        var low = 0;
        var high = _sourceMap.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var entry = _sourceMap[middle];
            if (position < entry.ExpandedSpan.Start)
            {
                high = middle - 1;
                continue;
            }
            if (position >= entry.ExpandedSpan.End)
            {
                low = middle + 1;
                continue;
            }

            var offset = position - entry.ExpandedSpan.Start;
            var available = entry.ExpandedSpan.End - position;
            var length = Math.Min(expandedSpan.Length, available);
            return new Avm1SourceLocation(
                entry.SourcePath,
                new Avm1TextSpan(entry.SourceSpan.Start + offset, length));
        }

        return new Avm1SourceLocation(Path, expandedSpan);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

public sealed class Avm1PreprocessorResult
{
    private readonly Avm1PreprocessorDiagnostic[] _diagnostics;

    internal Avm1PreprocessorResult(
        Avm1ExpandedSource source,
        Avm1PreprocessorDiagnostic[] diagnostics)
    {
        Source = source;
        _diagnostics = diagnostics;
    }

    public Avm1ExpandedSource Source { get; }

    public IReadOnlyList<Avm1PreprocessorDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic =>
        diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);
}

public readonly record struct Avm1IncludeResolution(
    bool Succeeded,
    string Path,
    string Text,
    string? Error)
{
    public static Avm1IncludeResolution Success(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        return new Avm1IncludeResolution(true, path, text, null);
    }

    public static Avm1IncludeResolution Failure(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new Avm1IncludeResolution(false, string.Empty, string.Empty, error);
    }
}

public interface IAvm1IncludeResolver
{
    Avm1IncludeResolution Resolve(
        string includingPath,
        string includePath,
        CancellationToken cancellationToken = default);
}

public sealed class Avm1FileSystemIncludeResolver : IAvm1IncludeResolver
{
    private readonly string[] _searchPaths;

    public Avm1FileSystemIncludeResolver(
        IEnumerable<string>? searchPaths = null)
    {
        _searchPaths = searchPaths?
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray() ?? [];
    }

    public IReadOnlyList<string> SearchPaths => _searchPaths;

    public Avm1IncludeResolution Resolve(
        string includingPath,
        string includePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(includingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(includePath);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<string>(_searchPaths.Length + 2);
        if (Path.IsPathRooted(includePath))
        {
            candidates.Add(includePath);
        }
        else
        {
            var directory = Path.GetDirectoryName(includingPath);
            if (!string.IsNullOrWhiteSpace(directory))
                candidates.Add(Path.Combine(directory, includePath));
            foreach (var searchPath in _searchPaths)
                candidates.Add(Path.Combine(searchPath, includePath));
        }

        foreach (var candidate in candidates
                     .Select(Path.GetFullPath)
                     .Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(candidate))
                    continue;
                return Avm1IncludeResolution.Success(
                    candidate,
                    File.ReadAllText(candidate));
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or NotSupportedException)
            {
                return Avm1IncludeResolution.Failure(
                    $"Could not read include '{candidate}': {exception.Message}");
            }
        }

        return Avm1IncludeResolution.Failure(
            $"Include '{includePath}' was not found relative to '{includingPath}'.");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed class Avm1DictionaryIncludeResolver : IAvm1IncludeResolver
{
    private readonly Dictionary<string, string> _sources;

    public Avm1DictionaryIncludeResolver(
        IEnumerable<KeyValuePair<string, string>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = new Dictionary<string, string>(PathComparer);
        foreach (var source in sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Key);
            ArgumentNullException.ThrowIfNull(source.Value);
            _sources.Add(NormalizePath(source.Key), source.Value);
        }
    }

    public Avm1IncludeResolution Resolve(
        string includingPath,
        string includePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = Path.IsPathRooted(includePath)
            ? NormalizePath(includePath)
            : NormalizePath(Path.Combine(
                Path.GetDirectoryName(includingPath) ?? string.Empty,
                includePath));
        return _sources.TryGetValue(candidate, out var text)
            ? Avm1IncludeResolution.Success(candidate, text)
            : Avm1IncludeResolution.Failure(
                $"Include '{includePath}' was not found relative to '{includingPath}'.");
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
