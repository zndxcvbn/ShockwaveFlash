namespace ShockwaveFlash.Avm1.Types;

[Flags]
public enum GetUrlFlags : byte
{
    MethodNone = 0,
    MethodGet = 1,
    MethodPost = 2,
    MethodMask = 3,
    ReservedMask = 0x3C,

    LoadTarget = 1 << 6,
    LoadVariables = 1 << 7
}
