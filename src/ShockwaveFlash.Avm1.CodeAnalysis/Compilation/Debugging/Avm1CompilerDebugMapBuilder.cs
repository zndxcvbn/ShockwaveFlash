using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Preprocessing;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

internal static class Avm1CompilerDebugMapBuilder
{
    public static Avm1CompilerDebugMap Build(
        IReadOnlyList<Avm1PreprocessorResult> preprocessing,
        Avm1ProgramArtifact? artifact)
    {
        var expandedSources = preprocessing
            .Select(result => result.Source)
            .ToArray();
        BuildDocuments(
            expandedSources,
            out var documents,
            out var sourceDocuments);

        var classMaps = artifact?.Classes
            .Select(sourceClass => sourceClass.SourceMap)
            .ToArray() ?? [];
        var initial = new Avm1CompilerDebugMap(
            documents,
            [],
            expandedSources,
            sourceDocuments,
            classMaps,
            mappedEntryCount: 0,
            unresolvedEntryCount: 0);
        if (artifact is null)
            return initial;

        var points = new List<Avm1CompiledSequencePoint>();
        var mappedEntryCount = 0;
        var unresolvedEntryCount = 0;
        for (var classIndex = 0;
             classIndex < artifact.Classes.Count;
             classIndex++)
        {
            foreach (var entry in artifact.Classes[classIndex].SourceMap.Entries)
            {
                var methodMap = artifact.Classes[classIndex].SourceMap
                    .GetMethodSourceMap(entry.MethodIndex);
                if (!initial.TryResolveOrigin(
                        methodMap.Arena,
                        entry.Origin,
                        out var source))
                {
                    unresolvedEntryCount++;
                    continue;
                }

                mappedEntryCount++;
                AddOrMerge(points, new Avm1CompiledSequencePoint(
                    classIndex,
                    entry.MethodIndex,
                    entry.MethodEntry.CodeUnit,
                    entry.ByteOffset,
                    entry.ByteLength,
                    source,
                    entry.MethodEntry.SyntheticKind is not
                        Avm1SyntheticActionKind.None));
            }
        }

        return new Avm1CompilerDebugMap(
            documents,
            points.ToArray(),
            expandedSources,
            sourceDocuments,
            classMaps,
            mappedEntryCount,
            unresolvedEntryCount);
    }

    private static void BuildDocuments(
        Avm1ExpandedSource[] sources,
        out Avm1DebugDocument[] documents,
        out Dictionary<string, Avm1DebugDocumentIndex>[] sourceDocuments)
    {
        var result = new List<Avm1DebugDocument>();
        sourceDocuments = new Dictionary<string, Avm1DebugDocumentIndex>[
            sources.Length];
        for (var sourceIndex = 0;
             sourceIndex < sources.Length;
             sourceIndex++)
        {
            var lookup = new Dictionary<string, Avm1DebugDocumentIndex>(
                PathComparer);
            sourceDocuments[sourceIndex] = lookup;
            foreach (var sourceDocument in sources[sourceIndex].Documents)
            {
                if (lookup.ContainsKey(sourceDocument.Path))
                {
                    throw new InvalidOperationException(
                        $"Expanded source {sources[sourceIndex].Path} contains " +
                        $"document {sourceDocument.Path} more than once.");
                }

                var document = FindDocument(result, sourceDocument);
                if (!document.IsValid)
                {
                    document = new Avm1DebugDocumentIndex(result.Count);
                    result.Add(new Avm1DebugDocument(document, sourceDocument));
                }
                lookup.Add(sourceDocument.Path, document);
            }
        }
        documents = result.ToArray();
    }

    private static Avm1DebugDocumentIndex FindDocument(
        List<Avm1DebugDocument> documents,
        Avm1SourceDocument source)
    {
        for (var i = 0; i < documents.Count; i++)
        {
            if (PathComparer.Equals(documents[i].Path, source.Path) &&
                string.Equals(documents[i].Text, source.Text, StringComparison.Ordinal))
            {
                return new Avm1DebugDocumentIndex(i);
            }
        }
        return Avm1DebugDocumentIndex.Invalid;
    }

    private static void AddOrMerge(
        List<Avm1CompiledSequencePoint> points,
        Avm1CompiledSequencePoint point)
    {
        if (points.Count == 0)
        {
            points.Add(point);
            return;
        }

        var previous = points[^1];
        if (previous.ClassIndex == point.ClassIndex &&
            previous.MethodIndex == point.MethodIndex &&
            previous.CodeUnit == point.CodeUnit &&
            previous.EndByteOffset == point.ByteOffset &&
            previous.Source == point.Source)
        {
            points[^1] = previous with
            {
                ByteLength = checked(previous.ByteLength + point.ByteLength),
                ContainsSyntheticActions =
                    previous.ContainsSyntheticActions ||
                    point.ContainsSyntheticActions
            };
            return;
        }

        points.Add(point);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
