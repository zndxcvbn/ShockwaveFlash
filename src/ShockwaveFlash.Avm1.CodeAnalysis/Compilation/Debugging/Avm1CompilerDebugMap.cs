using System.Globalization;
using ShockwaveFlash.Avm1.Compilation.Preprocessing;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public readonly record struct Avm1DebugDocumentIndex(int Value)
{
    public static readonly Avm1DebugDocumentIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "document" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public sealed class Avm1DebugDocument
{
    private readonly Avm1SourceDocument _source;

    internal Avm1DebugDocument(
        Avm1DebugDocumentIndex index,
        Avm1SourceDocument source)
    {
        Index = index;
        _source = source;
    }

    public Avm1DebugDocumentIndex Index { get; }

    public string Path => _source.Path;

    public string Text => _source.Text;

    public int LineCount => _source.LineCount;

    public Avm1SourceLinePosition GetLinePosition(int offset) =>
        _source.GetLinePosition(offset);

    public Avm1SourceLineSpan GetLineSpan(Avm1TextSpan span) =>
        _source.GetLineSpan(span);
}

public readonly record struct Avm1PhysicalSourceSpan(
    Avm1DebugDocumentIndex Document,
    Avm1TextSpan Span,
    Avm1SourceLineSpan Lines)
{
    public bool IsValid => Document.IsValid && Span.IsValid && Lines.IsValid;
}

public readonly record struct Avm1CompiledSequencePoint(
    int ClassIndex,
    int MethodIndex,
    Avm1CompiledCodeUnitIndex CodeUnit,
    int ByteOffset,
    int ByteLength,
    Avm1PhysicalSourceSpan Source,
    bool ContainsSyntheticActions)
{
    public int EndByteOffset => checked(ByteOffset + ByteLength);
}

public readonly record struct Avm1LinkedSequencePoint(
    int PlacementIndex,
    int TagStreamOffset,
    Avm1CompiledSequencePoint ClassPoint)
{
    public int ByteLength => ClassPoint.ByteLength;

    public int EndTagStreamOffset => checked(TagStreamOffset + ByteLength);

    public Avm1PhysicalSourceSpan Source => ClassPoint.Source;
}

public sealed class Avm1CompilerDebugMap
{
    private readonly Avm1DebugDocument[] _documents;
    private readonly Avm1CompiledSequencePoint[] _sequencePoints;
    private readonly Avm1ExpandedSource[] _expandedSources;
    private readonly Dictionary<string, Avm1DebugDocumentIndex>[]
        _sourceDocuments;
    private readonly Avm1CompiledClassSourceMap[] _classMaps;
    private readonly Dictionary<Avm1CompiledClassSourceMap, int> _classIndices;

    internal Avm1CompilerDebugMap(
        Avm1DebugDocument[] documents,
        Avm1CompiledSequencePoint[] sequencePoints,
        Avm1ExpandedSource[] expandedSources,
        Dictionary<string, Avm1DebugDocumentIndex>[] sourceDocuments,
        Avm1CompiledClassSourceMap[] classMaps,
        int mappedEntryCount,
        int unresolvedEntryCount)
    {
        _documents = documents;
        _sequencePoints = sequencePoints;
        _expandedSources = expandedSources;
        _sourceDocuments = sourceDocuments;
        _classMaps = classMaps;
        _classIndices = new Dictionary<Avm1CompiledClassSourceMap, int>(
            ReferenceEqualityComparer.Instance);
        for (var i = 0; i < classMaps.Length; i++)
        {
            if (!_classIndices.TryAdd(classMaps[i], i))
            {
                throw new InvalidOperationException(
                    "A compiled class source map occurs more than once in a program.");
            }
        }
        MappedEntryCount = mappedEntryCount;
        UnresolvedEntryCount = unresolvedEntryCount;
    }

    public IReadOnlyList<Avm1DebugDocument> Documents => _documents;

    public IReadOnlyList<Avm1CompiledSequencePoint> SequencePoints =>
        _sequencePoints;

    public int ClassCount => _classMaps.Length;

    public int MappedEntryCount { get; }

    public int UnresolvedEntryCount { get; }

    public Avm1DebugDocument GetDocument(Avm1DebugDocumentIndex document)
    {
        if (!document.IsValid || document.Value >= _documents.Length)
            throw new ArgumentOutOfRangeException(nameof(document));
        return _documents[document.Value];
    }

    public IEnumerable<Avm1CompiledSequencePoint> GetSequencePoints(
        int classIndex,
        int methodIndex)
    {
        ValidateMethodIndex(classIndex, methodIndex);
        foreach (var point in _sequencePoints)
        {
            if (point.ClassIndex == classIndex && point.MethodIndex == methodIndex)
                yield return point;
        }
    }

    public bool TryGetSequencePoint(
        int classIndex,
        int byteOffset,
        out Avm1CompiledSequencePoint point)
    {
        if (classIndex < 0 || classIndex >= _classMaps.Length || byteOffset < 0)
        {
            point = default;
            return false;
        }

        var best = -1;
        var bestDepth = -1;
        var bestLength = int.MaxValue;
        for (var i = 0; i < _sequencePoints.Length; i++)
        {
            var candidate = _sequencePoints[i];
            if (candidate.ClassIndex != classIndex ||
                byteOffset < candidate.ByteOffset ||
                byteOffset >= candidate.EndByteOffset)
            {
                continue;
            }

            var depth = GetDepth(candidate);
            if (depth < bestDepth ||
                (depth == bestDepth && candidate.ByteLength >= bestLength))
            {
                continue;
            }

            best = i;
            bestDepth = depth;
            bestLength = candidate.ByteLength;
        }

        if (best < 0)
        {
            point = default;
            return false;
        }

        point = _sequencePoints[best];
        return true;
    }

    public bool TryResolveOrigin(
        Avm1SourceArena arena,
        SourceOriginIndex originIndex,
        out Avm1PhysicalSourceSpan source)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!originIndex.IsValid || originIndex.Value >= arena.Origins.Count)
        {
            source = default;
            return false;
        }

        var origin = arena[originIndex];
        if (origin.RecoveryUnit < 0 ||
            origin.RecoveryUnit >= _expandedSources.Length ||
            origin.StartOffset < 0 ||
            origin.EndOffset < origin.StartOffset ||
            origin.EndOffset > _expandedSources[origin.RecoveryUnit].Text.Length)
        {
            source = default;
            return false;
        }

        var expanded = _expandedSources[origin.RecoveryUnit];
        var location = expanded.MapLocation(Avm1TextSpan.FromBounds(
            origin.StartOffset,
            origin.EndOffset));
        if (!location.Span.IsValid ||
            !_sourceDocuments[origin.RecoveryUnit].TryGetValue(
                location.Path,
                out var document) ||
            location.Span.End > _documents[document.Value].Text.Length)
        {
            source = default;
            return false;
        }

        source = new Avm1PhysicalSourceSpan(
            document,
            location.Span,
            _documents[document.Value].GetLineSpan(location.Span));
        return true;
    }

    public Avm1LinkedCompilerDebugMap CreateLinkedMap(
        Avm1SwfLinkResult linkResult)
    {
        ArgumentNullException.ThrowIfNull(linkResult);
        if (!linkResult.Succeeded)
            throw new ArgumentException(
                "Only a successful link result can be mapped.",
                nameof(linkResult));
        return Avm1LinkedCompilerDebugMap.Build(this, linkResult.SourceMap);
    }

    internal bool TryGetClassIndex(
        Avm1CompiledClassSourceMap sourceMap,
        out int classIndex) =>
        _classIndices.TryGetValue(sourceMap, out classIndex);

    internal int GetDepth(Avm1CompiledSequencePoint point)
    {
        var methodMap = _classMaps[point.ClassIndex]
            .GetMethodSourceMap(point.MethodIndex);
        return methodMap.CodeUnits[point.CodeUnit.Value].Depth;
    }

    private void ValidateMethodIndex(int classIndex, int methodIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(classIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            classIndex,
            _classMaps.Length);
        _classMaps[classIndex].GetMethodSourceMap(methodIndex);
    }
}

public sealed class Avm1LinkedCompilerDebugMap
{
    private readonly Avm1CompilerDebugMap _source;
    private readonly Avm1LinkedSequencePoint[] _sequencePoints;

    private Avm1LinkedCompilerDebugMap(
        Avm1CompilerDebugMap source,
        Avm1LinkedSequencePoint[] sequencePoints)
    {
        _source = source;
        _sequencePoints = sequencePoints;
    }

    public IReadOnlyList<Avm1DebugDocument> Documents => _source.Documents;

    public IReadOnlyList<Avm1LinkedSequencePoint> SequencePoints =>
        _sequencePoints;

    public bool TryGetSequencePoint(
        int tagStreamOffset,
        out Avm1LinkedSequencePoint point)
    {
        if (tagStreamOffset < 0)
        {
            point = default;
            return false;
        }

        var best = -1;
        var bestDepth = -1;
        var bestLength = int.MaxValue;
        for (var i = 0; i < _sequencePoints.Length; i++)
        {
            var candidate = _sequencePoints[i];
            if (tagStreamOffset < candidate.TagStreamOffset ||
                tagStreamOffset >= candidate.EndTagStreamOffset)
            {
                continue;
            }

            var depth = _source.GetDepth(candidate.ClassPoint);
            if (depth < bestDepth ||
                (depth == bestDepth && candidate.ByteLength >= bestLength))
            {
                continue;
            }

            best = i;
            bestDepth = depth;
            bestLength = candidate.ByteLength;
        }

        if (best < 0)
        {
            point = default;
            return false;
        }

        point = _sequencePoints[best];
        return true;
    }

    internal static Avm1LinkedCompilerDebugMap Build(
        Avm1CompilerDebugMap source,
        Avm1LinkedSourceMap linkedSourceMap)
    {
        var points = new List<Avm1LinkedSequencePoint>();
        foreach (var placement in linkedSourceMap.Placements)
        {
            var classMap = linkedSourceMap.GetClassSourceMap(
                placement.PlacementIndex);
            if (!source.TryGetClassIndex(classMap, out var classIndex))
                continue;

            foreach (var classPoint in source.SequencePoints)
            {
                if (classPoint.ClassIndex != classIndex)
                    continue;
                points.Add(new Avm1LinkedSequencePoint(
                    placement.PlacementIndex,
                    checked(placement.ActionStreamOffset + classPoint.ByteOffset),
                    classPoint));
            }
        }

        return new Avm1LinkedCompilerDebugMap(
            source,
            points
                .OrderBy(point => point.TagStreamOffset)
                .ThenBy(point => point.PlacementIndex)
                .ThenBy(point => point.ClassPoint.MethodIndex)
                .ThenBy(point => point.ClassPoint.CodeUnit.Value)
                .ToArray());
    }
}
