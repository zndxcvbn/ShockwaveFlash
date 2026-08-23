using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Text;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ClassTypeEnvironment
{
    private readonly HashSet<string> _classNames;

    internal Avm1ClassTypeEnvironment(
        string className,
        Dictionary<string, Avm1ClassMemberType> instanceMembers,
        Dictionary<string, Avm1ClassMemberType> staticMembers)
    {
        ClassName = NormalizeClassName(className);
        InstanceMembers = instanceMembers;
        StaticMembers = staticMembers;
        _classNames = BuildClassNames(className);
    }

    public string ClassName { get; }

    public IReadOnlyDictionary<string, Avm1ClassMemberType> InstanceMembers { get; }

    public IReadOnlyDictionary<string, Avm1ClassMemberType> StaticMembers { get; }

    public static Avm1ClassTypeEnvironment Empty(string className) =>
        new(className, new Dictionary<string, Avm1ClassMemberType>(StringComparer.Ordinal),
            new Dictionary<string, Avm1ClassMemberType>(StringComparer.Ordinal));

    public static Avm1ClassTypeEnvironment Create(
        string className,
        IReadOnlyDictionary<string, Avm1ClassMemberType>? instanceMembers = null,
        IReadOnlyDictionary<string, Avm1ClassMemberType>? staticMembers = null) =>
        new(
            className,
            instanceMembers is null
                ? new Dictionary<string, Avm1ClassMemberType>(StringComparer.Ordinal)
                : new Dictionary<string, Avm1ClassMemberType>(instanceMembers, StringComparer.Ordinal),
            staticMembers is null
                ? new Dictionary<string, Avm1ClassMemberType>(StringComparer.Ordinal)
                : new Dictionary<string, Avm1ClassMemberType>(staticMembers, StringComparer.Ordinal));

    public bool IsCurrentClass(string name) => _classNames.Contains(name);

    public bool TryGetInstanceMember(string name, out Avm1ClassMemberType type) =>
        InstanceMembers.TryGetValue(name, out type);

    public bool TryGetStaticMember(string owner, string name, out Avm1ClassMemberType type)
    {
        if (!IsCurrentClass(owner))
        {
            type = default;
            return false;
        }

        return StaticMembers.TryGetValue(name, out type);
    }

    internal bool EquivalentTo(Avm1ClassTypeEnvironment other) =>
        DictionaryEquals(InstanceMembers, other.InstanceMembers) &&
        DictionaryEquals(StaticMembers, other.StaticMembers);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, Avm1ClassMemberType> left,
        IReadOnlyDictionary<string, Avm1ClassMemberType> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (name, type) in left)
            if (!right.TryGetValue(name, out var otherType) || otherType != type)
                return false;
        return true;
    }

    private static HashSet<string> BuildClassNames(string className)
    {
        var normalized = NormalizeClassName(className);
        var separator = normalized.LastIndexOf('.');
        var shortName = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return new HashSet<string>(StringComparer.Ordinal)
        {
            className,
            normalized,
            shortName,
            "__Packages." + normalized
        };
    }

    private static string NormalizeClassName(string name) =>
        name.StartsWith("__Packages.", StringComparison.Ordinal) ? name["__Packages.".Length..] : name;
}

public readonly record struct Avm1ClassMemberType(
    Avm1InferredType Type,
    Avm1InferredType ElementType = Avm1InferredType.Unknown)
{
    public bool IsKnown => Type is not Avm1InferredType.Unknown;

    internal static Avm1ClassMemberType FromValue(Avm1ValueFact value) =>
        new(value.Type, value.ElementType);
}

public enum Avm1ClassBodyKind : byte
{
    Initializer,
    Constructor,
    Method
}

public readonly record struct Avm1ClassMethodInput(
    IReadOnlyList<Action> Actions,
    FunctionContext? Context,
    Avm1ClassBodyKind Kind);

public readonly record struct Avm1ClassMethodCoreInput(
    Avm1MethodCore Core,
    Avm1ClassBodyKind Kind);

public static class Avm1ClassTypeAnalysis
{
    public static Avm1ClassTypeEnvironment Build(
        string className,
        IReadOnlyList<Avm1ClassMethodInput> methods,
        byte swfVersion)
    {
        var cores = new List<Avm1ClassMethodCoreInput>(methods.Count);
        foreach (var method in methods)
        {
            if (method.Actions.Count == 0)
                continue;

            cores.Add(new Avm1ClassMethodCoreInput(
                Avm1Decompiler.BuildMethodCore(method.Actions, swfVersion, method.Context),
                method.Kind));
        }

        return Build(className, cores);
    }

    public static Avm1ClassTypeEnvironment Build(
        string className,
        IReadOnlyList<Avm1ClassMethodCoreInput> methods)
    {
        var environment = Avm1ClassTypeEnvironment.Empty(className);
        var maxIterations = Math.Max(3, Math.Min(16, methods.Count + 2));

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var builder = new EnvironmentBuilder(className);
            foreach (var method in methods)
            {
                var values = Avm1ValueAnalysis.Build(
                    method.Core.Instructions,
                    method.Core.TacIr,
                    method.Core.RegisterSsa,
                    method.Core.ValueOrigins,
                    method.Core.CatchPayloads,
                    method.Core.ConstantPools,
                    environment);
                CollectAssignments(method.Core, values, method.Kind, environment, builder);
            }

            var next = builder.Build();
            if (environment.EquivalentTo(next))
                return next;
            environment = next;
        }

        return environment;
    }

    private static void CollectAssignments(
        Avm1MethodCore core,
        Avm1ValueAnalysis values,
        Avm1ClassBodyKind bodyKind,
        Avm1ClassTypeEnvironment environment,
        EnvironmentBuilder builder)
    {
        var classFunctions = bodyKind is Avm1ClassBodyKind.Initializer
            ? FindClassFunctions(core, values, environment)
            : null;

        foreach (var instruction in core.TacIr.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.SetMember)
                continue;

            var member = values[instruction.Operand1];
            if (member.ConstantKind is not Avm1ConstantKind.String || member.StringValue is null)
                continue;

            var value = values[instruction.Operand2];
            var target = core.ValueOrigins[instruction.Operand0];
            switch (target.Kind)
            {
                case Avm1ValueOriginKind.This:
                    builder.AddInstance(member.StringValue, value);
                    break;

                case Avm1ValueOriginKind.Variable when target.Name is not null && environment.IsCurrentClass(target.Name):
                    builder.AddStatic(member.StringValue, value);
                    break;

                case Avm1ValueOriginKind.FunctionLiteral when classFunctions?.Contains(target.Source) is true:
                    builder.AddStatic(member.StringValue, value);
                    break;
            }
        }
    }

    private static HashSet<int> FindClassFunctions(
        Avm1MethodCore core,
        Avm1ValueAnalysis values,
        Avm1ClassTypeEnvironment environment)
    {
        var result = new HashSet<int>();
        foreach (var instruction in core.TacIr.Instructions)
        {
            if (instruction.Op is Avm1TacOp.SetVariable)
            {
                AddClassFunction(
                    values[instruction.Operand0],
                    core.ValueOrigins[instruction.Operand1]);
                continue;
            }

            if (instruction.Op is Avm1TacOp.SetMember &&
                core.ValueOrigins[instruction.Operand0] is
                { Kind: Avm1ValueOriginKind.Variable, Name: "_global" })
            {
                AddClassFunction(
                    values[instruction.Operand1],
                    core.ValueOrigins[instruction.Operand2]);
            }
        }

        return result;

        void AddClassFunction(Avm1ValueFact name, Avm1ValueOrigin value)
        {
            if (name.ConstantKind is Avm1ConstantKind.String &&
                name.StringValue is not null &&
                environment.IsCurrentClass(name.StringValue) &&
                value.Kind is Avm1ValueOriginKind.FunctionLiteral)
            {
                result.Add(value.Source);
            }
        }
    }

    private sealed class EnvironmentBuilder
    {
        private readonly string _className;
        private readonly Dictionary<string, TypeAccumulator> _instanceMembers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypeAccumulator> _staticMembers = new(StringComparer.Ordinal);

        public EnvironmentBuilder(string className)
        {
            _className = className;
        }

        public void AddInstance(string name, Avm1ValueFact value) => Add(_instanceMembers, name, value);

        public void AddStatic(string name, Avm1ValueFact value) => Add(_staticMembers, name, value);

        public Avm1ClassTypeEnvironment Build() =>
            new(_className, BuildKnown(_instanceMembers), BuildKnown(_staticMembers));

        private static void Add(
            Dictionary<string, TypeAccumulator> members,
            string name,
            Avm1ValueFact value)
        {
            members.TryGetValue(name, out var accumulator);
            accumulator.Add(Avm1ClassMemberType.FromValue(value));
            members[name] = accumulator;
        }

        private static Dictionary<string, Avm1ClassMemberType> BuildKnown(
            Dictionary<string, TypeAccumulator> members)
        {
            var result = new Dictionary<string, Avm1ClassMemberType>(StringComparer.Ordinal);
            foreach (var (name, accumulator) in members)
                if (accumulator.TryGetType(out var type))
                    result[name] = type;
            return result;
        }
    }

    private struct TypeAccumulator
    {
        private Avm1ClassMemberType _type;
        private bool _hasType;
        private bool _hasUnknownAssignment;
        private bool _hasConflict;

        public void Add(Avm1ClassMemberType type)
        {
            if (!type.IsKnown)
            {
                _hasUnknownAssignment = true;
                return;
            }

            if (!_hasType)
            {
                _type = type;
                _hasType = true;
                return;
            }

            if (_type.Type != type.Type)
            {
                _hasConflict = true;
                return;
            }

            if (_type.Type is Avm1InferredType.Array && _type.ElementType != type.ElementType)
                _type = _type with { ElementType = Avm1InferredType.Unknown };
        }

        public bool TryGetType(out Avm1ClassMemberType type)
        {
            type = _type;
            return _hasType && !_hasUnknownAssignment && !_hasConflict;
        }
    }
}
