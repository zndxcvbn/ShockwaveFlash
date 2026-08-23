using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal sealed class Avm1SourceRegisterPlan
{
    private readonly int[] _registers;
    private readonly Avm1SourceRegisterAllocation[] _allocations;

    private Avm1SourceRegisterPlan(
        int[] registers,
        Avm1SourceRegisterAllocation[] allocations)
    {
        _registers = registers;
        _allocations = allocations;
        MaximumRegister = allocations.Length == 0
            ? (byte)0
            : allocations.Max(allocation => allocation.Register);
    }

    public static Avm1SourceRegisterPlan Empty { get; } = new([], []);

    public IReadOnlyList<Avm1SourceRegisterAllocation> Allocations =>
        _allocations;

    public byte MaximumRegister { get; }

    public bool TryGetRegister(
        SourceSymbolIndex symbol,
        out byte register)
    {
        if (symbol.IsValid &&
            symbol.Value < _registers.Length &&
            _registers[symbol.Value] >= 0)
        {
            register = checked((byte)_registers[symbol.Value]);
            return true;
        }

        register = 0;
        return false;
    }

    public static Avm1SourceRegisterPlan Allocate(
        Avm1SourceRegisterCandidates candidates,
        Avm1SourceInterferenceGraph graph,
        int firstRegister,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(graph);
        if (candidates.Count == 0 || graph.Count == 0)
            return Empty;
        if (graph.Count != candidates.Count)
        {
            throw new ArgumentException(
                "The interference graph must describe every Source-register " +
                "candidate.",
                nameof(graph));
        }

        firstRegister = Math.Max(1, firstRegister);
        var availableRegisterCount = byte.MaxValue - firstRegister;
        if (availableRegisterCount <= 0)
            return Empty;

        var colors = new int[graph.Count];
        Array.Fill(colors, -1);
        var degrees = new int[graph.Count];
        for (var candidate = 0; candidate < graph.Count; candidate++)
            degrees[candidate] = graph.GetDegree(candidate);

        var colorWordCount = (availableRegisterCount + 63) / 64;
        var neighborColors = new ulong[graph.Count * colorWordCount];
        var saturationCounts = new int[graph.Count];
        for (var colored = 0; colored < graph.Count; colored++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = -1;
            var selectedSaturation = -1;
            var selectedDegree = -1;
            for (var candidate = 0; candidate < graph.Count; candidate++)
            {
                if (colors[candidate] != -1)
                    continue;

                var saturation = saturationCounts[candidate];
                var degree = degrees[candidate];
                if (saturation > selectedSaturation ||
                    saturation == selectedSaturation && degree > selectedDegree)
                {
                    selected = candidate;
                    selectedSaturation = saturation;
                    selectedDegree = degree;
                }
            }

            var selectedColor = FindFirstAvailableColor(
                neighborColors.AsSpan(
                    selected * colorWordCount,
                    colorWordCount),
                availableRegisterCount);
            colors[selected] = selectedColor >= 0 ? selectedColor : -2;
            if (selectedColor < 0)
                continue;

            for (var neighbor = 0; neighbor < graph.Count; neighbor++)
            {
                if (colors[neighbor] != -1 ||
                    !graph.Interferes(selected, neighbor))
                {
                    continue;
                }

                var word = selectedColor >> 6;
                var mask = 1UL << (selectedColor & 63);
                var offset = neighbor * colorWordCount + word;
                if ((neighborColors[offset] & mask) != 0)
                    continue;

                neighborColors[offset] |= mask;
                saturationCounts[neighbor]++;
            }
        }

        var registers = new int[candidates.SymbolCapacity];
        Array.Fill(registers, -1);
        var allocations = new List<Avm1SourceRegisterAllocation>(
            candidates.Count);
        foreach (var symbol in candidates.Symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = graph.GetOrdinal(symbol);
            if (ordinal < 0 || colors[ordinal] < 0)
                continue;

            var register = checked((byte)(firstRegister + colors[ordinal]));
            registers[symbol.Value] = register;
            allocations.Add(new Avm1SourceRegisterAllocation(symbol, register));
        }

        return allocations.Count == 0
            ? Empty
            : new Avm1SourceRegisterPlan(
                registers,
                allocations.ToArray());
    }

    private static int FindFirstAvailableColor(
        ReadOnlySpan<ulong> usedColors,
        int availableRegisterCount)
    {
        for (var wordIndex = 0; wordIndex < usedColors.Length; wordIndex++)
        {
            var available = ~usedColors[wordIndex];
            if (available == 0)
                continue;

            var color = wordIndex * 64 +
                System.Numerics.BitOperations.TrailingZeroCount(available);
            if (color < availableRegisterCount)
                return color;
        }

        return -1;
    }
}
