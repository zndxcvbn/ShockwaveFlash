namespace ShockwaveFlash.Avm1.Source;

public sealed record Avm1SourceQualifiedName
{
    public Avm1SourceQualifiedName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public string SimpleName
    {
        get
        {
            var separator = Value.LastIndexOf('.');
            return separator >= 0 ? Value[(separator + 1)..] : Value;
        }
    }

    public string NamespaceName
    {
        get
        {
            var separator = Value.LastIndexOf('.');
            return separator >= 0 ? Value[..separator] : string.Empty;
        }
    }

    public override string ToString() => Value;
}

public sealed record Avm1SourceTypeReference(Avm1SourceQualifiedName Name);

[Flags]
public enum Avm1SourceDeclarationModifiers : byte
{
    None = 0,
    Static = 1 << 0,
    Public = 1 << 1,
    Private = 1 << 2,
    Protected = 1 << 3,
    Final = 1 << 4,
    Override = 1 << 5,
    Dynamic = 1 << 6,
    Intrinsic = 1 << 7
}

public enum Avm1SourceMethodKind : byte
{
    Constructor,
    Method,
    Getter,
    Setter
}

public enum Avm1SourceTypeDeclarationKind : byte
{
    Class,
    Interface
}

public readonly record struct Avm1SourceClassOrigin(
    ushort SpriteId,
    string? RuntimeName,
    bool HasInitializer,
    Avm1SourceClassBytecodeOrigin? Bytecode = null);

public readonly record struct Avm1SourceMemberOrigin(
    string? RuntimeName,
    Avm1SourceMethodBytecodeOrigin? Bytecode = null);

public abstract class Avm1SourceClassMember
{
    protected Avm1SourceClassMember(
        string name,
        Avm1SourceDeclarationModifiers modifiers,
        Avm1SourceMemberOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Modifiers = modifiers;
        Origin = origin;
    }

    public string Name { get; }

    public Avm1SourceDeclarationModifiers Modifiers { get; }

    public Avm1SourceMemberOrigin Origin { get; }

    public abstract bool IsComplete { get; }
}

public sealed class Avm1SourceField : Avm1SourceClassMember
{
    public Avm1SourceField(
        string name,
        Avm1SourceDeclarationModifiers modifiers = Avm1SourceDeclarationModifiers.None,
        Avm1SourceTypeReference? declaredType = null,
        Avm1SourceTypeReference? inferredType = null,
        Avm1SourceExpressionFragment? initializer = null,
        Avm1SourceMemberOrigin origin = default)
        : base(name, modifiers, origin)
    {
        DeclaredType = declaredType;
        InferredType = inferredType;
        Initializer = initializer;
    }

    public Avm1SourceTypeReference? DeclaredType { get; }

    public Avm1SourceTypeReference? InferredType { get; }

    public Avm1SourceExpressionFragment? Initializer { get; }

    public override bool IsComplete => Initializer?.IsComplete ?? true;
}

public sealed class Avm1SourceMethodDeclaration : Avm1SourceClassMember
{
    public Avm1SourceMethodDeclaration(
        string name,
        Avm1SourceMethodKind kind,
        Avm1SourceMethod body,
        Avm1SourceDeclarationModifiers modifiers = Avm1SourceDeclarationModifiers.None,
        Avm1SourceTypeReference? declaredReturnType = null,
        Avm1SourceTypeReference? inferredReturnType = null,
        Avm1SourceMemberOrigin origin = default,
        bool hasBody = true)
        : base(name, modifiers, origin)
    {
        ArgumentNullException.ThrowIfNull(body);
        Kind = kind;
        Body = body;
        DeclaredReturnType = declaredReturnType;
        InferredReturnType = inferredReturnType;
        HasBody = hasBody;
    }

    public Avm1SourceMethodKind Kind { get; }

    public Avm1SourceMethod Body { get; }

    public Avm1SourceTypeReference? DeclaredReturnType { get; }

    public Avm1SourceTypeReference? InferredReturnType { get; }

    public bool HasBody { get; }

    public override bool IsComplete => Body.IsComplete;
}

public enum Avm1SourceClassInitializationStepKind : byte
{
    PackageDefinition,
    ClassDefinition,
    Inheritance,
    Interfaces,
    MemberDefinition,
    AccessorDefinition,
    PropertyFlags,
    Linkage,
    ResidualStatement
}

public readonly record struct Avm1SourceClassInitializationStep(
    Avm1SourceClassInitializationStepKind Kind,
    Avm1SourceClassMember? Member,
    SourceStatementIndex Statement);

public sealed class Avm1SourceClassInitializer
{
    private readonly Avm1SourceClassInitializationStep[] _steps;
    private readonly SourceStatementIndex[] _residualStatements;

    public Avm1SourceClassInitializer(
        Avm1SourceMethod body,
        IEnumerable<Avm1SourceClassInitializationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(steps);

        Body = body;
        _steps = steps.ToArray();
        foreach (var step in _steps)
        {
            if (step.Kind is
                    Avm1SourceClassInitializationStepKind.MemberDefinition or
                    Avm1SourceClassInitializationStepKind.AccessorDefinition &&
                step.Member is null)
            {
                throw new ArgumentException(
                    "A member initializer step must reference its declaration.",
                    nameof(steps));
            }
            if (step.Statement.IsValid &&
                step.Statement.Value >= body.Arena.Statements.Count)
            {
                throw new ArgumentException(
                    "An initializer step references a statement outside its Source arena.",
                    nameof(steps));
            }
        }

        _residualStatements = _steps
            .Where(step =>
                step.Kind is Avm1SourceClassInitializationStepKind.ResidualStatement &&
                step.Statement.IsValid)
            .Select(step => step.Statement)
            .Distinct()
            .ToArray();
    }

    public Avm1SourceMethod Body { get; }

    public IReadOnlyList<Avm1SourceClassInitializationStep> Steps => _steps;

    public IReadOnlyList<SourceStatementIndex> ResidualStatements => _residualStatements;

    public bool HasResidualStatements => _residualStatements.Length > 0;

    public bool IsComplete => Body.IsComplete;
}

public sealed class Avm1SourceClass
{
    private readonly Avm1SourceQualifiedName[] _interfaces;
    private readonly Avm1SourceClassMember[] _members;
    private readonly Avm1SourceField[] _fields;
    private readonly Avm1SourceMethodDeclaration[] _methods;
    private readonly Avm1SourceMethodDeclaration? _constructor;

    public Avm1SourceClass(
        Avm1SourceQualifiedName name,
        Avm1SourceQualifiedName? baseType,
        IEnumerable<Avm1SourceQualifiedName>? interfaces,
        IEnumerable<Avm1SourceClassMember> members,
        Avm1SourceDeclarationModifiers modifiers = Avm1SourceDeclarationModifiers.None,
        Avm1SourceClassOrigin origin = default,
        Avm1SourceClassInitializer? initializer = null,
        Avm1SourceTypeDeclarationKind kind = Avm1SourceTypeDeclarationKind.Class)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(members);

        Name = name;
        Kind = kind;
        BaseType = baseType;
        Modifiers = modifiers;
        Origin = origin;
        Initializer = initializer;
        _interfaces = interfaces?.ToArray() ?? [];
        _members = members.ToArray();
        _fields = _members.OfType<Avm1SourceField>().ToArray();
        var methodDeclarations = _members.OfType<Avm1SourceMethodDeclaration>().ToArray();
        var constructors = methodDeclarations.Where(method =>
            method.Kind is Avm1SourceMethodKind.Constructor).ToArray();
        if (constructors.Length > 1)
            throw new ArgumentException("A Source HIR class cannot have multiple constructors.", nameof(members));
        _constructor = constructors.FirstOrDefault();
        _methods = methodDeclarations.Where(method =>
            method.Kind is not Avm1SourceMethodKind.Constructor).ToArray();
        if (kind is Avm1SourceTypeDeclarationKind.Interface &&
            (baseType is not null || _constructor is not null || initializer is not null))
        {
            throw new ArgumentException(
                "A Source HIR interface cannot have a base class, constructor, or initializer.",
                nameof(kind));
        }
    }

    public Avm1SourceQualifiedName Name { get; }

    public Avm1SourceTypeDeclarationKind Kind { get; }

    public Avm1SourceQualifiedName? BaseType { get; }

    public IReadOnlyList<Avm1SourceQualifiedName> Interfaces => _interfaces;

    public IReadOnlyList<Avm1SourceClassMember> Members => _members;

    public IReadOnlyList<Avm1SourceField> Fields => _fields;

    public Avm1SourceMethodDeclaration? Constructor => _constructor;

    public IReadOnlyList<Avm1SourceMethodDeclaration> Methods => _methods;

    public Avm1SourceDeclarationModifiers Modifiers { get; }

    public Avm1SourceClassOrigin Origin { get; }

    public Avm1SourceClassInitializer? Initializer { get; }

    public bool IsComplete =>
        _members.All(member => member.IsComplete) &&
        (Initializer?.IsComplete ?? true);
}

public sealed class Avm1SourceFile
{
    private readonly Avm1SourceQualifiedName[] _imports;
    private readonly Avm1SourceClass[] _classes;

    public Avm1SourceFile(
        IEnumerable<Avm1SourceClass> classes,
        IEnumerable<Avm1SourceQualifiedName>? imports = null)
    {
        ArgumentNullException.ThrowIfNull(classes);
        _classes = classes.ToArray();
        _imports = imports?.ToArray() ?? [];
    }

    public IReadOnlyList<Avm1SourceQualifiedName> Imports => _imports;

    public IReadOnlyList<Avm1SourceClass> Classes => _classes;

    public bool IsComplete => _classes.All(sourceClass => sourceClass.IsComplete);

    public void WriteAs2(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        new Emit.Avm1SourceFileAs2Emitter(writer).Write(this);
    }

    public string GetAs2Text()
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        WriteAs2(writer);
        return writer.ToString();
    }
}
