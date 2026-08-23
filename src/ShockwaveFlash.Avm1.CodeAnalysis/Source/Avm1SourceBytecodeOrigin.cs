namespace ShockwaveFlash.Avm1.Source;

public readonly record struct Avm1SourceFunctionParameterOrigin(
    byte Register,
    string Name);

public sealed class Avm1SourceMethodBytecodeOrigin
{
    private readonly byte[] _body;
    private readonly Avm1SourceFunctionParameterOrigin[] _parameters;
    private readonly string[] _initialConstantPool;

    public Avm1SourceMethodBytecodeOrigin(
        byte swfVersion,
        int initializerActionIndex,
        ReadOnlyMemory<byte> body,
        byte registerCount,
        ushort functionFlags,
        IEnumerable<Avm1SourceFunctionParameterOrigin> parameters,
        IEnumerable<string>? initialConstantPool,
        string sourceFingerprint)
    {
        if (swfVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(swfVersion));
        ArgumentOutOfRangeException.ThrowIfNegative(initializerActionIndex);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);

        SwfVersion = swfVersion;
        InitializerActionIndex = initializerActionIndex;
        _body = body.ToArray();
        RegisterCount = registerCount;
        FunctionFlags = functionFlags;
        _parameters = parameters.ToArray();
        if (_parameters.Any(parameter => parameter.Name is null))
        {
            throw new ArgumentException(
                "Function parameter names cannot be null.",
                nameof(parameters));
        }
        _initialConstantPool = initialConstantPool?.ToArray() ?? [];
        if (_initialConstantPool.Any(value => value is null))
        {
            throw new ArgumentException(
                "Constant-pool entries cannot be null.",
                nameof(initialConstantPool));
        }
        SourceFingerprint = sourceFingerprint;
    }

    public byte SwfVersion { get; }

    public int InitializerActionIndex { get; }

    public ReadOnlyMemory<byte> Body => _body;

    public byte RegisterCount { get; }

    public ushort FunctionFlags { get; }

    public IReadOnlyList<Avm1SourceFunctionParameterOrigin> Parameters =>
        _parameters;

    public IReadOnlyList<string> InitialConstantPool => _initialConstantPool;

    public string SourceFingerprint { get; }
}

public sealed class Avm1SourceClassBytecodeOrigin
{
    private readonly byte[] _initializerBody;

    public Avm1SourceClassBytecodeOrigin(
        byte swfVersion,
        ReadOnlyMemory<byte> initializerBody,
        string sourceFingerprint,
        string shapeFingerprint)
    {
        if (swfVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(swfVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeFingerprint);

        SwfVersion = swfVersion;
        _initializerBody = initializerBody.ToArray();
        SourceFingerprint = sourceFingerprint;
        ShapeFingerprint = shapeFingerprint;
    }

    public byte SwfVersion { get; }

    public ReadOnlyMemory<byte> InitializerBody => _initializerBody;

    public string SourceFingerprint { get; }

    public string ShapeFingerprint { get; }
}
