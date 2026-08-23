using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Avm1.Compilation.Assembly;

internal enum Avm1LogicalControlKind : byte
{
    None,
    ByteBranch,
    ActionSkip
}

internal readonly record struct Avm1LogicalControl(
    Avm1LogicalControlKind Kind,
    ActionOpcode Opcode,
    Avm1LabelId Target,
    ushort Frame)
{
    public static readonly Avm1LogicalControl None = new(
        Avm1LogicalControlKind.None,
        default,
        Avm1LabelId.Invalid,
        0);

    public static Avm1LogicalControl CreateBranch(
        ActionOpcode opcode,
        Avm1LabelId target) =>
        new(Avm1LogicalControlKind.ByteBranch, opcode, target, 0);

    public static Avm1LogicalControl CreateSkip(
        ActionOpcode opcode,
        Avm1LabelId target,
        ushort frame) =>
        new(Avm1LogicalControlKind.ActionSkip, opcode, target, frame);
}

internal sealed class Avm1PhysicalLayoutResult
{
    public Avm1PhysicalLayoutResult(
        bool succeeded,
        Avm1Action[] actions,
        int[] labelOffsets,
        int[] labelActionIndices,
        Avm1AssemblyInstructionLocation[] instructionLocations,
        Avm1AssemblyActionLocation[] actionLocations)
    {
        Succeeded = succeeded;
        Actions = actions;
        LabelOffsets = labelOffsets;
        LabelActionIndices = labelActionIndices;
        InstructionLocations = instructionLocations;
        ActionLocations = actionLocations;
    }

    public bool Succeeded { get; }

    public Avm1Action[] Actions { get; }

    public int[] LabelOffsets { get; }

    public int[] LabelActionIndices { get; }

    public Avm1AssemblyInstructionLocation[] InstructionLocations { get; }

    public Avm1AssemblyActionLocation[] ActionLocations { get; }
}

internal static class Avm1PhysicalLayout
{
    internal const int MaximumTrampolineHopDistance = 32_000;

    public static Avm1PhysicalLayoutResult Build(
        IReadOnlyList<Avm1Action> logicalActions,
        IReadOnlyList<Avm1LogicalControl> controls,
        IReadOnlyList<int> logicalLabelActionIndices,
        byte swfVersion,
        List<Avm1AssemblyDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(logicalActions);
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(logicalLabelActionIndices);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (logicalActions.Count != controls.Count)
        {
            throw new ArgumentException(
                "Every logical action must have one control-flow descriptor.",
                nameof(controls));
        }

        return new Builder(
            logicalActions,
            controls,
            logicalLabelActionIndices,
            swfVersion,
            diagnostics).Build();
    }

    private sealed class Builder
    {
        private const int MaximumSyntheticActions = 1_000_000;

        private readonly int _logicalActionCount;
        private readonly int[] _logicalNodeIds;
        private readonly int[] _logicalLabelActionIndices;
        private readonly byte _swfVersion;
        private readonly List<Avm1AssemblyDiagnostic> _diagnostics;
        private readonly List<PhysicalNode> _nodes;
        private readonly List<int> _order;

        public Builder(
            IReadOnlyList<Avm1Action> logicalActions,
            IReadOnlyList<Avm1LogicalControl> controls,
            IReadOnlyList<int> logicalLabelActionIndices,
            byte swfVersion,
            List<Avm1AssemblyDiagnostic> diagnostics)
        {
            _logicalActionCount = logicalActions.Count;
            _logicalNodeIds = new int[_logicalActionCount];
            _logicalLabelActionIndices = logicalLabelActionIndices.ToArray();
            _swfVersion = swfVersion;
            _diagnostics = diagnostics;
            _nodes = new List<PhysicalNode>(_logicalActionCount);
            _order = new List<int>(_logicalActionCount);

            for (var i = 0; i < _logicalActionCount; i++)
            {
                var control = controls[i];
                var target = control.Kind is Avm1LogicalControlKind.None
                    ? PhysicalTarget.None
                    : PhysicalTarget.ForLabel(control.Target);
                var nodeId = _nodes.Count;
                _logicalNodeIds[i] = nodeId;
                _nodes.Add(new PhysicalNode(
                    logicalActions[i],
                    GetEncodedLength(
                        logicalActions[i],
                        new Avm1AssemblyInstructionIndex(i)),
                    new Avm1AssemblyInstructionIndex(i),
                    Avm1SyntheticActionKind.None,
                    control.Kind,
                    control.Opcode,
                    control.Frame,
                    target));
                _order.Add(nodeId);
            }
        }

        public Avm1PhysicalLayoutResult Build()
        {
            if (HasErrors())
                return Failed();
            if (!TryExpandLongBranches(out var state))
                return Failed();

            return Resolve(state);
        }

        private bool TryExpandLongBranches(out LayoutState state)
        {
            while (true)
            {
                if (!TryComputeLayout(out state))
                    return false;

                var expanded = false;
                for (var physicalIndex = 0;
                    physicalIndex < _order.Count;
                    physicalIndex++)
                {
                    var nodeId = _order[physicalIndex];
                    var node = _nodes[nodeId];
                    if (node.ControlKind is not Avm1LogicalControlKind.ByteBranch)
                        continue;

                    var targetOffset = GetTargetOffset(node.Target, state);
                    var displacement = (long)targetOffset -
                        (state.NodeOffsets[nodeId] + node.ByteLength);
                    if (displacement is >= short.MinValue and <= short.MaxValue)
                        continue;

                    if (TryShareTrampoline(
                            nodeId,
                            node,
                            targetOffset,
                            state))
                    {
                        expanded = true;
                        break;
                    }

                    if (!TryInsertTrampoline(nodeId, node, targetOffset, state))
                    {
                        _diagnostics.Add(new Avm1AssemblyDiagnostic(
                            "AVM1ASM014",
                            Avm1CompilationDiagnosticSeverity.Error,
                            node.Owner,
                            state.NodeOffsets[nodeId],
                            $"No action boundary can bridge the branch from offset " +
                            $"{state.NodeOffsets[nodeId]} to {targetOffset}."));
                        return false;
                    }

                    expanded = true;
                    break;
                }

                if (!expanded)
                    return true;
            }
        }

        private bool TryShareTrampoline(
            int sourceNodeId,
            PhysicalNode source,
            int targetOffset,
            LayoutState state)
        {
            var sourceOffset = state.NodeOffsets[sourceNodeId];
            var sourceEnd = sourceOffset + source.ByteLength;
            var forward = targetOffset > sourceEnd;
            var terminalTarget = GetTerminalTarget(source.Target);
            var candidateNodeId = -1;
            var candidateOffset = forward ? int.MinValue : int.MaxValue;

            foreach (var nodeId in _order)
            {
                if (nodeId == sourceNodeId)
                    continue;

                var candidate = _nodes[nodeId];
                if (candidate.SyntheticKind is not
                        Avm1SyntheticActionKind.TrampolineJump ||
                    GetTerminalTarget(candidate.Target) != terminalTarget)
                {
                    continue;
                }

                var offset = state.NodeOffsets[nodeId];
                var displacement = (long)offset - sourceEnd;
                if (displacement is < short.MinValue or > short.MaxValue)
                    continue;

                if (forward)
                {
                    if (offset <= sourceEnd ||
                        offset >= targetOffset ||
                        offset <= candidateOffset)
                    {
                        continue;
                    }
                }
                else if (offset >= sourceOffset ||
                    offset <= targetOffset ||
                    offset >= candidateOffset)
                {
                    continue;
                }

                candidateNodeId = nodeId;
                candidateOffset = offset;
            }

            if (candidateNodeId < 0)
                return false;

            source.Target = PhysicalTarget.ForNode(candidateNodeId);
            _nodes[sourceNodeId] = source;
            return true;
        }

        private PhysicalTarget GetTerminalTarget(PhysicalTarget target)
        {
            var remaining = _nodes.Count;
            while (target.Kind is PhysicalTargetKind.Node && remaining-- > 0)
            {
                var node = _nodes[target.Value];
                if (node.SyntheticKind is not
                    Avm1SyntheticActionKind.TrampolineJump)
                {
                    break;
                }
                target = node.Target;
            }

            if (remaining < 0)
            {
                throw new InvalidOperationException(
                    "A physical trampoline chain contains a cycle.");
            }
            return target;
        }

        private bool TryInsertTrampoline(
            int sourceNodeId,
            PhysicalNode source,
            int targetOffset,
            LayoutState state)
        {
            if (_nodes.Count - _logicalActionCount + 2 > MaximumSyntheticActions)
            {
                _diagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM015",
                    Avm1CompilationDiagnosticSeverity.Error,
                    source.Owner,
                    state.NodeOffsets[sourceNodeId],
                    "Long-branch expansion exceeded the synthetic-action safety limit."));
                return false;
            }

            var sourceOffset = state.NodeOffsets[sourceNodeId];
            var sourceEnd = sourceOffset + source.ByteLength;
            var forward = targetOffset > sourceEnd;
            var boundary = FindInsertionBoundary(
                forward,
                sourceOffset,
                sourceEnd,
                targetOffset,
                state);
            if (boundary < 0)
                return false;

            var baseNodeId = _logicalNodeIds[boundary];
            var insertIndex = _order.IndexOf(baseNodeId);
            if (insertIndex < 0)
                throw new InvalidOperationException("Logical layout node is missing.");

            var jumpLength = GetEncodedLength(
                new ActionJump(0),
                source.Owner);
            if (HasErrors())
                return false;

            var guardNodeId = _nodes.Count;
            _nodes.Add(new PhysicalNode(
                new ActionJump(0),
                jumpLength,
                source.Owner,
                Avm1SyntheticActionKind.TrampolineGuard,
                Avm1LogicalControlKind.ByteBranch,
                ActionOpcode.Jump,
                0,
                PhysicalTarget.ForNode(baseNodeId)));
            var trampolineNodeId = _nodes.Count;
            _nodes.Add(new PhysicalNode(
                new ActionJump(0),
                jumpLength,
                source.Owner,
                Avm1SyntheticActionKind.TrampolineJump,
                Avm1LogicalControlKind.ByteBranch,
                ActionOpcode.Jump,
                0,
                source.Target));

            _order.Insert(insertIndex, guardNodeId);
            _order.Insert(insertIndex + 1, trampolineNodeId);
            source.Target = PhysicalTarget.ForNode(trampolineNodeId);
            _nodes[sourceNodeId] = source;
            return true;
        }

        private int FindInsertionBoundary(
            bool forward,
            int sourceOffset,
            int sourceEnd,
            int targetOffset,
            LayoutState state)
        {
            var candidate = -1;
            var candidateOffset = forward ? int.MinValue : int.MaxValue;
            for (var logicalIndex = 0;
                logicalIndex < _logicalActionCount;
                logicalIndex++)
            {
                var boundaryOffset =
                    state.NodeOffsets[_logicalNodeIds[logicalIndex]];
                if (forward)
                {
                    if (boundaryOffset <= sourceEnd ||
                        boundaryOffset >= targetOffset ||
                        boundaryOffset >
                            (long)sourceEnd + MaximumTrampolineHopDistance ||
                        boundaryOffset <= candidateOffset)
                    {
                        continue;
                    }
                }
                else
                {
                    if (boundaryOffset >= sourceOffset ||
                        boundaryOffset <= targetOffset ||
                        boundaryOffset <
                            (long)sourceEnd - MaximumTrampolineHopDistance ||
                        boundaryOffset >= candidateOffset)
                    {
                        continue;
                    }
                }

                candidate = logicalIndex;
                candidateOffset = boundaryOffset;
            }
            return candidate;
        }

        private Avm1PhysicalLayoutResult Resolve(LayoutState state)
        {
            var labelOffsets = BuildLabelOffsets(state);
            var labelActionIndices = BuildLabelActionIndices(state);
            var actions = new Avm1Action[_order.Count];
            var actionLocations = new Avm1AssemblyActionLocation[_order.Count];
            var instructionLocations =
                new Avm1AssemblyInstructionLocation[_logicalActionCount];

            for (var actionIndex = 0; actionIndex < _order.Count; actionIndex++)
            {
                var nodeId = _order[actionIndex];
                var node = _nodes[nodeId];
                Avm1Action action;
                switch (node.ControlKind)
                {
                    case Avm1LogicalControlKind.None:
                        action = node.Action;
                        break;
                    case Avm1LogicalControlKind.ByteBranch:
                        {
                            var displacement = (long)GetTargetOffset(node.Target, state) -
                                (state.NodeOffsets[nodeId] + node.ByteLength);
                            if (displacement is < short.MinValue or > short.MaxValue)
                            {
                                _diagnostics.Add(new Avm1AssemblyDiagnostic(
                                    "AVM1ASM014",
                                    Avm1CompilationDiagnosticSeverity.Error,
                                    node.Owner,
                                    state.NodeOffsets[nodeId],
                                    $"Physical branch displacement {displacement} is out of range."));
                                continue;
                            }
                            action = CreateBranch(node.Opcode, (short)displacement);
                            break;
                        }
                    case Avm1LogicalControlKind.ActionSkip:
                        {
                            var targetActionIndex =
                                labelActionIndices[node.Target.Value];
                            var skippedActions = (long)targetActionIndex -
                                actionIndex - 1;
                            if (skippedActions is < byte.MinValue or > byte.MaxValue)
                            {
                                _diagnostics.Add(new Avm1AssemblyDiagnostic(
                                    "AVM1ASM013",
                                    Avm1CompilationDiagnosticSeverity.Error,
                                    node.Owner,
                                    state.NodeOffsets[nodeId],
                                    $"Skip from action {actionIndex} to action " +
                                    $"{targetActionIndex} requires SkipCount " +
                                    $"{skippedActions}, outside the unsigned 8-bit range."));
                                continue;
                            }
                            action = CreateSkip(
                                node.Opcode,
                                node.Frame,
                                (byte)skippedActions);
                            break;
                        }
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported control kind {node.ControlKind}.");
                }

                actions[actionIndex] = action;
                actionLocations[actionIndex] = new Avm1AssemblyActionLocation(
                    actionIndex,
                    state.NodeOffsets[nodeId],
                    node.ByteLength,
                    node.Owner,
                    node.SyntheticKind);
                if (node.SyntheticKind is Avm1SyntheticActionKind.None)
                {
                    instructionLocations[node.Owner.Value] =
                        new Avm1AssemblyInstructionLocation(
                            node.Owner,
                            actionIndex,
                            state.NodeOffsets[nodeId],
                            node.ByteLength);
                }
            }

            if (HasErrors())
                return Failed();
            return new Avm1PhysicalLayoutResult(
                true,
                actions,
                labelOffsets,
                labelActionIndices,
                instructionLocations,
                actionLocations);
        }

        private bool TryComputeLayout(out LayoutState state)
        {
            var nodeOffsets = new int[_nodes.Count];
            var nodeActionIndices = new int[_nodes.Count];
            long byteOffset = 0;
            for (var actionIndex = 0; actionIndex < _order.Count; actionIndex++)
            {
                var nodeId = _order[actionIndex];
                if (byteOffset > int.MaxValue)
                {
                    _diagnostics.Add(new Avm1AssemblyDiagnostic(
                        "AVM1ASM005",
                        Avm1CompilationDiagnosticSeverity.Error,
                        _nodes[nodeId].Owner,
                        int.MaxValue,
                        "The assembled code unit exceeds the supported 2 GB layout range."));
                    state = default;
                    return false;
                }

                nodeOffsets[nodeId] = (int)byteOffset;
                nodeActionIndices[nodeId] = actionIndex;
                byteOffset += _nodes[nodeId].ByteLength;
            }

            if (byteOffset > int.MaxValue)
            {
                _diagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM005",
                    Avm1CompilationDiagnosticSeverity.Error,
                    Avm1AssemblyInstructionIndex.Invalid,
                    int.MaxValue,
                    "The assembled code unit exceeds the supported 2 GB layout range."));
                state = default;
                return false;
            }

            state = new LayoutState(
                nodeOffsets,
                nodeActionIndices,
                (int)byteOffset);
            return true;
        }

        private int GetTargetOffset(PhysicalTarget target, LayoutState state) =>
            target.Kind switch
            {
                PhysicalTargetKind.Label => GetLabelOffset(target.Value, state),
                PhysicalTargetKind.Node => state.NodeOffsets[target.Value],
                _ => throw new InvalidOperationException("Control target is missing.")
            };

        private int GetLabelOffset(int label, LayoutState state)
        {
            var logicalIndex = _logicalLabelActionIndices[label];
            return logicalIndex == _logicalActionCount
                ? state.ByteLength
                : state.NodeOffsets[_logicalNodeIds[logicalIndex]];
        }

        private int[] BuildLabelOffsets(LayoutState state)
        {
            var result = new int[_logicalLabelActionIndices.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = GetLabelOffset(i, state);
            return result;
        }

        private int[] BuildLabelActionIndices(LayoutState state)
        {
            var result = new int[_logicalLabelActionIndices.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var logicalIndex = _logicalLabelActionIndices[i];
                result[i] = logicalIndex == _logicalActionCount
                    ? _order.Count
                    : state.NodeActionIndices[_logicalNodeIds[logicalIndex]];
            }
            return result;
        }

        private int GetEncodedLength(
            Avm1Action action,
            Avm1AssemblyInstructionIndex owner)
        {
            try
            {
                return Avm1Action.EncodeCollection([action], _swfVersion).Length;
            }
            catch (Exception exception) when (IsRecoverableCodecException(exception))
            {
                _diagnostics.Add(new Avm1AssemblyDiagnostic(
                    "AVM1ASM004",
                    Avm1CompilationDiagnosticSeverity.Error,
                    owner,
                    -1,
                    $"Cannot encode {action.Opcode}: {exception.Message}"));
                return 0;
            }
        }

        private bool HasErrors() => _diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

        private Avm1PhysicalLayoutResult Failed() =>
            new(
                false,
                [],
                CreateInvalidArray(_logicalLabelActionIndices.Length),
                CreateInvalidArray(_logicalLabelActionIndices.Length),
                new Avm1AssemblyInstructionLocation[_logicalActionCount],
                []);

        private static int[] CreateInvalidArray(int length)
        {
            var result = new int[length];
            Array.Fill(result, -1);
            return result;
        }

        private static Avm1Action CreateBranch(
            ActionOpcode opcode,
            short displacement) =>
            opcode switch
            {
                ActionOpcode.Jump => new ActionJump(displacement),
                ActionOpcode.If => new ActionIf(displacement),
                _ => throw new ArgumentOutOfRangeException(nameof(opcode))
            };

        private static Avm1Action CreateSkip(
            ActionOpcode opcode,
            ushort frame,
            byte skipCount) =>
            opcode switch
            {
                ActionOpcode.WaitForFrame =>
                    new ActionWaitForFrame(frame, skipCount),
                ActionOpcode.WaitForFrame2 => new ActionWaitForFrame2(skipCount),
                _ => throw new ArgumentOutOfRangeException(nameof(opcode))
            };

        private static bool IsRecoverableCodecException(Exception exception) =>
            exception is not (OutOfMemoryException or StackOverflowException or
                OperationCanceledException);

        private readonly record struct LayoutState(
            int[] NodeOffsets,
            int[] NodeActionIndices,
            int ByteLength);

        private record struct PhysicalNode(
            Avm1Action Action,
            int ByteLength,
            Avm1AssemblyInstructionIndex Owner,
            Avm1SyntheticActionKind SyntheticKind,
            Avm1LogicalControlKind ControlKind,
            ActionOpcode Opcode,
            ushort Frame,
            PhysicalTarget Target);

        private enum PhysicalTargetKind : byte
        {
            None,
            Label,
            Node
        }

        private readonly record struct PhysicalTarget(
            PhysicalTargetKind Kind,
            int Value)
        {
            public static readonly PhysicalTarget None = new(
                PhysicalTargetKind.None,
                -1);

            public static PhysicalTarget ForLabel(
                Avm1LabelId label) =>
                new(PhysicalTargetKind.Label, label.Value);

            public static PhysicalTarget ForNode(
                int nodeId) =>
                new(PhysicalTargetKind.Node, nodeId);
        }
    }
}
