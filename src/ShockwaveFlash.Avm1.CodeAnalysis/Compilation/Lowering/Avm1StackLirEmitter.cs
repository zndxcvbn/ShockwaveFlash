using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf2;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using System.Text;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Lowering;

internal static class Avm1StackLirEmitter
{
    private const int MaximumPushPayloadLength = ushort.MaxValue;

    public static Avm1StackLirEmission Emit(
        Avm1MirMethod mir,
        Avm1StackLirMethod stackLir,
        Avm1CompilationOptions options,
        IReadOnlyDictionary<SourceFunctionIndex, Avm1CompiledFunctionCodeUnit>
            compiledFunctions,
        IReadOnlyDictionary<Avm1MirWithSiteIndex, Avm1CompiledWithCodeUnit>
            compiledWithRegions,
        IReadOnlyDictionary<Avm1MirTrySiteIndex, Avm1CompiledTryCodeUnit>
            compiledTryRegions,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mir);
        ArgumentNullException.ThrowIfNull(stackLir);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(compiledFunctions);
        ArgumentNullException.ThrowIfNull(compiledWithRegions);
        ArgumentNullException.ThrowIfNull(compiledTryRegions);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var codeUnit = new Avm1CodeUnit();
        var instructionMap = new List<Avm1CompiledInstructionMap>(
            stackLir.Instructions.Count);
        var parentBranches = new List<Avm1PendingParentBranch>();
        var forwardedBranchIndices = new Dictionary<
            (Avm1MirCompletionKind Kind, SourceStatementIndex Target),
            int>();
        var forwardedBranches = new List<ForwardedParentBranch>();
        var constantPool = Avm1ConstantPoolPlanner.Create(stackLir, options);
        var pushEncoding = new Avm1Context(options.SwfVersion).Encoding;
        var blockLabels = new Avm1LabelId[mir.Blocks.Count];
        for (var i = 0; i < blockLabels.Length; i++)
            blockLabels[i] = codeUnit.DefineLabel();

        if (stackLir.Blocks.Count != mir.BlockLayout.Count)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP104",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                "Stack LIR block layout does not match MIR."));
        }

        for (var layoutIndex = 0;
            layoutIndex < stackLir.Blocks.Count;
            layoutIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = stackLir.Blocks[layoutIndex];
            if (!block.MirBlock.IsValid || block.MirBlock.Value >= mir.Blocks.Count ||
                layoutIndex >= mir.BlockLayout.Count ||
                block.MirBlock != mir.BlockLayout[layoutIndex] ||
                block.Instructions.Start < 0 ||
                block.Instructions.Count < 0 ||
                block.Instructions.Start >
                    stackLir.Instructions.Count - block.Instructions.Count)
            {
                diagnostics.Add(new Avm1CompilerDiagnostic(
                    "AVM1CMP104",
                    Avm1CompilationDiagnosticSeverity.Error,
                    SourceOriginIndex.Invalid,
                    -1,
                    $"Stack LIR block layout entry {layoutIndex} is invalid."));
                continue;
            }

            codeUnit.MarkLabel(blockLabels[block.MirBlock.Value]);
            var constantPoolSegment = constantPool.GetSegment(block.MirBlock);
            if (constantPool.ShouldEmitAtBlockStart(
                    block.MirBlock,
                    mir.EntryBlock))
            {
                codeUnit.Emit(new ActionConstantPool(
                    constantPoolSegment.Entries));
            }

            var instructionEnd = block.Instructions.Start + block.Instructions.Count;
            for (var instructionIndex = block.Instructions.Start;
                instructionIndex < instructionEnd;
                instructionIndex++)
            {
                var instruction = stackLir.Instructions[instructionIndex];
                if (options.OptimizationLevel is Avm1OptimizationLevel.Basic &&
                    instructionIndex == instructionEnd - 1 &&
                    instruction.Kind is Avm1StackLirInstructionKind.Branch &&
                    instruction.Opcode is ActionOpcode.Jump &&
                    IsPhysicalFallthroughTarget(
                        stackLir,
                        layoutIndex,
                        instruction.Target))
                {
                    continue;
                }

                if (options.OptimizationLevel is Avm1OptimizationLevel.Basic &&
                    TryCreatePushBatch(
                        stackLir,
                        instructionIndex,
                        instructionEnd,
                        constantPoolSegment,
                        pushEncoding,
                        out var packedPush,
                        out var lastPackedInstruction))
                {
                    var packedAssemblyInstruction = codeUnit.Emit(packedPush);
                    for (var packedInstructionIndex = instructionIndex;
                        packedInstructionIndex <= lastPackedInstruction;
                        packedInstructionIndex++)
                    {
                        var packedInstruction =
                            stackLir.Instructions[packedInstructionIndex];
                        instructionMap.Add(new Avm1CompiledInstructionMap(
                            packedAssemblyInstruction,
                            packedInstruction.Index,
                            packedInstruction.MirInstruction,
                            GetOrigin(mir, packedInstruction.MirInstruction)));
                    }
                    instructionIndex = lastPackedInstruction;
                    continue;
                }

                Avm1AssemblyInstructionIndex assemblyInstruction;
                if (instruction.Kind is Avm1StackLirInstructionKind.WaitForFrame)
                {
                    if (!instruction.Target.IsValid ||
                        instruction.Target.Value >= blockLabels.Length)
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            "Stack LIR frame-wait skip target is invalid."));
                        continue;
                    }

                    if (instruction.Opcode is ActionOpcode.WaitForFrame)
                    {
                        if (!instruction.Constant.IsValid ||
                            instruction.Constant.Value >= stackLir.Constants.Count ||
                            stackLir[instruction.Constant].Kind is not
                                Avm1MirConstantKind.Integer ||
                            stackLir[instruction.Constant].IntegerValue is
                                < 0 or > ushort.MaxValue)
                        {
                            diagnostics.Add(new Avm1CompilerDiagnostic(
                                "AVM1CMP104",
                                Avm1CompilationDiagnosticSeverity.Error,
                                GetOrigin(mir, instruction.MirInstruction),
                                -1,
                                "Stack LIR immediate frame wait has an invalid frame."));
                            continue;
                        }
                        assemblyInstruction = codeUnit.EmitWaitForFrame(
                            checked((ushort)stackLir[instruction.Constant].IntegerValue),
                            blockLabels[instruction.Target.Value]);
                    }
                    else if (instruction.Opcode is ActionOpcode.WaitForFrame2 &&
                             !instruction.Constant.IsValid)
                    {
                        assemblyInstruction = codeUnit.EmitWaitForFrame2(
                            blockLabels[instruction.Target.Value]);
                    }
                    else
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            "Stack LIR frame wait has an invalid opcode or payload."));
                        continue;
                    }
                }
                else if (instruction.Kind is Avm1StackLirInstructionKind.Branch)
                {
                    if (!instruction.Target.IsValid ||
                        instruction.Target.Value >= blockLabels.Length ||
                        instruction.Opcode is not (ActionOpcode.If or ActionOpcode.Jump))
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            "Stack LIR branch target or opcode is invalid."));
                        continue;
                    }

                    assemblyInstruction = codeUnit.EmitBranch(
                        instruction.Opcode,
                        blockLabels[instruction.Target.Value]);
                }
                else if (instruction.Kind is
                    Avm1StackLirInstructionKind.ParentBranch)
                {
                    if (!instruction.CompletionSite.IsValid ||
                        instruction.CompletionSite.Value >=
                            mir.CompletionSites.Count ||
                        instruction.Opcode is not ActionOpcode.Jump)
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            "Stack LIR parent branch metadata is invalid."));
                        continue;
                    }

                    var completion = mir[instruction.CompletionSite];
                    assemblyInstruction = codeUnit.EmitUnboundParentBranch(
                        instruction.Opcode);
                    parentBranches.Add(new Avm1PendingParentBranch(
                        assemblyInstruction,
                        completion.Kind,
                        completion.TargetStatement,
                        completion.Origin));
                }
                else if (instruction.Kind is
                    Avm1StackLirInstructionKind.DefineFunction)
                {
                    if (!TryEmitFunction(
                        codeUnit,
                        stackLir,
                        instruction,
                        compiledFunctions,
                        out assemblyInstruction,
                        out var error))
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            error));
                        continue;
                    }
                }
                else if (instruction.Kind is Avm1StackLirInstructionKind.Try)
                {
                    if (!TryEmitTry(
                        codeUnit,
                        mir,
                        blockLabels,
                        stackLir,
                        instruction,
                        compiledTryRegions,
                        GetForwardingLabel,
                        out assemblyInstruction,
                        out var error))
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            error));
                        continue;
                    }
                }
                else if (instruction.Kind is Avm1StackLirInstructionKind.With)
                {
                    if (!TryEmitWith(
                        codeUnit,
                        stackLir,
                        instruction,
                        compiledWithRegions,
                        out assemblyInstruction,
                        out var error))
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            error));
                        continue;
                    }
                }
                else if (instruction.Kind is
                    Avm1StackLirInstructionKind.PushTemporary or
                    Avm1StackLirInstructionKind.StoreTemporary)
                {
                    if (!TryEmitTemporary(
                        codeUnit,
                        stackLir,
                        instruction,
                        constantPoolSegment,
                        out assemblyInstruction,
                        out var error))
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            error));
                        continue;
                    }
                }
                else
                {
                    if (!TryCreateAction(
                        stackLir,
                        instruction,
                        constantPoolSegment,
                        out var action,
                        out var error))
                    {
                        diagnostics.Add(new Avm1CompilerDiagnostic(
                            "AVM1CMP104",
                            Avm1CompilationDiagnosticSeverity.Error,
                            GetOrigin(mir, instruction.MirInstruction),
                            -1,
                            error));
                        continue;
                    }

                    assemblyInstruction = codeUnit.Emit(action);
                }

                instructionMap.Add(new Avm1CompiledInstructionMap(
                    assemblyInstruction,
                    instruction.Index,
                    instruction.MirInstruction,
                    GetOrigin(mir, instruction.MirInstruction)));

                if (instruction.Kind is Avm1StackLirInstructionKind.Try &&
                    constantPool.ShouldRestoreAfterTry(
                        block.MirBlock,
                        instructionIndex))
                {
                    codeUnit.Emit(new ActionConstantPool(
                        constantPoolSegment.Entries));
                }
            }
        }

        if (forwardedBranches.Count != 0)
        {
            var normalExit = codeUnit.DefineLabel();
            codeUnit.EmitJump(normalExit);
            foreach (var forwarded in forwardedBranches)
            {
                codeUnit.MarkLabel(forwarded.Label);
                var instruction = codeUnit.EmitUnboundParentBranch(
                    ActionOpcode.Jump);
                parentBranches.Add(forwarded.Branch with
                {
                    AssemblyInstruction = instruction
                });
            }
            codeUnit.MarkLabel(normalExit);
        }

        return new Avm1StackLirEmission(
            codeUnit,
            instructionMap.ToArray(),
            parentBranches.ToArray());

        Avm1LabelId GetForwardingLabel(Avm1PendingParentBranch branch)
        {
            var key = (branch.Kind, branch.TargetStatement);
            if (forwardedBranchIndices.TryGetValue(key, out var existing))
                return forwardedBranches[existing].Label;

            var label = codeUnit.DefineLabel();
            forwardedBranchIndices.Add(key, forwardedBranches.Count);
            forwardedBranches.Add(new ForwardedParentBranch(label, branch));
            return label;
        }
    }

    private static bool IsPhysicalFallthroughTarget(
        Avm1StackLirMethod stackLir,
        int currentLayoutIndex,
        Avm1MirBlockIndex target)
    {
        if (!target.IsValid)
            return false;

        for (var layoutIndex = currentLayoutIndex + 1;
            layoutIndex < stackLir.Blocks.Count;
            layoutIndex++)
        {
            var block = stackLir.Blocks[layoutIndex];
            if (block.MirBlock == target)
                return true;
            if (block.Instructions.Count != 0)
                return false;
        }
        return false;
    }

    private static bool TryEmitTemporary(
        Avm1CodeUnit codeUnit,
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        Avm1ConstantPoolSegment constantPool,
        out Avm1AssemblyInstructionIndex assemblyInstruction,
        out string error)
    {
        assemblyInstruction = Avm1AssemblyInstructionIndex.Invalid;
        if (!instruction.Temporary.IsValid ||
            instruction.Temporary.Value >= stackLir.TemporaryStorages.Count)
        {
            error = "Stack LIR temporary handle is invalid.";
            return false;
        }

        var storage = stackLir[instruction.Temporary];
        if (storage.Kind is Avm1StackLirTemporaryStorageKind.Register)
        {
            assemblyInstruction = codeUnit.Emit(
                instruction.Kind is Avm1StackLirInstructionKind.PushTemporary
                    ? new ActionPush([PushValue.Register(storage.Register)])
                    : new ActionStoreRegister(storage.Register));
            error = string.Empty;
            return true;
        }

        if (storage.Kind is not Avm1StackLirTemporaryStorageKind.ActivationName ||
            !storage.Name.IsValid ||
            storage.Name.Value >= stackLir.Strings.Count)
        {
            error = "Stack LIR activation temporary storage is invalid.";
            return false;
        }

        var name = stackLir[storage.Name];
        if (instruction.Kind is Avm1StackLirInstructionKind.PushTemporary)
        {
            assemblyInstruction = codeUnit.Emit(
                new ActionPush([constantPool.CreatePushValue(name)]));
            codeUnit.Emit(new ActionGetVariable());
        }
        else
        {
            // DefineLocal consumes the duplicate and leaves the original value,
            // matching ActionStoreRegister's stack contract.
            assemblyInstruction = codeUnit.Emit(new ActionPushDuplicate());
            codeUnit.Emit(new ActionPush([constantPool.CreatePushValue(name)]));
            codeUnit.Emit(new ActionStackSwap());
            codeUnit.Emit(new ActionDefineLocal());
        }

        error = string.Empty;
        return true;
    }

    private static bool TryEmitFunction(
        Avm1CodeUnit codeUnit,
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        IReadOnlyDictionary<SourceFunctionIndex, Avm1CompiledFunctionCodeUnit>
            compiledFunctions,
        out Avm1AssemblyInstructionIndex assemblyInstruction,
        out string error)
    {
        assemblyInstruction = Avm1AssemblyInstructionIndex.Invalid;
        if (!instruction.Function.IsValid ||
            instruction.Function.Value >= stackLir.Functions.Count)
        {
            error = "Stack LIR function handle is invalid.";
            return false;
        }

        var function = stackLir[instruction.Function];
        if (!compiledFunctions.TryGetValue(
            function.SourceFunction,
            out var compiled))
        {
            error = $"Compiled child code unit for {function.SourceFunction} is missing.";
            return false;
        }
        if (compiled.Plan.IsDeclaration != function.IsDeclaration)
        {
            error = $"Compiled child code unit for {function.SourceFunction} " +
                "has a different declaration shape.";
            return false;
        }

        assemblyInstruction = codeUnit.EmitDefineFunction2(
            compiled.Plan.Name,
            compiled.Plan.RegisterCount,
            compiled.Plan.Flags,
            compiled.Plan.Parameters,
            compiled.CodeUnit);
        error = string.Empty;
        return true;
    }

    private static bool TryEmitTry(
        Avm1CodeUnit codeUnit,
        Avm1MirMethod mir,
        Avm1LabelId[] blockLabels,
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        IReadOnlyDictionary<Avm1MirTrySiteIndex, Avm1CompiledTryCodeUnit>
            compiledTryRegions,
        Func<Avm1PendingParentBranch, Avm1LabelId> getForwardingLabel,
        out Avm1AssemblyInstructionIndex assemblyInstruction,
        out string error)
    {
        assemblyInstruction = Avm1AssemblyInstructionIndex.Invalid;
        if (!instruction.TrySite.IsValid ||
            instruction.TrySite.Value >= stackLir.TrySites.Count)
        {
            error = "Stack LIR try handle is invalid.";
            return false;
        }

        var trySite = stackLir[instruction.TrySite];
        if (!compiledTryRegions.TryGetValue(trySite.MirTrySite, out var compiled))
        {
            error = $"Compiled nested code units for {trySite.MirTrySite} are missing.";
            return false;
        }
        if (compiled.Plan.Site != trySite.MirTrySite)
        {
            error = $"Compiled nested code units for {trySite.MirTrySite} " +
                "have inconsistent site metadata.";
            return false;
        }

        if (!TryBindParentBranches(
                compiled.TryBody,
                compiled.TryBodyParentBranches,
                codeUnit,
                mir,
                blockLabels,
                getForwardingLabel,
                out error) ||
            compiled.CatchBody is not null &&
            !TryBindParentBranches(
                compiled.CatchBody,
                compiled.CatchBodyParentBranches,
                codeUnit,
                mir,
                blockLabels,
                getForwardingLabel,
                out error) ||
            compiled.FinallyBody is not null &&
            !TryBindParentBranches(
                compiled.FinallyBody,
                compiled.FinallyBodyParentBranches,
                codeUnit,
                mir,
                blockLabels,
                getForwardingLabel,
                out error))
        {
            return false;
        }

        assemblyInstruction = codeUnit.EmitTry(
            compiled.Plan.Flags,
            compiled.Plan.CatchRegister,
            compiled.Plan.CatchVariable,
            compiled.TryBody,
            compiled.CatchBody,
            compiled.FinallyBody);
        error = string.Empty;
        return true;
    }

    private static bool TryBindParentBranches(
        Avm1CodeUnit child,
        IReadOnlyList<Avm1PendingParentBranch> branches,
        Avm1CodeUnit parent,
        Avm1MirMethod parentMir,
        Avm1LabelId[] parentBlockLabels,
        Func<Avm1PendingParentBranch, Avm1LabelId> getForwardingLabel,
        out string error)
    {
        foreach (var branch in branches)
        {
            var matches = parentMir.ControlTargets.Where(target =>
                target.Statement == branch.TargetStatement).ToArray();
            if (matches.Length == 0)
            {
                child.BindParentBranch(
                    branch.AssemblyInstruction,
                    parent,
                    getForwardingLabel(branch));
                continue;
            }
            if (matches.Length != 1)
            {
                error = $"Physical protected-region {branch.Kind} target " +
                    $"{branch.TargetStatement} resolves to {matches.Length} " +
                    "control targets in the direct parent code unit.";
                return false;
            }

            var control = matches[0];
            var targetBlock = branch.Kind switch
            {
                Avm1MirCompletionKind.Break => control.BreakBlock,
                Avm1MirCompletionKind.Continue
                    when control.Kind is Avm1MirControlTargetKind.Loop =>
                    control.ContinueBlock,
                _ => Avm1MirBlockIndex.Invalid
            };
            if (!targetBlock.IsValid ||
                targetBlock.Value >= parentBlockLabels.Length)
            {
                error = $"Physical protected-region {branch.Kind} target " +
                    $"{branch.TargetStatement} has no valid parent block.";
                return false;
            }

            child.BindParentBranch(
                branch.AssemblyInstruction,
                parent,
                parentBlockLabels[targetBlock.Value]);
        }

        error = string.Empty;
        return true;
    }

    private static bool TryEmitWith(
        Avm1CodeUnit codeUnit,
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        IReadOnlyDictionary<Avm1MirWithSiteIndex, Avm1CompiledWithCodeUnit>
            compiledWithRegions,
        out Avm1AssemblyInstructionIndex assemblyInstruction,
        out string error)
    {
        assemblyInstruction = Avm1AssemblyInstructionIndex.Invalid;
        if (!instruction.WithSite.IsValid ||
            instruction.WithSite.Value >= stackLir.WithSites.Count)
        {
            error = "Stack LIR with handle is invalid.";
            return false;
        }

        var withSite = stackLir[instruction.WithSite];
        if (!compiledWithRegions.TryGetValue(withSite.MirWithSite, out var compiled))
        {
            error = $"Compiled nested code unit for {withSite.MirWithSite} is missing.";
            return false;
        }
        if (compiled.Plan.Site != withSite.MirWithSite)
        {
            error = $"Compiled nested code unit for {withSite.MirWithSite} " +
                "has inconsistent site metadata.";
            return false;
        }

        assemblyInstruction = codeUnit.EmitWith(compiled.Body);
        error = string.Empty;
        return true;
    }

    private static bool TryCreatePushBatch(
        Avm1StackLirMethod stackLir,
        int firstInstructionIndex,
        int instructionEnd,
        Avm1ConstantPoolSegment constantPool,
        Encoding encoding,
        out ActionPush action,
        out int lastInstructionIndex)
    {
        action = null!;
        lastInstructionIndex = firstInstructionIndex;
        if (firstInstructionIndex + 1 >= instructionEnd)
            return false;

        var firstInstruction = stackLir.Instructions[firstInstructionIndex];
        if (!TryCreateDirectPushValue(
                stackLir,
                firstInstruction,
                constantPool,
                out var firstValue))
        {
            return false;
        }

        var secondInstruction = stackLir.Instructions[firstInstructionIndex + 1];
        if (secondInstruction.MirInstruction != firstInstruction.MirInstruction ||
            !TryCreateDirectPushValue(
                stackLir,
                secondInstruction,
                constantPool,
                out var secondValue))
        {
            return false;
        }

        var payloadLength = GetPushValuePayloadLength(firstValue, encoding) +
            GetPushValuePayloadLength(secondValue, encoding);
        if (payloadLength > MaximumPushPayloadLength)
            return false;

        var values = new List<PushValue>(4) { firstValue, secondValue };
        lastInstructionIndex = firstInstructionIndex + 1;
        for (var instructionIndex = firstInstructionIndex + 2;
            instructionIndex < instructionEnd;
            instructionIndex++)
        {
            var instruction = stackLir.Instructions[instructionIndex];
            if (instruction.MirInstruction != firstInstruction.MirInstruction ||
                !TryCreateDirectPushValue(
                    stackLir,
                    instruction,
                    constantPool,
                    out var value))
            {
                break;
            }

            var valueLength = GetPushValuePayloadLength(value, encoding);
            if (payloadLength + valueLength > MaximumPushPayloadLength)
                break;

            values.Add(value);
            payloadLength += valueLength;
            lastInstructionIndex = instructionIndex;
        }

        action = new ActionPush(values);
        return true;
    }

    private static bool TryCreateDirectPushValue(
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        Avm1ConstantPoolSegment constantPool,
        out PushValue value)
    {
        switch (instruction.Kind)
        {
            case Avm1StackLirInstructionKind.PushConstant:
                return TryGetConstant(
                    stackLir,
                    instruction,
                    constantPool,
                    out value,
                    out _);

            case Avm1StackLirInstructionKind.PushName:
                if (!instruction.Name.IsValid ||
                    instruction.Name.Value >= stackLir.Strings.Count)
                {
                    break;
                }
                value = constantPool.CreatePushValue(
                    stackLir[instruction.Name]);
                return true;

            case Avm1StackLirInstructionKind.PushRegister:
                value = PushValue.Register(instruction.Register);
                return true;

            case Avm1StackLirInstructionKind.PushTemporary:
                if (!instruction.Temporary.IsValid ||
                    instruction.Temporary.Value >=
                        stackLir.TemporaryStorages.Count)
                {
                    break;
                }

                var storage = stackLir[instruction.Temporary];
                if (storage.Kind is
                    Avm1StackLirTemporaryStorageKind.Register)
                {
                    value = PushValue.Register(storage.Register);
                    return true;
                }
                break;
        }

        value = null!;
        return false;
    }

    private static long GetPushValuePayloadLength(
        PushValue value,
        Encoding encoding) =>
        value switch
        {
            PushValue.PushValueUndefined or
            PushValue.PushValueNull => 1,
            PushValue.PushValueBoolean or
            PushValue.PushValueRegister or
            PushValue.PushValueConstant8 => 2,
            PushValue.PushValueConstant16 => 3,
            PushValue.PushValueFloat or
            PushValue.PushValueInteger => 5,
            PushValue.PushValueDouble => 9,
            PushValue.PushValueString text =>
                (long)encoding.GetByteCount(text.Value) + 2,
            _ => long.MaxValue
        };

    private static bool TryCreateAction(
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        Avm1ConstantPoolSegment constantPool,
        out Avm1Action action,
        out string error)
    {
        error = string.Empty;
        switch (instruction.Kind)
        {
            case Avm1StackLirInstructionKind.PushConstant:
                if (!TryGetConstant(
                    stackLir,
                    instruction,
                    constantPool,
                    out var value,
                    out error))
                {
                    action = null!;
                    return false;
                }
                action = new ActionPush([value]);
                return true;

            case Avm1StackLirInstructionKind.PushName:
                if (!instruction.Name.IsValid ||
                    instruction.Name.Value >= stackLir.Strings.Count)
                {
                    action = null!;
                    error = "Stack LIR name handle is invalid.";
                    return false;
                }
                action = new ActionPush([
                    constantPool.CreatePushValue(stackLir[instruction.Name])
                ]);
                return true;

            case Avm1StackLirInstructionKind.PushRegister:
                action = new ActionPush([PushValue.Register(instruction.Register)]);
                return true;

            case Avm1StackLirInstructionKind.StoreRegister:
                action = new ActionStoreRegister(instruction.Register);
                return true;

            case Avm1StackLirInstructionKind.SetTarget:
                if (!instruction.Name.IsValid ||
                    instruction.Name.Value >= stackLir.Strings.Count)
                {
                    action = null!;
                    error = "Stack LIR target name handle is invalid.";
                    return false;
                }
                action = new ActionSetTarget(stackLir[instruction.Name]);
                return true;

            case Avm1StackLirInstructionKind.GotoFrame2:
                action = new ActionGotoFrame2(
                    instruction.Play,
                    instruction.HasSceneBias,
                    instruction.SceneBias);
                return true;

            case Avm1StackLirInstructionKind.GotoFrame:
                if (instruction.Constant.IsValid == instruction.Name.IsValid)
                {
                    action = null!;
                    error = "Stack LIR immediate timeline transition has an invalid payload.";
                    return false;
                }
                if (instruction.Constant.IsValid)
                {
                    if (instruction.Constant.Value >= stackLir.Constants.Count ||
                        stackLir[instruction.Constant].Kind is not
                            Avm1MirConstantKind.Integer ||
                        stackLir[instruction.Constant].IntegerValue is
                            < 0 or > ushort.MaxValue)
                    {
                        action = null!;
                        error = "Stack LIR immediate frame index is invalid.";
                        return false;
                    }
                    action = new ActionGotoFrame(checked((ushort)
                        stackLir[instruction.Constant].IntegerValue));
                    return true;
                }
                if (instruction.Name.Value >= stackLir.Strings.Count)
                {
                    action = null!;
                    error = "Stack LIR frame label handle is invalid.";
                    return false;
                }
                action = new ActionGoToLabel(stackLir[instruction.Name]);
                return true;

            case Avm1StackLirInstructionKind.GetUrlImmediate:
                if (!instruction.UrlSite.IsValid ||
                    instruction.UrlSite.Value >= stackLir.UrlSites.Count)
                {
                    action = null!;
                    error = "Stack LIR URL site handle is invalid.";
                    return false;
                }
                var urlSite = stackLir[instruction.UrlSite];
                if (!urlSite.Url.IsValid ||
                    urlSite.Url.Value >= stackLir.Strings.Count ||
                    !urlSite.Target.IsValid ||
                    urlSite.Target.Value >= stackLir.Strings.Count)
                {
                    action = null!;
                    error = "Stack LIR URL site string handle is invalid.";
                    return false;
                }
                action = new ActionGetURL(
                    stackLir[urlSite.Url],
                    stackLir[urlSite.Target]);
                return true;

            case Avm1StackLirInstructionKind.GetUrl2:
                if (!instruction.UrlSite.IsValid ||
                    instruction.UrlSite.Value >= stackLir.UrlSites.Count)
                {
                    action = null!;
                    error = "Stack LIR URL request site handle is invalid.";
                    return false;
                }
                var stackUrlSite = stackLir[instruction.UrlSite];
                if (!ActionGetURL2.IsValidFlags(stackUrlSite.Flags))
                {
                    action = null!;
                    error = "Stack LIR URL request flags are invalid.";
                    return false;
                }
                action = new ActionGetURL2(stackUrlSite.Flags);
                return true;

            case Avm1StackLirInstructionKind.Action:
                return TryCreateSimpleAction(instruction.Opcode, out action, out error);

            default:
                action = null!;
                error = $"Stack LIR instruction {instruction.Kind} is not supported.";
                return false;
        }
    }

    private static bool TryGetConstant(
        Avm1StackLirMethod stackLir,
        Avm1StackLirInstruction instruction,
        Avm1ConstantPoolSegment constantPool,
        out PushValue value,
        out string error)
    {
        error = string.Empty;
        if (!instruction.Constant.IsValid ||
            instruction.Constant.Value >= stackLir.Constants.Count)
        {
            value = null!;
            error = "Stack LIR constant handle is invalid.";
            return false;
        }

        var constant = stackLir[instruction.Constant];
        switch (constant.Kind)
        {
            case Avm1MirConstantKind.Undefined:
                value = PushValue.Undefined();
                return true;
            case Avm1MirConstantKind.Null:
                value = PushValue.Null();
                return true;
            case Avm1MirConstantKind.Boolean:
                value = PushValue.Boolean(constant.BooleanValue);
                return true;
            case Avm1MirConstantKind.Integer:
                value = PushValue.Integer(constant.IntegerValue);
                return true;
            case Avm1MirConstantKind.Number:
                value = PushValue.Double(constant.NumberValue);
                return true;
            case Avm1MirConstantKind.String:
                if (!constant.StringValue.IsValid ||
                    constant.StringValue.Value >= stackLir.Strings.Count)
                {
                    value = null!;
                    error = "Stack LIR string constant is invalid.";
                    return false;
                }
                value = constantPool.CreatePushValue(
                    stackLir[constant.StringValue]);
                return true;
            default:
                value = null!;
                error = $"Stack LIR constant {constant.Kind} is not supported.";
                return false;
        }
    }

    private static bool TryCreateSimpleAction(
        ActionOpcode opcode,
        out Avm1Action action,
        out string error)
    {
        error = string.Empty;
        action = opcode switch
        {
            ActionOpcode.NextFrame => new ActionNextFrame(),
            ActionOpcode.PreviousFrame => new ActionPreviousFrame(),
            ActionOpcode.Play => new ActionPlay(),
            ActionOpcode.Stop => new ActionStop(),
            ActionOpcode.ToggleQuality => new ActionToggleQuality(),
            ActionOpcode.StopSounds => new ActionStopSounds(),
            ActionOpcode.Call => new ActionCall(),
            ActionOpcode.GetVariable => new ActionGetVariable(),
            ActionOpcode.SetVariable => new ActionSetVariable(),
            ActionOpcode.GetMember => new ActionGetMember(),
            ActionOpcode.SetMember => new ActionSetMember(),
            ActionOpcode.Delete => new ActionDelete(),
            ActionOpcode.Delete2 => new ActionDelete2(),
            ActionOpcode.Pop => new ActionPop(),
            ActionOpcode.Not => new ActionNot(),
            ActionOpcode.ToInteger => new ActionToInteger(),
            ActionOpcode.RandomNumber => new ActionRandomNumber(),
            ActionOpcode.StringLength => new ActionStringLength(),
            ActionOpcode.StringExtract => new ActionStringExtract(),
            ActionOpcode.MBStringLength => new ActionMBStringLength(),
            ActionOpcode.MBStringExtract => new ActionMBStringExtract(),
            ActionOpcode.GetProperty => new ActionGetProperty(),
            ActionOpcode.SetProperty => new ActionSetProperty(),
            ActionOpcode.CloneSprite => new ActionCloneSprite(),
            ActionOpcode.RemoveSprite => new ActionRemoveSprite(),
            ActionOpcode.StartDrag => new ActionStartDrag(),
            ActionOpcode.EndDrag => new ActionEndDrag(),
            ActionOpcode.SetTarget2 => new ActionSetTarget2(),
            ActionOpcode.CharToAscii => new ActionCharToAscii(),
            ActionOpcode.AsciiToChar => new ActionAsciiToChar(),
            ActionOpcode.MBCharToAscii => new ActionMBCharToAscii(),
            ActionOpcode.MBAsciiToChar => new ActionMBAsciiToChar(),
            ActionOpcode.Subtract => new ActionSubtract(),
            ActionOpcode.Multiply => new ActionMultiply(),
            ActionOpcode.Divide => new ActionDivide(),
            ActionOpcode.PushDuplicate => new ActionPushDuplicate(),
            ActionOpcode.StackSwap => new ActionStackSwap(),
            ActionOpcode.DefineLocal => new ActionDefineLocal(),
            ActionOpcode.DefineLocal2 => new ActionDefineLocal2(),
            ActionOpcode.CallFunction => new ActionCallFunction(),
            ActionOpcode.CallMethod => new ActionCallMethod(),
            ActionOpcode.NewObject => new ActionNewObject(),
            ActionOpcode.NewMethod => new ActionNewMethod(),
            ActionOpcode.InitArray => new ActionInitArray(),
            ActionOpcode.InitObject => new ActionInitObject(),
            ActionOpcode.Return => new ActionReturn(),
            ActionOpcode.Throw => new ActionThrow(),
            ActionOpcode.Modulo => new ActionModulo(),
            ActionOpcode.Add2 => new ActionAdd2(),
            ActionOpcode.StringAdd => new ActionStringAdd(),
            ActionOpcode.Equals2 => new ActionEquals2(),
            ActionOpcode.Less2 => new ActionLess2(),
            ActionOpcode.TypeOf => new ActionTypeOf(),
            ActionOpcode.ToNumber => new ActionToNumber(),
            ActionOpcode.ToString => new ActionToString(),
            ActionOpcode.TargetPath => new ActionTargetPath(),
            ActionOpcode.GetTime => new ActionGetTime(),
            ActionOpcode.Trace => new ActionTrace(),
            ActionOpcode.Increment => new ActionIncrement(),
            ActionOpcode.Decrement => new ActionDecrement(),
            ActionOpcode.BitAnd => new ActionBitAnd(),
            ActionOpcode.BitOr => new ActionBitOr(),
            ActionOpcode.BitXor => new ActionBitXor(),
            ActionOpcode.BitLShift => new ActionBitLShift(),
            ActionOpcode.BitRShift => new ActionBitRShift(),
            ActionOpcode.BitURShift => new ActionBitURShift(),
            ActionOpcode.InstanceOf => new ActionInstanceOf(),
            ActionOpcode.StrictEquals => new ActionStrictEquals(),
            ActionOpcode.Greater => new ActionGreater(),
            ActionOpcode.Enumerate2 => new ActionEnumerate2(),
            _ => null!
        };
        if (action is not null)
            return true;

        error = $"Stack LIR opcode {opcode} has no concrete action factory.";
        return false;
    }

    private static SourceOriginIndex GetOrigin(
        Avm1MirMethod mir,
        Avm1MirInstructionIndex instruction) =>
        instruction.IsValid && instruction.Value < mir.Instructions.Count
            ? mir[instruction].Origin
            : SourceOriginIndex.Invalid;
}

internal readonly record struct Avm1StackLirEmission(
    Avm1CodeUnit CodeUnit,
    Avm1CompiledInstructionMap[] InstructionMap,
    Avm1PendingParentBranch[] ParentBranches);

internal readonly record struct ForwardedParentBranch(
    Avm1LabelId Label,
    Avm1PendingParentBranch Branch);
