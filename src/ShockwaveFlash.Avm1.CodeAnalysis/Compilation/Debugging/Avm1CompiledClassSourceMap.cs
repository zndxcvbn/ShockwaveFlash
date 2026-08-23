using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public enum Avm1CompiledClassMethodBodyKind : byte
{
    Compiled,
    ReusedIdentical,
    ReusedUnmapped,
    Unplaced
}

public readonly record struct Avm1CompiledClassMethodSourceMap(
    int MethodIndex,
    int InitializerActionIndex,
    int ByteOffset,
    int ByteLength,
    Avm1CompiledClassMethodBodyKind Kind)
{
    public int EndByteOffset => checked(ByteOffset + ByteLength);

    public bool IsPlaced => InitializerActionIndex >= 0 && ByteOffset >= 0;

    public bool IsMapped => Kind is
        Avm1CompiledClassMethodBodyKind.Compiled or
        Avm1CompiledClassMethodBodyKind.ReusedIdentical;
}

public readonly record struct Avm1CompiledClassSourceMapEntry(
    int MethodIndex,
    int ByteOffset,
    Avm1CompiledSourceMapEntry MethodEntry)
{
    public int ByteLength => MethodEntry.ByteLength;

    public int EndByteOffset => checked(ByteOffset + ByteLength);

    public SourceOriginIndex Origin => MethodEntry.Origin;
}

public sealed class Avm1CompiledClassSourceMap
{
    private readonly Avm1CompiledClassMethodSourceMap[] _methodBodies;
    private readonly Avm1CompiledClassSourceMapEntry[] _entries;
    private readonly Avm1CompiledSourceMap[] _methodMaps;

    internal Avm1CompiledClassSourceMap(
        Avm1CompiledClassMethodSourceMap[] methodBodies,
        Avm1CompiledClassSourceMapEntry[] entries,
        Avm1CompiledSourceMap[] methodMaps)
    {
        _methodBodies = methodBodies;
        _entries = entries;
        _methodMaps = methodMaps;
    }

    public IReadOnlyList<Avm1CompiledClassMethodSourceMap> MethodBodies =>
        _methodBodies;

    public IReadOnlyList<Avm1CompiledClassSourceMapEntry> Entries => _entries;

    public Avm1CompiledSourceMap GetMethodSourceMap(int methodIndex)
    {
        ValidateMethodIndex(methodIndex);
        return _methodMaps[methodIndex];
    }

    public IEnumerable<Avm1CompiledClassSourceMapEntry> GetEntries(
        int methodIndex,
        SourceOriginIndex origin)
    {
        ValidateMethodIndex(methodIndex);
        var methodMap = _methodMaps[methodIndex];
        if (!origin.IsValid || origin.Value >= methodMap.Arena.Origins.Count)
            throw new ArgumentOutOfRangeException(nameof(origin));

        foreach (var entry in _entries)
        {
            if (entry.MethodIndex == methodIndex && entry.Origin == origin)
                yield return entry;
        }
    }

    public bool TryGetEntry(
        int byteOffset,
        out Avm1CompiledClassSourceMapEntry entry)
    {
        if (byteOffset < 0)
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
            if (byteOffset < candidate.ByteOffset ||
                byteOffset >= candidate.EndByteOffset)
            {
                continue;
            }

            var methodMap = _methodMaps[candidate.MethodIndex];
            var depth = methodMap.CodeUnits[candidate.MethodEntry.CodeUnit.Value].Depth;
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
        int byteOffset,
        out Avm1SourceOrigin origin)
    {
        if (!TryGetEntry(byteOffset, out var entry))
        {
            origin = default;
            return false;
        }

        var arena = _methodMaps[entry.MethodIndex].Arena;
        if (entry.Origin.Value >= arena.Origins.Count)
        {
            origin = default;
            return false;
        }

        origin = arena[entry.Origin];
        return true;
    }

    internal static Avm1CompiledClassSourceMap Empty(
        IReadOnlyList<Avm1CompiledClassMember> methods) =>
        new(
            [],
            [],
            methods.Select(method => method.Method.SourceMap).ToArray());

    private void ValidateMethodIndex(int methodIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(methodIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            methodIndex,
            _methodMaps.Length);
    }
}
