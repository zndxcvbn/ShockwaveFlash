using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1StaticFieldAnalysis
{
    private readonly Avm1StaticFieldBinding[] _bindings;
    private readonly Dictionary<(int Register, int Version), string> _registerAliases;

    private Avm1StaticFieldAnalysis(
        Avm1StaticFieldBinding[] bindings,
        Dictionary<(int Register, int Version), string> registerAliases)
    {
        _bindings = bindings;
        _registerAliases = registerAliases;
    }

    public IReadOnlyList<Avm1StaticFieldBinding> Bindings => _bindings;

    internal IReadOnlyDictionary<(int Register, int Version), string> RegisterAliases =>
        _registerAliases;

    public static Avm1StaticFieldAnalysis Build(
        Avm1MethodDecompilation initializer,
        string className,
        IReadOnlySet<string> methodNames)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(methodNames);

        var resolver = new Avm1ClassMetadataAnalysis.QualifiedNameResolver(initializer.Core);
        var classFunction = FindClassFunction(initializer, className, resolver);
        if (!classFunction.IsValid)
            return new Avm1StaticFieldAnalysis([], []);

        var registerAliases = new Dictionary<(int Register, int Version), string>();
        foreach (var access in initializer.RegisterSsa.Accesses)
        {
            if (access.Kind is not Avm1RegisterAccessKind.Write ||
                initializer.ValueOrigins[access.Source] is not
                {
                    Kind: Avm1ValueOriginKind.FunctionLiteral,
                    Source: var source
                } ||
                source != classFunction.Value)
            {
                continue;
            }

            registerAliases[(access.Register, access.Version)] = className;
        }

        var assignmentByAction = new Dictionary<ActionIndex, Avm1AstNode>();
        foreach (var node in initializer.StructuredAst.Nodes)
        {
            if (node.Kind is Avm1AstNodeKind.AssignMember && node.EndAction.IsValid)
                assignmentByAction.TryAdd(node.EndAction, node);
        }

        var bindings = new List<Avm1StaticFieldBinding>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var instruction in initializer.TacIr.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.SetMember ||
                !IsCurrentClassOwner(
                    initializer,
                    instruction.Operand0,
                    classFunction,
                    className,
                    resolver) ||
                !TryGetString(initializer, instruction.Operand1, out var name) ||
                !IsFieldName(name) ||
                name is "prototype" or "__constructor__")
            {
                continue;
            }

            if (methodNames.Contains(name) &&
                initializer.ValueOrigins[instruction.Operand2].Kind is
                    Avm1ValueOriginKind.FunctionLiteral)
            {
                continue;
            }

            var expression = AstIndex.Invalid;
            if (assignmentByAction.TryGetValue(instruction.Action, out var assignment) &&
                Avm1AstArena.GetChildCount(assignment) == 3)
            {
                expression = initializer.StructuredAst.GetChild(assignment, 2);
            }

            var binding = new Avm1StaticFieldBinding(
                name,
                expression,
                instruction.Action);
            if (indexByName.TryGetValue(name, out var existingIndex))
                bindings[existingIndex] = binding;
            else
            {
                indexByName.Add(name, bindings.Count);
                bindings.Add(binding);
            }
        }

        return new Avm1StaticFieldAnalysis(bindings.ToArray(), registerAliases);
    }

    private static bool IsCurrentClassOwner(
        Avm1MethodDecompilation initializer,
        ValueIndex owner,
        ValueIndex classFunction,
        string className,
        Avm1ClassMetadataAnalysis.QualifiedNameResolver resolver)
    {
        if (initializer.ValueOrigins[owner] is
            {
                Kind: Avm1ValueOriginKind.FunctionLiteral,
                Source: var source
            } &&
            source == classFunction.Value)
        {
            return true;
        }

        var resolved = Avm1ClassMetadataAnalysis.NormalizeQualifiedName(
            resolver.Resolve(owner));
        return Avm1ClassMetadataAnalysis.NamesIdentifySameClass(
            resolved,
            className);
    }

    public static void CollectReferencedFields(
        Avm1MethodCore method,
        string className,
        ISet<string> excludedNames,
        ISet<string> fields)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(excludedNames);
        ArgumentNullException.ThrowIfNull(fields);

        foreach (var instruction in method.TacIr.Instructions)
        {
            if (instruction.Op is not (Avm1TacOp.GetMember or Avm1TacOp.SetMember))
                continue;

            var owner = Avm1ClassMetadataAnalysis.NormalizeQualifiedName(
                ResolveQualifiedOrigin(method.ValueOrigins[instruction.Operand0]));
            if (!Avm1ClassMetadataAnalysis.NamesIdentifySameClass(owner, className))
                continue;

            var memberOrigin = method.ValueOrigins[instruction.Operand1];
            var member = memberOrigin.Kind is Avm1ValueOriginKind.StringLiteral
                ? memberOrigin.Name
                : null;
            if (member is null ||
                !IsFieldName(member) ||
                excludedNames.Contains(member))
            {
                continue;
            }

            fields.Add(member);
        }
    }

    private static string? ResolveQualifiedOrigin(Avm1ValueOrigin origin) =>
        origin.Kind switch
        {
            Avm1ValueOriginKind.Variable => origin.Name,
            Avm1ValueOriginKind.StaticMember when
                origin.Name is not null && origin.Member is not null =>
                origin.Name + "." + origin.Member,
            _ => null
        };

    private static ValueIndex FindClassFunction(
        Avm1MethodDecompilation initializer,
        string className,
        Avm1ClassMetadataAnalysis.QualifiedNameResolver resolver)
    {
        foreach (var instruction in initializer.TacIr.Instructions)
        {
            ValueIndex name;
            ValueIndex value;
            switch (instruction.Op)
            {
                case Avm1TacOp.SetVariable:
                case Avm1TacOp.DefineLocal:
                    name = instruction.Operand0;
                    value = instruction.Operand1;
                    break;
                case Avm1TacOp.SetMember:
                    var owner = resolver.Resolve(instruction.Operand0);
                    var member = resolver.Resolve(instruction.Operand1);
                    var qualifiedName = string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(member)
                        ? null
                        : owner + "." + member;
                    if (!Avm1ClassMetadataAnalysis.NamesIdentifySameClass(
                        Avm1ClassMetadataAnalysis.NormalizeQualifiedName(qualifiedName),
                        className))
                    {
                        continue;
                    }

                    value = instruction.Operand2;
                    if (initializer.ValueOrigins[value] is
                        {
                            Kind: Avm1ValueOriginKind.FunctionLiteral,
                            Source: var memberSource
                        })
                    {
                        return new ValueIndex(memberSource);
                    }
                    continue;
                default:
                    continue;
            }

            var resolvedName = Avm1ClassMetadataAnalysis.NormalizeQualifiedName(
                resolver.Resolve(name));
            if (!Avm1ClassMetadataAnalysis.NamesIdentifySameClass(resolvedName, className))
                continue;

            if (initializer.ValueOrigins[value] is
                {
                    Kind: Avm1ValueOriginKind.FunctionLiteral,
                    Source: var source
                })
            {
                return new ValueIndex(source);
            }
        }

        return ValueIndex.Invalid;
    }

    private static bool TryGetString(
        Avm1MethodDecompilation method,
        ValueIndex value,
        out string text)
    {
        var fact = method.ValueAnalysis[value];
        text = fact.StringValue ?? string.Empty;
        return fact.ConstantKind is Avm1ConstantKind.String && fact.StringValue is not null;
    }

    private static bool IsFieldName(string value)
    {
        if (value.Length == 0 ||
            (!char.IsLetter(value[0]) && value[0] is not ('_' or '$')))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsLetterOrDigit(value[i]) && value[i] is not ('_' or '$'))
                return false;
        }

        return true;
    }
}

public readonly record struct Avm1StaticFieldBinding(
    string Name,
    AstIndex Initializer,
    ActionIndex Action);
