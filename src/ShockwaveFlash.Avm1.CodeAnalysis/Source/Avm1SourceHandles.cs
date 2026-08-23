using System.Globalization;

namespace ShockwaveFlash.Avm1.Source;

public readonly record struct SourceExpressionIndex(int Value)
{
    public static readonly SourceExpressionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "expr" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceStatementIndex(int Value)
{
    public static readonly SourceStatementIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "stmt" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceFunctionIndex(int Value)
{
    public static readonly SourceFunctionIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "function" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceCodeUnitIndex(int Value)
{
    public static readonly SourceCodeUnitIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "code-unit" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceScopeIndex(int Value)
{
    public static readonly SourceScopeIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "scope" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceSymbolIndex(int Value)
{
    public static readonly SourceSymbolIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "symbol" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceLabelIndex(int Value)
{
    public static readonly SourceLabelIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "label" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceTypeIndex(int Value)
{
    public static readonly SourceTypeIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "type" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceStringIndex(int Value)
{
    public static readonly SourceStringIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "string" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceLiteralIndex(int Value)
{
    public static readonly SourceLiteralIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "literal" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceOriginIndex(int Value)
{
    public static readonly SourceOriginIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "origin" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceOpaqueIndex(int Value)
{
    public static readonly SourceOpaqueIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "opaque" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceProgramSymbolIndex(int Value)
{
    public static readonly SourceProgramSymbolIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "program-symbol" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceProgramTypeIndex(int Value)
{
    public static readonly SourceProgramTypeIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "program-type" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceProgramSignatureIndex(int Value)
{
    public static readonly SourceProgramSignatureIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => IsValid
        ? "program-signature" + Value.ToString(CultureInfo.InvariantCulture)
        : "<invalid>";
}

public readonly record struct SourceExpressionList(int Start, int Count)
{
    public static readonly SourceExpressionList Empty = new(0, 0);
}

public readonly record struct SourceStatementList(int Start, int Count)
{
    public static readonly SourceStatementList Empty = new(0, 0);
}

public readonly record struct SourceSymbolList(int Start, int Count)
{
    public static readonly SourceSymbolList Empty = new(0, 0);
}

public readonly record struct SourceProgramSymbolList(int Start, int Count)
{
    public static readonly SourceProgramSymbolList Empty = new(0, 0);
}

public readonly record struct SourceProgramParameterList(int Start, int Count)
{
    public static readonly SourceProgramParameterList Empty = new(0, 0);
}

public readonly record struct SourceProgramTypeList(int Start, int Count)
{
    public static readonly SourceProgramTypeList Empty = new(0, 0);
}
