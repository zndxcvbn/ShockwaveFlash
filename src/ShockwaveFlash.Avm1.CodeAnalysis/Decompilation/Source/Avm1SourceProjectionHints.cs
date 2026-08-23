namespace ShockwaveFlash.Avm1.Decompilation;

internal sealed class Avm1SourceProjectionHints
{
    private readonly HashSet<int> _sourceRegisters;
    private readonly HashSet<int> _identityPreservingRegisters;
    private readonly Avm1SourceProjectionHints[] _nestedFunctions;

    public Avm1SourceProjectionHints(
        IEnumerable<int> sourceRegisters,
        IEnumerable<int> identityPreservingRegisters,
        IEnumerable<Avm1SourceProjectionHints> nestedFunctions)
    {
        ArgumentNullException.ThrowIfNull(sourceRegisters);
        ArgumentNullException.ThrowIfNull(identityPreservingRegisters);
        ArgumentNullException.ThrowIfNull(nestedFunctions);
        _sourceRegisters = sourceRegisters.ToHashSet();
        _identityPreservingRegisters = identityPreservingRegisters.ToHashSet();
        _nestedFunctions = nestedFunctions.ToArray();
    }

    public bool HasSourceRegisters => _sourceRegisters.Count != 0;

    public bool IsSourceRegister(int register) =>
        _sourceRegisters.Contains(register);

    public bool PreservesSourceIdentity(int register) =>
        _identityPreservingRegisters.Contains(register);

    public Avm1SourceProjectionHints? GetNestedFunction(int ordinal) =>
        (uint)ordinal < (uint)_nestedFunctions.Length
            ? _nestedFunctions[ordinal]
            : null;
}
