using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Text;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ValueOriginAnalysis
{
    private Avm1ValueOriginAnalysis(Avm1ValueOrigin[] origins)
    {
        Origins = origins;
    }

    public IReadOnlyList<Avm1ValueOrigin> Origins { get; }

    public Avm1ValueOrigin this[ValueIndex value] => value.IsValid && value.Value < Origins.Count
        ? Origins[value.Value]
        : Avm1ValueOrigin.Unknown;

    public static Avm1ValueOriginAnalysis Build(
        Avm1InstructionTable instructions,
        Avm1TacIr tac,
        Avm1RegisterSsa registerSsa,
        Avm1CatchPayloadAnalysis catchPayloads,
        Avm1ConstantPoolAnalysis constantPools,
        FunctionContext? context)
    {
        var origins = new Avm1ValueOrigin[GetValueCount(tac)];
        Array.Fill(origins, Avm1ValueOrigin.Unknown);

        var registerOrigins = BuildPreloadedRegisterOrigins(context);
        var accessesByInstruction = registerSsa.Accesses.ToDictionary(access => access.Instruction);
        var maxIterations = Math.Max(4, tac.Count + registerSsa.PhiNodes.Count + 2);

        var changed = true;
        for (var iteration = 0; changed && iteration < maxIterations; iteration++)
        {
            changed = false;
            var pushPositions = new Dictionary<ActionIndex, int>();

            foreach (var phi in registerSsa.PhiNodes)
            {
                var merged = MergeRegisterPhi(phi, registerOrigins);
                changed |= SetRegisterOrigin(registerOrigins, phi.Register, phi.Version, merged);
            }

            foreach (var instruction in tac.Instructions)
            {
                var origin = EvaluateInstruction(
                    instructions,
                    tac,
                    instruction,
                    origins,
                    registerOrigins,
                    accessesByInstruction,
                    pushPositions,
                    constantPools,
                    catchPayloads);

                if (DefinesValue(instruction.Op) && instruction.Result.IsValid)
                    changed |= SetValueOrigin(origins, instruction.Result, origin);

                if (instruction.Op is Avm1TacOp.StoreRegister or Avm1TacOp.CatchEnter &&
                    accessesByInstruction.TryGetValue(instruction.Index, out var access))
                {
                    var source = access.Source.IsValid
                        ? access.Source
                        : instruction.Operand0;
                    changed |= SetRegisterOrigin(
                        registerOrigins,
                        access.Register,
                        access.Version,
                        GetOrigin(origins, source));
                }
            }
        }

        return new Avm1ValueOriginAnalysis(origins);
    }

    private static Avm1ValueOrigin EvaluateInstruction(
        Avm1InstructionTable instructions,
        Avm1TacIr tac,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueOrigin> origins,
        Dictionary<(int Register, int Version), Avm1ValueOrigin> registerOrigins,
        Dictionary<IrIndex, Avm1RegisterAccess> accessesByInstruction,
        Dictionary<ActionIndex, int> pushPositions,
        Avm1ConstantPoolAnalysis constantPools,
        Avm1CatchPayloadAnalysis catchPayloads)
    {
        var pushPosition = -1;
        if (instruction.Op is Avm1TacOp.LoadConstant or Avm1TacOp.LoadRegister &&
            instruction.Action.IsValid &&
            instructions[instruction.Action].Action is ActionPush)
        {
            pushPosition = pushPositions.GetValueOrDefault(instruction.Action);
            pushPositions[instruction.Action] = pushPosition + 1;
        }

        return instruction.Op switch
        {
            Avm1TacOp.Phi => MergeValuePhi(tac, instruction, origins),
            Avm1TacOp.LoadConstant => EvaluateLoadConstant(
                instructions,
                instruction,
                pushPosition,
                constantPools.TryGetEntries(instruction.Action, out var pool)
                    ? pool
                    : []),
            Avm1TacOp.LoadRegister => EvaluateLoadRegister(instruction, registerOrigins, accessesByInstruction),
            Avm1TacOp.Copy or Avm1TacOp.Cast => GetOrigin(origins, instruction.Operand0),
            Avm1TacOp.GetVariable => EvaluateGetVariable(
                tac,
                origins,
                instruction,
                catchPayloads),
            Avm1TacOp.GetMember => EvaluateGetMember(origins, instruction),
            Avm1TacOp.NewObject => EvaluateNewObject(origins, instruction),
            Avm1TacOp.FunctionLiteral => Avm1ValueOrigin.FunctionLiteral(instruction.Result),
            _ => Avm1ValueOrigin.Unknown
        };
    }

    private static Avm1ValueOrigin EvaluateLoadConstant(
        Avm1InstructionTable instructions,
        Avm1TacInstruction instruction,
        int position,
        IReadOnlyList<string> pool)
    {
        if (!instruction.Action.IsValid ||
            instructions[instruction.Action].Action is not ActionPush push ||
            position < 0 ||
            position >= push.PushValues.Count)
        {
            return Avm1ValueOrigin.Unknown;
        }

        var text = push.PushValues[position] switch
        {
            PushValue.PushValueString value => value.Value,
            PushValue.PushValueConstant8 value when value.ConstantIndex < pool.Count => pool[value.ConstantIndex],
            PushValue.PushValueConstant16 value when value.ConstantIndex < pool.Count => pool[value.ConstantIndex],
            _ => null
        };
        return text is null ? Avm1ValueOrigin.Unknown : Avm1ValueOrigin.StringLiteral(text);
    }

    private static Avm1ValueOrigin EvaluateLoadRegister(
        Avm1TacInstruction instruction,
        Dictionary<(int Register, int Version), Avm1ValueOrigin> registerOrigins,
        Dictionary<IrIndex, Avm1RegisterAccess> accessesByInstruction)
    {
        var register = instruction.IntOperand;
        var version = 0;
        if (accessesByInstruction.TryGetValue(instruction.Index, out var access))
        {
            register = access.Register;
            version = access.Version;
        }

        return registerOrigins.GetValueOrDefault((register, version), Avm1ValueOrigin.Unknown);
    }

    private static Avm1ValueOrigin EvaluateGetVariable(
        Avm1TacIr tac,
        IReadOnlyList<Avm1ValueOrigin> origins,
        Avm1TacInstruction instruction,
        Avm1CatchPayloadAnalysis catchPayloads)
    {
        var name = GetOrigin(origins, instruction.Operand0);
        if (name.Kind is not Avm1ValueOriginKind.StringLiteral || name.Name is null)
            return Avm1ValueOrigin.Unknown;

        if (catchPayloads.TryGetExactVariableSource(
            instruction.Action,
            name.Name,
            out var binding,
            out var catchSource))
        {
            if (MayWriteCatchVariable(tac, origins, binding, name.Name))
                return Avm1ValueOrigin.Variable(name.Name);

            return GetOrigin(origins, catchSource);
        }

        return name.Name == "this"
            ? Avm1ValueOrigin.This
            : Avm1ValueOrigin.Variable(name.Name);
    }

    private static bool MayWriteCatchVariable(
        Avm1TacIr tac,
        IReadOnlyList<Avm1ValueOrigin> origins,
        Avm1CatchPayloadBinding binding,
        string name)
    {
        foreach (var candidate in tac.Instructions)
        {
            if (candidate.Op is not Avm1TacOp.SetVariable ||
                candidate.Action.Value < binding.BodyStartAction.Value ||
                candidate.Action.Value >= binding.BodyEndAction.Value)
            {
                continue;
            }

            var writtenName = GetOrigin(origins, candidate.Operand0);
            if (writtenName.Kind is Avm1ValueOriginKind.StringLiteral &&
                !string.Equals(writtenName.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static Avm1ValueOrigin EvaluateGetMember(
        IReadOnlyList<Avm1ValueOrigin> origins,
        Avm1TacInstruction instruction)
    {
        var target = GetOrigin(origins, instruction.Operand0);
        var member = GetOrigin(origins, instruction.Operand1);
        if (member.Kind is not Avm1ValueOriginKind.StringLiteral || member.Name is null)
            return Avm1ValueOrigin.Unknown;

        return target.Kind switch
        {
            Avm1ValueOriginKind.This => Avm1ValueOrigin.InstanceMember(member.Name),
            Avm1ValueOriginKind.Variable when target.Name is not null =>
                Avm1ValueOrigin.StaticMember(target.Name, member.Name),
            Avm1ValueOriginKind.StaticMember when
                target.Name is not null && target.Member is not null =>
                Avm1ValueOrigin.StaticMember(
                    target.Name + "." + target.Member,
                    member.Name),
            _ => Avm1ValueOrigin.Unknown
        };
    }

    private static Avm1ValueOrigin EvaluateNewObject(
        IReadOnlyList<Avm1ValueOrigin> origins,
        Avm1TacInstruction instruction)
    {
        var constructor = GetOrigin(origins, instruction.Operand0);
        return constructor.Kind is Avm1ValueOriginKind.StringLiteral &&
            constructor.Name is not null
                ? Avm1ValueOrigin.ConstructedObject(constructor.Name)
                : Avm1ValueOrigin.Unknown;
    }

    private static Avm1ValueOrigin MergeValuePhi(
        Avm1TacIr tac,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueOrigin> origins)
    {
        if (!instruction.Operand2.IsValid || instruction.OperandCount <= 0)
            return Avm1ValueOrigin.Unknown;

        Avm1ValueOrigin? merged = null;
        var end = Math.Min(instruction.Operand2.Value + instruction.OperandCount, tac.ValueOperands.Count);
        for (var i = instruction.Operand2.Value; i < end; i++)
        {
            var incoming = GetOrigin(origins, tac.ValueOperands[i]);
            if (incoming.Kind is Avm1ValueOriginKind.Unknown)
                return Avm1ValueOrigin.Unknown;

            if (merged is not null && merged.Value != incoming)
                return Avm1ValueOrigin.Unknown;
            merged = incoming;
        }

        return merged ?? Avm1ValueOrigin.Unknown;
    }

    private static Avm1ValueOrigin MergeRegisterPhi(
        Avm1RegisterPhiNode phi,
        Dictionary<(int Register, int Version), Avm1ValueOrigin> registerOrigins)
    {
        Avm1ValueOrigin? merged = null;
        foreach (var version in phi.IncomingVersions)
        {
            if (!registerOrigins.TryGetValue((phi.Register, version), out var incoming) ||
                incoming.Kind is Avm1ValueOriginKind.Unknown)
            {
                return Avm1ValueOrigin.Unknown;
            }

            if (merged is not null && merged.Value != incoming)
                return Avm1ValueOrigin.Unknown;
            merged = incoming;
        }

        return merged ?? Avm1ValueOrigin.Unknown;
    }

    private static Dictionary<(int Register, int Version), Avm1ValueOrigin> BuildPreloadedRegisterOrigins(
        FunctionContext? context)
    {
        var result = new Dictionary<(int Register, int Version), Avm1ValueOrigin>();
        if (context is null)
            return result;

        var register = 1;
        if (context.PreloadThis)
            result[(register++, 0)] = Avm1ValueOrigin.This;
        if (context.PreloadArguments)
            result[(register++, 0)] = Avm1ValueOrigin.Variable("arguments");
        if (context.PreloadSuper)
            result[(register++, 0)] = Avm1ValueOrigin.Variable("super");
        if (context.PreloadRoot)
            result[(register++, 0)] = Avm1ValueOrigin.Variable("_root");
        if (context.PreloadParent)
            result[(register++, 0)] = Avm1ValueOrigin.Variable("_parent");
        if (context.PreloadGlobal)
            result[(register++, 0)] = Avm1ValueOrigin.Variable("_global");

        foreach (var parameter in context.Parameters)
        {
            if (parameter.Register > 0 && parameter.Name.Length > 0)
                result[(parameter.Register, 0)] = Avm1ValueOrigin.Variable(parameter.Name);
        }

        return result;
    }

    private static int GetValueCount(Avm1TacIr tac)
    {
        var count = 0;
        foreach (var instruction in tac.Instructions)
        {
            count = Math.Max(count, instruction.Result.Value + 1);
            count = Math.Max(count, instruction.Operand0.Value + 1);
            count = Math.Max(count, instruction.Operand1.Value + 1);
            count = Math.Max(count, instruction.Operand2.Value + 1);
        }

        foreach (var operand in tac.ValueOperands)
            count = Math.Max(count, operand.Value + 1);
        return count;
    }

    private static bool DefinesValue(Avm1TacOp op) => op is
        Avm1TacOp.Phi or
        Avm1TacOp.LoadConstant or
        Avm1TacOp.LoadRegister or
        Avm1TacOp.Copy or
        Avm1TacOp.Unary or
        Avm1TacOp.Intrinsic or
        Avm1TacOp.HostIntrinsic or
        Avm1TacOp.Binary or
        Avm1TacOp.GetVariable or
        Avm1TacOp.GetMember or
        Avm1TacOp.Delete or
        Avm1TacOp.GetTime or
        Avm1TacOp.InitArray or
        Avm1TacOp.InitObject or
        Avm1TacOp.NewObject or
        Avm1TacOp.NewMethod or
        Avm1TacOp.CallFunction or
        Avm1TacOp.CallMethod or
        Avm1TacOp.FunctionLiteral or
        Avm1TacOp.Cast or
        Avm1TacOp.Enumerate;

    private static Avm1ValueOrigin GetOrigin(IReadOnlyList<Avm1ValueOrigin> origins, ValueIndex value) =>
        value.IsValid && value.Value < origins.Count ? origins[value.Value] : Avm1ValueOrigin.Unknown;

    private static bool SetValueOrigin(Avm1ValueOrigin[] origins, ValueIndex value, Avm1ValueOrigin origin)
    {
        if (!value.IsValid || value.Value >= origins.Length || origins[value.Value] == origin)
            return false;
        origins[value.Value] = origin;
        return true;
    }

    private static bool SetRegisterOrigin(
        Dictionary<(int Register, int Version), Avm1ValueOrigin> origins,
        int register,
        int version,
        Avm1ValueOrigin origin)
    {
        var key = (register, version);
        if (origins.TryGetValue(key, out var current) && current == origin)
            return false;
        origins[key] = origin;
        return true;
    }
}

public enum Avm1ValueOriginKind : byte
{
    Unknown,
    StringLiteral,
    This,
    Variable,
    InstanceMember,
    StaticMember,
    FunctionLiteral,
    ConstructedObject
}

public readonly record struct Avm1ValueOrigin(
    Avm1ValueOriginKind Kind,
    string? Name,
    string? Member,
    int Source)
{
    public static Avm1ValueOrigin Unknown => default;
    public static Avm1ValueOrigin This => new(Avm1ValueOriginKind.This, null, null, -1);
    public static Avm1ValueOrigin FunctionLiteral(ValueIndex source) =>
        new(Avm1ValueOriginKind.FunctionLiteral, null, null, source.Value);
    public static Avm1ValueOrigin StringLiteral(string value) =>
        new(Avm1ValueOriginKind.StringLiteral, value, null, -1);
    public static Avm1ValueOrigin Variable(string name) =>
        new(Avm1ValueOriginKind.Variable, name, null, -1);
    public static Avm1ValueOrigin InstanceMember(string member) =>
        new(Avm1ValueOriginKind.InstanceMember, null, member, -1);
    public static Avm1ValueOrigin StaticMember(string owner, string member) =>
        new(Avm1ValueOriginKind.StaticMember, owner, member, -1);
    public static Avm1ValueOrigin ConstructedObject(string typeName) =>
        new(Avm1ValueOriginKind.ConstructedObject, typeName, null, -1);
}
