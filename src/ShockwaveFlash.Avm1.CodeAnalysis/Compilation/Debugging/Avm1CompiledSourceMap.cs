using System.Globalization;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public readonly record struct Avm1CompiledCodeUnitIndex(int Value)
{
    public static readonly Avm1CompiledCodeUnitIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "codeUnit" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public enum Avm1CompiledCodeUnitKind : byte
{
    Method,
    Function,
    With,
    Try,
    Catch,
    Finally
}

public readonly record struct Avm1CompiledSourceMapCodeUnit(
    Avm1CompiledCodeUnitIndex Index,
    Avm1CompiledCodeUnitIndex Parent,
    Avm1CompiledCodeUnitKind Kind,
    int Depth,
    int ByteOffset,
    int ByteLength,
    Avm1AssemblyInstructionIndex ParentInstruction)
{
    public int EndByteOffset => checked(ByteOffset + ByteLength);
}

public readonly record struct Avm1CompiledSourceMapEntry(
    Avm1CompiledCodeUnitIndex CodeUnit,
    SourceOriginIndex Origin,
    int ActionIndex,
    int ByteOffset,
    int ByteLength,
    Avm1AssemblyInstructionIndex AssemblyInstruction,
    Avm1StackLirInstructionIndex StackInstruction,
    Avm1MirInstructionIndex MirInstruction,
    Avm1SyntheticActionKind SyntheticKind)
{
    public int EndByteOffset => checked(ByteOffset + ByteLength);
}

public sealed class Avm1CompiledSourceMap
{
    private readonly Avm1CompiledSourceMapCodeUnit[] _codeUnits;
    private readonly Avm1CompiledSourceMapEntry[] _entries;

    internal Avm1CompiledSourceMap(
        Avm1SourceArena arena,
        Avm1CompiledSourceMapCodeUnit[] codeUnits,
        Avm1CompiledSourceMapEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(arena);
        Arena = arena;
        _codeUnits = codeUnits;
        _entries = entries;
    }

    public Avm1SourceArena Arena { get; }

    public IReadOnlyList<Avm1CompiledSourceMapCodeUnit> CodeUnits =>
        _codeUnits;

    public IReadOnlyList<Avm1CompiledSourceMapEntry> Entries => _entries;

    public IEnumerable<Avm1CompiledSourceMapEntry> GetEntries(
        SourceOriginIndex origin)
    {
        if (!origin.IsValid || origin.Value >= Arena.Origins.Count)
            throw new ArgumentOutOfRangeException(nameof(origin));

        foreach (var entry in _entries)
        {
            if (entry.Origin == origin)
                yield return entry;
        }
    }

    public bool TryGetEntry(
        int byteOffset,
        out Avm1CompiledSourceMapEntry entry)
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

            var depth = _codeUnits[candidate.CodeUnit.Value].Depth;
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
        if (!TryGetEntry(byteOffset, out var entry) ||
            entry.Origin.Value >= Arena.Origins.Count)
        {
            origin = default;
            return false;
        }

        origin = Arena[entry.Origin];
        return true;
    }
}
