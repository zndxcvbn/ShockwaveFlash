using System.Globalization;

namespace ShockwaveFlash.Avm1.Decompilation;

public readonly record struct ActionIndex(int Value)
{
    public static readonly ActionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct BlockIndex(int Value)
{
    public static readonly BlockIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct CompletionEdgeIndex(int Value)
{
    public static readonly CompletionEdgeIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct CompletionValueIndex(int Value)
{
    public static readonly CompletionValueIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "completion" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct CompletionInputIndex(int Value)
{
    public static readonly CompletionInputIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct CatchBindingIndex(int Value)
{
    public static readonly CatchBindingIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct CatchSourceIndex(int Value)
{
    public static readonly CatchSourceIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct FlowContextIndex(int Value)
{
    public static readonly FlowContextIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "context" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct FlowPointIndex(int Value)
{
    public static readonly FlowPointIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "flow" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct FlowTransitionIndex(int Value)
{
    public static readonly FlowTransitionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public enum Avm1FlowState : byte
{
    Normal,
    Return,
    Throw,
    Merged
}

public readonly record struct CodeRegionIndex(int Value)
{
    public static readonly CodeRegionIndex Invalid = new(-1);
    public static readonly CodeRegionIndex Root = new(0);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "region" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct ValueIndex(int Value)
{
    public static readonly ValueIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "v" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct IrIndex(int Value)
{
    public static readonly IrIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "ir" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}

public readonly record struct AstIndex(int Value)
{
    public static readonly AstIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid ? "ast" + Value.ToString(CultureInfo.InvariantCulture) : "<invalid>";
}
