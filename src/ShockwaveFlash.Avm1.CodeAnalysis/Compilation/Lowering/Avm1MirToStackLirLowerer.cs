using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation.Lowering;

internal readonly record struct Avm1StackLirLoweringOptions(
    Avm1TemporaryRegisterRange ReservedTemporaryRegisters,
    string? ActivationTemporaryPrefix,
    bool OptimizeConditionalFallthrough);

internal static class Avm1MirToStackLirLowerer
{
    public static Avm1StackLirMethod Lower(
        Avm1MirMethod mir,
        Avm1StackLirLoweringOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mir);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var builder = new StackLirBuilder(
            CreateTemporaryRegisters(options),
            options.ActivationTemporaryPrefix);
        var stack = new List<Avm1MirValueIndex>();
        var stackCarries = new Dictionary<Avm1MirBlockIndex, StackCarry>();
        if (!TryValidateControlFlow(mir, diagnostics))
            return builder.ToMethod();

        for (var layoutIndex = 0; layoutIndex < mir.BlockLayout.Count; layoutIndex++)
        {
            var blockIndex = mir.BlockLayout[layoutIndex];
            var nextBlockIndex = layoutIndex + 1 < mir.BlockLayout.Count
                ? mir.BlockLayout[layoutIndex + 1]
                : Avm1MirBlockIndex.Invalid;
            var block = mir[blockIndex];
            builder.StartBlock(blockIndex);
            if (stackCarries.Remove(blockIndex, out var carry))
            {
                for (var i = 0; i < carry.Values.Length; i++)
                {
                    builder.EmitPushTemporary(carry.Temporaries[i], carry.Owner);
                    builder.ReleaseTemporary(carry.Temporaries[i]);
                    stack.Add(carry.Values[i]);
                }
            }
            var instructionEnd = block.Instructions.Start + block.Instructions.Count;
            for (var instructionIndex = block.Instructions.Start;
                instructionIndex < instructionEnd;
                instructionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instruction = mir.Instructions[instructionIndex];
                switch (instruction.Kind)
                {
                    case Avm1MirInstructionKind.Constant:
                        if (!TryGetConstant(mir, instruction, diagnostics, out var constant))
                            continue;
                        builder.EmitPushConstant(
                            constant,
                            mir,
                            instruction.Index);
                        stack.Add(instruction.Result);
                        break;

                    case Avm1MirInstructionKind.StoreTemporary:
                        if (!ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        if (!builder.TryGetOrAcquireMirTemporary(
                                instruction.Temporary,
                                out var storedTemporary))
                        {
                            diagnostics.Add(new Avm1CompilerDiagnostic(
                                "AVM1CMP111",
                                Avm1CompilationDiagnosticSeverity.Error,
                                instruction.Origin,
                                -1,
                                "Persistent MIR value transport requires an " +
                                "available compiler-owned temporary register."));
                            return builder.ToMethod();
                        }

                        builder.EmitStoreTemporary(storedTemporary, instruction.Index);
                        if (instruction.Result.IsValid)
                        {
                            stack[^1] = instruction.Result;
                        }
                        else
                        {
                            builder.EmitAction(
                                ActionOpcode.Pop,
                                stackDelta: -1,
                                instruction.Index);
                            stack.RemoveAt(stack.Count - 1);
                        }
                        break;

                    case Avm1MirInstructionKind.LoadTemporary:
                        if (!builder.TryGetMirTemporary(
                                instruction.Temporary,
                                out var loadedTemporary))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                $"MIR {instruction.Temporary} is loaded before it is stored.");
                            continue;
                        }

                        builder.EmitPushTemporary(loadedTemporary, instruction.Index);
                        stack.Add(instruction.Result);
                        break;

                    case Avm1MirInstructionKind.BeginEnumeration:
                        if (!ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        // The verifier tracks the dynamic stream below the
                        // ordinary fixed-depth Stack LIR values.
                        builder.EmitAction(
                            ActionOpcode.Enumerate2,
                            stackDelta: -1,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.EnumerateNext:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        if (!builder.TryGetOrAcquireMirTemporary(
                                instruction.Temporary,
                                out var enumerationTemporary))
                        {
                            diagnostics.Add(new Avm1CompilerDiagnostic(
                                "AVM1CMP111",
                                Avm1CompilationDiagnosticSeverity.Error,
                                instruction.Origin,
                                -1,
                                "For-in enumeration requires an available " +
                                "compiler-owned temporary register."));
                            return builder.ToMethod();
                        }

                        builder.EmitStoreTemporary(
                            enumerationTemporary,
                            instruction.Index);
                        builder.EmitNull(instruction.Index);
                        builder.EmitAction(
                            ActionOpcode.Equals2,
                            stackDelta: 0,
                            instruction.Index);
                        EmitConditionalBranch(
                            builder,
                            options,
                            instruction.AlternativeTarget,
                            instruction.Target,
                            nextBlockIndex,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.EndEnumeration:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        builder.EmitNull(instruction.Index);
                        builder.EmitAction(
                            ActionOpcode.Equals2,
                            stackDelta: 0,
                            instruction.Index);
                        builder.EmitAction(
                            ActionOpcode.Not,
                            stackDelta: 0,
                            instruction.Index);
                        builder.EmitBranch(
                            ActionOpcode.If,
                            blockIndex,
                            stackDelta: -1,
                            instruction.Index);
                        if (instruction.Target != nextBlockIndex)
                        {
                            builder.EmitBranch(
                                ActionOpcode.Jump,
                                instruction.Target,
                                stackDelta: 0,
                                instruction.Index);
                        }
                        break;

                    case Avm1MirInstructionKind.LoadLValue:
                        if (!TryGetLValue(mir, instruction, diagnostics, out var loadLValue))
                            continue;
                        if (loadLValue.Kind is Avm1MirLValueKind.Register)
                        {
                            builder.EmitPushRegister(
                                loadLValue.Register,
                                instruction.Index);
                            stack.Add(instruction.Result);
                        }
                        else if (loadLValue.Kind is Avm1MirLValueKind.Name)
                        {
                            if (!TryGetLValueName(
                                mir,
                                instruction,
                                loadLValue,
                                diagnostics,
                                out var loadName))
                            {
                                continue;
                            }
                            builder.EmitPushName(loadName, instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.GetVariable,
                                stackDelta: 0,
                                instruction.Index);
                            stack.Add(instruction.Result);
                        }
                        else if (loadLValue.Kind is Avm1MirLValueKind.Member)
                        {
                            if (!ExpectMemberOperands(
                                stack,
                                loadLValue,
                                instruction,
                                value: Avm1MirValueIndex.Invalid,
                                diagnostics: diagnostics))
                            {
                                continue;
                            }
                            builder.EmitAction(
                                ActionOpcode.GetMember,
                                stackDelta: -1,
                                instruction.Index);
                            stack.RemoveAt(stack.Count - 1);
                            stack[^1] = instruction.Result;
                        }
                        else if (TryGetCapturedMemberTemporaries(
                            builder,
                            instruction,
                            loadLValue,
                            diagnostics,
                            out var loadReceiverTemporary,
                            out var loadKeyTemporary))
                        {
                            builder.EmitPushTemporary(
                                loadReceiverTemporary,
                                instruction.Index);
                            builder.EmitPushTemporary(
                                loadKeyTemporary,
                                instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.GetMember,
                                stackDelta: -1,
                                instruction.Index);
                            stack.Add(instruction.Result);
                        }
                        break;

                    case Avm1MirInstructionKind.EvaluateName:
                        if (!ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        if (!instruction.Result.IsValid)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR evaluated-name lookup has no result value.");
                            continue;
                        }
                        builder.EmitAction(
                            ActionOpcode.GetVariable,
                            stackDelta: 0,
                            instruction.Index);
                        stack[^1] = instruction.Result;
                        break;

                    case Avm1MirInstructionKind.LoadCurrentFunction:
                        if (!TryGetLValue(
                                mir,
                                instruction,
                                diagnostics,
                                out var argumentsStorage))
                        {
                            continue;
                        }
                        if (argumentsStorage.Kind is not
                            Avm1MirLValueKind.Register)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR current-function load requires a preloaded " +
                                    "arguments register.");
                            continue;
                        }

                        builder.EmitPushRegister(
                            argumentsStorage.Register,
                            instruction.Index);
                        builder.EmitPushName("callee", instruction.Index);
                        builder.EmitAction(
                            ActionOpcode.GetMember,
                            stackDelta: -1,
                            instruction.Index);
                        stack.Add(instruction.Result);
                        break;

                    case Avm1MirInstructionKind.DeclareLocal:
                        if (instruction.LValue.IsValid)
                        {
                            if (!TryGetLValue(
                                    mir,
                                    instruction,
                                    diagnostics,
                                    out var declarationStorage))
                            {
                                continue;
                            }

                            if (declarationStorage.Kind is
                                Avm1MirLValueKind.Register)
                            {
                                if (instruction.Operand.IsValid)
                                {
                                    if (!ExpectTop(
                                            stack,
                                            instruction.Operand,
                                            instruction,
                                            diagnostics))
                                    {
                                        continue;
                                    }
                                    builder.EmitStoreRegister(
                                        declarationStorage.Register,
                                        instruction.Index);
                                    builder.EmitAction(
                                        ActionOpcode.Pop,
                                        stackDelta: -1,
                                        instruction.Index);
                                    stack.RemoveAt(stack.Count - 1);
                                }
                                RequireEmptyBoundary(stack, instruction, diagnostics);
                                break;
                            }

                            if (declarationStorage.Kind is not
                                Avm1MirLValueKind.Name)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR local declaration lvalue must use name " +
                                        "or register storage.");
                                continue;
                            }
                        }

                        if (!TryGetName(mir, instruction, diagnostics, out var localName))
                            continue;
                        if (instruction.Operand.IsValid)
                        {
                            if (!ExpectTop(stack, instruction.Operand, instruction, diagnostics))
                                continue;
                            builder.EmitPushName(localName, instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.StackSwap,
                                stackDelta: 0,
                                instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.DefineLocal,
                                stackDelta: -2,
                                instruction.Index);
                            stack.RemoveAt(stack.Count - 1);
                        }
                        else
                        {
                            builder.EmitPushName(localName, instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.DefineLocal2,
                                stackDelta: -1,
                                instruction.Index);
                        }
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.DefineFunction:
                        if (!instruction.FunctionSite.IsValid ||
                            instruction.FunctionSite.Value >= mir.FunctionSites.Count)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR function site handle is invalid.");
                            continue;
                        }

                        var functionSite = mir[instruction.FunctionSite];
                        if (functionSite.IsDeclaration == instruction.Result.IsValid)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR function declaration/value result shape is invalid.");
                            continue;
                        }

                        builder.EmitFunction(
                            functionSite,
                            instruction.Index,
                            instruction.Result.IsValid ? 1 : 0);
                        if (instruction.Result.IsValid)
                            stack.Add(instruction.Result);
                        else
                            RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.Try:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        if (!instruction.TrySite.IsValid ||
                            instruction.TrySite.Value >= mir.TrySites.Count)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR try-site handle is invalid.");
                            continue;
                        }

                        builder.EmitTry(mir[instruction.TrySite], instruction.Index);
                        break;

                    case Avm1MirInstructionKind.With:
                        if (!ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        if (!instruction.WithSite.IsValid ||
                            instruction.WithSite.Value >= mir.WithSites.Count)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR with-site handle is invalid.");
                            continue;
                        }

                        builder.EmitWith(mir[instruction.WithSite], instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.InitializePendingCompletion:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        builder.EmitInteger(0, instruction.Index);
                        if (!TryEmitCompletionStore(
                                mir,
                                builder,
                                instruction.CompletionStorage,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.LoadPendingCompletion:
                        if (!TryEmitCompletionLoad(
                                mir,
                                builder,
                                instruction.CompletionStorage,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        stack.Add(instruction.Result);
                        break;

                    case Avm1MirInstructionKind.SetPendingCompletion:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        if (!instruction.CompletionSite.IsValid ||
                            instruction.CompletionSite.Value >= mir.CompletionSites.Count)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR completion-site handle is invalid.");
                            continue;
                        }

                        var completionSite = mir[instruction.CompletionSite];
                        builder.EmitInteger(completionSite.Token, instruction.Index);
                        if (!TryEmitCompletionStore(
                                mir,
                                builder,
                                completionSite.Storage,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.ExternalControlBranch:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        if (!instruction.CompletionSite.IsValid ||
                            instruction.CompletionSite.Value >=
                                mir.CompletionSites.Count)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR external control branch has no valid " +
                                "completion-site identity.");
                            continue;
                        }
                        builder.EmitParentBranch(
                            instruction.CompletionSite,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.StoreLValue:
                        if (!TryGetLValue(mir, instruction, diagnostics, out var storeLValue))
                        {
                            continue;
                        }
                        if (storeLValue.Kind is Avm1MirLValueKind.Register)
                        {
                            if (!ExpectTop(
                                    stack,
                                    instruction.Operand,
                                    instruction,
                                    diagnostics))
                            {
                                continue;
                            }
                            builder.EmitStoreRegister(
                                storeLValue.Register,
                                instruction.Index);
                            if (instruction.Result.IsValid)
                            {
                                stack[^1] = instruction.Result;
                            }
                            else
                            {
                                builder.EmitAction(
                                    ActionOpcode.Pop,
                                    stackDelta: -1,
                                    instruction.Index);
                                stack.RemoveAt(stack.Count - 1);
                            }
                        }
                        else if (storeLValue.Kind is Avm1MirLValueKind.Name)
                        {
                            if (!TryGetLValueName(
                                    mir,
                                    instruction,
                                    storeLValue,
                                    diagnostics,
                                    out var storeName) ||
                                !ExpectTop(
                                    stack,
                                    instruction.Operand,
                                    instruction,
                                    diagnostics))
                            {
                                continue;
                            }
                            if (instruction.Result.IsValid)
                            {
                                builder.EmitAction(
                                    ActionOpcode.PushDuplicate,
                                    stackDelta: 1,
                                    instruction.Index);
                            }
                            builder.EmitPushName(storeName, instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.StackSwap,
                                stackDelta: 0,
                                instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.SetVariable,
                                stackDelta: -2,
                                instruction.Index);
                            if (instruction.Result.IsValid)
                            {
                                stack[^1] = instruction.Result;
                            }
                            else
                            {
                                stack.RemoveAt(stack.Count - 1);
                            }
                        }
                        else if (storeLValue.Kind is Avm1MirLValueKind.Member)
                        {
                            if (!ExpectMemberOperands(
                                stack,
                                storeLValue,
                                instruction,
                                instruction.Operand,
                                diagnostics))
                            {
                                continue;
                            }

                            var temporary = Avm1StackLirTemporaryIndex.Invalid;
                            if (instruction.Result.IsValid &&
                                !builder.TryAcquireTemporary(out temporary))
                            {
                                diagnostics.Add(new Avm1CompilerDiagnostic(
                                    "AVM1CMP106",
                                    Avm1CompilationDiagnosticSeverity.Error,
                                    instruction.Origin,
                                    -1,
                                    "Member assignment result requires an available " +
                                    "compiler-owned temporary register."));
                                continue;
                            }

                            if (instruction.Result.IsValid)
                                builder.EmitStoreTemporary(temporary, instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.SetMember,
                                stackDelta: -3,
                                instruction.Index);
                            stack.RemoveRange(stack.Count - 3, 3);
                            if (instruction.Result.IsValid)
                            {
                                builder.EmitPushTemporary(temporary, instruction.Index);
                                builder.ReleaseTemporary(temporary);
                                stack.Add(instruction.Result);
                            }
                            else
                            {
                                RequireEmptyBoundary(stack, instruction, diagnostics);
                            }
                        }
                        else if (ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics) &&
                            TryGetCapturedMemberTemporaries(
                                builder,
                                instruction,
                                storeLValue,
                                diagnostics,
                                out var storeReceiverTemporary,
                                out var storeKeyTemporary))
                        {
                            if (instruction.Result.IsValid)
                            {
                                builder.EmitAction(
                                    ActionOpcode.PushDuplicate,
                                    stackDelta: 1,
                                    instruction.Index);
                            }
                            builder.EmitPushTemporary(
                                storeReceiverTemporary,
                                instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.StackSwap,
                                stackDelta: 0,
                                instruction.Index);
                            builder.EmitStoreTemporary(
                                storeReceiverTemporary,
                                instruction.Index);
                            builder.EmitPushTemporary(
                                storeKeyTemporary,
                                instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.StackSwap,
                                stackDelta: 0,
                                instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.SetMember,
                                stackDelta: -3,
                                instruction.Index);

                            if (instruction.Result.IsValid)
                                stack[^1] = instruction.Result;
                            else
                                stack.RemoveAt(stack.Count - 1);
                        }
                        break;

                    case Avm1MirInstructionKind.DeleteLValue:
                        if (!instruction.Result.IsValid)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR delete instruction has no result value.");
                            continue;
                        }
                        if (!TryGetLValue(
                            mir,
                            instruction,
                            diagnostics,
                            out var deleteLValue))
                        {
                            continue;
                        }

                        if (deleteLValue.Kind is Avm1MirLValueKind.Name)
                        {
                            if (!TryGetLValueName(
                                mir,
                                instruction,
                                deleteLValue,
                                diagnostics,
                                out var deleteName))
                            {
                                continue;
                            }
                            builder.EmitPushName(deleteName, instruction.Index);
                            builder.EmitAction(
                                ActionOpcode.Delete2,
                                stackDelta: 0,
                                instruction.Index);
                            stack.Add(instruction.Result);
                        }
                        else if (deleteLValue.Kind is Avm1MirLValueKind.ComputedName)
                        {
                            if (!ExpectTop(
                                stack,
                                deleteLValue.Key,
                                instruction,
                                diagnostics))
                            {
                                continue;
                            }
                            builder.EmitAction(
                                ActionOpcode.Delete2,
                                stackDelta: 0,
                                instruction.Index);
                            stack[^1] = instruction.Result;
                        }
                        else if (deleteLValue.Kind is Avm1MirLValueKind.Member)
                        {
                            if (!ExpectMemberOperands(
                                stack,
                                deleteLValue,
                                instruction,
                                value: Avm1MirValueIndex.Invalid,
                                diagnostics: diagnostics))
                            {
                                continue;
                            }
                            builder.EmitAction(
                                ActionOpcode.Delete,
                                stackDelta: -1,
                                instruction.Index);
                            stack.RemoveAt(stack.Count - 1);
                            stack[^1] = instruction.Result;
                        }
                        else
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                $"MIR delete lvalue kind {deleteLValue.Kind} is invalid.");
                        }
                        break;

                    case Avm1MirInstructionKind.Invoke:
                        var diagnosticCount = diagnostics.Count;
                        LowerInvocation(
                            mir,
                            instruction,
                            builder,
                            stack,
                            diagnostics);
                        if (diagnostics
                            .Skip(diagnosticCount)
                            .Any(diagnostic => diagnostic.Severity is
                                Avm1CompilationDiagnosticSeverity.Error))
                        {
                            return builder.ToMethod();
                        }
                        break;

                    case Avm1MirInstructionKind.Aggregate:
                        LowerAggregate(
                            mir,
                            instruction,
                            builder,
                            stack,
                            diagnostics);
                        break;

                    case Avm1MirInstructionKind.Intrinsic:
                        LowerIntrinsic(
                            mir,
                            instruction,
                            builder,
                            stack,
                            diagnostics);
                        break;

                    case Avm1MirInstructionKind.StorePhi:
                        if (!TryGetPhiSite(
                                mir,
                                instruction,
                                diagnostics,
                                out _) ||
                            !ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            continue;
                        }
                        if (!builder.TryGetOrAcquirePhiTemporary(
                            instruction.PhiSite,
                            out var storePhiTemporary))
                        {
                            diagnostics.Add(new Avm1CompilerDiagnostic(
                                "AVM1CMP109",
                                Avm1CompilationDiagnosticSeverity.Error,
                                instruction.Origin,
                                -1,
                                "Value-producing control-flow join requires an " +
                                "available compiler-owned temporary register."));
                            return builder.ToMethod();
                        }

                        builder.EmitStoreTemporary(
                            storePhiTemporary,
                            instruction.Index);
                        if (instruction.Result.IsValid)
                        {
                            stack[^1] = instruction.Result;
                        }
                        else
                        {
                            builder.EmitAction(
                                ActionOpcode.Pop,
                                stackDelta: -1,
                                instruction.Index);
                            stack.RemoveAt(stack.Count - 1);
                            RequireEmptyBoundary(stack, instruction, diagnostics);
                        }
                        break;

                    case Avm1MirInstructionKind.Phi:
                        if (!TryGetPhiSite(
                                mir,
                                instruction,
                                diagnostics,
                                out var phiSite) ||
                            phiSite.MergeBlock != blockIndex)
                        {
                            continue;
                        }
                        if (!builder.TryGetPhiTemporary(
                            instruction.PhiSite,
                            out var phiTemporary))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                $"MIR {instruction.PhiSite} reaches its merge block " +
                                "without a stored incoming value.");
                            continue;
                        }

                        builder.EmitPushTemporary(phiTemporary, instruction.Index);
                        builder.ReleasePhiTemporary(instruction.PhiSite);
                        stack.Add(instruction.Result);
                        break;

                    case Avm1MirInstructionKind.Unary:
                        if (!ExpectTop(stack, instruction.Operand, instruction, diagnostics))
                            continue;
                        if (!TryGetUnaryAction(instruction.Operator, out var unaryOpcode))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                $"MIR unary operator {instruction.Operator} has no Stack LIR selection.");
                            continue;
                        }
                        builder.EmitAction(unaryOpcode, stackDelta: 0, instruction.Index);
                        stack[^1] = instruction.Result;
                        break;

                    case Avm1MirInstructionKind.GetTime:
                        if (!instruction.Result.IsValid || instruction.Operand.IsValid)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR GetTime must have a result and no operand.");
                            continue;
                        }
                        builder.EmitAction(
                            ActionOpcode.GetTime,
                            stackDelta: 1,
                            instruction.Index);
                        stack.Add(instruction.Result);
                        break;

                    case Avm1MirInstructionKind.Trace:
                        if (instruction.Result.IsValid ||
                            !ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            if (instruction.Result.IsValid)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR Trace cannot produce a result value.");
                            }
                            continue;
                        }
                        builder.EmitAction(
                            ActionOpcode.Trace,
                            stackDelta: -1,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        break;

                    case Avm1MirInstructionKind.SetTargetImmediate:
                        if (instruction.Result.IsValid ||
                            instruction.Operand.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            !TryGetName(
                                mir,
                                instruction,
                                diagnostics,
                                out var immediateTarget))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR immediate target transition has an invalid payload.");
                            continue;
                        }
                        builder.EmitSetTarget(
                            immediateTarget,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.SetTarget:
                        if (instruction.Result.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            !ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            if (instruction.Result.IsValid)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR target transition cannot produce a result value.");
                            }
                            continue;
                        }
                        builder.EmitAction(
                            ActionOpcode.SetTarget2,
                            stackDelta: -1,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        break;

                    case Avm1MirInstructionKind.TimelineNextFrame:
                    case Avm1MirInstructionKind.TimelinePreviousFrame:
                    case Avm1MirInstructionKind.TimelinePlay:
                    case Avm1MirInstructionKind.TimelineStop:
                    case Avm1MirInstructionKind.TimelineStopSounds:
                    case Avm1MirInstructionKind.TimelineToggleQuality:
                        if (instruction.Result.IsValid ||
                            instruction.Operand.IsValid ||
                            instruction.SecondaryOperand.IsValid)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                $"MIR {instruction.Kind} cannot have value operands or a result.");
                            continue;
                        }
                        builder.EmitAction(
                            instruction.Kind switch
                            {
                                Avm1MirInstructionKind.TimelineNextFrame =>
                                    ActionOpcode.NextFrame,
                                Avm1MirInstructionKind.TimelinePreviousFrame =>
                                    ActionOpcode.PreviousFrame,
                                Avm1MirInstructionKind.TimelinePlay =>
                                    ActionOpcode.Play,
                                Avm1MirInstructionKind.TimelineStop =>
                                    ActionOpcode.Stop,
                                Avm1MirInstructionKind.TimelineStopSounds =>
                                    ActionOpcode.StopSounds,
                                Avm1MirInstructionKind.TimelineToggleQuality =>
                                    ActionOpcode.ToggleQuality,
                                _ => throw new InvalidOperationException()
                            },
                            stackDelta: 0,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.TimelineCall:
                        if (instruction.Result.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            !ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            if (instruction.Result.IsValid)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR timeline call cannot produce a result value.");
                            }
                            continue;
                        }
                        builder.EmitAction(
                            ActionOpcode.Call,
                            stackDelta: -1,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        break;

                    case Avm1MirInstructionKind.TimelineGotoAndPlay:
                    case Avm1MirInstructionKind.TimelineGotoAndStop:
                        if (instruction.Result.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            !ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            if (instruction.Result.IsValid)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    $"MIR {instruction.Kind} cannot produce a result value.");
                            }
                            continue;
                        }
                        ushort? sceneBias = null;
                        if (instruction.Constant.IsValid)
                        {
                            if (!TryGetConstant(
                                    mir,
                                    instruction,
                                    diagnostics,
                                    out var bias) ||
                                bias.Kind is not Avm1MirConstantKind.Integer ||
                                bias.IntegerValue is < 0 or > ushort.MaxValue)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR timeline scene bias is invalid.");
                                continue;
                            }
                            sceneBias = checked((ushort)bias.IntegerValue);
                        }
                        builder.EmitGotoFrame2(
                            instruction.Kind is
                                Avm1MirInstructionKind.TimelineGotoAndPlay,
                            sceneBias,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        break;

                    case Avm1MirInstructionKind.TimelineGotoImmediateAndPlay:
                    case Avm1MirInstructionKind.TimelineGotoImmediateAndStop:
                        if (instruction.Result.IsValid ||
                            instruction.Operand.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            instruction.Constant.IsValid == instruction.Name.IsValid)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR immediate timeline transition has an invalid payload.");
                            continue;
                        }

                        if (instruction.Constant.IsValid)
                        {
                            if (!TryGetConstant(
                                    mir,
                                    instruction,
                                    diagnostics,
                                    out var frame) ||
                                frame.Kind is not Avm1MirConstantKind.Integer ||
                                frame.IntegerValue is < 0 or > ushort.MaxValue)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR immediate frame index is invalid.");
                                continue;
                            }
                            builder.EmitGotoFrame(
                                checked((ushort)frame.IntegerValue),
                                instruction.Index);
                        }
                        else
                        {
                            if (!TryGetName(
                                    mir,
                                    instruction,
                                    diagnostics,
                                    out var label))
                            {
                                continue;
                            }
                            builder.EmitGotoLabel(label, instruction.Index);
                        }

                        if (instruction.Kind is
                            Avm1MirInstructionKind.TimelineGotoImmediateAndPlay)
                        {
                            builder.EmitAction(
                                ActionOpcode.Play,
                                stackDelta: 0,
                                instruction.Index);
                        }
                        break;

                    case Avm1MirInstructionKind.GetUrlImmediate:
                        if (instruction.Result.IsValid ||
                            instruction.Operand.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            !instruction.UrlSite.IsValid ||
                            instruction.UrlSite.Value >= mir.UrlSites.Count)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR immediate URL request has an invalid payload.");
                            continue;
                        }
                        var urlSite = mir[instruction.UrlSite];
                        if (!urlSite.Url.IsValid ||
                            urlSite.Url.Value >= mir.Strings.Count ||
                            !urlSite.Target.IsValid ||
                            urlSite.Target.Value >= mir.Strings.Count ||
                            urlSite.Flags is not GetUrlFlags.MethodNone)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR immediate URL request has invalid strings.");
                            continue;
                        }
                        builder.EmitGetUrlImmediate(
                            mir[urlSite.Url],
                            mir[urlSite.Target],
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.GetUrl2:
                        if (instruction.Result.IsValid ||
                            !instruction.UrlSite.IsValid ||
                            instruction.UrlSite.Value >= mir.UrlSites.Count ||
                            !ExpectBinaryOperands(stack, instruction, diagnostics))
                        {
                            if (instruction.Result.IsValid ||
                                !instruction.UrlSite.IsValid ||
                                instruction.UrlSite.Value >= mir.UrlSites.Count)
                            {
                                AddInvariant(
                                    diagnostics,
                                    instruction,
                                    "MIR stack URL request has an invalid payload.");
                            }
                            continue;
                        }
                        var stackUrlSite = mir[instruction.UrlSite];
                        if (stackUrlSite.Url.IsValid ||
                            stackUrlSite.Target.IsValid ||
                            !ActionGetURL2.IsValidFlags(stackUrlSite.Flags))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR stack URL request has invalid flags or strings.");
                            continue;
                        }
                        builder.EmitGetUrl2(
                            stackUrlSite.Flags,
                            instruction.Index);
                        stack.RemoveRange(stack.Count - 2, 2);
                        break;

                    case Avm1MirInstructionKind.Binary:
                        if (!ExpectBinaryOperands(stack, instruction, diagnostics))
                            continue;
                        if (!TryGetBinaryActions(
                            instruction.Operator,
                            out var binaryOpcode,
                            out var negate))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                $"MIR binary operator {instruction.Operator} has no Stack LIR selection.");
                            continue;
                        }
                        builder.EmitAction(binaryOpcode, stackDelta: -1, instruction.Index);
                        if (negate)
                        {
                            builder.EmitAction(
                                ActionOpcode.Not,
                                stackDelta: 0,
                                instruction.Index);
                        }
                        stack.RemoveAt(stack.Count - 1);
                        stack[^1] = instruction.Result;
                        break;

                    case Avm1MirInstructionKind.Discard:
                        if (!ExpectTop(stack, instruction.Operand, instruction, diagnostics))
                            continue;
                        builder.EmitAction(ActionOpcode.Pop, stackDelta: -1, instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.Return:
                        if (instruction.Operand.IsValid)
                        {
                            if (!ExpectTop(stack, instruction.Operand, instruction, diagnostics))
                                continue;
                        }
                        else
                        {
                            builder.EmitUndefined(instruction.Index);
                        }
                        builder.EmitAction(ActionOpcode.Return, stackDelta: -1, instruction.Index);
                        if (instruction.Operand.IsValid)
                            stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.Throw:
                        if (!ExpectTop(
                            stack,
                            instruction.Operand,
                            instruction,
                            diagnostics))
                        {
                            continue;
                        }
                        builder.EmitAction(
                            ActionOpcode.Throw,
                            stackDelta: -1,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.Break:
                    case Avm1MirInstructionKind.Continue:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        builder.EmitBranch(
                            ActionOpcode.Jump,
                            instruction.Target,
                            stackDelta: 0,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.Branch:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        builder.EmitBranch(
                            ActionOpcode.Jump,
                            instruction.Target,
                            stackDelta: 0,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.BranchIfTrue:
                        if (!ExpectTop(stack, instruction.Operand, instruction, diagnostics))
                            continue;
                        if (instruction.PhiSite.IsValid && stack.Count > 1 &&
                            !TrySpillStackPrefix(
                                mir,
                                instruction,
                                builder,
                                stack,
                                stackCarries,
                                diagnostics))
                        {
                            return builder.ToMethod();
                        }
                        EmitConditionalBranch(
                            builder,
                            options,
                            instruction.Target,
                            instruction.AlternativeTarget,
                            nextBlockIndex,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    case Avm1MirInstructionKind.WaitForFrameImmediate:
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        if (instruction.Result.IsValid ||
                            instruction.Operand.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            !instruction.Constant.IsValid ||
                            instruction.Target != nextBlockIndex ||
                            !instruction.AlternativeTarget.IsValid ||
                            !TryGetConstant(
                                mir,
                                instruction,
                                diagnostics,
                                out var immediateWaitFrame) ||
                            immediateWaitFrame.Kind is not
                                Avm1MirConstantKind.Integer ||
                            immediateWaitFrame.IntegerValue is
                                < 0 or > ushort.MaxValue)
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR immediate frame wait has an invalid frame or physical fallthrough.");
                            continue;
                        }
                        builder.EmitWaitForFrame(
                            checked((ushort)immediateWaitFrame.IntegerValue),
                            instruction.AlternativeTarget,
                            instruction.Index);
                        break;

                    case Avm1MirInstructionKind.WaitForFrame:
                        if (instruction.Result.IsValid ||
                            instruction.SecondaryOperand.IsValid ||
                            instruction.Constant.IsValid ||
                            instruction.Target != nextBlockIndex ||
                            !instruction.AlternativeTarget.IsValid ||
                            !ExpectTop(
                                stack,
                                instruction.Operand,
                                instruction,
                                diagnostics))
                        {
                            AddInvariant(
                                diagnostics,
                                instruction,
                                "MIR stack frame wait has an invalid operand or physical fallthrough.");
                            continue;
                        }
                        builder.EmitWaitForFrame2(
                            instruction.AlternativeTarget,
                            instruction.Index);
                        stack.RemoveAt(stack.Count - 1);
                        RequireEmptyBoundary(stack, instruction, diagnostics);
                        break;

                    default:
                        AddInvariant(
                            diagnostics,
                            instruction,
                            $"MIR instruction {instruction.Kind} has no Stack LIR lowering.");
                        break;
                }
            }

            if (stack.Count != 0)
            {
                diagnostics.Add(new Avm1CompilerDiagnostic(
                    "AVM1CMP103",
                    Avm1CompilationDiagnosticSeverity.Error,
                    SourceOriginIndex.Invalid,
                    -1,
                    $"MIR block {block.Index} ended with {stack.Count} live " +
                    "stack value(s)."));
                return builder.ToMethod();
            }
        }

        if (stack.Count != 0)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP103",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                $"MIR lowering ended with {stack.Count} live stack value(s)."));
        }
        if (builder.HasInvalidStack)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP103",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                "Stack LIR construction produced a negative stack depth."));
        }
        if (stackCarries.Count != 0)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP103",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                "One or more MIR stack carries have no reachable merge block."));
        }
        if (builder.HasLivePhiTemporaries)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP103",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                "One or more MIR phi temporaries remain live after lowering."));
        }

        return builder.ToMethod();
    }

    private static void EmitConditionalBranch(
        StackLirBuilder builder,
        Avm1StackLirLoweringOptions options,
        Avm1MirBlockIndex whenTrue,
        Avm1MirBlockIndex whenFalse,
        Avm1MirBlockIndex nextBlock,
        Avm1MirInstructionIndex owner)
    {
        if (options.OptimizeConditionalFallthrough &&
            whenTrue == nextBlock &&
            whenFalse != nextBlock)
        {
            builder.EmitAction(ActionOpcode.Not, stackDelta: 0, owner);
            builder.EmitBranch(
                ActionOpcode.If,
                whenFalse,
                stackDelta: -1,
                owner);
            return;
        }

        builder.EmitBranch(
            ActionOpcode.If,
            whenTrue,
            stackDelta: -1,
            owner);
        if (whenFalse != nextBlock)
        {
            builder.EmitBranch(
                ActionOpcode.Jump,
                whenFalse,
                stackDelta: 0,
                owner);
        }
    }

    private static bool TryValidateControlFlow(
        Avm1MirMethod mir,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        var isValid = true;
        void Add(string message)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP103",
                Avm1CompilationDiagnosticSeverity.Error,
                SourceOriginIndex.Invalid,
                -1,
                message));
            isValid = false;
        }

        if (mir.Blocks.Count == 0)
        {
            Add("MIR method has no entry block.");
            return false;
        }
        if (!mir.EntryBlock.IsValid || mir.EntryBlock.Value >= mir.Blocks.Count)
            Add("MIR entry-block handle is invalid.");
        if (mir.BlockLayout.Count != mir.Blocks.Count)
            Add("MIR block layout does not contain every block exactly once.");
        if (mir.TemporaryCount < 0)
            Add("MIR temporary count is negative.");

        var seen = new bool[mir.Blocks.Count];
        var expectedInstructionStart = 0;
        for (var layoutIndex = 0; layoutIndex < mir.BlockLayout.Count; layoutIndex++)
        {
            var blockIndex = mir.BlockLayout[layoutIndex];
            if (!blockIndex.IsValid || blockIndex.Value >= mir.Blocks.Count)
            {
                Add($"MIR block layout entry {layoutIndex} is invalid.");
                continue;
            }
            if (seen[blockIndex.Value])
            {
                Add($"MIR block layout contains {blockIndex} more than once.");
                continue;
            }
            seen[blockIndex.Value] = true;

            var block = mir[blockIndex];
            if (block.Index != blockIndex)
                Add($"MIR block table identity for {blockIndex} is inconsistent.");
            if (layoutIndex == 0 && blockIndex != mir.EntryBlock)
                Add("MIR entry block is not first in physical layout.");
            if (block.Instructions.Start != expectedInstructionStart ||
                block.Instructions.Count < 0 ||
                block.Instructions.Start >
                    mir.Instructions.Count - block.Instructions.Count)
            {
                Add($"MIR instruction range for {blockIndex} is invalid or non-contiguous.");
                continue;
            }

            var instructionEnd = block.Instructions.Start + block.Instructions.Count;
            for (var i = block.Instructions.Start; i < instructionEnd; i++)
            {
                var instruction = mir.Instructions[i];
                if (instruction.Index.Value != i)
                    Add($"MIR instruction identity at index {i} is inconsistent.");
                ValidatePhiReference(mir, blockIndex, instruction, Add);
                ValidateControlTargetReference(mir, blockIndex, instruction, Add);
                ValidateTemporaryReference(mir, blockIndex, instruction, Add);
                ValidateTrySiteReference(mir, blockIndex, instruction, Add);
                ValidateWithSiteReference(mir, blockIndex, instruction, Add);
                ValidateCompletionSiteReference(mir, blockIndex, instruction, Add);
                ValidateCompletionStorageReference(
                    mir,
                    blockIndex,
                    instruction,
                    Add);

                var isTerminator = IsTerminator(instruction.Kind);
                if (isTerminator && i != instructionEnd - 1)
                    Add($"MIR terminator {instruction.Index} is not last in {blockIndex}.");
            }

            if (block.Instructions.Count == 0)
            {
                if (block.IsReachable && layoutIndex != mir.BlockLayout.Count - 1)
                {
                    Add(
                        $"Reachable empty MIR block {blockIndex} is not the final " +
                        "exit block.");
                }
            }
            else
            {
                var terminator = mir.Instructions[instructionEnd - 1];
                ValidateTerminator(mir, blockIndex, terminator, Add);
                if (layoutIndex != mir.BlockLayout.Count - 1 &&
                    !IsTerminator(terminator.Kind))
                {
                    Add($"Non-final MIR block {blockIndex} has no explicit terminator.");
                }
            }

            expectedInstructionStart = instructionEnd;
        }

        if (seen.Any(value => !value))
            Add("MIR block layout omits one or more blocks.");
        if (expectedInstructionStart != mir.Instructions.Count)
            Add("MIR block ranges do not cover the complete instruction array.");
        ValidatePhiSites(mir, Add);
        ValidateControlTargets(mir, Add);
        ValidateTrySites(mir, Add);
        ValidateWithSites(mir, Add);
        ValidateCompletionSites(mir, Add);
        return isValid;
    }

    private static void ValidateCompletionSiteReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresSite = instruction.Kind is
            Avm1MirInstructionKind.SetPendingCompletion or
            Avm1MirInstructionKind.ExternalControlBranch;
        if (!instruction.CompletionSite.IsValid)
        {
            if (requiresSite)
            {
                addDiagnostic(
                    $"MIR SetPendingCompletion in {block} has no completion site.");
            }
            return;
        }
        if (instruction.CompletionSite.Value >= mir.CompletionSites.Count)
        {
            addDiagnostic(
                $"MIR instruction {instruction.Index} has an invalid " +
                "completion-site handle.");
            return;
        }
        if (!requiresSite)
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references " +
                "a completion site.");
        }
    }

    private static void ValidateCompletionSites(
        Avm1MirMethod mir,
        Action<string> addDiagnostic)
    {
        for (var siteIndex = 0; siteIndex < mir.CompletionSites.Count; siteIndex++)
        {
            var expectedIndex = new Avm1MirCompletionSiteIndex(siteIndex);
            var site = mir.CompletionSites[siteIndex];
            if (site.Index != expectedIndex)
            {
                addDiagnostic(
                    $"MIR completion-site identity at index {siteIndex} is " +
                    "inconsistent.");
            }
            if (!Enum.IsDefined(site.Kind))
                addDiagnostic($"MIR {expectedIndex} has an invalid completion kind.");
            if (!site.TargetStatement.IsValid)
                addDiagnostic($"MIR {expectedIndex} has no Source target statement.");
            var physicalBranch = mir.Instructions.Any(instruction =>
                instruction.Kind is Avm1MirInstructionKind.ExternalControlBranch &&
                instruction.CompletionSite == expectedIndex);
            ValidateCompletionStorage(
                mir,
                site.Storage,
                $"MIR {expectedIndex}",
                allowNone: physicalBranch,
                addDiagnostic);
            if (site.Token <= 0)
                addDiagnostic($"MIR {expectedIndex} has a non-positive token.");
        }
    }

    private static void ValidateCompletionStorageReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresStorage = instruction.Kind is
            Avm1MirInstructionKind.InitializePendingCompletion or
            Avm1MirInstructionKind.LoadPendingCompletion;
        if (instruction.CompletionStorage.Kind is
            Avm1MirCompletionStorageKind.None)
        {
            if (requiresStorage)
            {
                addDiagnostic(
                    $"MIR {instruction.Kind} in {block} has no completion " +
                    "storage.");
            }
            return;
        }
        if (!requiresStorage)
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references " +
                "completion storage.");
            return;
        }

        ValidateCompletionStorage(
            mir,
            instruction.CompletionStorage,
            $"MIR {instruction.Kind} in {block}",
            allowNone: false,
            addDiagnostic);
    }

    private static void ValidateTrySiteReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresTrySite = instruction.Kind is Avm1MirInstructionKind.Try;
        if (!instruction.TrySite.IsValid)
        {
            if (requiresTrySite)
                addDiagnostic($"MIR Try in {block} has no try site.");
            return;
        }
        if (instruction.TrySite.Value >= mir.TrySites.Count)
        {
            addDiagnostic(
                $"MIR instruction {instruction.Index} has an invalid try-site handle.");
            return;
        }
        if (!requiresTrySite)
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references a try site.");
        }
    }

    private static void ValidateTrySites(
        Avm1MirMethod mir,
        Action<string> addDiagnostic)
    {
        var expectedScopeStart = 0;
        for (var siteIndex = 0; siteIndex < mir.TrySites.Count; siteIndex++)
        {
            var expectedIndex = new Avm1MirTrySiteIndex(siteIndex);
            var site = mir.TrySites[siteIndex];
            if (site.Index != expectedIndex)
                addDiagnostic($"MIR try-site identity at index {siteIndex} is inconsistent.");
            if (!site.Statement.IsValid)
                addDiagnostic($"MIR {expectedIndex} has no Source try statement.");
            if (!site.TryBody.IsValid)
                addDiagnostic($"MIR {expectedIndex} has no try body.");
            if (!site.CatchBody.IsValid && !site.FinallyBody.IsValid)
                addDiagnostic($"MIR {expectedIndex} has neither catch nor finally body.");
            if (site.CatchBody.IsValid != site.CatchSymbol.IsValid)
            {
                addDiagnostic(
                    $"MIR {expectedIndex} catch body and catch symbol disagree.");
            }
            if (site.VisibleControlScopes.Start != expectedScopeStart ||
                site.VisibleControlScopes.Count < 0 ||
                site.VisibleControlScopes.Start >
                    mir.VisibleControlScopes.Count -
                    site.VisibleControlScopes.Count)
            {
                addDiagnostic(
                    $"MIR visible-scope range for {expectedIndex} is invalid or " +
                    "non-contiguous.");
                continue;
            }
            if (site.VisibleControlScopes.Count == 0)
            {
                if (site.UsesPhysicalParentBranches)
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} uses physical parent branches " +
                        "without visible control scopes.");
                }
                if (site.CompletionStorage.Kind is not
                    Avm1MirCompletionStorageKind.None)
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} has completion storage without " +
                        "visible control scopes.");
                }
            }
            else
                ValidateCompletionStorage(
                    mir,
                    site.CompletionStorage,
                    $"MIR {expectedIndex}",
                    allowNone: site.UsesPhysicalParentBranches,
                    addDiagnostic);

            if (site.UsesPhysicalParentBranches &&
                site.CompletionStorage.Kind is not
                    Avm1MirCompletionStorageKind.None)
            {
                addDiagnostic(
                    $"MIR {expectedIndex} combines physical parent branches " +
                    "with completion storage.");
            }

            for (var scopeIndex = 0;
                scopeIndex < site.VisibleControlScopes.Count;
                scopeIndex++)
            {
                var scope = mir.GetVisibleControlScope(site, scopeIndex);
                if (!scope.Statement.IsValid)
                    addDiagnostic($"MIR {expectedIndex} has an invalid visible scope.");
                if (!Enum.IsDefined(scope.Kind))
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} has an invalid visible-scope kind.");
                }
                if (site.UsesPhysicalParentBranches && scope.IsEnumeration)
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} uses a physical parent branch " +
                        "through an enumeration scope.");
                }
            }
            expectedScopeStart = site.VisibleControlScopes.Start +
                site.VisibleControlScopes.Count;
        }

        if (expectedScopeStart != mir.VisibleControlScopes.Count)
            addDiagnostic("MIR try-site ranges do not cover the visible-scope table.");
    }

    private static void ValidateWithSiteReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresWithSite = instruction.Kind is Avm1MirInstructionKind.With;
        if (!instruction.WithSite.IsValid)
        {
            if (requiresWithSite)
                addDiagnostic($"MIR With in {block} has no with site.");
            return;
        }
        if (instruction.WithSite.Value >= mir.WithSites.Count)
        {
            addDiagnostic(
                $"MIR instruction {instruction.Index} has an invalid with-site " +
                "handle.");
            return;
        }
        if (!requiresWithSite)
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references a " +
                "with site.");
        }
    }

    private static void ValidateWithSites(
        Avm1MirMethod mir,
        Action<string> addDiagnostic)
    {
        var expectedScopeStart = 0;
        for (var siteIndex = 0; siteIndex < mir.WithSites.Count; siteIndex++)
        {
            var expectedIndex = new Avm1MirWithSiteIndex(siteIndex);
            var site = mir.WithSites[siteIndex];
            if (site.Index != expectedIndex)
                addDiagnostic($"MIR with-site identity at index {siteIndex} is inconsistent.");
            if (!site.Statement.IsValid)
                addDiagnostic($"MIR {expectedIndex} has no Source with statement.");
            if (!site.Body.IsValid)
                addDiagnostic($"MIR {expectedIndex} has no with body.");
            if (site.VisibleControlScopes.Start != expectedScopeStart ||
                site.VisibleControlScopes.Count < 0 ||
                site.VisibleControlScopes.Start >
                    mir.WithVisibleControlScopes.Count -
                    site.VisibleControlScopes.Count)
            {
                addDiagnostic(
                    $"MIR visible-scope range for {expectedIndex} is invalid or " +
                    "non-contiguous.");
                continue;
            }
            if (site.VisibleControlScopes.Count == 0)
            {
                if (site.CompletionStorage.Kind is not
                    Avm1MirCompletionStorageKind.None)
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} has completion storage without " +
                        "visible control scopes.");
                }
            }
            else
                ValidateCompletionStorage(
                    mir,
                    site.CompletionStorage,
                    $"MIR {expectedIndex}",
                    allowNone: false,
                    addDiagnostic);

            for (var scopeIndex = 0;
                scopeIndex < site.VisibleControlScopes.Count;
                scopeIndex++)
            {
                var scope = mir.GetVisibleControlScope(site, scopeIndex);
                if (!scope.Statement.IsValid)
                    addDiagnostic($"MIR {expectedIndex} has an invalid visible scope.");
                if (!Enum.IsDefined(scope.Kind))
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} has an invalid visible-scope kind.");
                }
            }
            expectedScopeStart = site.VisibleControlScopes.Start +
                site.VisibleControlScopes.Count;
        }

        if (expectedScopeStart != mir.WithVisibleControlScopes.Count)
            addDiagnostic("MIR with-site ranges do not cover the visible-scope table.");
    }

    private static void ValidateCompletionStorage(
        Avm1MirMethod mir,
        Avm1MirCompletionStorage storage,
        string owner,
        bool allowNone,
        Action<string> addDiagnostic)
    {
        if (!Enum.IsDefined(storage.Kind))
        {
            addDiagnostic($"{owner} has an invalid completion-storage kind.");
            return;
        }

        switch (storage.Kind)
        {
            case Avm1MirCompletionStorageKind.None:
                if (!allowNone)
                    addDiagnostic($"{owner} has no completion storage.");
                if (storage.Name.IsValid || storage.Register != 0)
                    addDiagnostic($"{owner} has malformed empty completion storage.");
                break;

            case Avm1MirCompletionStorageKind.ActivationName:
                if (!storage.Name.IsValid || storage.Name.Value >= mir.Strings.Count)
                    addDiagnostic($"{owner} has an invalid completion variable.");
                if (storage.Register != 0)
                    addDiagnostic($"{owner} activation storage also names a register.");
                break;

            case Avm1MirCompletionStorageKind.Register:
                if (storage.Name.IsValid)
                    addDiagnostic($"{owner} register storage also names a variable.");
                break;
        }
    }

    private static void ValidateTemporaryReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresTemporary = instruction.Kind is
            Avm1MirInstructionKind.LoadTemporary or
            Avm1MirInstructionKind.StoreTemporary or
            Avm1MirInstructionKind.EnumerateNext;
        if (!instruction.Temporary.IsValid)
        {
            if (requiresTemporary)
            {
                addDiagnostic(
                    $"MIR {instruction.Kind} in {block} has no temporary slot.");
            }
            return;
        }
        if (instruction.Temporary.Value >= mir.TemporaryCount)
        {
            addDiagnostic(
                $"MIR instruction {instruction.Index} has an invalid temporary handle.");
            return;
        }
        if (!requiresTemporary)
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references a temporary.");
            return;
        }

        if (instruction.Kind is Avm1MirInstructionKind.LoadTemporary)
        {
            if (!instruction.Result.IsValid)
                addDiagnostic($"MIR LoadTemporary in {block} has no result value.");
            if (instruction.Operand.IsValid)
                addDiagnostic($"MIR LoadTemporary in {block} has an unexpected operand.");
        }
        else if (instruction.Kind is Avm1MirInstructionKind.StoreTemporary)
        {
            if (!instruction.Operand.IsValid)
                addDiagnostic($"MIR StoreTemporary in {block} has no source value.");
        }
        else
        {
            if (instruction.Operand.IsValid || instruction.Result.IsValid)
            {
                addDiagnostic(
                    $"MIR EnumerateNext in {block} has an unexpected value payload.");
            }
        }
    }

    private static void ValidateControlTargetReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresTarget = instruction.Kind is
            Avm1MirInstructionKind.Break or Avm1MirInstructionKind.Continue;
        if (!instruction.ControlTarget.IsValid)
        {
            if (requiresTarget)
            {
                addDiagnostic(
                    $"MIR {instruction.Kind} in {block} has no control target.");
            }
            return;
        }
        if (instruction.ControlTarget.Value >= mir.ControlTargets.Count)
        {
            addDiagnostic(
                $"MIR instruction {instruction.Index} has an invalid " +
                "control-target handle.");
            return;
        }
        if (!requiresTarget)
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references " +
                "a control target.");
        }
    }

    private static void ValidateControlTargets(
        Avm1MirMethod mir,
        Action<string> addDiagnostic)
    {
        for (var targetIndex = 0;
            targetIndex < mir.ControlTargets.Count;
            targetIndex++)
        {
            var expectedIndex = new Avm1MirControlTargetIndex(targetIndex);
            var target = mir.ControlTargets[targetIndex];
            if (target.Index != expectedIndex)
            {
                addDiagnostic(
                    $"MIR control-target identity at index {targetIndex} is inconsistent.");
            }
            if (target.Kind is not (
                Avm1MirControlTargetKind.Loop or
                Avm1MirControlTargetKind.Switch))
            {
                addDiagnostic($"MIR {expectedIndex} has an invalid target kind.");
            }
            if (!target.Statement.IsValid)
                addDiagnostic($"MIR {expectedIndex} has no Source statement identity.");
            if (!IsValidTarget(mir, target.BreakBlock))
                addDiagnostic($"MIR {expectedIndex} has an invalid break block.");
            if (target.Kind is Avm1MirControlTargetKind.Loop &&
                !IsValidTarget(mir, target.ContinueBlock))
            {
                addDiagnostic($"MIR {expectedIndex} has an invalid continue block.");
            }
            if (target.Kind is Avm1MirControlTargetKind.Switch &&
                target.ContinueBlock.IsValid)
            {
                addDiagnostic($"MIR switch {expectedIndex} has a continue block.");
            }
            if (target.Name.IsValid && target.Name.Value >= mir.Strings.Count)
                addDiagnostic($"MIR {expectedIndex} has an invalid label name.");
        }
    }

    private static void ValidatePhiReference(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        var requiresPhi = instruction.Kind is
            Avm1MirInstructionKind.StorePhi or Avm1MirInstructionKind.Phi;
        if (!instruction.PhiSite.IsValid)
        {
            if (requiresPhi)
                addDiagnostic($"MIR {instruction.Kind} in {block} has no phi site.");
            return;
        }
        if (instruction.PhiSite.Value >= mir.PhiSites.Count)
        {
            addDiagnostic(
                $"MIR instruction {instruction.Index} has an invalid phi-site handle.");
            return;
        }
        if (instruction.Kind is not (
            Avm1MirInstructionKind.StorePhi or
            Avm1MirInstructionKind.Phi or
            Avm1MirInstructionKind.Branch or
            Avm1MirInstructionKind.BranchIfTrue))
        {
            addDiagnostic(
                $"MIR {instruction.Kind} in {block} unexpectedly references a phi site.");
        }

        var site = mir[instruction.PhiSite];
        switch (instruction.Kind)
        {
            case Avm1MirInstructionKind.StorePhi when !instruction.Operand.IsValid:
                addDiagnostic($"MIR StorePhi in {block} has no incoming value.");
                break;

            case Avm1MirInstructionKind.Phi:
                if (!instruction.Result.IsValid)
                    addDiagnostic($"MIR Phi in {block} has no result value.");
                if (site.MergeBlock != block)
                {
                    addDiagnostic(
                        $"MIR {instruction.PhiSite} is loaded in {block}, not its " +
                        $"merge block {site.MergeBlock}.");
                }
                break;
        }
    }

    private static void ValidatePhiSites(
        Avm1MirMethod mir,
        Action<string> addDiagnostic)
    {
        var expectedIncomingStart = 0;
        for (var siteIndex = 0; siteIndex < mir.PhiSites.Count; siteIndex++)
        {
            var expectedIndex = new Avm1MirPhiSiteIndex(siteIndex);
            var site = mir.PhiSites[siteIndex];
            if (site.Index != expectedIndex)
                addDiagnostic($"MIR phi-site identity at index {siteIndex} is inconsistent.");
            if (!IsValidTarget(mir, site.MergeBlock))
                addDiagnostic($"MIR {expectedIndex} has an invalid merge block.");
            if (site.Incomings.Start != expectedIncomingStart ||
                site.Incomings.Count < 1 ||
                site.Incomings.Start >
                    mir.PhiIncomings.Count - site.Incomings.Count)
            {
                addDiagnostic(
                    $"MIR incoming range for {expectedIndex} is invalid or non-contiguous.");
                continue;
            }

            var predecessors = new HashSet<Avm1MirBlockIndex>();
            for (var incomingIndex = 0;
                incomingIndex < site.Incomings.Count;
                incomingIndex++)
            {
                var incoming = mir.GetPhiIncoming(site, incomingIndex);
                if (!IsValidTarget(mir, incoming.Predecessor))
                {
                    addDiagnostic($"MIR {expectedIndex} has an invalid predecessor.");
                    continue;
                }
                if (!incoming.Value.IsValid || incoming.Value.Value >= mir.ValueCount)
                    addDiagnostic($"MIR {expectedIndex} has an invalid incoming value.");
                if (!predecessors.Add(incoming.Predecessor))
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} has duplicate predecessor " +
                        $"{incoming.Predecessor}.");
                }

                var predecessor = mir[incoming.Predecessor];
                var end = predecessor.Instructions.Start + predecessor.Instructions.Count;
                var hasStore = false;
                for (var instructionIndex = predecessor.Instructions.Start;
                    instructionIndex < end;
                    instructionIndex++)
                {
                    var instruction = mir.Instructions[instructionIndex];
                    hasStore |= instruction.Kind is Avm1MirInstructionKind.StorePhi &&
                        instruction.PhiSite == expectedIndex &&
                        instruction.Operand == incoming.Value;
                }
                if (!hasStore)
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} predecessor {incoming.Predecessor} " +
                        "does not store its incoming value.");
                }
                if (predecessor.Instructions.Count == 0)
                    continue;

                var terminator = mir.Instructions[end - 1];
                var reachesMerge = terminator.Kind switch
                {
                    Avm1MirInstructionKind.Branch =>
                        terminator.Target == site.MergeBlock,
                    Avm1MirInstructionKind.BranchIfTrue =>
                        terminator.Target == site.MergeBlock ||
                        terminator.AlternativeTarget == site.MergeBlock,
                    _ => false
                };
                if (!reachesMerge || terminator.PhiSite != expectedIndex)
                {
                    addDiagnostic(
                        $"MIR {expectedIndex} predecessor {incoming.Predecessor} " +
                        "does not terminate at its merge block.");
                }
            }

            if (IsValidTarget(mir, site.MergeBlock))
            {
                var merge = mir[site.MergeBlock];
                if (merge.Instructions.Count == 0 ||
                    mir.Instructions[merge.Instructions.Start].Kind is not
                        Avm1MirInstructionKind.Phi ||
                    mir.Instructions[merge.Instructions.Start].PhiSite != expectedIndex)
                {
                    addDiagnostic(
                        $"MIR merge block {site.MergeBlock} does not start with " +
                        $"the load for {expectedIndex}.");
                }
            }

            expectedIncomingStart = site.Incomings.Start + site.Incomings.Count;
        }

        if (expectedIncomingStart != mir.PhiIncomings.Count)
            addDiagnostic("MIR phi ranges do not cover the complete incoming table.");
    }

    private static void ValidateTerminator(
        Avm1MirMethod mir,
        Avm1MirBlockIndex block,
        Avm1MirInstruction instruction,
        Action<string> addDiagnostic)
    {
        switch (instruction.Kind)
        {
            case Avm1MirInstructionKind.Branch:
                if (!IsValidTarget(mir, instruction.Target))
                    addDiagnostic($"MIR branch in {block} has an invalid target.");
                if (instruction.AlternativeTarget.IsValid)
                    addDiagnostic($"MIR branch in {block} has an unexpected alternative target.");
                break;

            case Avm1MirInstructionKind.ExternalControlBranch:
                if (instruction.Target.IsValid ||
                    instruction.AlternativeTarget.IsValid)
                {
                    addDiagnostic(
                        $"MIR external control branch in {block} has a local target.");
                }
                break;

            case Avm1MirInstructionKind.Break:
            case Avm1MirInstructionKind.Continue:
                if (!IsValidTarget(mir, instruction.Target))
                {
                    addDiagnostic(
                        $"MIR {instruction.Kind} in {block} has an invalid target.");
                    break;
                }
                if (!instruction.ControlTarget.IsValid ||
                    instruction.ControlTarget.Value >= mir.ControlTargets.Count)
                {
                    break;
                }

                var controlTarget = mir[instruction.ControlTarget];
                var expectedTarget = instruction.Kind is Avm1MirInstructionKind.Break
                    ? controlTarget.BreakBlock
                    : controlTarget.ContinueBlock;
                var resolvedTarget = instruction.AlternativeTarget.IsValid
                    ? instruction.AlternativeTarget
                    : instruction.Target;
                if (resolvedTarget != expectedTarget)
                {
                    addDiagnostic(
                        $"MIR {instruction.Kind} in {block} does not target its " +
                        "resolved control block.");
                }
                break;

            case Avm1MirInstructionKind.BranchIfTrue:
                if (!instruction.Operand.IsValid)
                    addDiagnostic($"MIR conditional branch in {block} has no condition value.");
                if (!IsValidTarget(mir, instruction.Target) ||
                    !IsValidTarget(mir, instruction.AlternativeTarget))
                {
                    addDiagnostic(
                        $"MIR conditional branch in {block} has an invalid successor.");
                }
                break;

            case Avm1MirInstructionKind.WaitForFrameImmediate:
                if (instruction.Operand.IsValid || !instruction.Constant.IsValid)
                {
                    addDiagnostic(
                        $"MIR immediate frame wait in {block} has an invalid frame payload.");
                }
                if (!IsValidTarget(mir, instruction.Target) ||
                    !IsValidTarget(mir, instruction.AlternativeTarget) ||
                    instruction.Target == instruction.AlternativeTarget)
                {
                    addDiagnostic(
                        $"MIR immediate frame wait in {block} has invalid successors.");
                }
                break;

            case Avm1MirInstructionKind.WaitForFrame:
                if (!instruction.Operand.IsValid || instruction.Constant.IsValid)
                {
                    addDiagnostic(
                        $"MIR stack frame wait in {block} has an invalid frame payload.");
                }
                if (!IsValidTarget(mir, instruction.Target) ||
                    !IsValidTarget(mir, instruction.AlternativeTarget) ||
                    instruction.Target == instruction.AlternativeTarget)
                {
                    addDiagnostic(
                        $"MIR stack frame wait in {block} has invalid successors.");
                }
                break;

            case Avm1MirInstructionKind.EnumerateNext:
                if (!IsValidTarget(mir, instruction.Target) ||
                    !IsValidTarget(mir, instruction.AlternativeTarget) ||
                    instruction.Target == instruction.AlternativeTarget)
                {
                    addDiagnostic(
                        $"MIR EnumerateNext in {block} has invalid successors.");
                }
                break;

            case Avm1MirInstructionKind.EndEnumeration:
                if (!IsValidTarget(mir, instruction.Target))
                {
                    addDiagnostic(
                        $"MIR EndEnumeration in {block} has an invalid continuation.");
                }
                if (instruction.AlternativeTarget.IsValid)
                {
                    addDiagnostic(
                        $"MIR EndEnumeration in {block} has an unexpected " +
                        "alternative target.");
                }
                break;

            case Avm1MirInstructionKind.Throw:
                if (!instruction.Operand.IsValid)
                    addDiagnostic($"MIR Throw in {block} has no value.");
                if (instruction.Result.IsValid)
                    addDiagnostic($"MIR Throw in {block} has an unexpected result.");
                if (instruction.Target.IsValid || instruction.AlternativeTarget.IsValid)
                    addDiagnostic($"MIR Throw in {block} has a branch target.");
                break;

            default:
                if (instruction.Target.IsValid || instruction.AlternativeTarget.IsValid)
                    addDiagnostic($"Non-branch MIR instruction in {block} has a branch target.");
                break;
        }
    }

    private static bool IsTerminator(Avm1MirInstructionKind kind) =>
        kind is Avm1MirInstructionKind.Return or
            Avm1MirInstructionKind.Throw or
            Avm1MirInstructionKind.Break or
            Avm1MirInstructionKind.Continue or
            Avm1MirInstructionKind.ExternalControlBranch or
            Avm1MirInstructionKind.Branch or
            Avm1MirInstructionKind.BranchIfTrue or
            Avm1MirInstructionKind.WaitForFrameImmediate or
            Avm1MirInstructionKind.WaitForFrame or
            Avm1MirInstructionKind.EnumerateNext or
            Avm1MirInstructionKind.EndEnumeration;

    private static bool IsValidTarget(
        Avm1MirMethod mir,
        Avm1MirBlockIndex target) =>
        target.IsValid && target.Value < mir.Blocks.Count;

    private static byte[] CreateTemporaryRegisters(
        Avm1StackLirLoweringOptions options)
    {
        var range = options.ReservedTemporaryRegisters;
        if (range.IsEmpty)
            return [];

        var registers = new byte[range.Count];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = checked((byte)(range.First + i));
        return registers;
    }

    private static bool TryEmitCompletionLoad(
        Avm1MirMethod mir,
        StackLirBuilder builder,
        Avm1MirCompletionStorage storage,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (storage.Kind is Avm1MirCompletionStorageKind.Register)
        {
            builder.EmitPushRegister(storage.Register, instruction.Index);
            return true;
        }
        if (storage.Kind is Avm1MirCompletionStorageKind.ActivationName &&
            storage.Name.IsValid &&
            storage.Name.Value < mir.Strings.Count)
        {
            builder.EmitPushName(mir[storage.Name], instruction.Index);
            builder.EmitAction(
                ActionOpcode.GetVariable,
                stackDelta: 0,
                instruction.Index);
            return true;
        }

        AddInvariant(
            diagnostics,
            instruction,
            "MIR completion load has invalid storage.");
        return false;
    }

    private static bool TryEmitCompletionStore(
        Avm1MirMethod mir,
        StackLirBuilder builder,
        Avm1MirCompletionStorage storage,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (storage.Kind is Avm1MirCompletionStorageKind.Register)
        {
            builder.EmitStoreRegister(storage.Register, instruction.Index);
            builder.EmitAction(
                ActionOpcode.Pop,
                stackDelta: -1,
                instruction.Index);
            return true;
        }
        if (storage.Kind is Avm1MirCompletionStorageKind.ActivationName &&
            storage.Name.IsValid &&
            storage.Name.Value < mir.Strings.Count)
        {
            builder.EmitPushName(mir[storage.Name], instruction.Index);
            builder.EmitAction(
                ActionOpcode.StackSwap,
                stackDelta: 0,
                instruction.Index);
            builder.EmitAction(
                ActionOpcode.SetVariable,
                stackDelta: -2,
                instruction.Index);
            return true;
        }

        AddInvariant(
            diagnostics,
            instruction,
            "MIR completion store has invalid storage.");
        return false;
    }

    private static bool TryGetPhiSite(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1MirPhiSite phiSite)
    {
        if (instruction.PhiSite.IsValid &&
            instruction.PhiSite.Value < mir.PhiSites.Count)
        {
            phiSite = mir[instruction.PhiSite];
            return true;
        }

        phiSite = default;
        AddInvariant(
            diagnostics,
            instruction,
            $"MIR {instruction.Kind} has an invalid phi-site handle.");
        return false;
    }

    private static bool TrySpillStackPrefix(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        StackLirBuilder builder,
        List<Avm1MirValueIndex> stack,
        Dictionary<Avm1MirBlockIndex, StackCarry> stackCarries,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (!TryGetPhiSite(mir, instruction, diagnostics, out var phiSite))
            return false;
        if (stackCarries.ContainsKey(phiSite.MergeBlock))
        {
            AddInvariant(
                diagnostics,
                instruction,
                $"MIR merge block {phiSite.MergeBlock} has more than one " +
                "live stack-prefix carry.");
            return false;
        }
        if (!builder.TryAcquireTemporaries(stack.Count, out var temporaries))
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP110",
                Avm1CompilationDiagnosticSeverity.Error,
                instruction.Origin,
                -1,
                $"Control-flow expression with {stack.Count - 1} outer live " +
                "value(s) requires " + stack.Count + " simultaneous temporary " +
                "register(s) to preserve an empty AVM1 stack at block edges."));
            return false;
        }

        var condition = stack[^1];
        var prefixValues = stack.Take(stack.Count - 1).ToArray();
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            builder.EmitStoreTemporary(temporaries[i], instruction.Index);
            builder.EmitAction(ActionOpcode.Pop, stackDelta: -1, instruction.Index);
        }

        builder.EmitPushTemporary(temporaries[^1], instruction.Index);
        builder.ReleaseTemporary(temporaries[^1]);
        stack.Clear();
        stack.Add(condition);
        stackCarries.Add(
            phiSite.MergeBlock,
            new StackCarry(
                prefixValues,
                temporaries[..^1],
                instruction.Index));
        return true;
    }

    private static void LowerInvocation(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        StackLirBuilder builder,
        List<Avm1MirValueIndex> stack,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (!TryGetCallSite(
            mir,
            instruction,
            diagnostics,
            requireResult: true,
            out var callSite,
            out var name))
        {
            return;
        }

        var targetValueCount = callSite.TargetKind switch
        {
            Avm1MirInvocationTargetKind.Name => 0,
            Avm1MirInvocationTargetKind.Member => 2,
            Avm1MirInvocationTargetKind.Value => 1,
            _ => throw new InvalidOperationException(
                $"Unsupported invocation target kind {callSite.TargetKind}.")
        };
        var components = new Avm1MirValueIndex[
            targetValueCount + callSite.Arguments.Count];
        switch (callSite.TargetKind)
        {
            case Avm1MirInvocationTargetKind.Member:
                components[0] = callSite.Target;
                components[1] = callSite.MemberName;
                break;
            case Avm1MirInvocationTargetKind.Value:
                components[0] = callSite.Target;
                break;
        }
        for (var i = 0; i < callSite.Arguments.Count; i++)
            components[targetValueCount + i] = mir.GetCallArgument(callSite, i);

        if (callSite.TargetEvaluationOrder is
            Avm1MirInvocationTargetOrder.AfterArguments)
        {
            LowerInvocationWithArgumentsFirst(
                mir,
                instruction,
                callSite,
                name,
                components,
                targetValueCount,
                builder,
                stack,
                diagnostics);
            return;
        }

        var evaluationComponents = (Avm1MirValueIndex[])components.Clone();
        if (callSite.ArgumentEvaluationOrder is Avm1MirEvaluationOrder.Reverse)
        {
            Array.Reverse(
                evaluationComponents,
                targetValueCount,
                callSite.Arguments.Count);
        }

        if (!ExpectSuffix(
            stack,
            evaluationComponents,
            instruction,
            "invocation components",
            diagnostics))
            return;
        if (!builder.TryAcquireTemporaries(
            components.Length,
            out var temporaries))
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP107",
                Avm1CompilationDiagnosticSeverity.Error,
                instruction.Origin,
                -1,
                $"Invocation requires {components.Length} simultaneous temporary " +
                "storage slot(s), but the method plan provides fewer. " +
                builder.DescribeTemporaryPressure()));
            return;
        }

        for (var i = evaluationComponents.Length - 1; i >= 0; i--)
        {
            var logicalIndex = i;
            if (callSite.ArgumentEvaluationOrder is
                    Avm1MirEvaluationOrder.Reverse &&
                i >= targetValueCount)
            {
                logicalIndex = targetValueCount +
                    callSite.Arguments.Count - 1 -
                    (i - targetValueCount);
            }
            builder.EmitStoreTemporary(
                temporaries[logicalIndex],
                instruction.Index);
            builder.EmitAction(ActionOpcode.Pop, stackDelta: -1, instruction.Index);
            stack.RemoveAt(stack.Count - 1);
        }

        var argumentStart = targetValueCount;
        for (var i = callSite.Arguments.Count - 1; i >= 0; i--)
        {
            builder.EmitPushTemporary(
                temporaries[argumentStart + i],
                instruction.Index);
        }
        builder.EmitInteger(callSite.Arguments.Count, instruction.Index);

        ActionOpcode opcode;
        var stackDelta = -(callSite.Arguments.Count + 1);
        switch (callSite.TargetKind)
        {
            case Avm1MirInvocationTargetKind.Name:
                builder.EmitPushName(name!, instruction.Index);
                opcode = callSite.Kind is Avm1MirInvocationKind.Call
                    ? ActionOpcode.CallFunction
                    : ActionOpcode.NewObject;
                break;

            case Avm1MirInvocationTargetKind.Member:
                builder.EmitPushTemporary(temporaries[0], instruction.Index);
                builder.EmitPushTemporary(temporaries[1], instruction.Index);
                opcode = callSite.Kind is Avm1MirInvocationKind.Call
                    ? ActionOpcode.CallMethod
                    : ActionOpcode.NewMethod;
                stackDelta--;
                break;

            case Avm1MirInvocationTargetKind.Value:
                builder.EmitPushTemporary(temporaries[0], instruction.Index);
                builder.EmitPushName(string.Empty, instruction.Index);
                opcode = callSite.Kind is Avm1MirInvocationKind.Call
                    ? ActionOpcode.CallMethod
                    : ActionOpcode.NewMethod;
                stackDelta--;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported invocation target kind {callSite.TargetKind}.");
        }

        builder.EmitAction(opcode, stackDelta, instruction.Index);
        for (var i = temporaries.Length - 1; i >= 0; i--)
            builder.ReleaseTemporary(temporaries[i]);
        stack.Add(instruction.Result);
    }

    private static void LowerInvocationWithArgumentsFirst(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        Avm1MirCallSite callSite,
        string? name,
        Avm1MirValueIndex[] components,
        int targetValueCount,
        StackLirBuilder builder,
        List<Avm1MirValueIndex> stack,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (callSite.ArgumentEvaluationOrder is not
            Avm1MirEvaluationOrder.Reverse)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "Arguments-first invocation requires reverse argument evaluation.");
            return;
        }

        var evaluationComponents = new Avm1MirValueIndex[components.Length];
        for (var i = 0; i < callSite.Arguments.Count; i++)
        {
            evaluationComponents[i] = mir.GetCallArgument(
                callSite,
                callSite.Arguments.Count - 1 - i);
        }
        for (var i = 0; i < targetValueCount; i++)
        {
            evaluationComponents[callSite.Arguments.Count + i] =
                components[i];
        }

        if (!ExpectSuffix(
                stack,
                evaluationComponents,
                instruction,
                "arguments-first invocation components",
                diagnostics))
        {
            return;
        }
        if (!builder.TryAcquireTemporaries(
                targetValueCount,
                out var targetTemporaries))
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP107",
                Avm1CompilationDiagnosticSeverity.Error,
                instruction.Origin,
                -1,
                $"Invocation target requires {targetValueCount} simultaneous " +
                "temporary storage slot(s), but the method plan provides fewer. " +
                builder.DescribeTemporaryPressure()));
            return;
        }

        for (var i = targetValueCount - 1; i >= 0; i--)
        {
            builder.EmitStoreTemporary(
                targetTemporaries[i],
                instruction.Index);
            builder.EmitAction(
                ActionOpcode.Pop,
                stackDelta: -1,
                instruction.Index);
            stack.RemoveAt(stack.Count - 1);
        }

        builder.EmitInteger(callSite.Arguments.Count, instruction.Index);
        ActionOpcode opcode;
        var stackDelta = -(callSite.Arguments.Count + 1);
        switch (callSite.TargetKind)
        {
            case Avm1MirInvocationTargetKind.Name:
                builder.EmitPushName(name!, instruction.Index);
                opcode = callSite.Kind is Avm1MirInvocationKind.Call
                    ? ActionOpcode.CallFunction
                    : ActionOpcode.NewObject;
                break;

            case Avm1MirInvocationTargetKind.Member:
                builder.EmitPushTemporary(
                    targetTemporaries[0],
                    instruction.Index);
                builder.EmitPushTemporary(
                    targetTemporaries[1],
                    instruction.Index);
                opcode = callSite.Kind is Avm1MirInvocationKind.Call
                    ? ActionOpcode.CallMethod
                    : ActionOpcode.NewMethod;
                stackDelta--;
                break;

            case Avm1MirInvocationTargetKind.Value:
                builder.EmitPushTemporary(
                    targetTemporaries[0],
                    instruction.Index);
                builder.EmitPushName(string.Empty, instruction.Index);
                opcode = callSite.Kind is Avm1MirInvocationKind.Call
                    ? ActionOpcode.CallMethod
                    : ActionOpcode.NewMethod;
                stackDelta--;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported invocation target kind {callSite.TargetKind}.");
        }

        builder.EmitAction(opcode, stackDelta, instruction.Index);
        for (var i = targetTemporaries.Length - 1; i >= 0; i--)
            builder.ReleaseTemporary(targetTemporaries[i]);
        if (callSite.Arguments.Count != 0)
        {
            stack.RemoveRange(
                stack.Count - callSite.Arguments.Count,
                callSite.Arguments.Count);
        }
        stack.Add(instruction.Result);
    }

    private static void LowerAggregate(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        StackLirBuilder builder,
        List<Avm1MirValueIndex> stack,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (!TryGetAggregateSite(
            mir,
            instruction,
            diagnostics,
            out var aggregateSite))
        {
            return;
        }

        var values = new Avm1MirValueIndex[aggregateSite.Values.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = mir.GetAggregateValue(aggregateSite, i);

        var evaluationValues = (Avm1MirValueIndex[])values.Clone();
        if (aggregateSite.ValueEvaluationOrder is Avm1MirEvaluationOrder.Reverse)
            Array.Reverse(evaluationValues);

        if (!ExpectSuffix(
            stack,
            evaluationValues,
            instruction,
            "aggregate values",
            diagnostics))
        {
            return;
        }
        if (!builder.TryAcquireTemporaries(values.Length, out var temporaries))
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP108",
                Avm1CompilationDiagnosticSeverity.Error,
                instruction.Origin,
                -1,
                $"Aggregate literal requires {values.Length} simultaneous temporary " +
                "register(s), but the method plan provides fewer."));
            return;
        }

        for (var i = evaluationValues.Length - 1; i >= 0; i--)
        {
            var logicalIndex = aggregateSite.ValueEvaluationOrder is
                Avm1MirEvaluationOrder.Reverse
                    ? evaluationValues.Length - 1 - i
                    : i;
            builder.EmitStoreTemporary(
                temporaries[logicalIndex],
                instruction.Index);
            builder.EmitAction(ActionOpcode.Pop, stackDelta: -1, instruction.Index);
            stack.RemoveAt(stack.Count - 1);
        }

        ActionOpcode opcode;
        int itemCount;
        switch (aggregateSite.Kind)
        {
            case Avm1MirAggregateKind.Array:
                for (var i = temporaries.Length - 1; i >= 0; i--)
                    builder.EmitPushTemporary(temporaries[i], instruction.Index);
                itemCount = temporaries.Length;
                opcode = ActionOpcode.InitArray;
                break;

            case Avm1MirAggregateKind.Object:
                for (var pair = 0; pair < temporaries.Length / 2; pair++)
                {
                    builder.EmitPushTemporary(
                        temporaries[pair * 2],
                        instruction.Index);
                    builder.EmitPushTemporary(
                        temporaries[pair * 2 + 1],
                        instruction.Index);
                }
                itemCount = temporaries.Length / 2;
                opcode = ActionOpcode.InitObject;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported aggregate kind {aggregateSite.Kind}.");
        }

        builder.EmitInteger(itemCount, instruction.Index);
        builder.EmitAction(opcode, -values.Length, instruction.Index);
        for (var i = temporaries.Length - 1; i >= 0; i--)
            builder.ReleaseTemporary(temporaries[i]);
        stack.Add(instruction.Result);
    }

    private static void LowerIntrinsic(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        StackLirBuilder builder,
        List<Avm1MirValueIndex> stack,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (!TryGetCallSite(
                mir,
                instruction,
                diagnostics,
                requireResult: false,
                out var callSite,
                out var name))
        {
            return;
        }

        if (instruction.Operator is Avm1MirOperator.StartDrag)
        {
            LowerStartDragIntrinsic(
                mir,
                instruction,
                callSite,
                name,
                builder,
                stack,
                diagnostics);
            return;
        }

        var (opcode, expectedName, arity, producesResult) =
            instruction.Operator switch
            {
                Avm1MirOperator.StringExtract =>
                    (ActionOpcode.StringExtract, "substring", 3, true),
                Avm1MirOperator.MbStringExtract =>
                    (ActionOpcode.MBStringExtract, "mbsubstring", 3, true),
                Avm1MirOperator.GetProperty =>
                    (ActionOpcode.GetProperty, "getProperty", 2, true),
                Avm1MirOperator.SetProperty =>
                    (ActionOpcode.SetProperty, "setProperty", 3, false),
                Avm1MirOperator.CloneSprite =>
                    (ActionOpcode.CloneSprite, "duplicateMovieClip", 3, false),
                Avm1MirOperator.RemoveSprite =>
                    (ActionOpcode.RemoveSprite, "removeMovieClip", 1, false),
                Avm1MirOperator.EndDrag =>
                    (ActionOpcode.EndDrag, "stopDrag", 0, false),
                _ => (default, null, -1, false)
            };
        if (expectedName is null ||
            callSite.TargetKind is not Avm1MirInvocationTargetKind.Name ||
            callSite.Kind is not Avm1MirInvocationKind.Call ||
            callSite.TargetEvaluationOrder is not
                Avm1MirInvocationTargetOrder.BeforeArguments ||
            callSite.ArgumentEvaluationOrder is not Avm1MirEvaluationOrder.Forward ||
            callSite.Arguments.Count != arity ||
            !string.Equals(name, expectedName, StringComparison.Ordinal) ||
            instruction.Result.IsValid != producesResult ||
            instruction.Operand.IsValid ||
            instruction.SecondaryOperand.IsValid)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR intrinsic has invalid metadata.");
            return;
        }

        var arguments = new Avm1MirValueIndex[callSite.Arguments.Count];
        for (var i = 0; i < arguments.Length; i++)
            arguments[i] = mir.GetCallArgument(callSite, i);
        if (!ExpectSuffix(
                stack,
                arguments,
                instruction,
                "intrinsic arguments",
                diagnostics))
        {
            return;
        }

        builder.EmitAction(
            opcode,
            stackDelta: producesResult ? 1 - arguments.Length : -arguments.Length,
            instruction.Index);
        stack.RemoveRange(stack.Count - arguments.Length, arguments.Length);
        if (producesResult)
            stack.Add(instruction.Result);
    }

    private static void LowerStartDragIntrinsic(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        Avm1MirCallSite callSite,
        string? name,
        StackLirBuilder builder,
        List<Avm1MirValueIndex> stack,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (callSite.TargetKind is not Avm1MirInvocationTargetKind.Name ||
            callSite.Kind is not Avm1MirInvocationKind.Call ||
            callSite.TargetEvaluationOrder is not
                Avm1MirInvocationTargetOrder.BeforeArguments ||
            callSite.ArgumentEvaluationOrder is not Avm1MirEvaluationOrder.Forward ||
            callSite.Arguments.Count is not (2 or 6) ||
            !string.Equals(name, "startDrag", StringComparison.Ordinal) ||
            instruction.Result.IsValid ||
            instruction.Operand.IsValid ||
            instruction.SecondaryOperand.IsValid)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR startDrag intrinsic has invalid metadata.");
            return;
        }

        var arguments = new Avm1MirValueIndex[callSite.Arguments.Count];
        for (var i = 0; i < arguments.Length; i++)
            arguments[i] = mir.GetCallArgument(callSite, i);
        if (!ExpectSuffix(
                stack,
                arguments,
                instruction,
                "startDrag arguments",
                diagnostics))
        {
            return;
        }
        if (!builder.TryAcquireTemporaries(arguments.Length, out var temporaries))
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                "AVM1CMP107",
                Avm1CompilationDiagnosticSeverity.Error,
                instruction.Origin,
                -1,
                $"startDrag requires {arguments.Length} simultaneous temporary " +
                "register(s), but the method plan provides fewer."));
            return;
        }

        for (var i = arguments.Length - 1; i >= 0; i--)
        {
            builder.EmitStoreTemporary(temporaries[i], instruction.Index);
            builder.EmitAction(ActionOpcode.Pop, stackDelta: -1, instruction.Index);
            stack.RemoveAt(stack.Count - 1);
        }

        if (arguments.Length == 6)
        {
            for (var i = 2; i < arguments.Length; i++)
                builder.EmitPushTemporary(temporaries[i], instruction.Index);
            builder.EmitBoolean(true, instruction.Index);
        }
        else
        {
            builder.EmitBoolean(false, instruction.Index);
        }
        builder.EmitPushTemporary(temporaries[1], instruction.Index);
        builder.EmitPushTemporary(temporaries[0], instruction.Index);
        builder.EmitAction(
            ActionOpcode.StartDrag,
            stackDelta: arguments.Length == 6 ? -7 : -3,
            instruction.Index);

        for (var i = temporaries.Length - 1; i >= 0; i--)
            builder.ReleaseTemporary(temporaries[i]);
    }

    private static bool TryGetCallSite(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics,
        bool requireResult,
        out Avm1MirCallSite callSite,
        out string? name)
    {
        callSite = default;
        name = null;
        if (!instruction.CallSite.IsValid ||
            instruction.CallSite.Value >= mir.CallSites.Count)
        {
            AddInvariant(diagnostics, instruction, "MIR call-site handle is invalid.");
            return false;
        }

        callSite = mir[instruction.CallSite];
        if (requireResult && !instruction.Result.IsValid)
        {
            AddInvariant(diagnostics, instruction, "MIR invocation has no result value.");
            return false;
        }
        if (callSite.Arguments.Start < 0 ||
            callSite.Arguments.Count < 0 ||
            callSite.Arguments.Start >
                mir.CallArguments.Count - callSite.Arguments.Count)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR call-site argument range is invalid.");
            return false;
        }
        if (!Enum.IsDefined(callSite.ArgumentEvaluationOrder))
        {
            AddInvariant(
                diagnostics,
                instruction,
                $"MIR argument evaluation order " +
                $"{callSite.ArgumentEvaluationOrder} is invalid.");
            return false;
        }
        if (!Enum.IsDefined(callSite.TargetEvaluationOrder))
        {
            AddInvariant(
                diagnostics,
                instruction,
                $"MIR invocation target evaluation order " +
                $"{callSite.TargetEvaluationOrder} is invalid.");
            return false;
        }

        switch (callSite.TargetKind)
        {
            case Avm1MirInvocationTargetKind.Name:
                if (!callSite.Name.IsValid || callSite.Name.Value >= mir.Strings.Count)
                {
                    AddInvariant(
                        diagnostics,
                        instruction,
                        "MIR named invocation target is invalid.");
                    return false;
                }
                name = mir[callSite.Name];
                break;

            case Avm1MirInvocationTargetKind.Member:
                if (!callSite.Target.IsValid || !callSite.MemberName.IsValid)
                {
                    AddInvariant(
                        diagnostics,
                        instruction,
                        "MIR member invocation target is invalid.");
                    return false;
                }
                break;

            case Avm1MirInvocationTargetKind.Value:
                if (!callSite.Target.IsValid)
                {
                    AddInvariant(
                        diagnostics,
                        instruction,
                        "MIR value invocation target is invalid.");
                    return false;
                }
                break;

            default:
                AddInvariant(
                    diagnostics,
                    instruction,
                    $"MIR invocation target kind {callSite.TargetKind} is invalid.");
                return false;
        }

        for (var i = 0; i < callSite.Arguments.Count; i++)
        {
            if (mir.GetCallArgument(callSite, i).IsValid)
                continue;

            AddInvariant(
                diagnostics,
                instruction,
                $"MIR invocation argument {i} is invalid.");
            return false;
        }
        return true;
    }

    private static bool TryGetAggregateSite(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1MirAggregateSite aggregateSite)
    {
        aggregateSite = default;
        if (!instruction.AggregateSite.IsValid ||
            instruction.AggregateSite.Value >= mir.AggregateSites.Count)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR aggregate-site handle is invalid.");
            return false;
        }

        aggregateSite = mir[instruction.AggregateSite];
        if (!instruction.Result.IsValid)
        {
            AddInvariant(diagnostics, instruction, "MIR aggregate has no result value.");
            return false;
        }
        if (aggregateSite.Values.Start < 0 ||
            aggregateSite.Values.Count < 0 ||
            aggregateSite.Values.Start >
                mir.AggregateValues.Count - aggregateSite.Values.Count)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR aggregate value range is invalid.");
            return false;
        }
        if (!Enum.IsDefined(aggregateSite.ValueEvaluationOrder))
        {
            AddInvariant(
                diagnostics,
                instruction,
                $"MIR aggregate evaluation order " +
                $"{aggregateSite.ValueEvaluationOrder} is invalid.");
            return false;
        }
        if (aggregateSite.Kind is Avm1MirAggregateKind.Object &&
            aggregateSite.Values.Count % 2 != 0)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR object aggregate has an odd key/value count.");
            return false;
        }
        if (aggregateSite.Kind is not (
            Avm1MirAggregateKind.Array or Avm1MirAggregateKind.Object))
        {
            AddInvariant(
                diagnostics,
                instruction,
                $"MIR aggregate kind {aggregateSite.Kind} is invalid.");
            return false;
        }

        for (var i = 0; i < aggregateSite.Values.Count; i++)
        {
            if (mir.GetAggregateValue(aggregateSite, i).IsValid)
                continue;

            AddInvariant(
                diagnostics,
                instruction,
                $"MIR aggregate value {i} is invalid.");
            return false;
        }
        return true;
    }

    private static bool TryGetConstant(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1MirConstant constant)
    {
        if (!instruction.Constant.IsValid ||
            instruction.Constant.Value >= mir.Constants.Count)
        {
            constant = default;
            AddInvariant(diagnostics, instruction, "MIR constant handle is invalid.");
            return false;
        }

        constant = mir[instruction.Constant];
        if (constant.Kind is Avm1MirConstantKind.String &&
            (!constant.StringValue.IsValid ||
                constant.StringValue.Value >= mir.Strings.Count))
        {
            AddInvariant(diagnostics, instruction, "MIR string constant is invalid.");
            return false;
        }
        return true;
    }

    private static bool TryGetName(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics,
        out string name)
    {
        if (!instruction.Name.IsValid || instruction.Name.Value >= mir.Strings.Count)
        {
            name = string.Empty;
            AddInvariant(diagnostics, instruction, "MIR name handle is invalid.");
            return false;
        }

        name = mir[instruction.Name];
        return true;
    }

    private static bool TryGetLValue(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1MirLValue lValue)
    {
        if (!instruction.LValue.IsValid ||
            instruction.LValue.Value >= mir.LValues.Count)
        {
            lValue = default;
            AddInvariant(diagnostics, instruction, "MIR lvalue handle is invalid.");
            return false;
        }

        lValue = mir[instruction.LValue];
        if (lValue.Kind is Avm1MirLValueKind.Member)
        {
            if (!lValue.Receiver.IsValid || !lValue.Key.IsValid)
            {
                AddInvariant(
                    diagnostics,
                    instruction,
                    "MIR member lvalue has no valid receiver or key.");
                return false;
            }
        }
        else if (lValue.Kind is Avm1MirLValueKind.ComputedName)
        {
            if (!lValue.Key.IsValid)
            {
                AddInvariant(
                    diagnostics,
                    instruction,
                    "MIR computed-name lvalue has no valid name value.");
                return false;
            }
        }
        else if (lValue.Kind is Avm1MirLValueKind.CapturedMember)
        {
            if (!lValue.ReceiverTemporary.IsValid ||
                lValue.ReceiverTemporary.Value >= mir.TemporaryCount ||
                !lValue.KeyTemporary.IsValid ||
                lValue.KeyTemporary.Value >= mir.TemporaryCount ||
                lValue.ReceiverTemporary == lValue.KeyTemporary)
            {
                AddInvariant(
                    diagnostics,
                    instruction,
                    "MIR captured member lvalue has invalid address temporaries.");
                return false;
            }
        }
        else if (lValue.Kind is Avm1MirLValueKind.Register)
        {
            if (lValue.Register == 0 ||
                !lValue.Symbol.IsValid &&
                instruction.Kind is not
                    Avm1MirInstructionKind.LoadCurrentFunction)
            {
                AddInvariant(
                    diagnostics,
                    instruction,
                    "MIR register lvalue has invalid symbol or register metadata.");
                return false;
            }
        }
        else if (lValue.Kind is not Avm1MirLValueKind.Name)
        {
            AddInvariant(diagnostics, instruction, "MIR lvalue kind is invalid.");
            return false;
        }
        return true;
    }

    private static bool TryGetCapturedMemberTemporaries(
        StackLirBuilder builder,
        Avm1MirInstruction instruction,
        Avm1MirLValue lValue,
        List<Avm1CompilerDiagnostic> diagnostics,
        out Avm1StackLirTemporaryIndex receiverTemporary,
        out Avm1StackLirTemporaryIndex keyTemporary)
    {
        receiverTemporary = Avm1StackLirTemporaryIndex.Invalid;
        keyTemporary = Avm1StackLirTemporaryIndex.Invalid;
        if (lValue.Kind is not Avm1MirLValueKind.CapturedMember)
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR lvalue does not contain a captured member address.");
            return false;
        }
        if (!builder.TryGetMirTemporary(
                lValue.ReceiverTemporary,
                out receiverTemporary) ||
            !builder.TryGetMirTemporary(lValue.KeyTemporary, out keyTemporary))
        {
            AddInvariant(
                diagnostics,
                instruction,
                "MIR captured member address is used before it is stored.");
            return false;
        }
        return true;
    }

    private static bool TryGetLValueName(
        Avm1MirMethod mir,
        Avm1MirInstruction instruction,
        Avm1MirLValue lValue,
        List<Avm1CompilerDiagnostic> diagnostics,
        out string name)
    {
        if (lValue.Kind is not Avm1MirLValueKind.Name ||
            !lValue.Name.IsValid ||
            lValue.Name.Value >= mir.Strings.Count)
        {
            name = string.Empty;
            AddInvariant(diagnostics, instruction, "MIR name lvalue is invalid.");
            return false;
        }

        name = mir[lValue.Name];
        return true;
    }

    private static bool ExpectTop(
        List<Avm1MirValueIndex> stack,
        Avm1MirValueIndex expected,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (stack.Count > 0 && stack[^1] == expected)
            return true;

        AddInvariant(
            diagnostics,
            instruction,
            $"MIR operand {expected} is not the current stack value.");
        return false;
    }

    private static bool ExpectBinaryOperands(
        List<Avm1MirValueIndex> stack,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (stack.Count >= 2 &&
            stack[^2] == instruction.Operand &&
            stack[^1] == instruction.SecondaryOperand)
        {
            return true;
        }

        AddInvariant(
            diagnostics,
            instruction,
            $"MIR operands {instruction.Operand}, {instruction.SecondaryOperand} " +
            "are not in source evaluation order on the virtual stack.");
        return false;
    }

    private static bool ExpectMemberOperands(
        List<Avm1MirValueIndex> stack,
        Avm1MirLValue lValue,
        Avm1MirInstruction instruction,
        Avm1MirValueIndex value,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        var expectedCount = value.IsValid ? 3 : 2;
        var receiverIndex = stack.Count - expectedCount;
        if (receiverIndex >= 0 &&
            stack[receiverIndex] == lValue.Receiver &&
            stack[receiverIndex + 1] == lValue.Key &&
            (!value.IsValid || stack[receiverIndex + 2] == value))
        {
            return true;
        }

        var suffix = value.IsValid ? $", {value}" : string.Empty;
        AddInvariant(
            diagnostics,
            instruction,
            $"MIR member operands {lValue.Receiver}, {lValue.Key}{suffix} " +
            "are not in source evaluation order on the virtual stack.");
        return false;
    }

    private static bool ExpectSuffix(
        List<Avm1MirValueIndex> stack,
        Avm1MirValueIndex[] expected,
        Avm1MirInstruction instruction,
        string description,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        var start = stack.Count - expected.Length;
        if (start >= 0)
        {
            var matches = true;
            for (var i = 0; i < expected.Length; i++)
            {
                if (stack[start + i] == expected[i])
                    continue;

                matches = false;
                break;
            }
            if (matches)
                return true;
        }

        AddInvariant(
            diagnostics,
            instruction,
            $"MIR {description} are not a contiguous virtual-stack suffix " +
            "in source evaluation order.");
        return false;
    }

    private static void RequireEmptyBoundary(
        List<Avm1MirValueIndex> stack,
        Avm1MirInstruction instruction,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (stack.Count == 0)
            return;

        AddInvariant(
            diagnostics,
            instruction,
            $"Statement boundary retains {stack.Count} MIR value(s).");
    }

    private static void AddInvariant(
        List<Avm1CompilerDiagnostic> diagnostics,
        Avm1MirInstruction instruction,
        string message) =>
        diagnostics.Add(new Avm1CompilerDiagnostic(
            "AVM1CMP103",
            Avm1CompilationDiagnosticSeverity.Error,
            instruction.Origin,
            -1,
            message));

    private static bool TryGetUnaryAction(
        Avm1MirOperator @operator,
        out ActionOpcode opcode)
    {
        opcode = @operator switch
        {
            Avm1MirOperator.LogicalNot => ActionOpcode.Not,
            Avm1MirOperator.TypeOf => ActionOpcode.TypeOf,
            Avm1MirOperator.ToNumber => ActionOpcode.ToNumber,
            Avm1MirOperator.ToInteger => ActionOpcode.ToInteger,
            Avm1MirOperator.RandomNumber => ActionOpcode.RandomNumber,
            Avm1MirOperator.StringLength => ActionOpcode.StringLength,
            Avm1MirOperator.MbStringLength => ActionOpcode.MBStringLength,
            Avm1MirOperator.CharToAscii => ActionOpcode.CharToAscii,
            Avm1MirOperator.AsciiToChar => ActionOpcode.AsciiToChar,
            Avm1MirOperator.MbCharToAscii => ActionOpcode.MBCharToAscii,
            Avm1MirOperator.MbAsciiToChar => ActionOpcode.MBAsciiToChar,
            Avm1MirOperator.ToString => ActionOpcode.ToString,
            Avm1MirOperator.TargetPath => ActionOpcode.TargetPath,
            Avm1MirOperator.Increment => ActionOpcode.Increment,
            Avm1MirOperator.Decrement => ActionOpcode.Decrement,
            _ => default
        };
        return @operator is
            Avm1MirOperator.LogicalNot or
            Avm1MirOperator.TypeOf or
            Avm1MirOperator.ToNumber or
            Avm1MirOperator.ToInteger or
            Avm1MirOperator.RandomNumber or
            Avm1MirOperator.StringLength or
            Avm1MirOperator.MbStringLength or
            Avm1MirOperator.CharToAscii or
            Avm1MirOperator.AsciiToChar or
            Avm1MirOperator.MbCharToAscii or
            Avm1MirOperator.MbAsciiToChar or
            Avm1MirOperator.ToString or
            Avm1MirOperator.TargetPath or
            Avm1MirOperator.Increment or
            Avm1MirOperator.Decrement;
    }

    private static bool TryGetBinaryActions(
        Avm1MirOperator @operator,
        out ActionOpcode opcode,
        out bool negate)
    {
        negate = @operator is
            Avm1MirOperator.NotEqual or
            Avm1MirOperator.StrictNotEqual or
            Avm1MirOperator.LessOrEqual or
            Avm1MirOperator.GreaterOrEqual;
        opcode = @operator switch
        {
            Avm1MirOperator.Add => ActionOpcode.Add2,
            Avm1MirOperator.StringAdd => ActionOpcode.StringAdd,
            Avm1MirOperator.Subtract => ActionOpcode.Subtract,
            Avm1MirOperator.Multiply => ActionOpcode.Multiply,
            Avm1MirOperator.Divide => ActionOpcode.Divide,
            Avm1MirOperator.Modulo => ActionOpcode.Modulo,
            Avm1MirOperator.Equal or Avm1MirOperator.NotEqual =>
                ActionOpcode.Equals2,
            Avm1MirOperator.StrictEqual or Avm1MirOperator.StrictNotEqual =>
                ActionOpcode.StrictEquals,
            Avm1MirOperator.Less or Avm1MirOperator.GreaterOrEqual =>
                ActionOpcode.Less2,
            Avm1MirOperator.Greater or Avm1MirOperator.LessOrEqual =>
                ActionOpcode.Greater,
            Avm1MirOperator.BitAnd => ActionOpcode.BitAnd,
            Avm1MirOperator.BitOr => ActionOpcode.BitOr,
            Avm1MirOperator.BitXor => ActionOpcode.BitXor,
            Avm1MirOperator.ShiftLeft => ActionOpcode.BitLShift,
            Avm1MirOperator.ShiftRight => ActionOpcode.BitRShift,
            Avm1MirOperator.ShiftRightUnsigned => ActionOpcode.BitURShift,
            Avm1MirOperator.InstanceOf => ActionOpcode.InstanceOf,
            _ => default
        };
        return @operator is
            Avm1MirOperator.Add or
            Avm1MirOperator.StringAdd or
            Avm1MirOperator.Subtract or
            Avm1MirOperator.Multiply or
            Avm1MirOperator.Divide or
            Avm1MirOperator.Modulo or
            Avm1MirOperator.Equal or
            Avm1MirOperator.NotEqual or
            Avm1MirOperator.StrictEqual or
            Avm1MirOperator.StrictNotEqual or
            Avm1MirOperator.Less or
            Avm1MirOperator.LessOrEqual or
            Avm1MirOperator.Greater or
            Avm1MirOperator.GreaterOrEqual or
            Avm1MirOperator.BitAnd or
            Avm1MirOperator.BitOr or
            Avm1MirOperator.BitXor or
            Avm1MirOperator.ShiftLeft or
            Avm1MirOperator.ShiftRight or
            Avm1MirOperator.ShiftRightUnsigned or
            Avm1MirOperator.InstanceOf;
    }

    private sealed record StackCarry(
        Avm1MirValueIndex[] Values,
        Avm1StackLirTemporaryIndex[] Temporaries,
        Avm1MirInstructionIndex Owner);

    private sealed class StackLirBuilder
    {
        private readonly List<Avm1StackLirInstruction> _instructions = [];
        private readonly List<Avm1StackLirBlock> _blocks = [];
        private readonly List<Avm1StackLirConstant> _constants = [];
        private readonly List<Avm1StackLirFunction> _functions = [];
        private readonly List<Avm1StackLirTry> _trySites = [];
        private readonly List<Avm1StackLirWith> _withSites = [];
        private readonly List<Avm1StackLirUrlSite> _urlSites = [];
        private readonly List<string> _strings = [];
        private readonly List<Avm1StackLirTemporaryStorage> _temporaryStorages = [];
        private readonly Stack<Avm1StackLirTemporaryIndex> _freeTemporaries = [];
        private readonly Dictionary<Avm1MirPhiSiteIndex, Avm1StackLirTemporaryIndex>
            _phiTemporaries = [];
        private readonly Dictionary<Avm1MirTemporaryIndex, Avm1StackLirTemporaryIndex>
            _mirTemporaries = [];
        private readonly Dictionary<string, Avm1StackLirStringIndex> _stringIndices =
            new(StringComparer.Ordinal);
        private readonly byte[] _reservedTemporaryRegisters;
        private readonly string? _activationTemporaryPrefix;
        private Avm1MirBlockIndex _currentBlock = Avm1MirBlockIndex.Invalid;
        private int _currentBlockStart;
        private int _stackDepth;
        private int _maximumStackDepth;

        public StackLirBuilder(
            byte[] reservedTemporaryRegisters,
            string? activationTemporaryPrefix)
        {
            _reservedTemporaryRegisters = reservedTemporaryRegisters;
            _activationTemporaryPrefix = activationTemporaryPrefix;
        }

        public bool HasInvalidStack { get; private set; }

        public bool HasLivePhiTemporaries => _phiTemporaries.Count != 0;

        public void StartBlock(Avm1MirBlockIndex block)
        {
            FinishCurrentBlock();
            _currentBlock = block;
            _currentBlockStart = _instructions.Count;
        }

        public void EmitPushConstant(
            Avm1MirConstant constant,
            Avm1MirMethod mir,
            Avm1MirInstructionIndex owner)
        {
            string? stringValue = constant.Kind is Avm1MirConstantKind.String
                ? mir[constant.StringValue]
                : null;
            var index = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                index,
                constant.Kind,
                constant.BooleanValue,
                constant.IntegerValue,
                constant.NumberValue,
                stringValue is null
                    ? Avm1StackLirStringIndex.Invalid
                    : InternString(stringValue)));
            Add(
                Avm1StackLirInstructionKind.PushConstant,
                index,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1);
        }

        public void EmitUndefined(Avm1MirInstructionIndex owner)
        {
            var index = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                index,
                Avm1MirConstantKind.Undefined,
                false,
                0,
                0,
                Avm1StackLirStringIndex.Invalid));
            Add(
                Avm1StackLirInstructionKind.PushConstant,
                index,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1);
        }

        public void EmitNull(Avm1MirInstructionIndex owner)
        {
            var index = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                index,
                Avm1MirConstantKind.Null,
                false,
                0,
                0,
                Avm1StackLirStringIndex.Invalid));
            Add(
                Avm1StackLirInstructionKind.PushConstant,
                index,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1);
        }

        public void EmitBoolean(
            bool value,
            Avm1MirInstructionIndex owner)
        {
            var index = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                index,
                Avm1MirConstantKind.Boolean,
                value,
                0,
                0,
                Avm1StackLirStringIndex.Invalid));
            Add(
                Avm1StackLirInstructionKind.PushConstant,
                index,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1);
        }

        public void EmitInteger(int value, Avm1MirInstructionIndex owner)
        {
            var index = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                index,
                Avm1MirConstantKind.Integer,
                false,
                value,
                0,
                Avm1StackLirStringIndex.Invalid));
            Add(
                Avm1StackLirInstructionKind.PushConstant,
                index,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1);
        }

        public void EmitPushName(string name, Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.PushName,
                Avm1StackLirConstantIndex.Invalid,
                InternString(name),
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1);

        public bool TryAcquireTemporary(
            out Avm1StackLirTemporaryIndex temporary)
        {
            if (_freeTemporaries.TryPop(out temporary))
            {
                return true;
            }
            var storageIndex = _temporaryStorages.Count;
            if (storageIndex < _reservedTemporaryRegisters.Length)
            {
                temporary = new Avm1StackLirTemporaryIndex(storageIndex);
                _temporaryStorages.Add(Avm1StackLirTemporaryStorage.InRegister(
                    _reservedTemporaryRegisters[storageIndex]));
                return true;
            }
            if (_activationTemporaryPrefix is null)
            {
                temporary = Avm1StackLirTemporaryIndex.Invalid;
                return false;
            }

            temporary = new Avm1StackLirTemporaryIndex(storageIndex);
            var spillName = string.Concat(
                _activationTemporaryPrefix,
                "_",
                storageIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            _temporaryStorages.Add(Avm1StackLirTemporaryStorage.InActivation(
                InternString(spillName)));
            return true;
        }

        public bool TryAcquireTemporaries(
            int count,
            out Avm1StackLirTemporaryIndex[] temporaries)
        {
            temporaries = new Avm1StackLirTemporaryIndex[count];
            var acquired = 0;
            for (; acquired < count; acquired++)
            {
                if (TryAcquireTemporary(out temporaries[acquired]))
                    continue;

                for (var i = acquired - 1; i >= 0; i--)
                    ReleaseTemporary(temporaries[i]);
                temporaries = [];
                return false;
            }
            return true;
        }

        public void ReleaseTemporary(Avm1StackLirTemporaryIndex temporary) =>
            _freeTemporaries.Push(temporary);

        public string DescribeTemporaryPressure()
        {
            var activeStorageCount = _temporaryStorages.Count -
                _freeTemporaries.Count;
            return string.Concat(
                activeStorageCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                " storage slot(s) are active (",
                _mirTemporaries.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                " MIR transport(s), ",
                _phiTemporaries.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                " phi transport(s)); ",
                _reservedTemporaryRegisters.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                " physical slot(s) are reserved and activation spills are ",
                _activationTemporaryPrefix is null ? "disabled." : "enabled.");
        }

        public bool TryGetOrAcquireMirTemporary(
            Avm1MirTemporaryIndex mirTemporary,
            out Avm1StackLirTemporaryIndex temporary)
        {
            if (_mirTemporaries.TryGetValue(mirTemporary, out temporary))
                return true;
            if (!TryAcquireTemporary(out temporary))
                return false;

            _mirTemporaries.Add(mirTemporary, temporary);
            return true;
        }

        public bool TryGetMirTemporary(
            Avm1MirTemporaryIndex mirTemporary,
            out Avm1StackLirTemporaryIndex temporary) =>
            _mirTemporaries.TryGetValue(mirTemporary, out temporary);

        public bool TryGetOrAcquirePhiTemporary(
            Avm1MirPhiSiteIndex phiSite,
            out Avm1StackLirTemporaryIndex temporary)
        {
            if (_phiTemporaries.TryGetValue(phiSite, out temporary))
                return true;
            if (!TryAcquireTemporary(out temporary))
                return false;

            _phiTemporaries.Add(phiSite, temporary);
            return true;
        }

        public bool TryGetPhiTemporary(
            Avm1MirPhiSiteIndex phiSite,
            out Avm1StackLirTemporaryIndex temporary) =>
            _phiTemporaries.TryGetValue(phiSite, out temporary);

        public void ReleasePhiTemporary(Avm1MirPhiSiteIndex phiSite)
        {
            var temporary = _phiTemporaries[phiSite];
            _phiTemporaries.Remove(phiSite);
            ReleaseTemporary(temporary);
        }

        public void EmitPushTemporary(
            Avm1StackLirTemporaryIndex temporary,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.PushTemporary,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                temporary,
                default,
                owner,
                stackDelta: 1);

        public void EmitStoreTemporary(
            Avm1StackLirTemporaryIndex temporary,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.StoreTemporary,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                temporary,
                default,
                owner,
                stackDelta: 0,
                stackPeak: _temporaryStorages[temporary.Value].Kind is
                    Avm1StackLirTemporaryStorageKind.ActivationName
                    ? 2
                    : 0);

        public void EmitPushRegister(
            byte register,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.PushRegister,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 1,
                register: register);

        public void EmitStoreRegister(
            byte register,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.StoreRegister,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 0,
                register: register);

        public void EmitFunction(
            Avm1MirFunctionSite function,
            Avm1MirInstructionIndex owner,
            int stackDelta)
        {
            var index = new Avm1StackLirFunctionIndex(_functions.Count);
            _functions.Add(new Avm1StackLirFunction(
                index,
                function.Function,
                function.IsDeclaration));
            Add(
                Avm1StackLirInstructionKind.DefineFunction,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta,
                function: index);
        }

        public void EmitTry(
            Avm1MirTrySite trySite,
            Avm1MirInstructionIndex owner)
        {
            var index = new Avm1StackLirTryIndex(_trySites.Count);
            _trySites.Add(new Avm1StackLirTry(index, trySite.Index));
            Add(
                Avm1StackLirInstructionKind.Try,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: 0,
                trySite: index);
        }

        public void EmitWith(
            Avm1MirWithSite withSite,
            Avm1MirInstructionIndex owner)
        {
            var index = new Avm1StackLirWithIndex(_withSites.Count);
            _withSites.Add(new Avm1StackLirWith(index, withSite.Index));
            Add(
                Avm1StackLirInstructionKind.With,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                default,
                owner,
                stackDelta: -1,
                withSite: index);
        }

        public void EmitAction(
            ActionOpcode opcode,
            int stackDelta,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.Action,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                opcode,
                owner,
                stackDelta);

        public void EmitGotoFrame2(
            bool play,
            ushort? sceneBias,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.GotoFrame2,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.GotoFrame2,
                owner,
                stackDelta: -1,
                play: play,
                hasSceneBias: sceneBias.HasValue,
                sceneBias: sceneBias.GetValueOrDefault());

        public void EmitSetTarget(
            string target,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.SetTarget,
                Avm1StackLirConstantIndex.Invalid,
                InternString(target),
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.SetTarget,
                owner,
                stackDelta: 0);

        public void EmitGotoFrame(
            ushort frame,
            Avm1MirInstructionIndex owner)
        {
            var constant = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                constant,
                Avm1MirConstantKind.Integer,
                false,
                frame,
                0,
                Avm1StackLirStringIndex.Invalid));
            Add(
                Avm1StackLirInstructionKind.GotoFrame,
                constant,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.GotoFrame,
                owner,
                stackDelta: 0);
        }

        public void EmitGotoLabel(
            string label,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.GotoFrame,
                Avm1StackLirConstantIndex.Invalid,
                InternString(label),
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.GoToLabel,
                owner,
                stackDelta: 0);

        public void EmitWaitForFrame(
            ushort frame,
            Avm1MirBlockIndex skipTarget,
            Avm1MirInstructionIndex owner)
        {
            var constant = new Avm1StackLirConstantIndex(_constants.Count);
            _constants.Add(new Avm1StackLirConstant(
                constant,
                Avm1MirConstantKind.Integer,
                false,
                frame,
                0,
                Avm1StackLirStringIndex.Invalid));
            Add(
                Avm1StackLirInstructionKind.WaitForFrame,
                constant,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.WaitForFrame,
                owner,
                stackDelta: 0,
                target: skipTarget);
        }

        public void EmitWaitForFrame2(
            Avm1MirBlockIndex skipTarget,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.WaitForFrame,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.WaitForFrame2,
                owner,
                stackDelta: -1,
                target: skipTarget);

        public void EmitGetUrlImmediate(
            string url,
            string target,
            Avm1MirInstructionIndex owner)
        {
            var site = new Avm1StackLirUrlSiteIndex(_urlSites.Count);
            _urlSites.Add(new Avm1StackLirUrlSite(
                site,
                InternString(url),
                InternString(target),
                GetUrlFlags.MethodNone));
            Add(
                Avm1StackLirInstructionKind.GetUrlImmediate,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.GetURL,
                owner,
                stackDelta: 0,
                urlSite: site);
        }

        public void EmitGetUrl2(
            GetUrlFlags flags,
            Avm1MirInstructionIndex owner)
        {
            if (!ActionGetURL2.IsValidFlags(flags))
                throw new ArgumentOutOfRangeException(nameof(flags));

            var site = new Avm1StackLirUrlSiteIndex(_urlSites.Count);
            _urlSites.Add(new Avm1StackLirUrlSite(
                site,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                flags));
            Add(
                Avm1StackLirInstructionKind.GetUrl2,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.GetURL2,
                owner,
                stackDelta: -2,
                urlSite: site);
        }

        public void EmitBranch(
            ActionOpcode opcode,
            Avm1MirBlockIndex target,
            int stackDelta,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.Branch,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                opcode,
                owner,
                stackDelta,
                target);

        public void EmitParentBranch(
            Avm1MirCompletionSiteIndex completionSite,
            Avm1MirInstructionIndex owner) =>
            Add(
                Avm1StackLirInstructionKind.ParentBranch,
                Avm1StackLirConstantIndex.Invalid,
                Avm1StackLirStringIndex.Invalid,
                Avm1StackLirTemporaryIndex.Invalid,
                ActionOpcode.Jump,
                owner,
                stackDelta: 0,
                completionSite: completionSite);

        public Avm1StackLirMethod ToMethod()
        {
            FinishCurrentBlock();
            return new Avm1StackLirMethod(
                _instructions.ToArray(),
                _blocks.ToArray(),
                _constants.ToArray(),
                _functions.ToArray(),
                _trySites.ToArray(),
                _withSites.ToArray(),
                _urlSites.ToArray(),
                _strings.ToArray(),
                _temporaryStorages.ToArray(),
                _maximumStackDepth);
        }

        private void Add(
            Avm1StackLirInstructionKind kind,
            Avm1StackLirConstantIndex constant,
            Avm1StackLirStringIndex name,
            Avm1StackLirTemporaryIndex temporary,
            ActionOpcode opcode,
            Avm1MirInstructionIndex owner,
            int stackDelta,
            Avm1MirBlockIndex? target = null,
            Avm1StackLirFunctionIndex? function = null,
            Avm1StackLirTryIndex? trySite = null,
            Avm1StackLirWithIndex? withSite = null,
            Avm1StackLirUrlSiteIndex? urlSite = null,
            Avm1MirCompletionSiteIndex? completionSite = null,
            byte register = 0,
            bool play = false,
            bool hasSceneBias = false,
            ushort sceneBias = 0,
            int stackPeak = 0)
        {
            var index = new Avm1StackLirInstructionIndex(_instructions.Count);
            _instructions.Add(new Avm1StackLirInstruction(
                index,
                kind,
                constant,
                name,
                temporary,
                function ?? Avm1StackLirFunctionIndex.Invalid,
                trySite ?? Avm1StackLirTryIndex.Invalid,
                withSite ?? Avm1StackLirWithIndex.Invalid,
                urlSite ?? Avm1StackLirUrlSiteIndex.Invalid,
                completionSite ?? Avm1MirCompletionSiteIndex.Invalid,
                register,
                play,
                hasSceneBias,
                sceneBias,
                opcode,
                target ?? Avm1MirBlockIndex.Invalid,
                owner));

            _maximumStackDepth = Math.Max(
                _maximumStackDepth,
                _stackDepth + stackPeak);
            _stackDepth += stackDelta;
            if (_stackDepth < 0)
            {
                HasInvalidStack = true;
                _stackDepth = 0;
            }
            _maximumStackDepth = Math.Max(_maximumStackDepth, _stackDepth);
        }

        private void FinishCurrentBlock()
        {
            if (!_currentBlock.IsValid)
                return;

            _blocks.Add(new Avm1StackLirBlock(
                _currentBlock,
                new Avm1StackLirInstructionRange(
                    _currentBlockStart,
                    _instructions.Count - _currentBlockStart)));
            _currentBlock = Avm1MirBlockIndex.Invalid;
        }

        private Avm1StackLirStringIndex InternString(string value)
        {
            if (_stringIndices.TryGetValue(value, out var existing))
                return existing;

            var index = new Avm1StackLirStringIndex(_strings.Count);
            _strings.Add(value);
            _stringIndices.Add(value, index);
            return index;
        }
    }
}
