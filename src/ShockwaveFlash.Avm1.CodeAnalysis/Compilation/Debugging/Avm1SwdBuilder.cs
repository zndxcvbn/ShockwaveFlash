namespace ShockwaveFlash.Avm1.Compilation;

public static class Avm1SwdBuilder
{
    public static Avm1SwdArtifact Build(
        Avm1CompilerDebugMap sourceMap,
        Avm1SwfLinkResult linkResult)
    {
        ArgumentNullException.ThrowIfNull(sourceMap);
        ArgumentNullException.ThrowIfNull(linkResult);
        if (!linkResult.Succeeded)
        {
            throw new ArgumentException(
                "SWD data can only be built for a successful SWF link result.",
                nameof(linkResult));
        }
        if (linkResult.Debugger is not { } debugger)
        {
            throw new ArgumentException(
                "The SWF link result has no matching debugger configuration.",
                nameof(linkResult));
        }

        var linkedMap = sourceMap.CreateLinkedMap(linkResult);
        var candidates = SelectLineCandidates(linkResult, linkedMap);
        var usedDocuments = candidates
            .Select(candidate => candidate.Point.Source.Document.Value)
            .Distinct()
            .Order()
            .ToArray();
        var records = new List<Avm1SwdRecord>(
            1 + usedDocuments.Length + candidates.Length);
        records.Add(new Avm1SwdDebugIdRecord(debugger.DebugId));

        for (var i = 0; i < usedDocuments.Length; i++)
        {
            var moduleId = checked((uint)i + 1);
            var document = sourceMap.GetDocument(
                new Avm1DebugDocumentIndex(usedDocuments[i]));
            records.Add(new Avm1SwdSourceFileRecord(
                moduleId,
                Avm1SwdSourceFileRecord.ActionScriptBitmap,
                document.Path,
                document.Text));

            foreach (var candidate in candidates
                         .Where(candidate =>
                             candidate.Point.Source.Document == document.Index)
                         .OrderBy(candidate => candidate.SwfByteOffset)
                         .ThenBy(candidate => candidate.Line))
            {
                records.Add(new Avm1SwdOffsetMapRecord(
                    moduleId,
                    checked((uint)candidate.Line),
                    checked((uint)candidate.SwfByteOffset)));
            }
        }

        var file = new Avm1SwdFile(Avm1SwdFile.CurrentVersion, records);
        return new Avm1SwdArtifact(
            file,
            Avm1SwdCodec.Encode(file),
            linkedMap);
    }

    private static LineCandidate[] SelectLineCandidates(
        Avm1SwfLinkResult linkResult,
        Avm1LinkedCompilerDebugMap linkedMap)
    {
        var filePrefix = linkResult.SourceMap.UncompressedTagStreamFileOffset;
        var candidates = new Dictionary<LineKey, LineCandidate>();
        foreach (var point in linkedMap.SequencePoints)
        {
            var line = point.Source.Lines.Start.Line;
            var key = new LineKey(point.Source.Document, line);
            var candidate = new LineCandidate(
                point,
                line,
                checked(filePrefix + point.TagStreamOffset));
            if (!candidates.TryGetValue(key, out var prior) ||
                IsPreferred(candidate, prior))
            {
                candidates[key] = candidate;
            }
        }
        return candidates.Values
            .OrderBy(candidate => candidate.Point.Source.Document.Value)
            .ThenBy(candidate => candidate.Line)
            .ThenBy(candidate => candidate.SwfByteOffset)
            .ToArray();
    }

    private static bool IsPreferred(LineCandidate candidate, LineCandidate prior)
    {
        if (candidate.Point.ClassPoint.ContainsSyntheticActions !=
            prior.Point.ClassPoint.ContainsSyntheticActions)
        {
            return !candidate.Point.ClassPoint.ContainsSyntheticActions;
        }
        return candidate.SwfByteOffset < prior.SwfByteOffset;
    }

    private readonly record struct LineKey(
        Avm1DebugDocumentIndex Document,
        int Line);

    private readonly record struct LineCandidate(
        Avm1LinkedSequencePoint Point,
        int Line,
        int SwfByteOffset);
}
