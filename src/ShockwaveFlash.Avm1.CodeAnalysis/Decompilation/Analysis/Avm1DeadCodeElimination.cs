using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public static class Avm1DeadCodeElimination
{
    public static HashSet<IrIndex> Run(
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa) =>
        RunCore(instructions: null, tacIr, registerSsa, valueAnalysis: null);

    public static HashSet<IrIndex> Run(
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ValueAnalysis? valueAnalysis) =>
        RunCore(instructions: null, tacIr, registerSsa, valueAnalysis);

    public static HashSet<IrIndex> Run(
        Avm1InstructionTable instructions,
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ValueAnalysis? valueAnalysis) =>
        RunCore(instructions, tacIr, registerSsa, valueAnalysis);

    private static HashSet<IrIndex> RunCore(
        Avm1InstructionTable? instructions,
        Avm1TacIr tacIr,
        Avm1RegisterSsa registerSsa,
        Avm1ValueAnalysis? valueAnalysis)
    {
        var liveInstructions = new bool[tacIr.Count];
        var worklist = new Queue<IrIndex>();
        var liveRegisterVersions = new HashSet<(int Register, int Version)>();

        var defByValue = new int[GetValueCount(tacIr)];
        Array.Fill(defByValue, -1);
        for (int i = 0; i < tacIr.Instructions.Count; i++)
        {
            var inst = tacIr.Instructions[i];
            if (inst.Result.IsValid && Avm1TacEffectAnalysis.DefinesValue(inst.Op))
                defByValue[inst.Result.Value] = inst.Index.Value;
        }

        var writeByRegisterVersion = new Dictionary<(int Register, int Version), IrIndex>();
        var accessByInstruction = new Dictionary<IrIndex, Avm1RegisterAccess>();
        foreach (var access in registerSsa.Accesses)
        {
            accessByInstruction[access.Instruction] = access;
            if (access.Kind == Avm1RegisterAccessKind.Write)
                writeByRegisterVersion[(access.Register, access.Version)] = access.Instruction;
        }
        var phiByRegisterVersion = registerSsa.PhiNodes.ToDictionary(
            phi => (phi.Register, phi.Version));

        for (int i = 0; i < tacIr.Instructions.Count; i++)
        {
            var inst = tacIr.Instructions[i];
            if (IsSemanticRoot(
                instructions,
                tacIr,
                inst,
                defByValue,
                accessByInstruction,
                valueAnalysis))
                MarkInstructionLive(inst.Index, liveInstructions, worklist);
        }

        while (worklist.Count > 0)
        {
            var idx = worklist.Dequeue();
            var inst = tacIr[idx];

            if (inst.Op is not (Avm1TacOp.InitArray or Avm1TacOp.InitObject))
                MarkOperandLive(inst.Operand0, defByValue, liveInstructions, worklist);

            if (inst.Op is not (Avm1TacOp.CallFunction or Avm1TacOp.NewObject))
                MarkOperandLive(inst.Operand1, defByValue, liveInstructions, worklist);

            if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(inst.Op))
                MarkOperandLive(inst.Operand2, defByValue, liveInstructions, worklist);

            foreach (var operand in GetValueOperands(tacIr, inst))
                MarkOperandLive(operand, defByValue, liveInstructions, worklist);

            if (inst.Op is Avm1TacOp.LoadRegister &&
                accessByInstruction.TryGetValue(inst.Index, out var read) &&
                read.Kind is Avm1RegisterAccessKind.Read &&
                !IsFoldedConstant(inst.Result, valueAnalysis))
            {
                MarkRegisterVersionLive(
                    read.Register,
                    read.Version,
                    writeByRegisterVersion,
                    phiByRegisterVersion,
                    liveRegisterVersions,
                    liveInstructions,
                    worklist);
            }
        }

        var deadInstructions = new HashSet<IrIndex>();
        for (int i = 0; i < tacIr.Instructions.Count; i++)
        {
            var inst = tacIr.Instructions[i];
            if (!liveInstructions[inst.Index.Value] &&
                (Avm1TacEffectAnalysis.DefinesValue(inst.Op) ||
                    inst.Op is Avm1TacOp.StoreRegister))
            {
                deadInstructions.Add(inst.Index);
            }
        }

        return deadInstructions;
    }

    private static bool IsSemanticRoot(
        Avm1InstructionTable? instructions,
        Avm1TacIr tacIr,
        Avm1TacInstruction instruction,
        int[] defByValue,
        Dictionary<IrIndex, Avm1RegisterAccess> accessByInstruction,
        Avm1ValueAnalysis? valueAnalysis)
    {
        if (IsNamedFunctionDefinition(instructions, instruction))
            return true;

        if (instruction.Op is not Avm1TacOp.StoreRegister)
            return Avm1TacEffectAnalysis.HasObservableEffect(
                instruction,
                valueAnalysis);

        if (valueAnalysis is null)
            return true;

        if (IsRegisterSelfUpdate(
            tacIr,
            instruction,
            defByValue,
            accessByInstruction))
        {
            return true;
        }

        var source = accessByInstruction.TryGetValue(instruction.Index, out var write) &&
            write.Source.IsValid
                ? write.Source
                : instruction.Operand0;
        return valueAnalysis[source].ConstantKind is Avm1ConstantKind.Undefined;
    }

    private static bool IsRegisterSelfUpdate(
        Avm1TacIr tacIr,
        Avm1TacInstruction store,
        int[] defByValue,
        Dictionary<IrIndex, Avm1RegisterAccess> accessByInstruction)
    {
        if (!TryGetDefinition(defByValue, store.Operand0, out var valueDefinition))
            return false;

        var value = tacIr[valueDefinition];
        if (value.Op is Avm1TacOp.Binary)
        {
            return IsSameRegisterLoad(value.Operand0) ||
                IsSameRegisterLoad(value.Operand1);
        }
        if (value.Op is Avm1TacOp.Unary)
            return IsSameRegisterLoad(value.Operand0);
        return false;

        bool IsSameRegisterLoad(ValueIndex operand)
        {
            if (!TryGetDefinition(defByValue, operand, out var operandDefinition))
                return false;

            var load = tacIr[operandDefinition];
            return load.Op is Avm1TacOp.LoadRegister &&
                accessByInstruction.TryGetValue(load.Index, out var read) &&
                read.Kind is Avm1RegisterAccessKind.Read &&
                read.Register == store.IntOperand;
        }
    }

    private static bool IsNamedFunctionDefinition(
        Avm1InstructionTable? instructions,
        Avm1TacInstruction instruction)
    {
        if (instructions is null ||
            instruction.Op is not Avm1TacOp.FunctionLiteral ||
            !instruction.Action.IsValid)
        {
            return false;
        }

        return instructions[instruction.Action].Action switch
        {
            ActionDefineFunction function => function.Name.Length > 0,
            ActionDefineFunction2 function => function.Name.Length > 0,
            _ => false
        };
    }

    private static bool IsFoldedConstant(
        ValueIndex value,
        Avm1ValueAnalysis? valueAnalysis) => valueAnalysis is not null &&
        valueAnalysis[value].ConstantKind is not Avm1ConstantKind.Unknown;

    private static void MarkRegisterVersionLive(
        int register,
        int version,
        Dictionary<(int Register, int Version), IrIndex> writeByRegisterVersion,
        Dictionary<(int Register, int Version), Avm1RegisterPhiNode> phiByRegisterVersion,
        HashSet<(int Register, int Version)> liveRegisterVersions,
        bool[] liveInstructions,
        Queue<IrIndex> worklist)
    {
        var key = (register, version);
        if (!liveRegisterVersions.Add(key))
            return;

        if (writeByRegisterVersion.TryGetValue(key, out var write))
            MarkInstructionLive(write, liveInstructions, worklist);

        if (!phiByRegisterVersion.TryGetValue(key, out var phi))
            return;

        foreach (var incomingVersion in phi.IncomingVersions)
        {
            MarkRegisterVersionLive(
                register,
                incomingVersion,
                writeByRegisterVersion,
                phiByRegisterVersion,
                liveRegisterVersions,
                liveInstructions,
                worklist);
        }
    }

    private static void MarkInstructionLive(
        IrIndex instruction,
        bool[] liveInstructions,
        Queue<IrIndex> worklist)
    {
        if (!instruction.IsValid ||
            (uint)instruction.Value >= (uint)liveInstructions.Length ||
            liveInstructions[instruction.Value])
        {
            return;
        }

        liveInstructions[instruction.Value] = true;
        worklist.Enqueue(instruction);
    }

    private static void MarkOperandLive(
        ValueIndex val,
        int[] defByValue,
        bool[] live,
        Queue<IrIndex> worklist)
    {
        if (TryGetDefinition(defByValue, val, out var definition))
            MarkInstructionLive(definition, live, worklist);
    }

    private static bool TryGetDefinition(
        int[] definitions,
        ValueIndex value,
        out IrIndex definition)
    {
        if (value.IsValid &&
            (uint)value.Value < (uint)definitions.Length &&
            definitions[value.Value] >= 0)
        {
            definition = new IrIndex(definitions[value.Value]);
            return true;
        }

        definition = IrIndex.Invalid;
        return false;
    }

    private static int GetValueCount(Avm1TacIr tacIr)
    {
        var maximum = -1;
        foreach (var instruction in tacIr.Instructions)
        {
            maximum = Math.Max(maximum, instruction.Result.Value);
            maximum = Math.Max(maximum, instruction.Operand0.Value);
            maximum = Math.Max(maximum, instruction.Operand1.Value);
            if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(instruction.Op))
                maximum = Math.Max(maximum, instruction.Operand2.Value);
        }
        foreach (var operand in tacIr.ValueOperands)
            maximum = Math.Max(maximum, operand.Value);
        return maximum + 1;
    }

    private static IEnumerable<ValueIndex> GetValueOperands(Avm1TacIr tacIr, Avm1TacInstruction inst)
    {
        if (!Avm1TacEffectAnalysis.HasValueOperandSideTable(inst.Op) ||
            !inst.Operand2.IsValid ||
            inst.OperandCount == 0)
            yield break;

        var start = inst.Operand2.Value;
        var operandSlots = inst.Op is Avm1TacOp.InitObject ? inst.OperandCount * 2 : inst.OperandCount;
        var end = Math.Min(start + operandSlots, tacIr.ValueOperands.Count);
        for (var i = start; i < end; i++)
            yield return tacIr.ValueOperands[i];
    }

}
