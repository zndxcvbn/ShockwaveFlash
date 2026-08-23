using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation.Analysis;

internal readonly record struct Avm1FunctionPreloadPlan(
    FunctionFlags Flags,
    byte PreloadRegisterCount,
    byte ThisRegister,
    byte ArgumentsRegister,
    byte SuperRegister,
    byte RootRegister,
    byte ParentRegister,
    byte GlobalRegister)
{
    public static readonly Avm1FunctionPreloadPlan Empty = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0);

    public static Avm1FunctionPreloadPlan Create(
        Avm1ClosureCodeUnitFlags codeUnitFlags,
        bool requiresSelfBinding)
    {
        var dynamicSpecialNamesRequired =
            (codeUnitFlags & (
                Avm1ClosureCodeUnitFlags.CapturesDynamicScope |
                Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope |
                Avm1ClosureCodeUnitFlags.UsesDynamicLookup |
                Avm1ClosureCodeUnitFlags.InvokesEval)) != 0;

        var usesThis = codeUnitFlags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesThis);
        var usesArguments = codeUnitFlags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesArguments);
        var usesSuper = codeUnitFlags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesSuper);
        var preloadThis = usesThis && !dynamicSpecialNamesRequired;
        var preloadArguments =
            requiresSelfBinding ||
            (usesArguments && !dynamicSpecialNamesRequired);
        var preloadSuper = usesSuper && !dynamicSpecialNamesRequired;
        var preloadRoot = codeUnitFlags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesRoot);
        var preloadParent = codeUnitFlags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesParent);
        var preloadGlobal = codeUnitFlags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesGlobal);

        var flags =
            (preloadThis ? FunctionFlags.PreloadThis : 0) |
            (preloadArguments ? FunctionFlags.PreloadArguments : 0) |
            (preloadSuper ? FunctionFlags.PreloadSuper : 0) |
            (preloadRoot ? FunctionFlags.PreloadRoot : 0) |
            (preloadParent ? FunctionFlags.PreloadParent : 0) |
            (preloadGlobal ? FunctionFlags.PreloadGlobal : 0);
        if (!dynamicSpecialNamesRequired)
        {
            if (!usesThis)
                flags |= FunctionFlags.SuppressThis;
            if (!usesArguments && !requiresSelfBinding)
                flags |= FunctionFlags.SuppressArguments;
            if (!usesSuper)
                flags |= FunctionFlags.SuppressSuper;
        }

        byte nextRegister = 1;
        var thisRegister = TakeRegister(preloadThis);
        var argumentsRegister = TakeRegister(preloadArguments);
        var superRegister = TakeRegister(preloadSuper);
        var rootRegister = TakeRegister(preloadRoot);
        var parentRegister = TakeRegister(preloadParent);
        var globalRegister = TakeRegister(preloadGlobal);
        return new Avm1FunctionPreloadPlan(
            flags,
            checked((byte)(nextRegister - 1)),
            thisRegister,
            argumentsRegister,
            superRegister,
            rootRegister,
            parentRegister,
            globalRegister);

        byte TakeRegister(bool preload)
        {
            if (!preload)
                return 0;
            return nextRegister++;
        }
    }

    public static Avm1FunctionPreloadPlan FromFlags(FunctionFlags flags)
    {
        byte nextRegister = 1;
        var thisRegister = TakeRegister(FunctionFlags.PreloadThis);
        var argumentsRegister = TakeRegister(FunctionFlags.PreloadArguments);
        var superRegister = TakeRegister(FunctionFlags.PreloadSuper);
        var rootRegister = TakeRegister(FunctionFlags.PreloadRoot);
        var parentRegister = TakeRegister(FunctionFlags.PreloadParent);
        var globalRegister = TakeRegister(FunctionFlags.PreloadGlobal);
        return new Avm1FunctionPreloadPlan(
            flags,
            checked((byte)(nextRegister - 1)),
            thisRegister,
            argumentsRegister,
            superRegister,
            rootRegister,
            parentRegister,
            globalRegister);

        byte TakeRegister(FunctionFlags preloadFlag)
        {
            if (!flags.HasFlag(preloadFlag))
                return 0;
            return nextRegister++;
        }
    }

    public bool TryGetRegister(
        Avm1SourceSymbolKind symbolKind,
        out byte register)
    {
        register = symbolKind switch
        {
            Avm1SourceSymbolKind.This => ThisRegister,
            Avm1SourceSymbolKind.Arguments => ArgumentsRegister,
            Avm1SourceSymbolKind.Super => SuperRegister,
            Avm1SourceSymbolKind.Root => RootRegister,
            Avm1SourceSymbolKind.Parent => ParentRegister,
            Avm1SourceSymbolKind.Global => GlobalRegister,
            _ => 0
        };
        return register != 0;
    }
}
