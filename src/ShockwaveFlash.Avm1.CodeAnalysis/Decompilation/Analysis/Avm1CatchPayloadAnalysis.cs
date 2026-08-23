using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1CatchPayloadAnalysis
{
    private readonly ValueIndex[] _sources;
    private readonly CatchBindingIndex[] _bindingByAction;
    private readonly string?[] _variableNames;

    private Avm1CatchPayloadAnalysis(
        Avm1CatchPayloadBinding[] bindings,
        ValueIndex[] sources,
        CatchBindingIndex[] bindingByAction,
        string?[] variableNames)
    {
        Bindings = bindings;
        _sources = sources;
        Sources = sources;
        _bindingByAction = bindingByAction;
        _variableNames = variableNames;
    }

    public IReadOnlyList<Avm1CatchPayloadBinding> Bindings { get; }

    public IReadOnlyList<ValueIndex> Sources { get; }

    public ReadOnlySpan<ValueIndex> GetSources(Avm1CatchPayloadBinding binding)
    {
        if (!binding.SourceStart.IsValid || binding.SourceCount == 0)
            return [];

        return _sources.AsSpan(binding.SourceStart.Value, binding.SourceCount);
    }

    public bool TryGetBinding(
        ActionIndex catchAction,
        out Avm1CatchPayloadBinding binding)
    {
        if (!catchAction.IsValid || catchAction.Value >= _bindingByAction.Length)
        {
            binding = default;
            return false;
        }

        var index = _bindingByAction[catchAction.Value];
        if (!index.IsValid)
        {
            binding = default;
            return false;
        }

        binding = Bindings[index.Value];
        return true;
    }

    public bool TryGetExactSource(ActionIndex catchAction, out ValueIndex source)
    {
        if (TryGetBinding(catchAction, out var binding))
            return TryGetExactSource(binding, out source);

        source = ValueIndex.Invalid;
        return false;
    }

    public bool TryGetExactVariableSource(
        ActionIndex useAction,
        string name,
        out ValueIndex source) => TryGetExactVariableSource(
            useAction,
            name,
            out _,
            out source);

    public bool TryGetExactVariableSource(
        ActionIndex useAction,
        string name,
        out Avm1CatchPayloadBinding binding,
        out ValueIndex source)
    {
        Avm1CatchPayloadBinding? best = null;
        foreach (var candidate in Bindings)
        {
            if (useAction.Value < candidate.BodyStartAction.Value ||
                useAction.Value >= candidate.BodyEndAction.Value ||
                !string.Equals(
                    _variableNames[candidate.Index.Value],
                    name,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (best is null ||
                candidate.BodyEndAction.Value - candidate.BodyStartAction.Value <
                best.Value.BodyEndAction.Value - best.Value.BodyStartAction.Value)
            {
                best = candidate;
            }
        }

        if (best is not null)
        {
            binding = best.Value;
            return TryGetExactSource(binding, out source);
        }

        binding = default;
        source = ValueIndex.Invalid;
        return false;
    }

    private bool TryGetExactSource(
        Avm1CatchPayloadBinding binding,
        out ValueIndex source)
    {
        if ((binding.Flags & Avm1CatchPayloadFlags.HasUnknownSource) == 0 &&
            binding.SourceCount == 1)
        {
            source = _sources[binding.SourceStart.Value];
            return true;
        }

        source = ValueIndex.Invalid;
        return false;
    }

    public static Avm1CatchPayloadAnalysis Build(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph cfg,
        Avm1CompletionSsa completionSsa)
    {
        var bindings = new List<Avm1CatchPayloadBinding>();
        var sources = new List<ValueIndex>();
        var variableNames = new List<string?>();
        var bindingByAction = Enumerable.Repeat(
            CatchBindingIndex.Invalid,
            instructions.Count).ToArray();
        var phiByResult = completionSsa.PhiNodes.ToDictionary(phi => phi.Result);

        foreach (var region in instructions.TryRegions)
        {
            if (!region.CatchEnterAction.IsValid ||
                !cfg.TryGetBlockForAction(region.CatchEnterAction, out var catchBlock))
            {
                continue;
            }

            var sourceSet = new HashSet<ValueIndex>();
            var visited = new HashSet<CompletionValueIndex>();
            var hasUnknownSource = false;
            var hasIncomingFlow = false;
            foreach (var flow in completionSsa.Flows)
            {
                if (flow.Target != catchBlock ||
                    flow.Kind is not Avm1CompletionKind.Throw)
                {
                    continue;
                }

                hasIncomingFlow = true;
                hasUnknownSource |= !CollectSources(
                    completionSsa,
                    phiByResult,
                    flow.Payload,
                    sourceSet,
                    visited);
            }

            hasUnknownSource |= !hasIncomingFlow;
            var orderedSources = sourceSet.OrderBy(value => value.Value).ToArray();
            hasUnknownSource |= orderedSources.Length == 0;
            var sourceStart = orderedSources.Length == 0
                ? CatchSourceIndex.Invalid
                : new CatchSourceIndex(sources.Count);
            sources.AddRange(orderedSources);

            var catchRegister = instructions[region.CatchEnterAction].Action is Avm1CatchStartAction catchStart
                ? catchStart.CatchRegister
                : -1;
            var tryAction = instructions[region.EnterAction].Action as ActionTry;
            var catchVariable = tryAction is not null &&
                !tryAction.Flags.HasFlag(TryFlags.CatchInRegister)
                    ? tryAction.CatchVariable
                    : null;
            var index = new CatchBindingIndex(bindings.Count);
            bindings.Add(new Avm1CatchPayloadBinding(
                index,
                region.CatchEnterAction,
                catchBlock,
                catchRegister,
                region.CatchBodyStartAction,
                region.CatchExitAction,
                sourceStart,
                orderedSources.Length,
                hasUnknownSource
                    ? Avm1CatchPayloadFlags.HasUnknownSource
                    : Avm1CatchPayloadFlags.None));
            variableNames.Add(catchVariable);
            bindingByAction[region.CatchEnterAction.Value] = index;
        }

        return new Avm1CatchPayloadAnalysis(
            bindings.ToArray(),
            sources.ToArray(),
            bindingByAction,
            variableNames.ToArray());
    }

    private static bool CollectSources(
        Avm1CompletionSsa completionSsa,
        IReadOnlyDictionary<CompletionValueIndex, Avm1CompletionPhiNode> phiByResult,
        CompletionValueIndex payload,
        HashSet<ValueIndex> sources,
        HashSet<CompletionValueIndex> visited)
    {
        if (!payload.IsValid || payload.Value >= completionSsa.Values.Count)
            return false;
        if (!visited.Add(payload))
            return true;

        var value = completionSsa.Values[payload.Value];
        if (value.Kind is Avm1CompletionValueKind.Source)
        {
            if (!value.SourceValue.IsValid)
                return false;
            sources.Add(value.SourceValue);
            return true;
        }

        if (value.Kind is not Avm1CompletionValueKind.Phi ||
            !phiByResult.TryGetValue(payload, out var phi))
        {
            return false;
        }

        var inputs = completionSsa.GetPhiInputs(phi);
        if (inputs.Length == 0)
            return false;

        var allKnown = true;
        foreach (var input in inputs)
        {
            allKnown &= CollectSources(
                completionSsa,
                phiByResult,
                input.Value,
                sources,
                visited);
        }

        return allKnown;
    }
}

[Flags]
public enum Avm1CatchPayloadFlags : byte
{
    None = 0,
    HasUnknownSource = 1
}

public readonly record struct Avm1CatchPayloadBinding(
    CatchBindingIndex Index,
    ActionIndex CatchAction,
    BlockIndex CatchBlock,
    int CatchRegister,
    ActionIndex BodyStartAction,
    ActionIndex BodyEndAction,
    CatchSourceIndex SourceStart,
    int SourceCount,
    Avm1CatchPayloadFlags Flags);
