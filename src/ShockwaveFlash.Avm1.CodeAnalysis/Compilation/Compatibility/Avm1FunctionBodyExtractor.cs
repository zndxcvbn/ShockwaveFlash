using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compatibility;

public sealed class Avm1FunctionBody
{
    internal Avm1FunctionBody(
        string name,
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        byte? registerCount,
        FunctionFlags flags,
        FunctionParameter[] parameters,
        bool usesDefineFunction2,
        IReadOnlyList<string>? initialConstantPool)
    {
        Name = name;
        Bytecode = bytecode;
        SwfVersion = swfVersion;
        RegisterCount = registerCount;
        Flags = flags;
        Parameters = parameters;
        UsesDefineFunction2 = usesDefineFunction2;
        InitialConstantPool = initialConstantPool?.ToArray();
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> Bytecode { get; }

    public byte SwfVersion { get; }

    public byte? RegisterCount { get; }

    public FunctionFlags Flags { get; }

    public IReadOnlyList<FunctionParameter> Parameters { get; }

    public bool UsesDefineFunction2 { get; }

    public IReadOnlyList<string>? InitialConstantPool { get; }

    public FunctionContext FunctionContext => new(Flags, Parameters);

    public Avm1MethodCompatibilityInput ToCompatibilityInput() =>
        new(Name, Bytecode, SwfVersion)
        {
            FunctionContext = FunctionContext,
            RegisterCount = RegisterCount,
            InitialConstantPool = InitialConstantPool
        };
}

public static class Avm1FunctionBodyExtractor
{
    public static IReadOnlyList<Avm1FunctionBody> ExtractTopLevel(
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        bool strict = true)
    {
        if (swfVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(swfVersion));

        var actions = Action.DecodeCollection(bytecode, swfVersion, strict);
        var functions = new List<Avm1FunctionBody>();
        string[]? activeConstantPool = null;
        for (var i = 0; i < actions.Count; i++)
        {
            switch (actions[i])
            {
                case ActionConstantPool constantPool:
                    activeConstantPool = constantPool.Constants.ToArray();
                    break;

                case ActionDefineFunction2 function:
                    functions.Add(new Avm1FunctionBody(
                        function.Name,
                        function.Body,
                        swfVersion,
                        function.RegisterCount,
                        function.Flags,
                        function.Parameters.ToArray(),
                        usesDefineFunction2: true,
                        activeConstantPool));
                    break;

                case ActionDefineFunction function:
                    functions.Add(new Avm1FunctionBody(
                        function.Name,
                        function.Body,
                        swfVersion,
                        registerCount: null,
                        flags: 0,
                        function.Parameters.Select(parameter =>
                            new FunctionParameter(0, parameter)).ToArray(),
                        usesDefineFunction2: false,
                        activeConstantPool));
                    break;
            }
        }
        return functions;
    }
}
