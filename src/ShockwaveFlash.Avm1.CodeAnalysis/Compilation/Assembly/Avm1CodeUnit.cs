using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Types;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Assembly;

public sealed class Avm1CodeUnit
{
    private readonly Avm1ActionAssembler _assembler = new();

    internal Avm1ActionAssembler Assembler => _assembler;

    public int InstructionCount => _assembler.InstructionCount;

    public int LabelCount => _assembler.LabelCount;

    public Avm1LabelId DefineLabel() => _assembler.DefineLabel();

    public void MarkLabel(Avm1LabelId label) => _assembler.MarkLabel(label);

    public Avm1AssemblyInstructionIndex Emit(Avm1Action action) =>
        _assembler.Emit(action);

    public Avm1AssemblyInstructionIndex EmitJump(Avm1LabelId target) =>
        _assembler.EmitJump(target);

    public Avm1AssemblyInstructionIndex EmitIf(Avm1LabelId target) =>
        _assembler.EmitIf(target);

    public Avm1AssemblyInstructionIndex EmitBranch(
        ActionOpcode opcode,
        Avm1LabelId target) =>
        _assembler.EmitBranch(opcode, target);

    public Avm1AssemblyInstructionIndex EmitParentJump(
        Avm1CodeUnit parent,
        Avm1LabelId target) =>
        EmitParentBranch(ActionOpcode.Jump, parent, target);

    public Avm1AssemblyInstructionIndex EmitParentIf(
        Avm1CodeUnit parent,
        Avm1LabelId target) =>
        EmitParentBranch(ActionOpcode.If, parent, target);

    public Avm1AssemblyInstructionIndex EmitParentBranch(
        ActionOpcode opcode,
        Avm1CodeUnit parent,
        Avm1LabelId target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return _assembler.EmitParentBranch(opcode, parent._assembler, target);
    }

    internal Avm1AssemblyInstructionIndex EmitUnboundParentBranch(
        ActionOpcode opcode) =>
        _assembler.EmitUnboundParentBranch(opcode);

    internal void BindParentBranch(
        Avm1AssemblyInstructionIndex instruction,
        Avm1CodeUnit parent,
        Avm1LabelId target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _assembler.BindParentBranch(instruction, parent._assembler, target);
    }

    public Avm1AssemblyInstructionIndex EmitWaitForFrame(
        ushort frame,
        Avm1LabelId target) =>
        _assembler.EmitWaitForFrame(frame, target);

    public Avm1AssemblyInstructionIndex EmitWaitForFrame2(
        Avm1LabelId target) =>
        _assembler.EmitWaitForFrame2(target);

    public Avm1AssemblyInstructionIndex EmitDefineFunction(
        string name,
        IReadOnlyList<string> parameters,
        Avm1CodeUnit body) =>
        _assembler.EmitDefineFunction(name, parameters, body);

    public Avm1AssemblyInstructionIndex EmitDefineFunction2(
        string name,
        byte registerCount,
        FunctionFlags flags,
        IReadOnlyList<FunctionParameter> parameters,
        Avm1CodeUnit body) =>
        _assembler.EmitDefineFunction2(
            name,
            registerCount,
            flags,
            parameters,
            body);

    public Avm1AssemblyInstructionIndex EmitWith(Avm1CodeUnit body) =>
        _assembler.EmitWith(body);

    public Avm1AssemblyInstructionIndex EmitTry(
        TryFlags flags,
        byte catchRegister,
        string catchVariable,
        Avm1CodeUnit tryBody,
        Avm1CodeUnit? catchBody = null,
        Avm1CodeUnit? finallyBody = null) =>
        _assembler.EmitTry(
            flags,
            catchRegister,
            catchVariable,
            tryBody,
            catchBody,
            finallyBody);

    public void Append(Avm1ActionBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Append(body, body.Actions.Count, replacements: null);
    }

    public void Append(
        Avm1ActionBody body,
        IReadOnlyDictionary<int, Avm1Action> replacements)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(replacements);
        Append(body, body.Actions.Count, replacements);
    }

    internal void Append(Avm1ActionBody body, int actionCount)
    {
        Append(body, actionCount, replacements: null);
    }

    private void Append(
        Avm1ActionBody body,
        int actionCount,
        IReadOnlyDictionary<int, Avm1Action>? replacements)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!body.Succeeded)
        {
            throw new ArgumentException(
                "Only a successfully assembled action body can be appended.",
                nameof(body));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(actionCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            actionCount,
            body.Actions.Count);
        if (replacements is not null)
        {
            foreach (var replacement in replacements)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(replacement.Key);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                    replacement.Key,
                    actionCount);
                ArgumentNullException.ThrowIfNull(replacement.Value);
                if (body.Actions[replacement.Key] is
                        ActionJump or ActionIf or
                        ActionWaitForFrame or ActionWaitForFrame2 ||
                    replacement.Value is
                        ActionJump or ActionIf or
                        ActionWaitForFrame or ActionWaitForFrame2)
                {
                    throw new ArgumentException(
                        "Control-flow actions cannot be replaced while importing a code unit.",
                        nameof(replacements));
                }
            }
        }

        var labels = new Dictionary<int, Avm1LabelId>();
        for (var i = 0; i < actionCount; i++)
        {
            switch (body.Actions[i])
            {
                case ActionJump jump:
                    _ = GetBranchTargetBoundary(body, actionCount, i, jump.BranchOffset);
                    GetOrCreateLabel(GetBranchTargetBoundary(
                        body,
                        actionCount,
                        i,
                        jump.BranchOffset));
                    break;
                case ActionIf branch:
                    _ = GetBranchTargetBoundary(body, actionCount, i, branch.BranchOffset);
                    GetOrCreateLabel(GetBranchTargetBoundary(
                        body,
                        actionCount,
                        i,
                        branch.BranchOffset));
                    break;
                case ActionWaitForFrame wait:
                    GetOrCreateLabel(GetSkipTargetBoundary(
                        actionCount,
                        i,
                        wait.SkipCount));
                    break;
                case ActionWaitForFrame2 wait:
                    GetOrCreateLabel(GetSkipTargetBoundary(
                        actionCount,
                        i,
                        wait.SkipCount));
                    break;
            }
        }

        for (var i = 0; i < actionCount; i++)
        {
            if (labels.TryGetValue(i, out var label))
                MarkLabel(label);

            if (replacements?.TryGetValue(i, out var replacement) is true)
            {
                Emit(replacement);
                continue;
            }

            switch (body.Actions[i])
            {
                case ActionJump jump:
                    EmitJump(labels[GetBranchTargetBoundary(
                        body,
                        actionCount,
                        i,
                        jump.BranchOffset)]);
                    break;
                case ActionIf branch:
                    EmitIf(labels[GetBranchTargetBoundary(
                        body,
                        actionCount,
                        i,
                        branch.BranchOffset)]);
                    break;
                case ActionWaitForFrame wait:
                    EmitWaitForFrame(
                        wait.Frame,
                        labels[GetSkipTargetBoundary(
                            actionCount,
                            i,
                            wait.SkipCount)]);
                    break;
                case ActionWaitForFrame2 wait:
                    EmitWaitForFrame2(labels[GetSkipTargetBoundary(
                        actionCount,
                        i,
                        wait.SkipCount)]);
                    break;
                default:
                    Emit(body.Actions[i]);
                    break;
            }
        }

        if (labels.TryGetValue(actionCount, out var endLabel))
            MarkLabel(endLabel);
        return;

        Avm1LabelId GetOrCreateLabel(int boundary)
        {
            if (!labels.TryGetValue(boundary, out var label))
            {
                label = DefineLabel();
                labels.Add(boundary, label);
            }
            return label;
        }
    }

    public Avm1ActionBody Assemble(Avm1AssemblyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Avm1AssemblyContext().Assemble(this, options);
    }

    private static int GetBranchTargetBoundary(
        Avm1ActionBody body,
        int actionCount,
        int actionIndex,
        short branchOffset)
    {
        var actionEnd = actionIndex + 1 < body.ActionOffsets.Count
            ? body.ActionOffsets[actionIndex + 1]
            : body.Bytes.Length;
        var targetOffset = checked(actionEnd + branchOffset);
        for (var boundary = 0; boundary <= actionCount; boundary++)
        {
            var boundaryOffset = boundary < body.ActionOffsets.Count
                ? body.ActionOffsets[boundary]
                : body.Bytes.Length;
            if (boundaryOffset == targetOffset)
                return boundary;
        }

        throw new ArgumentException(
            $"Action {actionIndex} branches to byte offset {targetOffset}, " +
            "which is outside the appended action boundary.",
            nameof(body));
    }

    private static int GetSkipTargetBoundary(
        int actionCount,
        int actionIndex,
        byte skipCount)
    {
        var target = actionIndex + 1 + skipCount;
        if (target <= actionCount)
            return target;
        throw new ArgumentException(
            $"Action {actionIndex} skips to action boundary {target}, which is " +
            "outside the appended action boundary.",
            nameof(actionCount));
    }
}
