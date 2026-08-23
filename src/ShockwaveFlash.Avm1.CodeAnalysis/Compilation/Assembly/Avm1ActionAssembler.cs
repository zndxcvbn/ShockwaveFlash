using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Assembly;

public sealed class Avm1ActionAssembler
{
    private readonly List<Entry> _entries = [];
    private readonly List<Avm1Action> _actions = [];
    private readonly List<DefineFunctionTemplate> _defineFunctions = [];
    private readonly List<DefineFunction2Template> _defineFunctions2 = [];
    private readonly List<WithTemplate> _withBodies = [];
    private readonly List<TryTemplate> _tryBodies = [];
    private readonly List<ExternalBranchTemplate> _externalBranches = [];
    private readonly HashSet<int> _referencedLabels = [];
    private int _instructionCount;
    private int _labelCount;

    public int InstructionCount => _instructionCount;

    public int LabelCount => _labelCount;

    public Avm1LabelId DefineLabel() => new(_labelCount++);

    public void MarkLabel(Avm1LabelId label)
    {
        ValidateLabel(label);
        _entries.Add(Entry.CreateLabel(label));
    }

    public Avm1AssemblyInstructionIndex Emit(Avm1Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action is ActionJump or ActionIf or
            ActionWaitForFrame or ActionWaitForFrame2)
        {
            throw new ArgumentException(
                "Relocatable control-flow actions must use a symbolic label.",
                nameof(action));
        }

        var instruction = new Avm1AssemblyInstructionIndex(_instructionCount++);
        var actionIndex = _actions.Count;
        _actions.Add(action);
        _entries.Add(Entry.CreateAction(instruction, actionIndex));
        return instruction;
    }

    public Avm1AssemblyInstructionIndex EmitJump(Avm1LabelId target) =>
        EmitBranch(ActionOpcode.Jump, target);

    public Avm1AssemblyInstructionIndex EmitIf(Avm1LabelId target) =>
        EmitBranch(ActionOpcode.If, target);

    public Avm1AssemblyInstructionIndex EmitBranch(
        ActionOpcode opcode,
        Avm1LabelId target)
    {
        if (opcode is not (ActionOpcode.Jump or ActionOpcode.If))
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                opcode,
                "Only ActionJump and ActionIf use signed byte-offset relocations.");
        }

        ValidateLabel(target);
        _referencedLabels.Add(target.Value);
        var instruction = new Avm1AssemblyInstructionIndex(_instructionCount++);
        _entries.Add(Entry.CreateBranch(instruction, opcode, target));
        return instruction;
    }

    internal Avm1AssemblyInstructionIndex EmitParentBranch(
        ActionOpcode opcode,
        Avm1ActionAssembler parent,
        Avm1LabelId target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (opcode is not (ActionOpcode.Jump or ActionOpcode.If))
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                opcode,
                "Only ActionJump and ActionIf use signed byte-offset relocations.");
        }

        parent.ValidateLabel(target);
        parent._referencedLabels.Add(target.Value);
        var templateIndex = _externalBranches.Count;
        _externalBranches.Add(new ExternalBranchTemplate(
            opcode,
            parent,
            target));
        var instruction = new Avm1AssemblyInstructionIndex(_instructionCount++);
        _entries.Add(Entry.CreateExternalBranch(instruction, templateIndex));
        return instruction;
    }

    internal Avm1AssemblyInstructionIndex EmitUnboundParentBranch(
        ActionOpcode opcode)
    {
        if (opcode is not (ActionOpcode.Jump or ActionOpcode.If))
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                opcode,
                "Only ActionJump and ActionIf use signed byte-offset relocations.");
        }

        var templateIndex = _externalBranches.Count;
        _externalBranches.Add(new ExternalBranchTemplate(
            opcode,
            null,
            Avm1LabelId.Invalid));
        var instruction = new Avm1AssemblyInstructionIndex(_instructionCount++);
        _entries.Add(Entry.CreateExternalBranch(instruction, templateIndex));
        return instruction;
    }

    internal void BindParentBranch(
        Avm1AssemblyInstructionIndex instruction,
        Avm1ActionAssembler parent,
        Avm1LabelId target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!instruction.IsValid || instruction.Value >= _instructionCount)
            throw new ArgumentOutOfRangeException(nameof(instruction));

        var entryIndex = _entries.FindIndex(entry =>
            entry.Kind is EntryKind.ExternalBranch &&
            entry.Instruction == instruction);
        if (entryIndex < 0)
        {
            throw new ArgumentException(
                $"{instruction} is not an unbound parent branch.",
                nameof(instruction));
        }

        var entry = _entries[entryIndex];
        var external = _externalBranches[entry.PayloadIndex];
        if (external.Parent is not null)
        {
            throw new InvalidOperationException(
                $"{instruction} is already bound to a parent code unit.");
        }

        parent.ValidateLabel(target);
        parent._referencedLabels.Add(target.Value);
        _externalBranches[entry.PayloadIndex] = external with
        {
            Parent = parent,
            Target = target
        };
    }

    public Avm1AssemblyInstructionIndex EmitWaitForFrame(
        ushort frame,
        Avm1LabelId target) =>
        EmitSkip(ActionOpcode.WaitForFrame, frame, target);

    public Avm1AssemblyInstructionIndex EmitWaitForFrame2(
        Avm1LabelId target) =>
        EmitSkip(ActionOpcode.WaitForFrame2, frame: 0, target);

    public Avm1AssemblyInstructionIndex EmitDefineFunction(
        string name,
        IReadOnlyList<string> parameters,
        Avm1CodeUnit body)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(body);

        var parameterCopy = parameters.ToArray();
        if (parameterCopy.Any(static parameter => parameter is null))
        {
            throw new ArgumentException(
                "Function parameter names cannot be null.",
                nameof(parameters));
        }

        var templateIndex = _defineFunctions.Count;
        _defineFunctions.Add(new DefineFunctionTemplate(name, parameterCopy, body));
        return EmitNested(NestedActionKind.DefineFunction, templateIndex);
    }

    public Avm1AssemblyInstructionIndex EmitDefineFunction2(
        string name,
        byte registerCount,
        FunctionFlags flags,
        IReadOnlyList<FunctionParameter> parameters,
        Avm1CodeUnit body)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(body);

        var parameterCopy = parameters.ToArray();
        if (parameterCopy.Any(static parameter =>
            parameter is null || parameter.Name is null))
        {
            throw new ArgumentException(
                "Function parameters and their names cannot be null.",
                nameof(parameters));
        }

        var templateIndex = _defineFunctions2.Count;
        _defineFunctions2.Add(new DefineFunction2Template(
            name,
            registerCount,
            flags,
            parameterCopy,
            body));
        return EmitNested(NestedActionKind.DefineFunction2, templateIndex);
    }

    public Avm1AssemblyInstructionIndex EmitWith(Avm1CodeUnit body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var templateIndex = _withBodies.Count;
        _withBodies.Add(new WithTemplate(body));
        return EmitNested(NestedActionKind.With, templateIndex);
    }

    public Avm1AssemblyInstructionIndex EmitTry(
        TryFlags flags,
        byte catchRegister,
        string catchVariable,
        Avm1CodeUnit tryBody,
        Avm1CodeUnit? catchBody = null,
        Avm1CodeUnit? finallyBody = null)
    {
        ArgumentNullException.ThrowIfNull(catchVariable);
        ArgumentNullException.ThrowIfNull(tryBody);

        var templateIndex = _tryBodies.Count;
        _tryBodies.Add(new TryTemplate(
            flags,
            catchRegister,
            catchVariable,
            tryBody,
            catchBody,
            finallyBody));
        return EmitNested(NestedActionKind.Try, templateIndex);
    }

    public Avm1ActionBody Assemble(Avm1AssemblyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return AssembleCore(options, new Avm1AssemblyContext());
    }

    internal Avm1ActionBody AssembleCore(
        Avm1AssemblyOptions options,
        Avm1AssemblyContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        options = Avm1AssemblyContext.NormalizeOptions(options);

        var diagnostics = new List<Avm1AssemblyDiagnostic>();
        if (options.SwfVersion == 0)
        {
            diagnostics.Add(new Avm1AssemblyDiagnostic(
                "AVM1ASM000",
                Avm1CompilationDiagnosticSeverity.Error,
                Avm1AssemblyInstructionIndex.Invalid,
                -1,
                "SWF version 0 is not a valid AVM1 target."));
        }

        var labelOffsets = new int[_labelCount];
        Array.Fill(labelOffsets, -1);
        var labelActionIndices = new int[_labelCount];
        Array.Fill(labelActionIndices, -1);
        var instructionLocations = new Avm1AssemblyInstructionLocation[_instructionCount];
        var nestedBodies = new List<Avm1NestedActionBody>();

        ValidateExternalBranches(options, diagnostics);

        if (HasErrors(diagnostics))
        {
            return Failed(
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                nestedBodies,
                diagnostics);
        }

        var nestedActions = new Avm1Action?[_instructionCount];
        AssembleNestedActions(
            options,
            context,
            nestedActions,
            nestedBodies,
            diagnostics);
        if (HasErrors(diagnostics))
        {
            return Failed(
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                nestedBodies,
                diagnostics);
        }

        var byteOffset = 0;
        var actionIndex = 0;
        foreach (var entry in _entries)
        {
            if (entry.Kind is EntryKind.Label)
            {
                if (labelOffsets[entry.Label.Value] >= 0)
                {
                    diagnostics.Add(new Avm1AssemblyDiagnostic(
                        "AVM1ASM002",
                        Avm1CompilationDiagnosticSeverity.Error,
                        Avm1AssemblyInstructionIndex.Invalid,
                        byteOffset,
                        $"{entry.Label} is marked more than once."));
                }
                else
                {
                    labelOffsets[entry.Label.Value] = byteOffset;
                    labelActionIndices[entry.Label.Value] = actionIndex;
                }
                continue;
            }

            var action = entry.Kind switch
            {
                EntryKind.Action => _actions[entry.PayloadIndex],
                EntryKind.Nested => GetNestedAction(entry, nestedActions),
                EntryKind.Branch => CreateBranch(entry.Opcode, branchOffset: 0),
                EntryKind.ExternalBranch => CreateBranch(
                    _externalBranches[entry.PayloadIndex].Opcode,
                    branchOffset: 0),
                EntryKind.Skip => CreateSkip(entry.Opcode, entry.Frame, skipCount: 0),
                _ => throw new InvalidOperationException(
                    $"Unsupported assembly entry kind {entry.Kind}.")
            };
            int byteLength;
            try
            {
                byteLength = Avm1Action.EncodeCollection([action], options.SwfVersion).Length;
            }
            catch (Exception exception) when (IsRecoverableCodecException(exception))
            {
                diagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM004",
                    Avm1CompilationDiagnosticSeverity.Error,
                    entry.Instruction,
                    byteOffset,
                    $"Cannot encode {action.Opcode}: {exception.Message}"));
                byteLength = 0;
            }

            instructionLocations[entry.Instruction.Value] =
                new Avm1AssemblyInstructionLocation(
                    entry.Instruction,
                    actionIndex++,
                    byteOffset,
                    byteLength);
            try
            {
                byteOffset = checked(byteOffset + byteLength);
            }
            catch (OverflowException)
            {
                diagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM005",
                    Avm1CompilationDiagnosticSeverity.Error,
                    entry.Instruction,
                    byteOffset,
                    "The assembled code unit exceeds the supported 2 GB layout range."));
            }
        }

        foreach (var label in _referencedLabels)
        {
            if (labelOffsets[label] < 0)
            {
                diagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM001",
                    Avm1CompilationDiagnosticSeverity.Error,
                    FindFirstReference(new Avm1LabelId(label)),
                    -1,
                    $"label{label} is referenced but never marked."));
            }
        }

        if (HasErrors(diagnostics))
        {
            return Failed(
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                nestedBodies,
                diagnostics);
        }

        var logicalActions = new Avm1Action[_instructionCount];
        var controls = new Avm1LogicalControl[_instructionCount];
        Array.Fill(controls, Avm1LogicalControl.None);
        foreach (var entry in _entries)
        {
            if (entry.Kind is EntryKind.Label)
                continue;

            switch (entry.Kind)
            {
                case EntryKind.Action:
                    logicalActions[entry.Instruction.Value] =
                        _actions[entry.PayloadIndex];
                    break;
                case EntryKind.Nested:
                    logicalActions[entry.Instruction.Value] =
                        GetNestedAction(entry, nestedActions);
                    break;
                case EntryKind.Branch:
                    logicalActions[entry.Instruction.Value] =
                        CreateBranch(entry.Opcode, branchOffset: 0);
                    controls[entry.Instruction.Value] =
                        Avm1LogicalControl.CreateBranch(entry.Opcode, entry.Label);
                    break;
                case EntryKind.ExternalBranch:
                    logicalActions[entry.Instruction.Value] = CreateBranch(
                        _externalBranches[entry.PayloadIndex].Opcode,
                        branchOffset: 0);
                    break;
                case EntryKind.Skip:
                    logicalActions[entry.Instruction.Value] =
                        CreateSkip(entry.Opcode, entry.Frame, skipCount: 0);
                    controls[entry.Instruction.Value] =
                        Avm1LogicalControl.CreateSkip(
                            entry.Opcode,
                            entry.Label,
                            entry.Frame);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported assembly entry kind {entry.Kind}.");
            }
        }

        var physicalLayout = Avm1PhysicalLayout.Build(
            logicalActions,
            controls,
            labelActionIndices,
            options.SwfVersion,
            diagnostics);
        if (!physicalLayout.Succeeded)
        {
            return Failed(
                physicalLayout.LabelOffsets,
                physicalLayout.LabelActionIndices,
                physicalLayout.InstructionLocations,
                nestedBodies,
                diagnostics);
        }

        var resolvedActions = physicalLayout.Actions;
        labelOffsets = physicalLayout.LabelOffsets;
        labelActionIndices = physicalLayout.LabelActionIndices;
        instructionLocations = physicalLayout.InstructionLocations;
        var actionLocations = physicalLayout.ActionLocations;
        var externalBranchRelocations = BuildExternalBranchRelocations(
            instructionLocations);

        ResolveNestedExternalBranches(
            resolvedActions,
            instructionLocations,
            labelOffsets,
            nestedBodies,
            options.SwfVersion,
            diagnostics);
        if (HasErrors(diagnostics))
        {
            return Failed(
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                nestedBodies,
                diagnostics);
        }

        ResolveNestedBodyOffsets(
            resolvedActions,
            instructionLocations,
            nestedBodies);

        ReadOnlyMemory<byte> bytes;
        try
        {
            bytes = Avm1Action.EncodeCollection(resolvedActions, options.SwfVersion);
        }
        catch (Exception exception) when (IsRecoverableCodecException(exception))
        {
            diagnostics.Add(new Avm1AssemblyDiagnostic(
                "AVM1ASM004",
                Avm1CompilationDiagnosticSeverity.Error,
                Avm1AssemblyInstructionIndex.Invalid,
                -1,
                $"Cannot encode the action body: {exception.Message}"));
            return Failed(
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                nestedBodies,
                diagnostics);
        }

        var verification = Avm1BytecodeVerifier.Verify(
            bytes,
            new Avm1BytecodeVerificationOptions(options.SwfVersion)
            {
                VerifyDataFlow = options.VerifyDataFlow,
                RequireEndAction = options.RequireEndAction,
                RequireEmptyStackAtExit = options.RequireEmptyStackAtExit,
                InitialConstantPoolCount = options.InitialConstantPoolCount,
                CodeUnitContext = options.CodeUnitContext
            });
        foreach (var diagnostic in verification.Diagnostics)
        {
            var instruction = diagnostic.ActionIndex >= 0 &&
                diagnostic.ActionIndex < actionLocations.Length
                ? actionLocations[diagnostic.ActionIndex].Owner
                : Avm1AssemblyInstructionIndex.Invalid;
            diagnostics.Add(new Avm1AssemblyDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                instruction,
                diagnostic.ByteOffset,
                diagnostic.Message));
        }

        if (!verification.Succeeded)
        {
            return Failed(
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                nestedBodies,
                diagnostics,
                verification.MaximumStackDepth,
                verification.MaximumStackDepthIsExact);
        }

        return new Avm1ActionBody(
            bytes,
            verification.Actions.ToArray(),
            verification.ActionOffsets.ToArray(),
            labelOffsets,
            labelActionIndices,
            instructionLocations,
            actionLocations,
            nestedBodies.ToArray(),
            externalBranchRelocations,
            verification.MaximumStackDepth,
            verification.MaximumStackDepthIsExact,
            diagnostics.ToArray());
    }

    private Avm1AssemblyInstructionIndex EmitNested(
        NestedActionKind kind,
        int templateIndex)
    {
        var instruction = new Avm1AssemblyInstructionIndex(_instructionCount++);
        _entries.Add(Entry.CreateNested(instruction, kind, templateIndex));
        return instruction;
    }

    private Avm1AssemblyInstructionIndex EmitSkip(
        ActionOpcode opcode,
        ushort frame,
        Avm1LabelId target)
    {
        if (opcode is not (ActionOpcode.WaitForFrame or ActionOpcode.WaitForFrame2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                opcode,
                "Only wait-for-frame actions use action-count skip relocations.");
        }

        ValidateLabel(target);
        _referencedLabels.Add(target.Value);
        var instruction = new Avm1AssemblyInstructionIndex(_instructionCount++);
        _entries.Add(Entry.CreateSkip(instruction, opcode, frame, target));
        return instruction;
    }

    private void AssembleNestedActions(
        Avm1AssemblyOptions options,
        Avm1AssemblyContext context,
        Avm1Action?[] nestedActions,
        List<Avm1NestedActionBody> nestedBodies,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        var nestedOptions = options with
        {
            RequireEndAction = false,
            RequireEmptyStackAtExit = true
        };
        var parentContext = options.CodeUnitContext!.Value;
        var poolEntries = AnalyzeConstantPoolEntries(parentContext.ConstantPool);

        foreach (var entry in _entries)
        {
            if (entry.Kind is not EntryKind.Nested)
                continue;

            var entryPool = poolEntries[entry.Instruction.Value] ??
                Avm1ConstantPoolContext.Unknown;

            switch (entry.NestedKind)
            {
                case NestedActionKind.DefineFunction:
                    {
                        var template = _defineFunctions[entry.PayloadIndex];
                        var body = AssembleNestedBody(
                            entry.Instruction,
                            Avm1NestedCodeUnitKind.DefineFunctionBody,
                            template.Body,
                            nestedOptions with
                            {
                                CodeUnitContext = Avm1CodeUnitContext.Legacy(entryPool),
                                ExternalBranchParent = null
                            },
                            context,
                            nestedBodies,
                            diagnostics);
                        if (body.Succeeded)
                        {
                            nestedActions[entry.Instruction.Value] =
                                new ActionDefineFunction(
                                    template.Name,
                                    template.Parameters,
                                    body.Bytes);
                        }
                        break;
                    }
                case NestedActionKind.DefineFunction2:
                    {
                        var template = _defineFunctions2[entry.PayloadIndex];
                        var body = AssembleNestedBody(
                            entry.Instruction,
                            Avm1NestedCodeUnitKind.DefineFunction2Body,
                            template.Body,
                            nestedOptions with
                            {
                                CodeUnitContext = Avm1CodeUnitContext.DefineFunction2(
                                    template.RegisterCount,
                                    entryPool),
                                ExternalBranchParent = null
                            },
                            context,
                            nestedBodies,
                            diagnostics);
                        if (body.Succeeded)
                        {
                            nestedActions[entry.Instruction.Value] =
                                new ActionDefineFunction2(
                                    template.Name,
                                    template.RegisterCount,
                                    template.Flags,
                                    template.Parameters,
                                    body.Bytes);
                        }
                        break;
                    }
                case NestedActionKind.With:
                    {
                        var template = _withBodies[entry.PayloadIndex];
                        var body = AssembleNestedBody(
                            entry.Instruction,
                            Avm1NestedCodeUnitKind.WithBody,
                            template.Body,
                            nestedOptions with
                            {
                                CodeUnitContext = parentContext.WithConstantPool(entryPool),
                                ExternalBranchParent = null
                            },
                            context,
                            nestedBodies,
                            diagnostics);
                        if (body.Succeeded)
                        {
                            nestedActions[entry.Instruction.Value] =
                                new ActionWith(body.Bytes);
                        }
                        break;
                    }
                case NestedActionKind.Try:
                    AssembleTry(
                        entry,
                        nestedOptions with
                        {
                            CodeUnitContext = parentContext.WithConstantPool(entryPool)
                        },
                        context,
                        nestedActions,
                        nestedBodies,
                        diagnostics);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported nested action kind {entry.NestedKind}.");
            }
        }
    }

    private Avm1ConstantPoolContext?[] AnalyzeConstantPoolEntries(
        Avm1ConstantPoolContext initialPool)
    {
        var result = new Avm1ConstantPoolContext?[_instructionCount];
        if (_instructionCount == 0)
            return result;

        var instructions = new Entry[_instructionCount];
        var labelInstructions = new int[_labelCount];
        Array.Fill(labelInstructions, -1);
        var nextInstruction = 0;
        foreach (var entry in _entries)
        {
            if (entry.Kind is EntryKind.Label)
            {
                if (labelInstructions[entry.Label.Value] < 0)
                    labelInstructions[entry.Label.Value] = nextInstruction;
                continue;
            }

            instructions[entry.Instruction.Value] = entry;
            nextInstruction++;
        }

        result[0] = initialPool;
        var worklist = new Queue<int>();
        var queued = new bool[_instructionCount];
        worklist.Enqueue(0);
        queued[0] = true;

        while (worklist.Count > 0)
        {
            var instructionIndex = worklist.Dequeue();
            queued[instructionIndex] = false;
            var entry = instructions[instructionIndex];
            var exitPool = result[instructionIndex]!.Value;
            if (entry.Kind is EntryKind.Action &&
                _actions[entry.PayloadIndex] is ActionConstantPool constantPool)
            {
                exitPool = Avm1ConstantPoolContext.Available(
                    constantPool.Constants.Count);
            }

            if (entry.Kind is EntryKind.Action &&
                _actions[entry.PayloadIndex].Opcode is
                    ActionOpcode.End or ActionOpcode.Return or ActionOpcode.Throw)
            {
                continue;
            }

            if (entry.Kind is EntryKind.Branch)
            {
                Propagate(labelInstructions[entry.Label.Value], exitPool);
                if (entry.Opcode is ActionOpcode.If)
                    Propagate(instructionIndex + 1, exitPool);
                continue;
            }

            if (entry.Kind is EntryKind.ExternalBranch)
            {
                var external = _externalBranches[entry.PayloadIndex];
                if (external.Opcode is ActionOpcode.If)
                    Propagate(instructionIndex + 1, exitPool);
                continue;
            }

            if (entry.Kind is EntryKind.Skip)
            {
                Propagate(labelInstructions[entry.Label.Value], exitPool);
                Propagate(instructionIndex + 1, exitPool);
                continue;
            }

            Propagate(instructionIndex + 1, exitPool);
        }

        return result;

        void Propagate(int target, Avm1ConstantPoolContext incoming)
        {
            if (target < 0 || target >= _instructionCount)
                return;

            var existing = result[target];
            if (!existing.HasValue)
            {
                result[target] = incoming;
                Enqueue(target);
                return;
            }

            var merged = existing.Value.Merge(incoming);
            if (merged == existing.Value)
                return;
            result[target] = merged;
            Enqueue(target);
        }

        void Enqueue(int instructionIndex)
        {
            if (queued[instructionIndex])
                return;
            queued[instructionIndex] = true;
            worklist.Enqueue(instructionIndex);
        }
    }

    private void AssembleTry(
        Entry entry,
        Avm1AssemblyOptions nestedOptions,
        Avm1AssemblyContext context,
        Avm1Action?[] nestedActions,
        List<Avm1NestedActionBody> nestedBodies,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        var template = _tryBodies[entry.PayloadIndex];
        if (!ValidateTryTemplate(template, entry.Instruction, diagnostics))
            return;

        nestedOptions = nestedOptions with
        {
            ExternalBranchParent = this
        };

        var tryBody = AssembleNestedBody(
            entry.Instruction,
            Avm1NestedCodeUnitKind.TryBody,
            template.TryBody,
            nestedOptions,
            context,
            nestedBodies,
            diagnostics);
        Avm1ActionBody? catchBody = null;
        if (template.CatchBody is not null)
        {
            catchBody = AssembleNestedBody(
                entry.Instruction,
                Avm1NestedCodeUnitKind.CatchBody,
                template.CatchBody,
                nestedOptions,
                context,
                nestedBodies,
                diagnostics);
        }

        Avm1ActionBody? finallyBody = null;
        if (template.FinallyBody is not null)
        {
            finallyBody = AssembleNestedBody(
                entry.Instruction,
                Avm1NestedCodeUnitKind.FinallyBody,
                template.FinallyBody,
                nestedOptions,
                context,
                nestedBodies,
                diagnostics);
        }

        if (!tryBody.Succeeded ||
            catchBody is { Succeeded: false } ||
            finallyBody is { Succeeded: false })
        {
            return;
        }

        nestedActions[entry.Instruction.Value] = new ActionTry(
            template.Flags,
            template.CatchRegister,
            template.CatchVariable,
            tryBody.Bytes,
            catchBody?.Bytes ?? ReadOnlyMemory<byte>.Empty,
            finallyBody?.Bytes ?? ReadOnlyMemory<byte>.Empty);
    }

    private static Avm1ActionBody AssembleNestedBody(
        Avm1AssemblyInstructionIndex owner,
        Avm1NestedCodeUnitKind kind,
        Avm1CodeUnit codeUnit,
        Avm1AssemblyOptions options,
        Avm1AssemblyContext context,
        List<Avm1NestedActionBody> nestedBodies,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        var body = context.Assemble(codeUnit, options);
        nestedBodies.Add(new Avm1NestedActionBody(owner, kind, body)
        {
            AssemblyOptions = options
        });
        foreach (var diagnostic in body.Diagnostics)
        {
            diagnostics.Add(new Avm1AssemblyDiagnostic(
                "AVM1ASM010",
                diagnostic.Severity,
                owner,
                -1,
                $"Invalid {GetNestedBodyName(kind)} code unit " +
                $"({diagnostic.Code}): {diagnostic.Message}"));
        }
        return body;
    }

    private static bool ValidateTryTemplate(
        TryTemplate template,
        Avm1AssemblyInstructionIndex instruction,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        const TryFlags knownFlags = TryFlags.CatchBlock |
            TryFlags.FinallyBlock |
            TryFlags.CatchInRegister;
        var hasCatch = template.Flags.HasFlag(TryFlags.CatchBlock);
        var hasFinally = template.Flags.HasFlag(TryFlags.FinallyBlock);
        var catchInRegister = template.Flags.HasFlag(TryFlags.CatchInRegister);
        var issue = (template.Flags & ~knownFlags) != 0
            ? "The try action contains unknown flag bits."
            : hasCatch != (template.CatchBody is not null)
                ? "CatchBlock must match the presence of a catch code unit."
                : hasFinally != (template.FinallyBody is not null)
                    ? "FinallyBlock must match the presence of a finally code unit."
                    : catchInRegister && !hasCatch
                        ? "CatchInRegister requires CatchBlock."
                        : catchInRegister && template.CatchVariable.Length != 0
                            ? "A register catch must not also specify a catch variable."
                            : !catchInRegister && template.CatchRegister != 0
                                ? "A named catch must not also specify a catch register."
                                : null;
        if (issue is null)
            return true;

        diagnostics.Add(new Avm1AssemblyDiagnostic(
            "AVM1ASM012",
            Avm1CompilationDiagnosticSeverity.Error,
            instruction,
            -1,
            issue));
        return false;
    }

    private void ResolveNestedExternalBranches(
        Avm1Action[] resolvedActions,
        Avm1AssemblyInstructionLocation[] instructionLocations,
        int[] labelOffsets,
        List<Avm1NestedActionBody> nestedBodies,
        byte swfVersion,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        foreach (var entry in _entries)
        {
            if (entry.Kind is not EntryKind.Nested ||
                entry.NestedKind is not NestedActionKind.Try)
            {
                continue;
            }

            var ownerLocation = instructionLocations[entry.Instruction.Value];
            if (resolvedActions[ownerLocation.ActionIndex] is not ActionTry actionTry)
            {
                throw new InvalidOperationException(
                    "A try template did not produce an ActionTry.");
            }

            var nestedLength = checked(
                actionTry.TryBody.Length +
                actionTry.CatchBody.Length +
                actionTry.FinallyBody.Length);
            var tryBase = checked(
                ownerLocation.ByteOffset + ownerLocation.ByteLength - nestedLength);
            var catchBase = checked(tryBase + actionTry.TryBody.Length);
            var finallyBase = checked(catchBase + actionTry.CatchBody.Length);

            var tryBody = ResolveBody(
                Avm1NestedCodeUnitKind.TryBody,
                tryBase,
                actionTry.TryBody);
            var catchBody = ResolveBody(
                Avm1NestedCodeUnitKind.CatchBody,
                catchBase,
                actionTry.CatchBody);
            var finallyBody = ResolveBody(
                Avm1NestedCodeUnitKind.FinallyBody,
                finallyBase,
                actionTry.FinallyBody);

            if (HasErrors(diagnostics))
                continue;

            resolvedActions[ownerLocation.ActionIndex] = new ActionTry(
                actionTry.Flags,
                actionTry.CatchRegister,
                actionTry.CatchVariable,
                tryBody,
                catchBody,
                finallyBody);

            ReadOnlyMemory<byte> ResolveBody(
                Avm1NestedCodeUnitKind kind,
                int bodyBase,
                ReadOnlyMemory<byte> fallback)
            {
                var nestedIndex = nestedBodies.FindIndex(nested =>
                    nested.Owner == entry.Instruction && nested.Kind == kind);
                if (nestedIndex < 0)
                    return fallback;

                var nested = nestedBodies[nestedIndex];
                if (nested.Body.ExternalBranchRelocations.Count == 0)
                    return nested.Body.Bytes;

                var resolved = ResolveExternalBranches(
                    entry.Instruction,
                    kind,
                    nested,
                    bodyBase,
                    labelOffsets,
                    swfVersion,
                    diagnostics);
                nestedBodies[nestedIndex] = nested with { Body = resolved };
                return resolved.Bytes;
            }
        }
    }

    private Avm1ActionBody ResolveExternalBranches(
        Avm1AssemblyInstructionIndex owner,
        Avm1NestedCodeUnitKind kind,
        Avm1NestedActionBody nested,
        int bodyBase,
        int[] labelOffsets,
        byte swfVersion,
        List<Avm1AssemblyDiagnostic> parentDiagnostics)
    {
        var actions = nested.Body.Actions.ToArray();
        var externalExitOffsets = new HashSet<int>();
        foreach (var relocation in nested.Body.ExternalBranchRelocations)
        {
            if (!ReferenceEquals(relocation.TargetAssembler, this) ||
                relocation.Target.Value < 0 ||
                relocation.Target.Value >= labelOffsets.Length ||
                labelOffsets[relocation.Target.Value] < 0)
            {
                parentDiagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM016",
                    Avm1CompilationDiagnosticSeverity.Error,
                    owner,
                    bodyBase + relocation.ByteOffset,
                    $"The {GetNestedBodyName(kind)} branch {relocation.Instruction} " +
                    "does not target a marked label in its direct parent."));
                continue;
            }

            var targetOffset = labelOffsets[relocation.Target.Value];
            var branchEnd = checked(
                bodyBase + relocation.ByteOffset + relocation.ByteLength);
            var displacement = (long)targetOffset - branchEnd;
            if (displacement is < short.MinValue or > short.MaxValue)
            {
                parentDiagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM017",
                    Avm1CompilationDiagnosticSeverity.Error,
                    owner,
                    bodyBase + relocation.ByteOffset,
                    $"The {GetNestedBodyName(kind)} branch {relocation.Instruction} " +
                    $"requires parent displacement {displacement}, outside the " +
                    "signed 16-bit range."));
                continue;
            }

            actions[relocation.ActionIndex] = CreateBranch(
                relocation.Opcode,
                (short)displacement);
            externalExitOffsets.Add(checked(targetOffset - bodyBase));
        }

        if (HasErrors(parentDiagnostics))
            return nested.Body;

        ReadOnlyMemory<byte> bytes;
        try
        {
            bytes = Avm1Action.EncodeCollection(actions, swfVersion);
        }
        catch (Exception exception) when (IsRecoverableCodecException(exception))
        {
            parentDiagnostics.Add(new Avm1AssemblyDiagnostic(
                "AVM1ASM004",
                Avm1CompilationDiagnosticSeverity.Error,
                owner,
                bodyBase,
                $"Cannot encode resolved {GetNestedBodyName(kind)} body: " +
                exception.Message));
            return nested.Body;
        }

        if (bytes.Length != nested.Body.Bytes.Length)
        {
            throw new InvalidOperationException(
                "Resolving a fixed-size branch changed the nested body length.");
        }

        var options = nested.AssemblyOptions ??
            throw new InvalidOperationException(
                "A nested assembly result has no verification options.");
        var verification = Avm1BytecodeVerifier.Verify(
            bytes,
            new Avm1BytecodeVerificationOptions(swfVersion)
            {
                VerifyDataFlow = options.VerifyDataFlow,
                RequireEndAction = options.RequireEndAction,
                RequireEmptyStackAtExit = options.RequireEmptyStackAtExit,
                InitialConstantPoolCount = options.InitialConstantPoolCount,
                CodeUnitContext = options.CodeUnitContext,
                ExternalBranchExitOffsets = externalExitOffsets.Order().ToArray()
            });
        var childDiagnostics = verification.Diagnostics.Select(diagnostic =>
        {
            var instruction = diagnostic.ActionIndex >= 0 &&
                diagnostic.ActionIndex < nested.Body.ActionLocations.Count
                ? nested.Body.ActionLocations[diagnostic.ActionIndex].Owner
                : Avm1AssemblyInstructionIndex.Invalid;
            return new Avm1AssemblyDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                instruction,
                diagnostic.ByteOffset,
                diagnostic.Message);
        }).ToArray();
        foreach (var diagnostic in childDiagnostics)
        {
            parentDiagnostics.Add(new Avm1AssemblyDiagnostic(
                "AVM1ASM010",
                diagnostic.Severity,
                owner,
                bodyBase + diagnostic.ByteOffset,
                $"Invalid {GetNestedBodyName(kind)} code unit " +
                $"({diagnostic.Code}): {diagnostic.Message}"));
        }

        return new Avm1ActionBody(
            bytes,
            verification.Actions.ToArray(),
            verification.ActionOffsets.ToArray(),
            nested.Body.LabelOffsets.ToArray(),
            nested.Body.LabelActionIndices.ToArray(),
            nested.Body.InstructionLocations.ToArray(),
            nested.Body.ActionLocations.ToArray(),
            nested.Body.NestedBodies.ToArray(),
            [],
            verification.MaximumStackDepth,
            verification.MaximumStackDepthIsExact,
            childDiagnostics);
    }

    private static string GetNestedBodyName(Avm1NestedCodeUnitKind kind) =>
        kind switch
        {
            Avm1NestedCodeUnitKind.DefineFunctionBody => "function",
            Avm1NestedCodeUnitKind.DefineFunction2Body => "function2",
            Avm1NestedCodeUnitKind.WithBody => "with",
            Avm1NestedCodeUnitKind.TryBody => "try",
            Avm1NestedCodeUnitKind.CatchBody => "catch",
            Avm1NestedCodeUnitKind.FinallyBody => "finally",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static void ResolveNestedBodyOffsets(
        Avm1Action[] actions,
        Avm1AssemblyInstructionLocation[] instructionLocations,
        List<Avm1NestedActionBody> nestedBodies)
    {
        for (var i = 0; i < nestedBodies.Count; i++)
        {
            var nested = nestedBodies[i];
            var owner = instructionLocations[nested.Owner.Value];
            var action = actions[owner.ActionIndex];
            var offset = nested.Kind switch
            {
                Avm1NestedCodeUnitKind.DefineFunctionBody
                    when action is ActionDefineFunction function =>
                    checked(owner.ByteOffset + owner.ByteLength -
                        function.Body.Length),
                Avm1NestedCodeUnitKind.DefineFunction2Body
                    when action is ActionDefineFunction2 function =>
                    checked(owner.ByteOffset + owner.ByteLength -
                        function.Body.Length),
                Avm1NestedCodeUnitKind.WithBody
                    when action is ActionWith actionWith =>
                    checked(owner.ByteOffset + owner.ByteLength -
                        actionWith.Body.Length),
                Avm1NestedCodeUnitKind.TryBody
                    when action is ActionTry actionTry =>
                    GetTryBodyOffset(owner, actionTry),
                Avm1NestedCodeUnitKind.CatchBody
                    when action is ActionTry actionTry =>
                    checked(GetTryBodyOffset(owner, actionTry) +
                        actionTry.TryBody.Length),
                Avm1NestedCodeUnitKind.FinallyBody
                    when action is ActionTry actionTry =>
                    checked(GetTryBodyOffset(owner, actionTry) +
                        actionTry.TryBody.Length + actionTry.CatchBody.Length),
                _ => throw new InvalidOperationException(
                    $"Nested {nested.Kind} body does not match its owner action.")
            };
            nestedBodies[i] = nested with { ByteOffset = offset };
        }
    }

    private static int GetTryBodyOffset(
        Avm1AssemblyInstructionLocation owner,
        ActionTry action) =>
        checked(owner.ByteOffset + owner.ByteLength - action.TryBody.Length -
            action.CatchBody.Length - action.FinallyBody.Length);

    private static Avm1Action GetNestedAction(
        Entry entry,
        Avm1Action?[] nestedActions) =>
        nestedActions[entry.Instruction.Value] ??
        throw new InvalidOperationException(
            $"Nested action {entry.Instruction} was not assembled.");

    private void ValidateExternalBranches(
        Avm1AssemblyOptions options,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        foreach (var entry in _entries)
        {
            if (entry.Kind is not EntryKind.ExternalBranch)
                continue;

            var external = _externalBranches[entry.PayloadIndex];
            if (external.Parent is not null &&
                ReferenceEquals(external.Parent, options.ExternalBranchParent))
                continue;

            diagnostics.Add(new Avm1AssemblyDiagnostic(
                "AVM1ASM016",
                Avm1CompilationDiagnosticSeverity.Error,
                entry.Instruction,
                -1,
                "A parent branch is valid only in a directly nested try, catch, " +
                "or finally code unit assembled by its target parent."));
        }
    }

    private Avm1ExternalBranchRelocation[] BuildExternalBranchRelocations(
        Avm1AssemblyInstructionLocation[] instructionLocations)
    {
        if (_externalBranches.Count == 0)
            return [];

        var relocations = new List<Avm1ExternalBranchRelocation>(
            _externalBranches.Count);
        foreach (var entry in _entries)
        {
            if (entry.Kind is not EntryKind.ExternalBranch)
                continue;

            var external = _externalBranches[entry.PayloadIndex];
            var location = instructionLocations[entry.Instruction.Value];
            relocations.Add(new Avm1ExternalBranchRelocation(
                entry.Instruction,
                location.ActionIndex,
                location.ByteOffset,
                location.ByteLength,
                external.Opcode,
                external.Parent ?? throw new InvalidOperationException(
                    "An unbound parent branch reached physical layout."),
                external.Target));
        }
        return relocations.ToArray();
    }

    private void ValidateLabel(Avm1LabelId label)
    {
        if (!label.IsValid || label.Value >= _labelCount)
            throw new ArgumentOutOfRangeException(nameof(label));
    }

    private Avm1AssemblyInstructionIndex FindFirstReference(Avm1LabelId label)
    {
        foreach (var entry in _entries)
        {
            if (entry.Kind is EntryKind.Branch or EntryKind.Skip &&
                entry.Label == label)
            {
                return entry.Instruction;
            }
        }
        return Avm1AssemblyInstructionIndex.Invalid;
    }

    private static Avm1Action CreateBranch(ActionOpcode opcode, short branchOffset) =>
        opcode switch
        {
            ActionOpcode.Jump => new ActionJump(branchOffset),
            ActionOpcode.If => new ActionIf(branchOffset),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode))
        };

    private static Avm1Action CreateSkip(
        ActionOpcode opcode,
        ushort frame,
        byte skipCount) =>
        opcode switch
        {
            ActionOpcode.WaitForFrame => new ActionWaitForFrame(frame, skipCount),
            ActionOpcode.WaitForFrame2 => new ActionWaitForFrame2(skipCount),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode))
        };

    private static bool HasErrors(IEnumerable<Avm1AssemblyDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    private static Avm1ActionBody Failed(
        int[] labelOffsets,
        int[] labelActionIndices,
        Avm1AssemblyInstructionLocation[] instructionLocations,
        List<Avm1NestedActionBody> nestedBodies,
        List<Avm1AssemblyDiagnostic> diagnostics,
        int maximumStackDepth = 0,
        bool maximumStackDepthIsExact = true) =>
        new(
            ReadOnlyMemory<byte>.Empty,
            [],
            [],
            labelOffsets,
            labelActionIndices,
            instructionLocations,
            [],
            nestedBodies.ToArray(),
            [],
            maximumStackDepth,
            maximumStackDepthIsExact,
            diagnostics.ToArray());

    private static bool IsRecoverableCodecException(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or
            OperationCanceledException);

    private enum EntryKind : byte
    {
        Action,
        Label,
        Branch,
        ExternalBranch,
        Skip,
        Nested
    }

    private enum NestedActionKind : byte
    {
        DefineFunction,
        DefineFunction2,
        With,
        Try
    }

    private readonly record struct Entry(
        EntryKind Kind,
        Avm1AssemblyInstructionIndex Instruction,
        int PayloadIndex,
        Avm1LabelId Label,
        ActionOpcode Opcode,
        ushort Frame,
        NestedActionKind NestedKind)
    {
        public static Entry CreateAction(
            Avm1AssemblyInstructionIndex instruction,
            int actionIndex) =>
            new(
                EntryKind.Action,
                instruction,
                actionIndex,
                Avm1LabelId.Invalid,
                default,
                0,
                default);

        public static Entry CreateLabel(Avm1LabelId label) =>
            new(
                EntryKind.Label,
                Avm1AssemblyInstructionIndex.Invalid,
                -1,
                label,
                default,
                0,
                default);

        public static Entry CreateBranch(
            Avm1AssemblyInstructionIndex instruction,
            ActionOpcode opcode,
            Avm1LabelId target) =>
            new(EntryKind.Branch, instruction, -1, target, opcode, 0, default);

        public static Entry CreateExternalBranch(
            Avm1AssemblyInstructionIndex instruction,
            int templateIndex) =>
            new(
                EntryKind.ExternalBranch,
                instruction,
                templateIndex,
                Avm1LabelId.Invalid,
                default,
                0,
                default);

        public static Entry CreateSkip(
            Avm1AssemblyInstructionIndex instruction,
            ActionOpcode opcode,
            ushort frame,
            Avm1LabelId target) =>
            new(EntryKind.Skip, instruction, -1, target, opcode, frame, default);

        public static Entry CreateNested(
            Avm1AssemblyInstructionIndex instruction,
            NestedActionKind kind,
            int templateIndex) =>
            new(
                EntryKind.Nested,
                instruction,
                templateIndex,
                Avm1LabelId.Invalid,
                default,
                0,
                kind);
    }

    private readonly record struct DefineFunctionTemplate(
        string Name,
        string[] Parameters,
        Avm1CodeUnit Body);

    private readonly record struct DefineFunction2Template(
        string Name,
        byte RegisterCount,
        FunctionFlags Flags,
        FunctionParameter[] Parameters,
        Avm1CodeUnit Body);

    private readonly record struct WithTemplate(Avm1CodeUnit Body);

    private readonly record struct ExternalBranchTemplate(
        ActionOpcode Opcode,
        Avm1ActionAssembler? Parent,
        Avm1LabelId Target);

    private readonly record struct TryTemplate(
        TryFlags Flags,
        byte CatchRegister,
        string CatchVariable,
        Avm1CodeUnit TryBody,
        Avm1CodeUnit? CatchBody,
        Avm1CodeUnit? FinallyBody);
}

internal sealed class Avm1AssemblyContext
{
    private readonly Dictionary<AssemblyCacheKey, Avm1ActionBody> _cache = [];
    private readonly HashSet<Avm1CodeUnit> _active =
        new(ReferenceEqualityComparer.Instance);

    public Avm1ActionBody Assemble(
        Avm1CodeUnit codeUnit,
        Avm1AssemblyOptions options)
    {
        ArgumentNullException.ThrowIfNull(codeUnit);
        ArgumentNullException.ThrowIfNull(options);

        options = NormalizeOptions(options);
        var cacheKey = new AssemblyCacheKey(codeUnit, options);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;
        if (!_active.Add(codeUnit))
            return CyclicReference(codeUnit);

        Avm1ActionBody result;
        try
        {
            result = codeUnit.Assembler.AssembleCore(options, this);
        }
        finally
        {
            _active.Remove(codeUnit);
        }

        _cache.Add(cacheKey, result);
        return result;
    }

    internal static Avm1AssemblyOptions NormalizeOptions(
        Avm1AssemblyOptions options) =>
        options.CodeUnitContext.HasValue
            ? options
            : options with
            {
                CodeUnitContext = Avm1CodeUnitContext.Legacy(
                    Avm1ConstantPoolContext.FromInitial(
                        options.InitialConstantPoolCount))
            };

    private static Avm1ActionBody CyclicReference(Avm1CodeUnit codeUnit)
    {
        var labelOffsets = new int[codeUnit.LabelCount];
        Array.Fill(labelOffsets, -1);
        var labelActionIndices = new int[codeUnit.LabelCount];
        Array.Fill(labelActionIndices, -1);
        return new Avm1ActionBody(
            ReadOnlyMemory<byte>.Empty,
            [],
            [],
            labelOffsets,
            labelActionIndices,
            new Avm1AssemblyInstructionLocation[codeUnit.InstructionCount],
            [],
            [],
            [],
            0,
            true,
            [
                new Avm1AssemblyDiagnostic(
                    "AVM1ASM011",
                    Avm1CompilationDiagnosticSeverity.Error,
                    Avm1AssemblyInstructionIndex.Invalid,
                    -1,
                    "A nested code unit cannot contain itself, directly or indirectly.")
            ]);
    }

    private readonly record struct AssemblyCacheKey(
        Avm1CodeUnit CodeUnit,
        Avm1AssemblyOptions Options);
}
