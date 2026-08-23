using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation;

public sealed record FunctionContext(
    FunctionFlags Flags,
    IReadOnlyList<FunctionParameter> Parameters)
{
    public bool PreloadThis => Flags.HasFlag(FunctionFlags.PreloadThis);

    public bool PreloadArguments => Flags.HasFlag(FunctionFlags.PreloadArguments);

    public bool PreloadSuper => Flags.HasFlag(FunctionFlags.PreloadSuper);

    public bool PreloadRoot => Flags.HasFlag(FunctionFlags.PreloadRoot);

    public bool PreloadParent => Flags.HasFlag(FunctionFlags.PreloadParent);

    public bool PreloadGlobal => Flags.HasFlag(FunctionFlags.PreloadGlobal);

    public FunctionContext(
        bool preloadThis,
        bool preloadSuper,
        IReadOnlyList<FunctionParameter> parameters)
        : this(
            (preloadThis ? FunctionFlags.PreloadThis : 0) |
            (preloadSuper ? FunctionFlags.PreloadSuper : 0),
            parameters)
    {
    }
}
