namespace ShockwaveFlash.Avm1.Source;

public interface IAvm1ReferenceProvider
{
    IEnumerable<Avm1SourceReferenceClass> GetClasses();
}

public sealed class Avm1ReferenceCatalog : IAvm1ReferenceProvider
{
    private readonly Avm1SourceReferenceClass[] _classes;

    public Avm1ReferenceCatalog(IEnumerable<Avm1SourceReferenceClass> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        _classes = classes.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceClass in _classes)
        {
            if (sourceClass is null)
            {
                throw new ArgumentException(
                    "A reference catalog cannot contain a null class.",
                    nameof(classes));
            }
            if (!names.Add(sourceClass.Name.Value))
            {
                throw new ArgumentException(
                    $"Reference class {sourceClass.Name.Value} is declared more than once.",
                    nameof(classes));
            }
        }
    }

    public IReadOnlyList<Avm1SourceReferenceClass> Classes => _classes;

    public IEnumerable<Avm1SourceReferenceClass> GetClasses() => _classes;
}

public sealed class Avm1CompositeReferenceProvider : IAvm1ReferenceProvider
{
    private readonly IAvm1ReferenceProvider[] _providers;

    public Avm1CompositeReferenceProvider(
        IEnumerable<IAvm1ReferenceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
        if (_providers.Any(provider => provider is null))
        {
            throw new ArgumentException(
                "A composite reference provider cannot contain null.",
                nameof(providers));
        }
    }

    public IReadOnlyList<IAvm1ReferenceProvider> Providers => _providers;

    public IEnumerable<Avm1SourceReferenceClass> GetClasses()
    {
        foreach (var provider in _providers)
        {
            var classes = provider.GetClasses() ?? throw new InvalidOperationException(
                "An AVM1 reference provider returned a null class sequence.");
            foreach (var sourceClass in classes)
            {
                yield return sourceClass ?? throw new InvalidOperationException(
                    "An AVM1 reference provider returned a null class.");
            }
        }
    }
}

public sealed class Avm1SourceReferenceClass
{
    private readonly Avm1SourceReferenceMember[] _members;
    private readonly Avm1SourceTypeReference[] _interfaces;

    public Avm1SourceReferenceClass(
        Avm1SourceQualifiedName name,
        IEnumerable<Avm1SourceReferenceMember>? members = null,
        Avm1SourceTypeReference? baseType = null,
        IEnumerable<Avm1SourceTypeReference>? interfaces = null,
        Avm1SourceDeclarationModifiers modifiers =
            Avm1SourceDeclarationModifiers.Public |
            Avm1SourceDeclarationModifiers.Intrinsic,
        Avm1SourceTypeDeclarationKind kind =
            Avm1SourceTypeDeclarationKind.Class)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Kind = kind;
        BaseType = baseType;
        Modifiers = modifiers;
        _members = members?.ToArray() ?? [];
        _interfaces = interfaces?.ToArray() ?? [];
        if (_members.Any(member => member is null))
        {
            throw new ArgumentException(
                "A reference class cannot contain a null member.",
                nameof(members));
        }
        if (_interfaces.Any(type => type is null))
        {
            throw new ArgumentException(
                "A reference class cannot contain a null interface.",
                nameof(interfaces));
        }
        if (kind is Avm1SourceTypeDeclarationKind.Interface && baseType is not null)
        {
            throw new ArgumentException(
                "A reference interface cannot declare a class base type.",
                nameof(baseType));
        }
    }

    public Avm1SourceQualifiedName Name { get; }

    public Avm1SourceTypeDeclarationKind Kind { get; }

    public Avm1SourceTypeReference? BaseType { get; }

    public IReadOnlyList<Avm1SourceTypeReference> Interfaces => _interfaces;

    public IReadOnlyList<Avm1SourceReferenceMember> Members => _members;

    public Avm1SourceDeclarationModifiers Modifiers { get; }
}

public enum Avm1SourceReferenceMemberKind : byte
{
    Field,
    Constructor,
    Method,
    Getter,
    Setter
}

public sealed class Avm1SourceReferenceMember
{
    private readonly Avm1SourceReferenceParameter[] _parameters;

    public Avm1SourceReferenceMember(
        string name,
        Avm1SourceReferenceMemberKind kind,
        Avm1SourceTypeReference? type = null,
        IEnumerable<Avm1SourceReferenceParameter>? parameters = null,
        Avm1SourceDeclarationModifiers modifiers = Avm1SourceDeclarationModifiers.Public)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Kind = kind;
        Type = type;
        Modifiers = modifiers;
        _parameters = parameters?.ToArray() ?? [];
        if (_parameters.Any(parameter => parameter is null))
        {
            throw new ArgumentException(
                "A reference member cannot contain a null parameter.",
                nameof(parameters));
        }
        if (kind is Avm1SourceReferenceMemberKind.Field && _parameters.Length != 0)
        {
            throw new ArgumentException(
                "A reference field cannot declare parameters.",
                nameof(parameters));
        }
        var restIndex = Array.FindIndex(
            _parameters,
            parameter => parameter.Flags.HasFlag(
                Avm1SourceReferenceParameterFlags.Rest));
        if (restIndex >= 0 && restIndex != _parameters.Length - 1)
        {
            throw new ArgumentException(
                "A rest parameter must be the final parameter.",
                nameof(parameters));
        }
    }

    public string Name { get; }

    public Avm1SourceReferenceMemberKind Kind { get; }

    public Avm1SourceTypeReference? Type { get; }

    public IReadOnlyList<Avm1SourceReferenceParameter> Parameters => _parameters;

    public Avm1SourceDeclarationModifiers Modifiers { get; }
}

[Flags]
public enum Avm1SourceReferenceParameterFlags : byte
{
    None = 0,
    Optional = 1 << 0,
    Rest = 1 << 1
}

public sealed class Avm1SourceReferenceParameter
{
    public Avm1SourceReferenceParameter(
        string name,
        Avm1SourceTypeReference? type = null,
        Avm1SourceReferenceParameterFlags flags = Avm1SourceReferenceParameterFlags.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type;
        Flags = flags;
    }

    public string Name { get; }

    public Avm1SourceTypeReference? Type { get; }

    public Avm1SourceReferenceParameterFlags Flags { get; }
}
