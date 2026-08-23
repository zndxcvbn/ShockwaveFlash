using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Tags;

namespace ShockwaveFlash.Avm1.Compilation;

public readonly record struct Avm1LinkedSourceMapPlacement(
    int PlacementIndex,
    Avm1SwfPlacementKind Kind,
    int TagIndex,
    TagCode TagCode,
    ushort SpriteId,
    int TagStreamOffset,
    int TagHeaderLength,
    int ActionDataOffset,
    int ActionStreamOffset,
    int ActionLength)
{
    public int EndActionStreamOffset => checked(ActionStreamOffset + ActionLength);
}

public readonly record struct Avm1LinkedSourceMapEntry(
    int PlacementIndex,
    int TagStreamOffset,
    Avm1CompiledClassSourceMapEntry ClassEntry)
{
    public int ByteLength => ClassEntry.ByteLength;

    public int EndTagStreamOffset => checked(TagStreamOffset + ByteLength);

    public int MethodIndex => ClassEntry.MethodIndex;

    public SourceOriginIndex Origin => ClassEntry.Origin;
}

public sealed class Avm1LinkedSourceMap
{
    private readonly Avm1LinkedSourceMapPlacement[] _placements;
    private readonly Avm1LinkedSourceMapEntry[] _entries;
    private readonly Avm1CompiledClassSourceMap[] _classMaps;

    internal Avm1LinkedSourceMap(
        int uncompressedTagStreamFileOffset,
        Avm1LinkedSourceMapPlacement[] placements,
        Avm1LinkedSourceMapEntry[] entries,
        Avm1CompiledClassSourceMap[] classMaps)
    {
        UncompressedTagStreamFileOffset = uncompressedTagStreamFileOffset;
        _placements = placements;
        _entries = entries;
        _classMaps = classMaps;
    }

    public IReadOnlyList<Avm1LinkedSourceMapPlacement> Placements => _placements;

    public IReadOnlyList<Avm1LinkedSourceMapEntry> Entries => _entries;

    public int UncompressedTagStreamFileOffset { get; }

    public Avm1CompiledClassSourceMap GetClassSourceMap(int placementIndex)
    {
        ValidatePlacementIndex(placementIndex);
        return _classMaps[placementIndex];
    }

    public IEnumerable<Avm1LinkedSourceMapEntry> GetEntries(
        int placementIndex,
        int methodIndex,
        SourceOriginIndex origin)
    {
        ValidatePlacementIndex(placementIndex);
        var classMap = _classMaps[placementIndex];
        var methodMap = classMap.GetMethodSourceMap(methodIndex);
        if (!origin.IsValid || origin.Value >= methodMap.Arena.Origins.Count)
            throw new ArgumentOutOfRangeException(nameof(origin));

        foreach (var entry in _entries)
        {
            if (entry.PlacementIndex == placementIndex &&
                entry.MethodIndex == methodIndex &&
                entry.Origin == origin)
            {
                yield return entry;
            }
        }
    }

    public bool TryGetEntry(
        int tagStreamOffset,
        out Avm1LinkedSourceMapEntry entry)
    {
        if (tagStreamOffset < 0)
        {
            entry = default;
            return false;
        }

        var best = -1;
        var bestDepth = -1;
        var bestLength = int.MaxValue;
        for (var i = 0; i < _entries.Length; i++)
        {
            var candidate = _entries[i];
            if (tagStreamOffset < candidate.TagStreamOffset ||
                tagStreamOffset >= candidate.EndTagStreamOffset)
            {
                continue;
            }

            var classMap = _classMaps[candidate.PlacementIndex];
            var methodMap = classMap.GetMethodSourceMap(candidate.MethodIndex);
            var depth = methodMap.CodeUnits[
                candidate.ClassEntry.MethodEntry.CodeUnit.Value].Depth;
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
            entry = default;
            return false;
        }

        entry = _entries[best];
        return true;
    }

    public bool TryGetOrigin(
        int tagStreamOffset,
        out Avm1SourceOrigin origin)
    {
        if (!TryGetEntry(tagStreamOffset, out var entry))
        {
            origin = default;
            return false;
        }

        var methodMap = _classMaps[entry.PlacementIndex]
            .GetMethodSourceMap(entry.MethodIndex);
        if (entry.Origin.Value >= methodMap.Arena.Origins.Count)
        {
            origin = default;
            return false;
        }

        origin = methodMap.Arena[entry.Origin];
        return true;
    }

    internal static Avm1LinkedSourceMap Build(
        IReadOnlyList<Avm1LinkedSourceMapSite> sites,
        int uncompressedTagStreamFileOffset)
    {
        if (sites.Count == 0)
            return Empty;
        ArgumentOutOfRangeException.ThrowIfNegative(
            uncompressedTagStreamFileOffset);

        var count = sites.Max(site => site.Placement.PlacementIndex) + 1;
        if (count != sites.Count)
        {
            throw new InvalidOperationException(
                "Linked source-map placements must be contiguous.");
        }

        var placements = new Avm1LinkedSourceMapPlacement[count];
        var classMaps = new Avm1CompiledClassSourceMap[count];
        var assigned = new bool[count];
        var entries = new List<Avm1LinkedSourceMapEntry>();
        foreach (var site in sites)
        {
            var placement = site.Placement;
            if (assigned[placement.PlacementIndex])
            {
                throw new InvalidOperationException(
                    $"Linked placement {placement.PlacementIndex} is duplicated.");
            }

            placements[placement.PlacementIndex] = placement;
            classMaps[placement.PlacementIndex] = site.ClassMap;
            assigned[placement.PlacementIndex] = true;
            foreach (var classEntry in site.ClassMap.Entries)
            {
                entries.Add(new Avm1LinkedSourceMapEntry(
                    placement.PlacementIndex,
                    checked(placement.ActionStreamOffset + classEntry.ByteOffset),
                    classEntry));
            }
        }

        return new Avm1LinkedSourceMap(
            uncompressedTagStreamFileOffset,
            placements,
            entries
                .OrderBy(entry => entry.TagStreamOffset)
                .ThenBy(entry => entry.PlacementIndex)
                .ThenBy(entry => entry.MethodIndex)
                .ToArray(),
            classMaps);
    }

    internal static Avm1LinkedSourceMap Empty { get; } = new(0, [], [], []);

    private void ValidatePlacementIndex(int placementIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(placementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            placementIndex,
            _classMaps.Length);
    }
}

internal readonly record struct Avm1LinkedSourceMapSite(
    Avm1LinkedSourceMapPlacement Placement,
    Avm1CompiledClassSourceMap ClassMap);
