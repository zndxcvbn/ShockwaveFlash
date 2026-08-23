using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ValueAnalysis
{
    private readonly record struct RegisterBoundary(
        ActionIndex Start,
        ActionIndex End,
        ActionIndex Continuation);

    private Avm1ValueAnalysis(Avm1ValueFact[] facts)
    {
        Facts = facts;
    }

    public IReadOnlyList<Avm1ValueFact> Facts { get; }

    public Avm1ValueFact this[ValueIndex value] => value.IsValid && value.Value < Facts.Count
        ? Facts[value.Value]
        : Avm1ValueFact.Unknown(value);

    public static Avm1ValueAnalysis Build(
        Avm1InstructionTable instructions,
        Avm1TacIr tac,
        Avm1RegisterSsa registerSsa,
        Avm1ValueOriginAnalysis valueOrigins,
        Avm1CatchPayloadAnalysis catchPayloads,
        Avm1ConstantPoolAnalysis constantPools,
        Avm1ClassTypeEnvironment? typeEnvironment = null)
    {
        var valueCount = GetValueCount(tac);
        var facts = new Avm1ValueFact[valueCount];
        for (var i = 0; i < facts.Length; i++)
            facts[i] = Avm1ValueFact.Unknown(new ValueIndex(i));

        var registerFacts = new Dictionary<(int Register, int Version), Avm1ValueFact>();
        var accessesByInstruction = registerSsa.Accesses.ToDictionary(a => a.Instruction);
        var boundaryRegisterReads = FindBoundaryRegisterReads(instructions, tac, registerSsa);

        var maxIterations = Math.Max(4, tac.Count + registerSsa.PhiNodes.Count + 2);
        var changed = true;
        for (var iteration = 0; changed && iteration < maxIterations; iteration++)
        {
            changed = false;
            var pushPositions = new Dictionary<ActionIndex, int>();

            foreach (var phi in registerSsa.PhiNodes)
                changed |= SetRegisterFact(registerFacts, phi.Register, phi.Version, EvaluatePhi(phi, registerFacts));

            foreach (var instruction in tac.Instructions)
            {
                if (instruction.Op is Avm1TacOp.TargetControl &&
                    instruction.Operand0.IsValid &&
                    instructions[instruction.Action].Action is
                        ActionSetTarget setTarget)
                {
                    changed |= SetValueFact(
                        facts,
                        instruction.Operand0,
                        Avm1ValueFact.String(
                            instruction.Operand0,
                            setTarget.TargetName));
                }

                var fact = EvaluateInstruction(
                    instructions,
                    tac,
                    instruction,
                    facts,
                    registerFacts,
                    accessesByInstruction,
                    boundaryRegisterReads,
                    pushPositions,
                    constantPools,
                    valueOrigins,
                    catchPayloads,
                    typeEnvironment);

                if (DefinesValue(instruction.Op) && instruction.Result.IsValid)
                    changed |= SetValueFact(facts, instruction.Result, fact);

                if (instruction.Op is Avm1TacOp.StoreRegister or Avm1TacOp.CatchEnter &&
                    accessesByInstruction.TryGetValue(instruction.Index, out var access))
                {
                    var source = access.Source.IsValid
                        ? access.Source
                        : instruction.Operand0;
                    changed |= SetRegisterFact(
                        registerFacts,
                        access.Register,
                        access.Version,
                        thisFact(facts, source));
                }
            }
        }

        return new Avm1ValueAnalysis(facts);

        static Avm1ValueFact thisFact(IReadOnlyList<Avm1ValueFact> facts, ValueIndex value)
        {
            return value.IsValid && value.Value < facts.Count
                ? facts[value.Value]
                : Avm1ValueFact.Unknown(value);
        }
    }

    private static HashSet<IrIndex> FindBoundaryRegisterReads(
        Avm1InstructionTable instructions,
        Avm1TacIr tac,
        Avm1RegisterSsa registerSsa)
    {
        var boundaries = new List<RegisterBoundary>(
            instructions.WithRegions.Count + instructions.TryRegions.Count * 3);
        foreach (var region in instructions.WithRegions)
        {
            boundaries.Add(new RegisterBoundary(
                region.BodyStartAction,
                region.ExitAction,
                new ActionIndex(region.ExitAction.Value + 1)));
        }
        foreach (var region in instructions.TryRegions)
        {
            AddBoundary(
                region.TryBodyStartAction,
                region.TryExitAction,
                region.ContinuationAction);
            AddBoundary(
                region.CatchBodyStartAction,
                region.CatchExitAction,
                region.ContinuationAction);
            AddBoundary(
                region.FinallyBodyStartAction,
                region.FinallyExitAction,
                region.ContinuationAction);
        }
        if (boundaries.Count == 0)
            return [];

        var completionDispatchValues = FindCompletionDispatchValues(instructions, tac);
        if (completionDispatchValues.Count == 0)
            return [];

        var writes = registerSsa.Accesses
            .Where(access => access.Kind is Avm1RegisterAccessKind.Write)
            .ToDictionary(
                access => (access.Register, access.Version),
                access => access);
        var phis = registerSsa.PhiNodes.ToDictionary(
            phi => (phi.Register, phi.Version));
        var result = new HashSet<IrIndex>();
        foreach (var read in registerSsa.Accesses)
        {
            if (read.Kind is not Avm1RegisterAccessKind.Read ||
                !read.Action.IsValid ||
                !completionDispatchValues.Contains(tac[read.Instruction].Result))
            {
                continue;
            }

            foreach (var boundary in boundaries)
            {
                if (!boundary.Continuation.IsValid ||
                    read.Action.Value < boundary.Continuation.Value)
                {
                    continue;
                }

                var visited = new HashSet<(int Register, int Version)>();
                if (DependsOnBoundaryWrite(
                    read.Register,
                    read.Version,
                    boundary,
                    visited))
                {
                    result.Add(read.Instruction);
                    break;
                }
            }
        }
        return result;

        void AddBoundary(
            ActionIndex start,
            ActionIndex end,
            ActionIndex continuation)
        {
            if (start.IsValid && end.IsValid && start.Value < end.Value)
                boundaries.Add(new RegisterBoundary(start, end, continuation));
        }

        bool DependsOnBoundaryWrite(
            int register,
            int version,
            RegisterBoundary boundary,
            HashSet<(int Register, int Version)> visited)
        {
            var key = (register, version);
            if (!visited.Add(key))
                return false;
            if (writes.TryGetValue(key, out var write) &&
                write.Action.IsValid &&
                write.Action.Value >= boundary.Start.Value &&
                write.Action.Value < boundary.End.Value)
            {
                return true;
            }
            if (!phis.TryGetValue(key, out var phi))
                return false;

            return phi.IncomingVersions.Any(incoming =>
                DependsOnBoundaryWrite(
                    register,
                    incoming,
                    boundary,
                    visited));
        }
    }

    private static HashSet<ValueIndex> FindCompletionDispatchValues(
        Avm1InstructionTable instructions,
        Avm1TacIr tac)
    {
        var integerConstants = new Dictionary<ValueIndex, int>();
        var pushPositions = new Dictionary<ActionIndex, int>();
        foreach (var instruction in tac.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.LoadConstant ||
                !instruction.Action.IsValid ||
                instructions[instruction.Action].Action is not ActionPush push)
            {
                continue;
            }

            var position = pushPositions.GetValueOrDefault(instruction.Action);
            pushPositions[instruction.Action] = position + 1;
            if (position < push.PushValues.Count &&
                push.PushValues[position] is PushValue.PushValueInteger integer &&
                integer.Value > 0)
            {
                integerConstants[instruction.Result] = integer.Value;
            }
        }

        var result = new HashSet<ValueIndex>();
        foreach (var instruction in tac.Instructions)
        {
            if (instruction.Op is not Avm1TacOp.Binary ||
                instruction.Opcode is not ActionOpcode.StrictEquals)
            {
                continue;
            }

            if (integerConstants.ContainsKey(instruction.Operand0))
                result.Add(instruction.Operand1);
            if (integerConstants.ContainsKey(instruction.Operand1))
                result.Add(instruction.Operand0);
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

    private static bool DefinesValue(Avm1TacOp op)
    {
        return op is Avm1TacOp.LoadConstant or
            Avm1TacOp.Phi or
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
    }

    private static Avm1ValueFact EvaluateInstruction(
        Avm1InstructionTable instructions,
        Avm1TacIr tac,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueFact> facts,
        Dictionary<(int Register, int Version), Avm1ValueFact> registerFacts,
        Dictionary<IrIndex, Avm1RegisterAccess> accessesByInstruction,
        IReadOnlySet<IrIndex> boundaryRegisterReads,
        Dictionary<ActionIndex, int> pushPositions,
        Avm1ConstantPoolAnalysis constantPools,
        Avm1ValueOriginAnalysis valueOrigins,
        Avm1CatchPayloadAnalysis catchPayloads,
        Avm1ClassTypeEnvironment? typeEnvironment)
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
            Avm1TacOp.Phi => EvaluateValuePhi(tac, instruction, facts),
            Avm1TacOp.LoadConstant => EvaluateLoadConstant(
                instructions,
                instruction,
                pushPosition,
                constantPools.TryGetEntries(instruction.Action, out var pool)
                    ? pool
                    : []),
            Avm1TacOp.LoadRegister => EvaluateLoadRegister(
                instruction,
                accessesByInstruction,
                boundaryRegisterReads,
                registerFacts),
            Avm1TacOp.Copy => GetFact(facts, instruction.Operand0, instruction.Result),
            Avm1TacOp.Unary => EvaluateUnary(instruction, facts),
            Avm1TacOp.Binary => EvaluateBinary(instructions, instruction, facts),
            Avm1TacOp.Intrinsic =>
                Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.String),
            Avm1TacOp.HostIntrinsic =>
                Avm1ValueFact.Unknown(instruction.Result),
            Avm1TacOp.GetVariable => EvaluateGetVariable(
                tac,
                instruction,
                facts,
                catchPayloads),
            Avm1TacOp.GetMember => EvaluateGetMember(instruction, facts, valueOrigins, typeEnvironment),
            Avm1TacOp.Delete => Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Boolean),
            Avm1TacOp.GetTime => Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Number),
            Avm1TacOp.InitArray => EvaluateInitArray(tac, instruction, facts),
            Avm1TacOp.InitObject or Avm1TacOp.NewObject or Avm1TacOp.NewMethod or Avm1TacOp.FunctionLiteral =>
                Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Object),
            Avm1TacOp.Cast => Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Object),
            _ => instruction.Result.IsValid ? Avm1ValueFact.Unknown(instruction.Result) : Avm1ValueFact.Unknown(ValueIndex.Invalid)
        };
    }

    private static Avm1ValueFact EvaluateGetVariable(
        Avm1TacIr tac,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueFact> facts,
        Avm1CatchPayloadAnalysis catchPayloads)
    {
        var name = GetFact(facts, instruction.Operand0, instruction.Result);
        if (name.ConstantKind is Avm1ConstantKind.String &&
            name.StringValue is not null &&
            catchPayloads.TryGetExactVariableSource(
                instruction.Action,
                name.StringValue,
                out var binding,
                out var catchSource))
        {
            if (MayWriteCatchVariable(tac, facts, binding, name.StringValue))
                return Avm1ValueFact.Unknown(instruction.Result);

            return GetFact(facts, catchSource, instruction.Result)
                .WithValue(instruction.Result);
        }

        return Avm1ValueFact.Unknown(instruction.Result);
    }

    private static bool MayWriteCatchVariable(
        Avm1TacIr tac,
        IReadOnlyList<Avm1ValueFact> facts,
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

            var writtenName = GetFact(facts, candidate.Operand0, candidate.Operand0);
            if (writtenName.ConstantKind is Avm1ConstantKind.String &&
                !string.Equals(writtenName.StringValue, name, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static Avm1ValueFact EvaluateGetMember(
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueFact> facts,
        Avm1ValueOriginAnalysis valueOrigins,
        Avm1ClassTypeEnvironment? typeEnvironment)
    {
        if (typeEnvironment is not null)
        {
            var origin = valueOrigins[instruction.Result];
            if (origin.Kind is Avm1ValueOriginKind.InstanceMember &&
                origin.Member is not null &&
                typeEnvironment.TryGetInstanceMember(origin.Member, out var instanceType))
            {
                return Avm1ValueFact.Typed(instruction.Result, instanceType.Type, instanceType.ElementType);
            }

            if (origin.Kind is Avm1ValueOriginKind.StaticMember &&
                origin.Name is not null &&
                origin.Member is not null &&
                typeEnvironment.TryGetStaticMember(origin.Name, origin.Member, out var staticType))
            {
                return Avm1ValueFact.Typed(instruction.Result, staticType.Type, staticType.ElementType);
            }
        }

        var target = GetFact(facts, instruction.Operand0, instruction.Result);
        var member = GetFact(facts, instruction.Operand1, instruction.Result);
        if (target.Type is not Avm1InferredType.Array)
            return Avm1ValueFact.Unknown(instruction.Result);

        if (member.ConstantKind is Avm1ConstantKind.String && member.StringValue == "length")
            return Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Integer);

        if (member.Type is Avm1InferredType.Integer or Avm1InferredType.Number &&
            target.ElementType is not Avm1InferredType.Unknown)
        {
            return Avm1ValueFact.Typed(instruction.Result, target.ElementType);
        }

        return Avm1ValueFact.Unknown(instruction.Result);
    }

    private static Avm1ValueFact EvaluateInitArray(
        Avm1TacIr tac,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueFact> facts)
    {
        var elementType = Avm1InferredType.Unknown;
        var hasElement = false;
        if (instruction.Operand2.IsValid && instruction.OperandCount > 0)
        {
            var end = Math.Min(instruction.Operand2.Value + instruction.OperandCount, tac.ValueOperands.Count);
            for (var i = instruction.Operand2.Value; i < end; i++)
            {
                var element = GetFact(facts, tac.ValueOperands[i], instruction.Result);
                if (element.Type is Avm1InferredType.Unknown)
                {
                    elementType = Avm1InferredType.Unknown;
                    hasElement = true;
                    break;
                }

                if (!hasElement)
                {
                    elementType = element.Type;
                    hasElement = true;
                    continue;
                }

                elementType = MergeArrayElementType(elementType, element.Type);
                if (elementType is Avm1InferredType.Unknown)
                    break;
            }
        }

        return Avm1ValueFact.Typed(
            instruction.Result,
            Avm1InferredType.Array,
            hasElement ? elementType : Avm1InferredType.Unknown);
    }

    private static Avm1InferredType MergeArrayElementType(
        Avm1InferredType left,
        Avm1InferredType right)
    {
        if (left == right)
            return left;

        if (left is Avm1InferredType.Integer or Avm1InferredType.Number &&
            right is Avm1InferredType.Integer or Avm1InferredType.Number)
        {
            return Avm1InferredType.Number;
        }

        return Avm1InferredType.Unknown;
    }

    private static Avm1ValueFact EvaluateLoadConstant(
        Avm1InstructionTable instructions,
        Avm1TacInstruction instruction,
        int position,
        IReadOnlyList<string> pool)
    {
        if (!instruction.Action.IsValid || instructions[instruction.Action].Action is not ActionPush push)
            return Avm1ValueFact.Unknown(instruction.Result);

        if (position < 0 || position >= push.PushValues.Count)
            return Avm1ValueFact.Unknown(instruction.Result);

        return FromPushValue(instruction.Result, push.PushValues[position], pool);
    }

    private static Avm1ValueFact EvaluateLoadRegister(
        Avm1TacInstruction instruction,
        Dictionary<IrIndex, Avm1RegisterAccess> accessesByInstruction,
        IReadOnlySet<IrIndex> boundaryRegisterReads,
        Dictionary<(int Register, int Version), Avm1ValueFact> registerFacts)
    {
        if (!accessesByInstruction.TryGetValue(instruction.Index, out var access))
            return Avm1ValueFact.RegisterReference(instruction.Result, instruction.IntOperand, 0);
        if (boundaryRegisterReads.Contains(instruction.Index))
        {
            return Avm1ValueFact.RegisterReference(
                instruction.Result,
                access.Register,
                access.Version);
        }

        return registerFacts.TryGetValue((access.Register, access.Version), out var fact)
            ? fact.WithValue(instruction.Result)
            : Avm1ValueFact.RegisterReference(instruction.Result, access.Register, access.Version);
    }

    private static Avm1ValueFact EvaluateUnary(Avm1TacInstruction instruction, IReadOnlyList<Avm1ValueFact> facts)
    {
        var operand = GetFact(facts, instruction.Operand0, instruction.Operand0);
        if (instruction.Opcode is ActionOpcode.Increment or ActionOpcode.Decrement)
        {
            if (operand.TryGetNumber(out var number))
            {
                return Avm1ValueFact.Number(
                    instruction.Result,
                    instruction.Opcode is ActionOpcode.Increment ? number + 1 : number - 1);
            }

            return Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Number);
        }

        if (instruction.Opcode is ActionOpcode.Not &&
            operand.ConstantKind is Avm1ConstantKind.Boolean)
        {
            return Avm1ValueFact.Boolean(instruction.Result, !operand.BooleanValue);
        }

        if (instruction.Opcode is ActionOpcode.Not)
            return Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Boolean);

        if (instruction.Opcode is ActionOpcode.TypeOf)
        {
            var typeName = operand.ConstantKind switch
            {
                Avm1ConstantKind.Undefined => "undefined",
                Avm1ConstantKind.Null => "object",
                Avm1ConstantKind.Boolean => "boolean",
                Avm1ConstantKind.Integer or Avm1ConstantKind.Number => "number",
                Avm1ConstantKind.String => "string",
                _ => null
            };
            return typeName is null
                ? Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.String)
                : Avm1ValueFact.String(instruction.Result, typeName);
        }

        return instruction.Opcode switch
        {
            ActionOpcode.ToNumber or ActionOpcode.ToInteger or
                ActionOpcode.RandomNumber or ActionOpcode.StringLength or
                ActionOpcode.MBStringLength or ActionOpcode.CharToAscii or
                ActionOpcode.MBCharToAscii =>
                Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Number),
            ActionOpcode.ToString or ActionOpcode.TargetPath or
                ActionOpcode.AsciiToChar or ActionOpcode.MBAsciiToChar =>
                Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.String),
            _ => Avm1ValueFact.Unknown(instruction.Result)
        };
    }

    private static Avm1ValueFact EvaluateBinary(
        Avm1InstructionTable instructions,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueFact> facts)
    {
        var left = GetFact(facts, instruction.Operand0, instruction.Operand0);
        var right = GetFact(facts, instruction.Operand1, instruction.Operand1);
        var action = instructions[instruction.Action].Action;

        if (TryFoldNumericBinary(instruction.Result, action, left, right, out var folded))
            return folded;

        return action switch
        {
            ActionAdd or ActionAdd2 or ActionSubtract or ActionMultiply or ActionDivide or ActionModulo =>
                Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Number),
            ActionEquals or ActionEquals2 or ActionStringEquals or ActionLess or ActionLess2 or
                ActionStringLess or ActionGreater or ActionStringGreater or ActionAnd or ActionOr or
                ActionStrictEquals or ActionInstanceOf =>
                Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.Boolean),
            ActionStringAdd => Avm1ValueFact.Typed(instruction.Result, Avm1InferredType.String),
            _ => Avm1ValueFact.Unknown(instruction.Result)
        };
    }

    private static bool TryFoldNumericBinary(
        ValueIndex result,
        Action action,
        Avm1ValueFact left,
        Avm1ValueFact right,
        out Avm1ValueFact folded)
    {
        folded = Avm1ValueFact.Unknown(result);

        if (!left.TryGetNumber(out var leftNumber) || !right.TryGetNumber(out var rightNumber))
            return false;

        if (action is ActionAdd or ActionAdd2)
        {
            folded = left.ConstantKind is Avm1ConstantKind.Integer && right.ConstantKind is Avm1ConstantKind.Integer
                ? Avm1ValueFact.Integer(result, left.IntegerValue + right.IntegerValue)
                : Avm1ValueFact.Number(result, leftNumber + rightNumber);
            return true;
        }

        if (action is ActionSubtract)
        {
            folded = left.ConstantKind is Avm1ConstantKind.Integer && right.ConstantKind is Avm1ConstantKind.Integer
                ? Avm1ValueFact.Integer(result, left.IntegerValue - right.IntegerValue)
                : Avm1ValueFact.Number(result, leftNumber - rightNumber);
            return true;
        }

        if (action is ActionMultiply)
        {
            folded = left.ConstantKind is Avm1ConstantKind.Integer && right.ConstantKind is Avm1ConstantKind.Integer
                ? Avm1ValueFact.Integer(result, left.IntegerValue * right.IntegerValue)
                : Avm1ValueFact.Number(result, leftNumber * rightNumber);
            return true;
        }

        if (action is ActionDivide)
        {
            folded = Avm1ValueFact.Number(result, leftNumber / rightNumber);
            return true;
        }

        if (action is ActionModulo)
        {
            folded = left.ConstantKind is Avm1ConstantKind.Integer &&
                right.ConstantKind is Avm1ConstantKind.Integer &&
                right.IntegerValue != 0 &&
                (left.IntegerValue != int.MinValue || right.IntegerValue != -1)
                ? Avm1ValueFact.Integer(result, left.IntegerValue % right.IntegerValue)
                : Avm1ValueFact.Number(result, leftNumber % rightNumber);
            return true;
        }

        return false;
    }

    private static Avm1ValueFact EvaluatePhi(
        Avm1RegisterPhiNode phi,
        Dictionary<(int Register, int Version), Avm1ValueFact> registerFacts)
    {
        Avm1ValueFact? merged = null;
        var type = Avm1InferredType.Unknown;
        var elementType = Avm1InferredType.Unknown;
        var hasElementType = false;
        var hasUnknownIncoming = false;

        foreach (var version in phi.IncomingVersions)
        {
            if (!registerFacts.TryGetValue((phi.Register, version), out var incoming) ||
                incoming.Type is Avm1InferredType.Register)
            {
                hasUnknownIncoming = true;
                continue;
            }

            type = MergeType(type, incoming.Type);
            elementType = MergePhiElementType(elementType, incoming.ElementType, ref hasElementType);
            if (merged is null)
            {
                merged = incoming.WithValue(ValueIndex.Invalid);
                continue;
            }

            if (!SameConstant(merged.Value, incoming))
                merged = Avm1ValueFact.Typed(ValueIndex.Invalid, type, elementType);
        }

        if (merged is null)
        {
            return Avm1ValueFact.RegisterReference(
                ValueIndex.Invalid,
                phi.Register,
                phi.Version);
        }

        return hasUnknownIncoming && merged.Value.ConstantKind is not Avm1ConstantKind.Unknown
            ? Avm1ValueFact.Typed(ValueIndex.Invalid, type, elementType)
            : merged.Value.WithValue(ValueIndex.Invalid);
    }

    private static Avm1ValueFact EvaluateValuePhi(
        Avm1TacIr tac,
        Avm1TacInstruction instruction,
        IReadOnlyList<Avm1ValueFact> facts)
    {
        if (!instruction.Operand2.IsValid || instruction.OperandCount <= 0)
            return Avm1ValueFact.Unknown(instruction.Result);

        Avm1ValueFact? merged = null;
        var type = Avm1InferredType.Unknown;
        var elementType = Avm1InferredType.Unknown;
        var hasElementType = false;
        var end = Math.Min(instruction.Operand2.Value + instruction.OperandCount, tac.ValueOperands.Count);
        for (var i = instruction.Operand2.Value; i < end; i++)
        {
            var incoming = GetFact(facts, tac.ValueOperands[i], instruction.Result);
            type = MergeType(type, incoming.Type);
            elementType = MergePhiElementType(elementType, incoming.ElementType, ref hasElementType);
            if (merged is null)
            {
                merged = incoming.WithValue(instruction.Result);
                continue;
            }

            if (!SameConstant(merged.Value, incoming))
                merged = Avm1ValueFact.Typed(instruction.Result, type, elementType);
        }

        return merged?.WithValue(instruction.Result) ?? Avm1ValueFact.Unknown(instruction.Result);
    }

    private static Avm1ValueFact FromPushValue(ValueIndex result, PushValue value, IReadOnlyList<string> pool)
    {
        return value switch
        {
            PushValue.PushValueUndefined => Avm1ValueFact.Undefined(result),
            PushValue.PushValueNull => Avm1ValueFact.Null(result),
            PushValue.PushValueBoolean x => Avm1ValueFact.Boolean(result, x.Value),
            PushValue.PushValueInteger x => Avm1ValueFact.Integer(result, x.Value),
            PushValue.PushValueFloat x => Avm1ValueFact.Number(result, x.Value),
            PushValue.PushValueDouble x => Avm1ValueFact.Number(result, x.Value),
            PushValue.PushValueString x => Avm1ValueFact.String(result, x.Value),
            PushValue.PushValueRegister x => Avm1ValueFact.RegisterReference(result, x.RegisterIndex, 0),
            // ИСПРАВЛЕНО: Преобразуем индексы пула констант во вполне реальные строки
            PushValue.PushValueConstant8 x => x.ConstantIndex < pool.Count ? Avm1ValueFact.String(result, pool[x.ConstantIndex]) : Avm1ValueFact.Unknown(result),
            PushValue.PushValueConstant16 x => x.ConstantIndex < pool.Count ? Avm1ValueFact.String(result, pool[x.ConstantIndex]) : Avm1ValueFact.Unknown(result),
            _ => Avm1ValueFact.Unknown(result)
        };
    }

    private static Avm1ValueFact GetFact(IReadOnlyList<Avm1ValueFact> facts, ValueIndex value, ValueIndex fallbackValue)
    {
        return value.IsValid && value.Value < facts.Count
            ? facts[value.Value]
            : Avm1ValueFact.Unknown(fallbackValue);
    }

    private static bool SetValueFact(Avm1ValueFact[] facts, ValueIndex value, Avm1ValueFact fact)
    {
        if (!value.IsValid || value.Value >= facts.Length || facts[value.Value].Equals(fact))
            return false;

        facts[value.Value] = fact.WithValue(value);
        return true;
    }

    private static bool SetRegisterFact(
        Dictionary<(int Register, int Version), Avm1ValueFact> facts,
        int register,
        int version,
        Avm1ValueFact fact)
    {
        var key = (register, version);
        var next = fact.WithValue(ValueIndex.Invalid);
        if (facts.TryGetValue(key, out var current) && current.Equals(next))
            return false;

        facts[key] = next;
        return true;
    }

    private static Avm1InferredType MergeType(Avm1InferredType left, Avm1InferredType right)
    {
        if (left is Avm1InferredType.Register)
            left = Avm1InferredType.Unknown;
        if (right is Avm1InferredType.Register)
            right = Avm1InferredType.Unknown;

        if (left is Avm1InferredType.Unknown)
            return right;

        if (right is Avm1InferredType.Unknown)
            return left;

        if (left is Avm1InferredType.Integer && right is Avm1InferredType.Number ||
            left is Avm1InferredType.Number && right is Avm1InferredType.Integer)
        {
            return Avm1InferredType.Number;
        }

        return left == right ? left : Avm1InferredType.Unknown;
    }

    private static Avm1InferredType MergePhiElementType(
        Avm1InferredType current,
        Avm1InferredType incoming,
        ref bool hasCurrent)
    {
        if (!hasCurrent)
        {
            hasCurrent = true;
            return incoming;
        }

        if (current is Avm1InferredType.Unknown || incoming is Avm1InferredType.Unknown)
            return Avm1InferredType.Unknown;

        return MergeArrayElementType(current, incoming);
    }

    private static bool SameConstant(Avm1ValueFact left, Avm1ValueFact right)
    {
        return left.ConstantKind == right.ConstantKind &&
            left.BooleanValue == right.BooleanValue &&
            left.IntegerValue == right.IntegerValue &&
            left.NumberValue.Equals(right.NumberValue) &&
            string.Equals(left.StringValue, right.StringValue, StringComparison.Ordinal);
    }
}

public enum Avm1InferredType
{
    Unknown,
    Undefined,
    Null,
    Boolean,
    Integer,
    Number,
    String,
    Object,
    Array,
    Register
}

public enum Avm1ConstantKind
{
    Unknown,
    Undefined,
    Null,
    Boolean,
    Integer,
    Number,
    String
}

public readonly record struct Avm1ValueFact(
    ValueIndex Value,
    Avm1InferredType Type,
    Avm1InferredType ElementType,
    Avm1ConstantKind ConstantKind,
    bool BooleanValue,
    int IntegerValue,
    double NumberValue,
    string? StringValue,
    int Register,
    int RegisterVersion)
{
    public static Avm1ValueFact Unknown(ValueIndex value) =>
        new(value, Avm1InferredType.Unknown, Avm1InferredType.Unknown, Avm1ConstantKind.Unknown, false, 0, 0, null, -1, 0);

    public static Avm1ValueFact Typed(
        ValueIndex value,
        Avm1InferredType type,
        Avm1InferredType elementType = Avm1InferredType.Unknown) =>
        new(value, type, elementType, Avm1ConstantKind.Unknown, false, 0, 0, null, -1, 0);

    public static Avm1ValueFact Undefined(ValueIndex value) =>
        new(value, Avm1InferredType.Undefined, Avm1InferredType.Unknown, Avm1ConstantKind.Undefined, false, 0, 0, null, -1, 0);

    public static Avm1ValueFact Null(ValueIndex value) =>
        new(value, Avm1InferredType.Null, Avm1InferredType.Unknown, Avm1ConstantKind.Null, false, 0, 0, null, -1, 0);

    public static Avm1ValueFact Boolean(ValueIndex value, bool valueConstant) =>
        new(value, Avm1InferredType.Boolean, Avm1InferredType.Unknown, Avm1ConstantKind.Boolean, valueConstant, 0, 0, null, -1, 0);

    public static Avm1ValueFact Integer(ValueIndex value, int valueConstant) =>
        new(value, Avm1InferredType.Integer, Avm1InferredType.Unknown, Avm1ConstantKind.Integer, false, valueConstant, valueConstant, null, -1, 0);

    public static Avm1ValueFact Number(ValueIndex value, double valueConstant) =>
        new(value, Avm1InferredType.Number, Avm1InferredType.Unknown, Avm1ConstantKind.Number, false, 0, valueConstant, null, -1, 0);

    public static Avm1ValueFact String(ValueIndex value, string valueConstant) =>
        new(value, Avm1InferredType.String, Avm1InferredType.Unknown, Avm1ConstantKind.String, false, 0, 0, valueConstant, -1, 0);

    public static Avm1ValueFact RegisterReference(ValueIndex value, int register, int version) =>
        new(value, Avm1InferredType.Register, Avm1InferredType.Unknown, Avm1ConstantKind.Unknown, false, 0, 0, null, register, version);

    public Avm1ValueFact WithValue(ValueIndex value) => this with { Value = value };

    public bool TryGetNumber(out double value)
    {
        if (ConstantKind is Avm1ConstantKind.Integer)
        {
            value = IntegerValue;
            return true;
        }

        if (ConstantKind is Avm1ConstantKind.Number)
        {
            value = NumberValue;
            return true;
        }

        value = 0;
        return false;
    }
}
