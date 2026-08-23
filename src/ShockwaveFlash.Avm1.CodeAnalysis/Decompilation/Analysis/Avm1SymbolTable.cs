using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1Symbol
{
    public int Id { get; }
    public int Register { get; }      // -1, если это временная переменная стека
    public int Version { get; }       // Версия SSA
    public ValueIndex TempValue { get; } // Ссылка на ValueIndex, если Register == -1
    public string Name { get; set; }
    public Avm1InferredType Type { get; set; }
    public bool IsDeclared { get; set; }

    public Avm1Symbol(int id, int register, int version, ValueIndex tempValue, string name, Avm1InferredType type)
    {
        Id = id;
        Register = register;
        Version = version;
        TempValue = tempValue;
        Name = name;
        Type = type;
        IsDeclared = false;
    }
}

public sealed class Avm1SymbolTable
{
    private readonly Dictionary<(int Register, int Version), Avm1Symbol> _registerSymbols = [];
    private readonly Dictionary<ValueIndex, Avm1Symbol> _tempSymbols = [];
    private readonly List<Avm1Symbol> _allSymbols = [];
    private readonly HashSet<string> _usedNames = [];

    // Зарезервированные ключевые слова ActionScript 2 во избежание конфликтов имен
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else",
        "extends", "false", "finally", "for", "function", "get", "if", "implements", "import", "in",
        "instanceof", "interface", "intrinsic", "new", "null", "private", "public", "return", "set",
        "static", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined", "var",
        "void", "while", "with", "arguments", "_root", "_parent", "_global"
    };

    public Avm1SymbolTable()
    {
        foreach (var kw in ReservedKeywords)
            _usedNames.Add(kw);
    }

    public IReadOnlyList<Avm1Symbol> Symbols => _allSymbols;

    public Avm1Symbol GetOrCreateRegisterSymbol(int register, int version, Avm1InferredType type)
    {
        var key = (register, version);
        if (_registerSymbols.TryGetValue(key, out var symbol))
            return symbol;

        string defaultName = $"_loc{register.ToString(System.Globalization.CultureInfo.InvariantCulture)}_v{version}";
        string name = SuggestUniqueName(defaultName, _usedNames);
        _usedNames.Add(name);

        symbol = new Avm1Symbol(_allSymbols.Count, register, version, ValueIndex.Invalid, name, type);
        _registerSymbols[key] = symbol;
        _allSymbols.Add(symbol);
        return symbol;
    }

    public Avm1Symbol GetOrCreateTempSymbol(ValueIndex value, Avm1InferredType type)
    {
        if (_tempSymbols.TryGetValue(value, out var symbol))
            return symbol;

        string defaultName = $"v{value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        string name = SuggestUniqueName(defaultName, _usedNames);
        _usedNames.Add(name);

        symbol = new Avm1Symbol(_allSymbols.Count, -1, 0, value, name, type);
        _tempSymbols[value] = symbol;
        _allSymbols.Add(symbol);
        return symbol;
    }

    public bool TryGetRegisterSymbol(int register, int version, out Avm1Symbol symbol) =>
        _registerSymbols.TryGetValue((register, version), out symbol!);

    internal void ReserveName(string name)
    {
        if (name.Length > 0)
            _usedNames.Add(name);
    }

    internal void AliasRegisterSymbol(int register, int version, Avm1Symbol symbol)
    {
        _registerSymbols[(register, version)] = symbol;
    }

    internal void SetPreferredName(Avm1Symbol symbol, string preferredName)
    {
        if (symbol.Name == preferredName)
            return;

        var name = SuggestUniqueName(preferredName, _usedNames);
        _usedNames.Add(name);
        symbol.Name = name;
    }

    public bool TryGetTempSymbol(ValueIndex value, out Avm1Symbol symbol) =>
        _tempSymbols.TryGetValue(value, out symbol!);

    internal static bool IsGeneratedRegisterSymbol(Avm1Symbol symbol) =>
        symbol.Register >= 0 && !symbol.IsDeclared;

    private static string SuggestUniqueName(string baseName, HashSet<string> used)
    {
        if (!used.Contains(baseName))
            return baseName;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName}_{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (!used.Contains(candidate))
                return candidate;
        }
        return $"{baseName}_{Guid.NewGuid().ToString("N")[..4]}";
    }
}
