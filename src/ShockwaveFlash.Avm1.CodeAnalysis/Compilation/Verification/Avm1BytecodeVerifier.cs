using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Verification;

public static class Avm1BytecodeVerifier
{
    private const int NotControlFlow = -2;

    public static Avm1BytecodeVerificationResult Verify(
        IReadOnlyList<Avm1Action> actions,
        Avm1BytecodeVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return Verify(
                Avm1Action.EncodeCollection(actions, options.SwfVersion),
                options);
        }
        catch (Exception exception) when (IsRecoverableCodecException(exception))
        {
            return InvalidBytecode(
                "AVM1VER001",
                $"Cannot encode the supplied actions: {exception.Message}");
        }
    }

    public static Avm1BytecodeVerificationResult Verify(
        ReadOnlyMemory<byte> bytecode,
        Avm1BytecodeVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options = options.CodeUnitContext.HasValue
            ? options
            : options with
            {
                CodeUnitContext = options.RegisterFile switch
                {
                    Avm1CompilationRegisterFile.DefineFunction2 =>
                        Avm1CodeUnitContext.DefineFunction2(
                            options.RegisterCount,
                            Avm1ConstantPoolContext.FromInitial(
                                options.InitialConstantPoolCount)),
                    _ => Avm1CodeUnitContext.Legacy(
                        Avm1ConstantPoolContext.FromInitial(
                            options.InitialConstantPoolCount))
                }
            };

        var diagnostics = new List<Avm1BytecodeVerificationDiagnostic>();
        if (options.SwfVersion == 0)
        {
            diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER000",
                Avm1CompilationDiagnosticSeverity.Error,
                -1,
                -1,
                "SWF version 0 is not a valid AVM1 target."));
        }

        Avm1Action[] actions;
        try
        {
            actions = Avm1Action.DecodeCollection(
                bytecode,
                options.SwfVersion,
                strict: true).ToArray();
        }
        catch (Exception exception) when (IsRecoverableCodecException(exception))
        {
            diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER001",
                Avm1CompilationDiagnosticSeverity.Error,
                -1,
                -1,
                $"Strict AVM1 decode failed: {exception.Message}"));
            return new Avm1BytecodeVerificationResult(
                [],
                [],
                bytecode.Length,
                0,
                true,
                diagnostics.ToArray());
        }

        ReadOnlyMemory<byte> canonicalBytes;
        try
        {
            canonicalBytes = Avm1Action.EncodeCollection(actions, options.SwfVersion);
        }
        catch (Exception exception) when (IsRecoverableCodecException(exception))
        {
            diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER002",
                Avm1CompilationDiagnosticSeverity.Error,
                -1,
                -1,
                $"Strictly decoded actions cannot be encoded again: {exception.Message}"));
            return new Avm1BytecodeVerificationResult(
                actions,
                [],
                bytecode.Length,
                0,
                true,
                diagnostics.ToArray());
        }

        if (!bytecode.Span.SequenceEqual(canonicalBytes.Span))
        {
            diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER002",
                Avm1CompilationDiagnosticSeverity.Error,
                -1,
                -1,
                "Strict decode/encode did not consume and reproduce the complete code unit."));
        }

        var actionOffsets = new int[actions.Length];
        var actionLengths = new int[actions.Length];
        var byteOffset = 0;
        for (var i = 0; i < actions.Length; i++)
        {
            actionOffsets[i] = byteOffset;
            actionLengths[i] = Avm1Action.EncodeCollection(
                [actions[i]],
                options.SwfVersion).Length;
            byteOffset = checked(byteOffset + actionLengths[i]);
        }

        var controlTargets = VerifyStructure(
            actions,
            actionOffsets,
            actionLengths,
            byteOffset,
            options,
            diagnostics);
        var nestedStack = VerifyOperandsAndNestedBodies(
            actions,
            actionOffsets,
            actionLengths,
            byteOffset,
            controlTargets,
            options,
            diagnostics);
        var rootStack = options.VerifyDataFlow
            ? VerifyStack(
                actions,
                actionOffsets,
                controlTargets,
                options,
                diagnostics)
            : new StackVerificationSummary(0, false);

        return new Avm1BytecodeVerificationResult(
            actions,
            actionOffsets,
            bytecode.Length,
            Math.Max(
                nestedStack.MaximumStackDepth,
                rootStack.MaximumStackDepth),
            nestedStack.IsExact && rootStack.IsExact,
            diagnostics.ToArray());
    }

    private static int[] VerifyStructure(
        Avm1Action[] actions,
        int[] actionOffsets,
        int[] actionLengths,
        int byteLength,
        Avm1BytecodeVerificationOptions options,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        var actionByOffset = new Dictionary<int, int>(actions.Length + 1);
        for (var i = 0; i < actions.Length; i++)
            actionByOffset.Add(actionOffsets[i], i);
        actionByOffset.Add(byteLength, actions.Length);

        var controlTargets = new int[actions.Length];
        Array.Fill(controlTargets, NotControlFlow);
        for (var i = 0; i < actions.Length; i++)
        {
            short? displacement = actions[i] switch
            {
                ActionJump jump => jump.BranchOffset,
                ActionIf conditional => conditional.BranchOffset,
                _ => null
            };
            if (displacement is not null)
            {
                var targetOffset = (long)actionOffsets[i] +
                    actionLengths[i] + displacement.Value;
                if (targetOffset is >= int.MinValue and <= int.MaxValue &&
                    options.ExternalBranchExitOffsets.Contains((int)targetOffset))
                {
                    controlTargets[i] = actions.Length;
                }
                else if (targetOffset is < 0 or > int.MaxValue ||
                    !actionByOffset.TryGetValue((int)targetOffset, out var target))
                {
                    controlTargets[i] = -1;
                    diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                        "AVM1VER003",
                        Avm1CompilationDiagnosticSeverity.Error,
                        i,
                        actionOffsets[i],
                        $"Branch target offset {targetOffset} is not an action or code-unit boundary."));
                }
                else
                {
                    controlTargets[i] = target;
                }
            }

            byte? skipCount = actions[i] switch
            {
                ActionWaitForFrame wait => wait.SkipCount,
                ActionWaitForFrame2 wait => wait.SkipCount,
                _ => null
            };
            if (skipCount is not null)
            {
                var target = (long)i + 1 + skipCount.Value;
                if (target > actions.Length)
                {
                    controlTargets[i] = -1;
                    diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                        "AVM1VER011",
                        Avm1CompilationDiagnosticSeverity.Error,
                        i,
                        actionOffsets[i],
                        $"SkipCount {skipCount.Value} targets action {target}, " +
                        $"past the code-unit end at action {actions.Length}."));
                }
                else
                {
                    controlTargets[i] = (int)target;
                }
            }

            if (actions[i].Opcode is ActionOpcode.End && i != actions.Length - 1)
            {
                diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER008",
                    Avm1CompilationDiagnosticSeverity.Error,
                    i,
                    actionOffsets[i],
                    "ActionEnd must be the final action in its code unit."));
            }
        }

        if (options.RequireEndAction &&
            (actions.Length == 0 || actions[^1].Opcode is not ActionOpcode.End))
        {
            diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER009",
                Avm1CompilationDiagnosticSeverity.Error,
                actions.Length - 1,
                actions.Length == 0 ? 0 : actionOffsets[^1],
                "The code unit must end with ActionEnd."));
        }

        return controlTargets;
    }

    private static StackVerificationSummary VerifyStack(
        Avm1Action[] actions,
        int[] actionOffsets,
        int[] controlTargets,
        Avm1BytecodeVerificationOptions options,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (actions.Length == 0)
            return new StackVerificationSummary(0, true);

        var entryStacks = new AbstractValue[]?[actions.Length];
        entryStacks[0] = [];
        var worklist = new Queue<int>();
        worklist.Enqueue(0);
        var queued = new bool[actions.Length];
        queued[0] = true;
        var maximumStackDepth = 0;
        var isExact = true;

        while (worklist.Count > 0)
        {
            var actionIndex = worklist.Dequeue();
            queued[actionIndex] = false;
            var action = actions[actionIndex];
            var stack = new List<AbstractValue>(entryStacks[actionIndex]!);
            maximumStackDepth = Math.Max(maximumStackDepth, stack.Count);

            if (!TryApplyStackAction(
                action,
                actionIndex,
                actionOffsets[actionIndex],
                stack,
                options,
                diagnostics,
                ref isExact,
                out var branchCondition))
                continue;
            maximumStackDepth = Math.Max(maximumStackDepth, stack.Count);

            if (action.Opcode is ActionOpcode.End or
                ActionOpcode.Return or ActionOpcode.Throw)
            {
                VerifyExitStack(actionIndex, stack);
                continue;
            }

            if (action is ActionJump)
            {
                if (controlTargets[actionIndex] >= 0)
                    Propagate(controlTargets[actionIndex], stack, actionIndex);
                continue;
            }

            if (action is ActionIf)
            {
                if (controlTargets[actionIndex] >= 0)
                {
                    Propagate(
                        controlTargets[actionIndex],
                        GetConditionalStack(stack, branchCondition, branchTaken: true),
                        actionIndex);
                }
                Propagate(
                    actionIndex + 1,
                    GetConditionalStack(stack, branchCondition, branchTaken: false),
                    actionIndex);
                continue;
            }

            if (action is ActionWaitForFrame or ActionWaitForFrame2)
            {
                if (controlTargets[actionIndex] >= 0)
                    Propagate(controlTargets[actionIndex], stack, actionIndex);
                Propagate(actionIndex + 1, stack, actionIndex);
                continue;
            }

            Propagate(actionIndex + 1, stack, actionIndex);
        }

        return new StackVerificationSummary(maximumStackDepth, isExact);

        void Propagate(
            int target,
            IReadOnlyList<AbstractValue> incoming,
            int source)
        {
            if (target == actions.Length)
            {
                VerifyExitStack(source, incoming);
                return;
            }
            if (target < 0 || target > actions.Length)
                return;

            var existing = entryStacks[target];
            if (existing is null)
            {
                entryStacks[target] = incoming.ToArray();
                Enqueue(target);
                return;
            }

            if (existing.Length != incoming.Count)
            {
                if (options.AllowRuntimeStackUnderflow &&
                    actions[target] is ActionPop)
                {
                    var mergedDepth = Math.Min(existing.Length, incoming.Count);
                    var compatibleMerge = new AbstractValue[mergedDepth];
                    var existingOffset = existing.Length - mergedDepth;
                    var incomingOffset = incoming.Count - mergedDepth;
                    for (var i = 0; i < mergedDepth; i++)
                    {
                        var existingValue = existing[existingOffset + i];
                        var incomingValue = incoming[incomingOffset + i];
                        compatibleMerge[i] = existingValue == incomingValue
                            ? existingValue
                            : AbstractValue.Unknown;
                    }

                    entryStacks[target] = compatibleMerge;
                    isExact = false;
                    AddDiagnostic(
                        diagnostics,
                        new Avm1BytecodeVerificationDiagnostic(
                            "AVM1VER021",
                            Avm1CompilationDiagnosticSeverity.Warning,
                            target,
                            actionOffsets[target],
                            $"Runtime-compatible cleanup Pop conservatively " +
                            $"merges incoming stack depths {existing.Length} and " +
                            $"{incoming.Count} from action {source} at depth " +
                            $"{mergedDepth}."));
                    Enqueue(target);
                    return;
                }

                AddDiagnostic(
                    diagnostics,
                    new Avm1BytecodeVerificationDiagnostic(
                        "AVM1VER005",
                        Avm1CompilationDiagnosticSeverity.Error,
                        target,
                        actionOffsets[target],
                        $"Inconsistent stack depth at action {target}: existing " +
                        $"{existing.Length}, incoming {incoming.Count} from action {source}."));
                return;
            }

            AbstractValue[]? merged = null;
            for (var i = 0; i < existing.Length; i++)
            {
                var incomingValue = incoming[i];
                if (existing[i] == incomingValue)
                    continue;

                if (existing[i].IsEnumerationSensitive ||
                    incomingValue.IsEnumerationSensitive)
                {
                    AddDiagnostic(
                        diagnostics,
                        new Avm1BytecodeVerificationDiagnostic(
                            "AVM1VER013",
                            Avm1CompilationDiagnosticSeverity.Error,
                            target,
                            actionOffsets[target],
                            $"Incompatible symbolic enumeration stack state at " +
                            $"slot {i}, arriving from action {source}."));
                }

                merged ??= (AbstractValue[])existing.Clone();
                merged[i] = AbstractValue.Unknown;
            }

            if (merged is null)
                return;

            entryStacks[target] = merged;
            Enqueue(target);
        }

        void Enqueue(int actionIndex)
        {
            if (queued[actionIndex])
                return;
            queued[actionIndex] = true;
            worklist.Enqueue(actionIndex);
        }

        void VerifyExitStack(
            int source,
            IReadOnlyList<AbstractValue> stack)
        {
            if (!options.RequireEmptyStackAtExit || stack.Count == 0)
                return;

            AddDiagnostic(
                diagnostics,
                new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER007",
                    Avm1CompilationDiagnosticSeverity.Error,
                    source,
                    actionOffsets[source],
                    $"Code-unit exit leaves {stack.Count} symbolic value(s) " +
                    "on the operand stack."));
        }
    }

    private static bool TryApplyStackAction(
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        List<AbstractValue> stack,
        Avm1BytecodeVerificationOptions options,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics,
        ref bool isExact,
        out AbstractValue branchCondition)
    {
        branchCondition = AbstractValue.Unknown;

        if (action is ActionPush push)
        {
            foreach (var value in push.PushValues)
                stack.Add(ToAbstractValue(value));
            return true;
        }

        if (action is ActionEnumerate or ActionEnumerate2)
        {
            if (!EnsureDepth(
                stack,
                1,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }
            if (!CanConsumeEnumerationValues(
                stack,
                1,
                action,
                actionIndex,
                byteOffset,
                diagnostics))
            {
                return false;
            }

            stack.RemoveAt(stack.Count - 1);
            stack.Add(AbstractValue.EnumerationStream(actionIndex));
            isExact = false;
            return true;
        }

        if (action is ActionStartDrag)
        {
            if (!EnsureDepth(
                stack,
                3,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }

            var constraint = stack[^3];
            if (!TryGetKnownTruthiness(constraint, out var isConstrained))
            {
                AddDiagnostic(
                    diagnostics,
                    new Avm1BytecodeVerificationDiagnostic(
                        "AVM1VER014",
                        Avm1CompilationDiagnosticSeverity.Error,
                        actionIndex,
                        byteOffset,
                        "StartDrag requires a statically known constraint flag " +
                        "to determine whether it consumes 3 or 7 values."));
                return false;
            }

            var popCount = isConstrained ? 7 : 3;
            if (!EnsureDepth(
                stack,
                popCount,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }
            if (!CanConsumeEnumerationValues(
                stack,
                popCount,
                action,
                actionIndex,
                byteOffset,
                diagnostics))
            {
                return false;
            }

            stack.RemoveRange(stack.Count - popCount, popCount);
            return true;
        }

        if (action.Opcode is ActionOpcode.CallFunction or ActionOpcode.NewObject)
        {
            return TryApplyCountedAction(
                action,
                actionIndex,
                byteOffset,
                stack,
                countOffsetFromTop: 1,
                fixedPopCount: 2,
                valuesPerCount: 1,
                pushCount: 1,
                options,
                ref isExact,
                diagnostics);
        }

        if (action.Opcode is ActionOpcode.CallMethod or ActionOpcode.NewMethod)
        {
            return TryApplyCountedAction(
                action,
                actionIndex,
                byteOffset,
                stack,
                countOffsetFromTop: 2,
                fixedPopCount: 3,
                valuesPerCount: 1,
                pushCount: 1,
                options,
                ref isExact,
                diagnostics);
        }

        if (action.Opcode is ActionOpcode.InitArray or ActionOpcode.InitObject)
        {
            return TryApplyCountedAction(
                action,
                actionIndex,
                byteOffset,
                stack,
                countOffsetFromTop: 0,
                fixedPopCount: 1,
                valuesPerCount: action.Opcode is ActionOpcode.InitObject ? 2 : 1,
                pushCount: 1,
                options,
                ref isExact,
                diagnostics);
        }

        if (action is ActionImplementsOp)
        {
            return TryApplyCountedAction(
                action,
                actionIndex,
                byteOffset,
                stack,
                countOffsetFromTop: 1,
                fixedPopCount: 2,
                valuesPerCount: 1,
                pushCount: 0,
                options,
                ref isExact,
                diagnostics);
        }

        if (action is ActionPushDuplicate)
        {
            if (!EnsureDepth(
                stack,
                1,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }
            if (stack[^1].Kind is AbstractValueKind.EnumerationStream)
            {
                // The stream marker represents the current key plus the
                // unknown remaining sequence. Duplicate copies only that
                // current value; the original marker continues to represent
                // the sequence consumed by the enumeration loop.
                stack.Add(AbstractValue.Unknown);
                return true;
            }

            stack.Add(stack[^1]);
            return true;
        }

        if (action is ActionStackSwap)
        {
            if (!EnsureDepth(
                stack,
                2,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }
            if (stack[^1].IsEnumerationSensitive ||
                stack[^2].IsEnumerationSensitive)
            {
                AddUnsupportedEnumerationDiagnostic(
                    action,
                    actionIndex,
                    byteOffset,
                    diagnostics);
                return false;
            }

            (stack[^2], stack[^1]) = (stack[^1], stack[^2]);
            return true;
        }

        if (action.Opcode is ActionOpcode.Equals or
            ActionOpcode.Equals2 or ActionOpcode.StrictEquals)
        {
            if (!EnsureDepth(
                stack,
                2,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }

            var right = stack[^1];
            var left = stack[^2];
            stack.RemoveRange(stack.Count - 2, 2);
            if (TryCreateEnumerationEndPredicate(left, right, out var predicate))
            {
                stack.Add(predicate);
                return true;
            }
            if (left.IsEnumerationSensitive || right.IsEnumerationSensitive)
            {
                AddUnsupportedEnumerationDiagnostic(
                    action,
                    actionIndex,
                    byteOffset,
                    diagnostics);
                return false;
            }

            stack.Add(AbstractValue.Unknown);
            return true;
        }

        if (action is ActionNot)
        {
            if (!EnsureDepth(
                stack,
                1,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }

            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (value.Kind is AbstractValueKind.EnumerationEndPredicate)
            {
                stack.Add(value with { Flag = !value.Flag });
                return true;
            }
            if (value.IsEnumerationSensitive)
            {
                AddUnsupportedEnumerationDiagnostic(
                    action,
                    actionIndex,
                    byteOffset,
                    diagnostics);
                return false;
            }

            stack.Add(AbstractValue.Unknown);
            return true;
        }

        if (action is ActionIf)
        {
            if (!EnsureDepth(
                stack,
                1,
                action,
                actionIndex,
                byteOffset,
                options,
                ref isExact,
                diagnostics))
            {
                return false;
            }

            branchCondition = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (branchCondition.Kind is AbstractValueKind.EnumerationStream)
            {
                AddUnsupportedEnumerationDiagnostic(
                    action,
                    actionIndex,
                    byteOffset,
                    diagnostics);
                return false;
            }
            return true;
        }

        if (!TryGetStackEffect(action, out var effect))
        {
            AddDiagnostic(
                diagnostics,
                new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER006",
                    Avm1CompilationDiagnosticSeverity.Error,
                    actionIndex,
                    byteOffset,
                    $"The verifier does not yet have an exact stack effect for " +
                    $"{action.Opcode}."));
            return false;
        }

        if (!EnsureDepth(
            stack,
            effect.RequiredDepth,
            action,
            actionIndex,
            byteOffset,
            options,
            ref isExact,
            diagnostics))
        {
            return false;
        }
        if (!CanConsumeEnumerationValues(
            stack,
            effect.PopCount,
            action,
            actionIndex,
            byteOffset,
            diagnostics))
        {
            return false;
        }

        if (effect.PopCount > 0)
            stack.RemoveRange(stack.Count - effect.PopCount, effect.PopCount);
        for (var i = 0; i < effect.PushCount; i++)
            stack.Add(AbstractValue.Unknown);
        return true;
    }

    private static bool TryApplyCountedAction(
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        List<AbstractValue> stack,
        int countOffsetFromTop,
        int fixedPopCount,
        int valuesPerCount,
        int pushCount,
        Avm1BytecodeVerificationOptions options,
        ref bool isExact,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (!EnsureDepth(
            stack,
            countOffsetFromTop + 1L,
            action,
            actionIndex,
            byteOffset,
            options,
            ref isExact,
            diagnostics))
        {
            return false;
        }

        var countValue = stack[stack.Count - countOffsetFromTop - 1];
        if (countValue.Kind is not AbstractValueKind.KnownInteger ||
            countValue.Payload < 0)
        {
            AddDiagnostic(
                diagnostics,
                new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER012",
                    Avm1CompilationDiagnosticSeverity.Error,
                    actionIndex,
                    byteOffset,
                    $"{action.Opcode} requires a statically known non-negative " +
                    "integer count on the operand stack."));
            return false;
        }

        var popCount = fixedPopCount +
            (long)countValue.Payload * valuesPerCount;
        if (!EnsureDepth(
            stack,
            popCount,
            action,
            actionIndex,
            byteOffset,
            options,
            ref isExact,
            diagnostics))
        {
            return false;
        }
        if (!CanConsumeEnumerationValues(
            stack,
            checked((int)popCount),
            action,
            actionIndex,
            byteOffset,
            diagnostics))
        {
            return false;
        }

        stack.RemoveRange(stack.Count - (int)popCount, (int)popCount);
        for (var i = 0; i < pushCount; i++)
            stack.Add(AbstractValue.Unknown);
        return true;
    }

    private static bool EnsureDepth(
        List<AbstractValue> stack,
        long requiredDepth,
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        Avm1BytecodeVerificationOptions options,
        ref bool isExact,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (requiredDepth <= stack.Count)
            return true;

        if (options.AllowRuntimeStackUnderflow && requiredDepth <= int.MaxValue)
        {
            var missing = checked((int)requiredDepth - stack.Count);
            stack.InsertRange(0, new AbstractValue[missing]);
            isExact = false;
            AddDiagnostic(
                diagnostics,
                new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER020",
                    Avm1CompilationDiagnosticSeverity.Warning,
                    actionIndex,
                    byteOffset,
                    $"Runtime-compatible stack underflow at {action.Opcode}: " +
                    $"depth {stack.Count - missing}, required {requiredDepth}; " +
                    $"inserted {missing} unknown value(s)."));
            return true;
        }

        AddDiagnostic(
            diagnostics,
            new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER004",
                Avm1CompilationDiagnosticSeverity.Error,
                actionIndex,
                byteOffset,
                $"Stack underflow at {action.Opcode}: depth {stack.Count}, " +
                $"required {requiredDepth}."));
        return false;
    }

    private static bool CanConsumeEnumerationValues(
        IReadOnlyList<AbstractValue> stack,
        int popCount,
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        for (var i = stack.Count - popCount; i < stack.Count; i++)
        {
            if (!stack[i].IsEnumerationSensitive)
                continue;

            AddUnsupportedEnumerationDiagnostic(
                action,
                actionIndex,
                byteOffset,
                diagnostics);
            return false;
        }
        return true;
    }

    private static void AddUnsupportedEnumerationDiagnostic(
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics) =>
        AddDiagnostic(
            diagnostics,
            new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER013",
                Avm1CompilationDiagnosticSeverity.Error,
                actionIndex,
                byteOffset,
                $"{action.Opcode} consumes a symbolic enumeration sequence in " +
                "a form whose stack shape cannot be proven."));

    private static bool TryCreateEnumerationEndPredicate(
        AbstractValue left,
        AbstractValue right,
        out AbstractValue predicate)
    {
        if (left.Kind is AbstractValueKind.EnumerationStream &&
            right.Kind is AbstractValueKind.Null)
        {
            predicate = AbstractValue.EnumerationEndPredicate(
                left.Payload,
                trueMeansEnd: true);
            return true;
        }
        if (right.Kind is AbstractValueKind.EnumerationStream &&
            left.Kind is AbstractValueKind.Null)
        {
            predicate = AbstractValue.EnumerationEndPredicate(
                right.Payload,
                trueMeansEnd: true);
            return true;
        }

        predicate = AbstractValue.Unknown;
        return false;
    }

    private static AbstractValue[] GetConditionalStack(
        IReadOnlyCollection<AbstractValue> stack,
        AbstractValue condition,
        bool branchTaken)
    {
        var result = stack.ToList();
        if (condition.Kind is AbstractValueKind.EnumerationEndPredicate &&
            branchTaken != condition.Flag)
        {
            result.Add(AbstractValue.EnumerationStream(condition.Payload));
        }
        return result.ToArray();
    }

    private static AbstractValue ToAbstractValue(PushValue value) =>
        value switch
        {
            PushValue.PushValueInteger integer =>
                AbstractValue.KnownInteger(integer.Value),
            PushValue.PushValueFloat number =>
                ToIntegralAbstractValue(number.Value),
            PushValue.PushValueDouble number =>
                ToIntegralAbstractValue(number.Value),
            PushValue.PushValueBoolean boolean =>
                AbstractValue.KnownBoolean(boolean.Value),
            PushValue.PushValueNull => AbstractValue.Null,
            _ => AbstractValue.Unknown
        };

    private static AbstractValue ToIntegralAbstractValue(double value)
    {
        if (!double.IsFinite(value) ||
            value < int.MinValue ||
            value > int.MaxValue ||
            value != Math.Truncate(value))
        {
            return AbstractValue.Unknown;
        }

        return AbstractValue.KnownInteger((int)value);
    }

    private static bool TryGetKnownTruthiness(
        AbstractValue value,
        out bool truthiness)
    {
        switch (value.Kind)
        {
            case AbstractValueKind.KnownInteger:
                truthiness = value.Payload != 0;
                return true;
            case AbstractValueKind.KnownBoolean:
                truthiness = value.Flag;
                return true;
            case AbstractValueKind.Null:
                truthiness = false;
                return true;
            default:
                truthiness = false;
                return false;
        }
    }

    private static StackVerificationSummary VerifyOperandsAndNestedBodies(
        Avm1Action[] actions,
        int[] actionOffsets,
        int[] actionLengths,
        int byteLength,
        int[] controlTargets,
        Avm1BytecodeVerificationOptions options,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        var context = options.CodeUnitContext!.Value;
        var poolEntries = AnalyzeConstantPoolFlow(
            actions,
            controlTargets,
            context.ConstantPool,
            options.SwfVersion);
        var maximumStackDepth = 0;
        var isExact = true;
        for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
        {
            VerifyTargetCompatibility(
                actions[actionIndex],
                actionIndex,
                actionOffsets[actionIndex],
                options.SwfVersion,
                diagnostics);

            VerifyRegisterOperandsAndMetadata(
                actions[actionIndex],
                actionIndex,
                actionOffsets[actionIndex],
                context,
                diagnostics);

            var entryPool = poolEntries[actionIndex] ??
                Avm1ConstantPoolContext.Unknown;
            if (poolEntries[actionIndex].HasValue)
            {
                VerifyConstantPoolReferences(
                    actions[actionIndex],
                    actionIndex,
                    actionOffsets[actionIndex],
                    entryPool,
                    diagnostics);
            }

            foreach (var nested in GetNestedBodies(
                actions[actionIndex],
                actionOffsets[actionIndex],
                actionLengths[actionIndex],
                actionOffsets,
                byteLength,
                options.ExternalBranchExitOffsets,
                context,
                entryPool,
                options.SwfVersion))
            {
                var result = Verify(
                    nested.Body,
                    options with
                    {
                        RequireEndAction = false,
                        CodeUnitContext = nested.Context,
                        ExternalBranchExitOffsets = nested.ExternalBranchExitOffsets
                    });
                maximumStackDepth = Math.Max(
                    maximumStackDepth,
                    result.MaximumStackDepth);
                isExact &= result.MaximumStackDepthIsExact;
                foreach (var diagnostic in result.Diagnostics)
                {
                    diagnostics.Add(new Avm1BytecodeVerificationDiagnostic(
                        "AVM1VER010",
                        diagnostic.Severity,
                        actionIndex,
                        actionOffsets[actionIndex],
                        $"Invalid {nested.Name} body ({diagnostic.Code}): " +
                        diagnostic.Message));
                }
            }
        }
        return new StackVerificationSummary(maximumStackDepth, isExact);
    }

    private static void VerifyTargetCompatibility(
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        byte swfVersion,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (swfVersion == 0 ||
            !Avm1ActionCapabilities.TryGetMinimumSwfVersion(
                action,
                out var minimumSwfVersion) ||
            swfVersion >= minimumSwfVersion)
        {
            return;
        }

        var feature = action.Opcode.ToString();
        if (action is ActionPush push &&
            Avm1ActionCapabilities.GetMinimumSwfVersion(ActionOpcode.Push) <= swfVersion)
        {
            var unsupportedValue = push.PushValues.First(value =>
                Avm1ActionCapabilities.GetMinimumSwfVersion(value) > swfVersion);
            feature = $"ActionPush {GetPushValueFormName(unsupportedValue)} value form";
        }

        AddDiagnostic(
            diagnostics,
            new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER019",
                Avm1CompilationDiagnosticSeverity.Error,
                actionIndex,
                byteOffset,
                $"{feature} requires SWF {minimumSwfVersion}, but the target is " +
                $"SWF {swfVersion}."));
    }

    private static string GetPushValueFormName(PushValue value) => value switch
    {
        PushValue.PushValueString => "string",
        PushValue.PushValueFloat => "float",
        PushValue.PushValueNull => "null",
        PushValue.PushValueUndefined => "undefined",
        PushValue.PushValueRegister => "register",
        PushValue.PushValueBoolean => "Boolean",
        PushValue.PushValueDouble => "double",
        PushValue.PushValueInteger => "integer",
        PushValue.PushValueConstant8 => "constant8",
        PushValue.PushValueConstant16 => "constant16",
        _ => value.GetType().Name
    };

    private static Avm1ConstantPoolContext?[] AnalyzeConstantPoolFlow(
        Avm1Action[] actions,
        int[] controlTargets,
        Avm1ConstantPoolContext initialPool,
        byte swfVersion)
    {
        var entries = new Avm1ConstantPoolContext?[actions.Length];
        if (actions.Length == 0)
            return entries;

        entries[0] = initialPool;
        var worklist = new Queue<int>();
        var queued = new bool[actions.Length];
        worklist.Enqueue(0);
        queued[0] = true;

        while (worklist.Count > 0)
        {
            var actionIndex = worklist.Dequeue();
            queued[actionIndex] = false;
            var action = actions[actionIndex];
            var exitPool = ApplyConstantPoolTransfer(
                action,
                entries[actionIndex]!.Value,
                swfVersion);

            if (action.Opcode is ActionOpcode.End or
                ActionOpcode.Return or ActionOpcode.Throw)
            {
                continue;
            }

            if (action is ActionJump)
            {
                if (controlTargets[actionIndex] >= 0)
                    Propagate(controlTargets[actionIndex], exitPool);
                continue;
            }

            if (action is ActionIf or ActionWaitForFrame or ActionWaitForFrame2)
            {
                if (controlTargets[actionIndex] >= 0)
                    Propagate(controlTargets[actionIndex], exitPool);
                Propagate(actionIndex + 1, exitPool);
                continue;
            }

            Propagate(actionIndex + 1, exitPool);
        }

        return entries;

        void Propagate(int target, Avm1ConstantPoolContext incoming)
        {
            if (target < 0 || target >= actions.Length)
                return;

            var existing = entries[target];
            if (!existing.HasValue)
            {
                entries[target] = incoming;
                Enqueue(target);
                return;
            }

            var merged = existing.Value.Merge(incoming);
            if (merged == existing.Value)
                return;
            entries[target] = merged;
            Enqueue(target);
        }

        void Enqueue(int actionIndex)
        {
            if (queued[actionIndex])
                return;
            queued[actionIndex] = true;
            worklist.Enqueue(actionIndex);
        }
    }

    private static Avm1ConstantPoolContext ApplyConstantPoolTransfer(
        Avm1Action action,
        Avm1ConstantPoolContext entryPool,
        byte swfVersion) =>
        action switch
        {
            ActionConstantPool constantPool =>
                Avm1ConstantPoolContext.Available(constantPool.Constants.Count),
            ActionTry actionTry => entryPool.Constrain(
                GetMinimumLeakingConstantPoolCount(actionTry, swfVersion)),
            _ => entryPool
        };

    private static int? GetMinimumLeakingConstantPoolCount(
        ActionTry actionTry,
        byte swfVersion) =>
        Minimum(
            GetMinimumLeakingConstantPoolCount(actionTry.TryBody, swfVersion),
            GetMinimumLeakingConstantPoolCount(actionTry.FinallyBody, swfVersion));

    private static int? GetMinimumLeakingConstantPoolCount(
        ReadOnlyMemory<byte> body,
        byte swfVersion)
    {
        if (body.IsEmpty)
            return null;

        IReadOnlyList<Avm1Action> actions;
        try
        {
            actions = Avm1Action.DecodeCollection(body, swfVersion, strict: true);
        }
        catch (Exception exception) when (IsRecoverableCodecException(exception))
        {
            return null;
        }

        int? result = null;
        foreach (var action in actions)
        {
            result = action switch
            {
                ActionConstantPool constantPool =>
                    Minimum(result, constantPool.Constants.Count),
                ActionTry nestedTry => Minimum(
                    result,
                    GetMinimumLeakingConstantPoolCount(nestedTry, swfVersion)),
                _ => result
            };
        }
        return result;
    }

    private static int? Minimum(int? left, int? right) =>
        left.HasValue
            ? right.HasValue ? Math.Min(left.Value, right.Value) : left
            : right;

    private static void VerifyRegisterOperandsAndMetadata(
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        Avm1CodeUnitContext context,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (action is ActionPush push)
        {
            foreach (var value in push.PushValues)
            {
                if (value is PushValue.PushValueRegister register)
                {
                    VerifyRegister(
                        register.RegisterIndex,
                        "ActionPush",
                        actionIndex,
                        byteOffset,
                        context,
                        diagnostics);
                }
            }
        }

        if (action is ActionStoreRegister store)
        {
            VerifyRegister(
                store.RegisterNumber,
                "ActionStoreRegister",
                actionIndex,
                byteOffset,
                context,
                diagnostics);
        }

        if (action is ActionDefineFunction2 function)
        {
            VerifyDefineFunction2Metadata(
                function,
                actionIndex,
                byteOffset,
                diagnostics);
        }

        if (action is ActionTry actionTry)
        {
            VerifyTryMetadata(
                actionTry,
                actionIndex,
                byteOffset,
                diagnostics);
            if (actionTry.Flags.HasFlag(TryFlags.CatchInRegister))
            {
                VerifyRegister(
                    actionTry.CatchRegister,
                    "ActionTry catch",
                    actionIndex,
                    byteOffset,
                    context,
                    diagnostics);
            }
        }
    }

    private static void VerifyRegister(
        byte register,
        string operand,
        int actionIndex,
        int byteOffset,
        Avm1CodeUnitContext context,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (context.IsRegisterValid(register))
            return;

        AddDiagnostic(
            diagnostics,
            new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER016",
                Avm1CompilationDiagnosticSeverity.Error,
                actionIndex,
                byteOffset,
                $"{operand} references register {register}, but the valid " +
                $"register range for this code unit is {context.ValidRegisterRange}."));
    }

    private static void VerifyDefineFunction2Metadata(
        ActionDefineFunction2 function,
        int actionIndex,
        int byteOffset,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        const FunctionFlags knownFlags =
            FunctionFlags.PreloadThis |
            FunctionFlags.SuppressThis |
            FunctionFlags.PreloadArguments |
            FunctionFlags.SuppressArguments |
            FunctionFlags.PreloadSuper |
            FunctionFlags.SuppressSuper |
            FunctionFlags.PreloadRoot |
            FunctionFlags.PreloadParent |
            FunctionFlags.PreloadGlobal;
        if ((function.Flags & ~knownFlags) != 0)
        {
            AddFunctionDiagnostic(
                $"Reserved FunctionFlags bits 0x{(ushort)(function.Flags & ~knownFlags):X4} " +
                "must be zero.");
        }

        VerifyPreloadSuppressPair(
            FunctionFlags.PreloadThis,
            FunctionFlags.SuppressThis,
            "this");
        VerifyPreloadSuppressPair(
            FunctionFlags.PreloadArguments,
            FunctionFlags.SuppressArguments,
            "arguments");
        VerifyPreloadSuppressPair(
            FunctionFlags.PreloadSuper,
            FunctionFlags.SuppressSuper,
            "super");

        var preloadCount =
            (function.Flags.HasFlag(FunctionFlags.PreloadThis) ? 1 : 0) +
            (function.Flags.HasFlag(FunctionFlags.PreloadArguments) ? 1 : 0) +
            (function.Flags.HasFlag(FunctionFlags.PreloadSuper) ? 1 : 0) +
            (function.Flags.HasFlag(FunctionFlags.PreloadRoot) ? 1 : 0) +
            (function.Flags.HasFlag(FunctionFlags.PreloadParent) ? 1 : 0) +
            (function.Flags.HasFlag(FunctionFlags.PreloadGlobal) ? 1 : 0);

        if (preloadCount >= function.RegisterCount && preloadCount != 0)
        {
            AddFunctionDiagnostic(
                $"The {preloadCount} preloaded values exceed RegisterCount " +
                $"{function.RegisterCount}.");
        }

        foreach (var parameter in function.Parameters)
        {
            if (parameter.Register == 0)
                continue;
            if (parameter.Register >= function.RegisterCount)
            {
                AddFunctionDiagnostic(
                    $"Parameter '{parameter.Name}' maps to register " +
                    $"{parameter.Register}, beyond RegisterCount " +
                    $"{function.RegisterCount}.");
            }
            else if (parameter.Register <= preloadCount)
            {
                AddFunctionDiagnostic(
                    $"Parameter '{parameter.Name}' maps to preload register " +
                    $"{parameter.Register}; preload registers occupy 1..{preloadCount}.");
            }
        }

        return;

        void VerifyPreloadSuppressPair(
            FunctionFlags preload,
            FunctionFlags suppress,
            string name)
        {
            if (function.Flags.HasFlag(preload) && function.Flags.HasFlag(suppress))
            {
                AddFunctionDiagnostic(
                    $"Preload and suppress flags cannot both be set for {name}.");
            }
        }

        void AddFunctionDiagnostic(string message) =>
            AddDiagnostic(
                diagnostics,
                new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER017",
                    Avm1CompilationDiagnosticSeverity.Error,
                    actionIndex,
                    byteOffset,
                    message));
    }

    private static void VerifyTryMetadata(
        ActionTry actionTry,
        int actionIndex,
        int byteOffset,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        const TryFlags knownFlags = TryFlags.CatchBlock |
            TryFlags.FinallyBlock |
            TryFlags.CatchInRegister;
        string? issue = null;
        if ((actionTry.Flags & ~knownFlags) != 0)
            issue = "Reserved ActionTry flag bits must be zero.";
        else if (actionTry.Flags.HasFlag(TryFlags.CatchInRegister) &&
            !actionTry.Flags.HasFlag(TryFlags.CatchBlock))
            issue = "CatchInRegister requires CatchBlock.";
        else if (!actionTry.Flags.HasFlag(TryFlags.CatchBlock) &&
            !actionTry.CatchBody.IsEmpty)
            issue = "A catch body is present without CatchBlock.";
        else if (!actionTry.Flags.HasFlag(TryFlags.FinallyBlock) &&
            !actionTry.FinallyBody.IsEmpty)
            issue = "A finally body is present without FinallyBlock.";

        if (issue is null)
            return;
        AddDiagnostic(
            diagnostics,
            new Avm1BytecodeVerificationDiagnostic(
                "AVM1VER018",
                Avm1CompilationDiagnosticSeverity.Error,
                actionIndex,
                byteOffset,
                issue));
    }

    private static void VerifyConstantPoolReferences(
        Avm1Action action,
        int actionIndex,
        int byteOffset,
        Avm1ConstantPoolContext entryPool,
        List<Avm1BytecodeVerificationDiagnostic> diagnostics)
    {
        if (action is not ActionPush push || !entryPool.IsKnown)
            return;

        foreach (var value in push.PushValues)
        {
            int? constantIndex = value switch
            {
                PushValue.PushValueConstant8 constant => constant.ConstantIndex,
                PushValue.PushValueConstant16 constant => constant.ConstantIndex,
                _ => null
            };
            if (!constantIndex.HasValue)
                continue;

            string message;
            if (!entryPool.MinimumCount.HasValue)
            {
                message = $"Constant-pool index {constantIndex.Value} is used " +
                    "without a pool active on every incoming control-flow path.";
            }
            else if (constantIndex.Value >= entryPool.MinimumCount.Value)
            {
                message = $"Constant-pool index {constantIndex.Value} is outside " +
                    $"the guaranteed pool size {entryPool.MinimumCount.Value}.";
            }
            else
            {
                continue;
            }

            AddDiagnostic(
                diagnostics,
                new Avm1BytecodeVerificationDiagnostic(
                    "AVM1VER015",
                    Avm1CompilationDiagnosticSeverity.Error,
                    actionIndex,
                    byteOffset,
                    message));
        }
    }

    private static IEnumerable<NestedBody> GetNestedBodies(
        Avm1Action action,
        int actionOffset,
        int actionLength,
        int[] parentActionOffsets,
        int parentByteLength,
        int[] parentExternalExits,
        Avm1CodeUnitContext parentContext,
        Avm1ConstantPoolContext entryPool,
        byte swfVersion)
    {
        switch (action)
        {
            case ActionDefineFunction function:
                yield return new NestedBody(
                    "function",
                    function.Body,
                    Avm1CodeUnitContext.Legacy(entryPool));
                break;
            case ActionDefineFunction2 function:
                yield return new NestedBody(
                    "function2",
                    function.Body,
                    Avm1CodeUnitContext.DefineFunction2(
                        function.RegisterCount,
                        entryPool));
                break;
            case ActionWith with:
                var withBodyOffset = checked(
                    actionOffset + actionLength - with.Body.Length);
                yield return new NestedBody(
                    "with",
                    with.Body,
                    parentContext.WithConstantPool(entryPool),
                    GetExternalExits(withBodyOffset));
                break;
            case ActionTry actionTry:
                var bodyLength = checked(
                    actionTry.TryBody.Length +
                    actionTry.CatchBody.Length +
                    actionTry.FinallyBody.Length);
                var tryBodyOffset = checked(
                    actionOffset + actionLength - bodyLength);
                var catchBodyOffset = checked(
                    tryBodyOffset + actionTry.TryBody.Length);
                var finallyBodyOffset = checked(
                    catchBodyOffset + actionTry.CatchBody.Length);
                var tryExitOffset = checked(
                    finallyBodyOffset + actionTry.FinallyBody.Length);
                yield return new NestedBody(
                    "try",
                    actionTry.TryBody,
                    parentContext.WithConstantPool(entryPool),
                    GetExternalExits(
                        tryBodyOffset,
                        catchBodyOffset,
                        finallyBodyOffset,
                        tryExitOffset));
                var handlerPool = entryPool.Constrain(
                    GetMinimumLeakingConstantPoolCount(
                        actionTry.TryBody,
                        swfVersion));
                if (actionTry.Flags.HasFlag(TryFlags.CatchBlock) ||
                    !actionTry.CatchBody.IsEmpty)
                {
                    yield return new NestedBody(
                        "catch",
                        actionTry.CatchBody,
                        parentContext.WithConstantPool(handlerPool),
                        GetExternalExits(
                            catchBodyOffset,
                            tryBodyOffset,
                            finallyBodyOffset,
                            tryExitOffset));
                }
                if (actionTry.Flags.HasFlag(TryFlags.FinallyBlock) ||
                    !actionTry.FinallyBody.IsEmpty)
                {
                    yield return new NestedBody(
                        "finally",
                        actionTry.FinallyBody,
                        parentContext.WithConstantPool(handlerPool),
                        GetExternalExits(
                            finallyBodyOffset,
                            tryBodyOffset,
                            catchBodyOffset,
                            tryExitOffset));
                }
                break;
        }

        int[] GetExternalExits(
            int nestedBodyOffset,
            params int[] additionalParentOffsets)
        {
            var exits = new HashSet<int>();
            for (var i = 0; i < parentActionOffsets.Length; i++)
            {
                exits.Add(checked(
                    parentActionOffsets[i] - nestedBodyOffset));
            }
            exits.Add(checked(parentByteLength - nestedBodyOffset));
            for (var i = 0; i < parentExternalExits.Length; i++)
            {
                exits.Add(checked(
                    parentExternalExits[i] - nestedBodyOffset));
            }
            for (var i = 0; i < additionalParentOffsets.Length; i++)
            {
                exits.Add(checked(
                    additionalParentOffsets[i] - nestedBodyOffset));
            }
            return exits.Order().ToArray();
        }
    }

    private static bool TryGetStackEffect(Avm1Action action, out StackEffect effect)
    {
        if (action is ActionPush push)
        {
            effect = new StackEffect(0, 0, push.PushValues.Count);
            return true;
        }
        if (action is ActionDefineFunction function)
        {
            effect = new StackEffect(
                0,
                0,
                string.IsNullOrEmpty(function.Name) ? 1 : 0);
            return true;
        }
        if (action is ActionDefineFunction2 function2)
        {
            effect = new StackEffect(
                0,
                0,
                string.IsNullOrEmpty(function2.Name) ? 1 : 0);
            return true;
        }

        effect = action.Opcode switch
        {
            ActionOpcode.End or ActionOpcode.NextFrame or ActionOpcode.PreviousFrame or
                ActionOpcode.Play or ActionOpcode.Stop or ActionOpcode.ToggleQuality or
                ActionOpcode.StopSounds or ActionOpcode.EndDrag or ActionOpcode.GotoFrame or
                ActionOpcode.GetURL or ActionOpcode.ConstantPool or ActionOpcode.WaitForFrame or
                ActionOpcode.SetTarget or ActionOpcode.GoToLabel or ActionOpcode.Jump or
                ActionOpcode.Try => StackEffect.None,

            ActionOpcode.GetTime => new StackEffect(0, 0, 1),
            ActionOpcode.StoreRegister => new StackEffect(1, 0, 0),
            ActionOpcode.PushDuplicate => new StackEffect(1, 1, 2),
            ActionOpcode.StackSwap => new StackEffect(2, 2, 2),

            ActionOpcode.Pop or ActionOpcode.SetTarget2 or ActionOpcode.RemoveSprite or
                ActionOpcode.Trace or ActionOpcode.DefineLocal2 or ActionOpcode.Return or
                ActionOpcode.Throw or ActionOpcode.If or ActionOpcode.Call or
                ActionOpcode.WaitForFrame2 or ActionOpcode.GotoFrame2 or ActionOpcode.With =>
                new StackEffect(1, 1, 0),

            ActionOpcode.GetVariable or ActionOpcode.Not or ActionOpcode.StringLength or
                ActionOpcode.ToInteger or ActionOpcode.RandomNumber or
                ActionOpcode.MBStringLength or ActionOpcode.CharToAscii or
                ActionOpcode.AsciiToChar or ActionOpcode.MBCharToAscii or
                ActionOpcode.MBAsciiToChar or ActionOpcode.Delete2 or ActionOpcode.TypeOf or
                ActionOpcode.TargetPath or ActionOpcode.ToNumber or ActionOpcode.ToString or
                ActionOpcode.Increment or ActionOpcode.Decrement =>
                new StackEffect(1, 1, 1),

            ActionOpcode.StringExtract or ActionOpcode.MBStringExtract =>
                new StackEffect(3, 3, 1),

            ActionOpcode.Add or ActionOpcode.Subtract or ActionOpcode.Multiply or
                ActionOpcode.Divide or ActionOpcode.Equals or ActionOpcode.Less or
                ActionOpcode.And or ActionOpcode.Or or ActionOpcode.StringEquals or
                ActionOpcode.StringAdd or ActionOpcode.StringLess or ActionOpcode.CastOp or
                ActionOpcode.Modulo or ActionOpcode.Add2 or ActionOpcode.Less2 or
                ActionOpcode.Equals2 or ActionOpcode.GetMember or ActionOpcode.Delete or
                ActionOpcode.InstanceOf or ActionOpcode.BitAnd or ActionOpcode.BitOr or
                ActionOpcode.BitXor or ActionOpcode.BitLShift or ActionOpcode.BitRShift or
                ActionOpcode.BitURShift or ActionOpcode.StrictEquals or ActionOpcode.Greater or
                ActionOpcode.StringGreater or ActionOpcode.GetProperty =>
                new StackEffect(2, 2, 1),

            ActionOpcode.SetVariable or ActionOpcode.GetURL2 or ActionOpcode.DefineLocal or
                ActionOpcode.Extends => new StackEffect(2, 2, 0),

            ActionOpcode.SetProperty or ActionOpcode.CloneSprite or ActionOpcode.SetMember =>
                new StackEffect(3, 3, 0),

            _ => StackEffect.Unknown
        };
        return effect.IsKnown;
    }

    private static void AddDiagnostic(
        List<Avm1BytecodeVerificationDiagnostic> diagnostics,
        Avm1BytecodeVerificationDiagnostic diagnostic)
    {
        if (!diagnostics.Contains(diagnostic))
            diagnostics.Add(diagnostic);
    }

    private static Avm1BytecodeVerificationResult InvalidBytecode(
        string code,
        string message) =>
        new(
            [],
            [],
            0,
            0,
            true,
            [
                new Avm1BytecodeVerificationDiagnostic(
                    code,
                    Avm1CompilationDiagnosticSeverity.Error,
                    -1,
                    -1,
                    message)
            ]);

    private static bool IsRecoverableCodecException(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or
            OperationCanceledException);

    private readonly record struct StackEffect(
        int RequiredDepth,
        int PopCount,
        int PushCount,
        bool IsKnown = true)
    {
        public static readonly StackEffect None = new(0, 0, 0);
        public static readonly StackEffect Unknown = new(0, 0, 0, IsKnown: false);
    }

    private enum AbstractValueKind : byte
    {
        Unknown,
        KnownInteger,
        KnownBoolean,
        Null,
        EnumerationStream,
        EnumerationEndPredicate
    }

    private readonly record struct AbstractValue(
        AbstractValueKind Kind,
        int Payload = 0,
        bool Flag = false)
    {
        public static readonly AbstractValue Unknown =
            new(AbstractValueKind.Unknown);
        public static readonly AbstractValue Null =
            new(AbstractValueKind.Null);

        public bool IsEnumerationSensitive =>
            Kind is AbstractValueKind.EnumerationStream or
                AbstractValueKind.EnumerationEndPredicate;

        public static AbstractValue KnownInteger(int value) =>
            new(AbstractValueKind.KnownInteger, value);

        public static AbstractValue KnownBoolean(bool value) =>
            new(AbstractValueKind.KnownBoolean, Flag: value);

        public static AbstractValue EnumerationStream(int actionIndex) =>
            new(AbstractValueKind.EnumerationStream, actionIndex);

        public static AbstractValue EnumerationEndPredicate(
            int actionIndex,
            bool trueMeansEnd) =>
            new(
                AbstractValueKind.EnumerationEndPredicate,
                actionIndex,
                trueMeansEnd);
    }

    private readonly record struct StackVerificationSummary(
        int MaximumStackDepth,
        bool IsExact);

    private readonly record struct NestedBody(
        string Name,
        ReadOnlyMemory<byte> Body,
        Avm1CodeUnitContext Context,
        int[]? ExternalExits = null)
    {
        public int[] ExternalBranchExitOffsets { get; } =
            ExternalExits ?? [];
    }
}
