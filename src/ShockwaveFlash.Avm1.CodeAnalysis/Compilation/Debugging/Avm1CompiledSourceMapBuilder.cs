using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

internal static class Avm1CompiledSourceMapBuilder
{
    public static Avm1CompiledSourceMap Build(
        Avm1SourceMethod source,
        Avm1MirMethod mir,
        Avm1StackLirMethod? stackLir,
        Avm1ActionBody? body,
        IReadOnlyList<Avm1CompiledInstructionMap> instructionMap,
        IReadOnlyList<Avm1CompiledFunctionArtifact> nestedFunctions,
        IReadOnlyList<Avm1CompiledWithArtifact> nestedWithRegions,
        IReadOnlyList<Avm1CompiledTryArtifact> nestedTryRegions)
    {
        if (stackLir is null || body is not { Succeeded: true })
            return new Avm1CompiledSourceMap(source.Arena, [], []);

        var builder = new Builder(source.Arena);
        builder.AddUnit(
            new Unit(
                source,
                mir,
                stackLir,
                instructionMap,
                nestedFunctions,
                nestedWithRegions,
                nestedTryRegions),
            body,
            Avm1CompiledCodeUnitKind.Method,
            Avm1CompiledCodeUnitIndex.Invalid,
            Avm1AssemblyInstructionIndex.Invalid,
            byteOffset: 0);
        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly Avm1SourceArena _arena;
        private readonly List<Avm1CompiledSourceMapCodeUnit> _codeUnits = [];
        private readonly List<Avm1CompiledSourceMapEntry> _entries = [];

        public Builder(Avm1SourceArena arena)
        {
            _arena = arena;
        }

        public Avm1CompiledSourceMap Build()
        {
            var entries = _entries
                .OrderBy(entry => entry.ByteOffset)
                .ThenBy(entry => _codeUnits[entry.CodeUnit.Value].Depth)
                .ThenBy(entry => entry.ActionIndex)
                .ToArray();
            return new Avm1CompiledSourceMap(
                _arena,
                _codeUnits.ToArray(),
                entries);
        }

        public void AddUnit(
            Unit source,
            Avm1ActionBody body,
            Avm1CompiledCodeUnitKind kind,
            Avm1CompiledCodeUnitIndex parent,
            Avm1AssemblyInstructionIndex parentInstruction,
            int byteOffset)
        {
            if (!ReferenceEquals(source.Source.Arena, _arena))
            {
                throw new InvalidOperationException(
                    "Nested source-map code units must share one source arena.");
            }

            var index = new Avm1CompiledCodeUnitIndex(_codeUnits.Count);
            var depth = parent.IsValid
                ? checked(_codeUnits[parent.Value].Depth + 1)
                : 0;
            _codeUnits.Add(new Avm1CompiledSourceMapCodeUnit(
                index,
                parent,
                kind,
                depth,
                byteOffset,
                body.Bytes.Length,
                parentInstruction));

            var mappings = new Dictionary<int, Avm1CompiledInstructionMap>();
            foreach (var mapping in source.InstructionMap)
            {
                if (!mapping.AssemblyInstruction.IsValid)
                    continue;

                if (mappings.TryGetValue(
                        mapping.AssemblyInstruction.Value,
                        out var existing))
                {
                    if (existing.MirInstruction != mapping.MirInstruction ||
                        existing.Origin != mapping.Origin)
                    {
                        throw new InvalidOperationException(
                            $"Assembly instruction {mapping.AssemblyInstruction} " +
                            "has incompatible compiler source mappings.");
                    }
                    continue;
                }

                mappings.Add(mapping.AssemblyInstruction.Value, mapping);
            }

            foreach (var location in body.ActionLocations)
            {
                if (!location.Owner.IsValid ||
                    !mappings.TryGetValue(location.Owner.Value, out var mapping) ||
                    !mapping.Origin.IsValid)
                {
                    continue;
                }
                if (mapping.Origin.Value >= _arena.Origins.Count)
                {
                    throw new InvalidOperationException(
                        $"Source origin {mapping.Origin} is outside its arena.");
                }

                _entries.Add(new Avm1CompiledSourceMapEntry(
                    index,
                    mapping.Origin,
                    location.ActionIndex,
                    checked(byteOffset + location.ByteOffset),
                    location.ByteLength,
                    mapping.AssemblyInstruction,
                    mapping.StackInstruction,
                    mapping.MirInstruction,
                    location.SyntheticKind));
            }

            foreach (var nested in body.NestedBodies)
            {
                if (nested.ByteOffset < 0)
                {
                    throw new InvalidOperationException(
                        $"Nested {nested.Kind} body has no physical byte offset.");
                }
                if (!mappings.TryGetValue(nested.Owner.Value, out var mapping))
                {
                    throw new InvalidOperationException(
                        $"Nested {nested.Kind} owner {nested.Owner} has no " +
                        "compiler instruction mapping.");
                }
                if (!TryGetNestedUnit(source, mapping, nested.Kind, out var child))
                {
                    throw new InvalidOperationException(
                        $"Nested {nested.Kind} owner {nested.Owner} has no " +
                        "matching compiler artifact.");
                }

                AddUnit(
                    child.Source,
                    nested.Body,
                    child.Kind,
                    index,
                    nested.Owner,
                    checked(byteOffset + nested.ByteOffset));
            }
        }

        private static bool TryGetNestedUnit(
            Unit parent,
            Avm1CompiledInstructionMap mapping,
            Avm1NestedCodeUnitKind nestedKind,
            out NestedUnit result)
        {
            result = default;
            if (!mapping.StackInstruction.IsValid ||
                mapping.StackInstruction.Value >= parent.StackLir.Instructions.Count)
            {
                return false;
            }

            var instruction = parent.StackLir[mapping.StackInstruction];
            if (nestedKind is Avm1NestedCodeUnitKind.DefineFunctionBody or
                Avm1NestedCodeUnitKind.DefineFunction2Body)
            {
                if (instruction.Kind is not Avm1StackLirInstructionKind.DefineFunction ||
                    !instruction.Function.IsValid ||
                    instruction.Function.Value >= parent.StackLir.Functions.Count)
                {
                    return false;
                }

                var function = parent.StackLir[instruction.Function];
                var artifact = parent.NestedFunctions.FirstOrDefault(candidate =>
                    candidate.Plan.Function == function.SourceFunction);
                if (artifact is null)
                    return false;

                result = new NestedUnit(
                    CreateUnit(artifact),
                    Avm1CompiledCodeUnitKind.Function);
                return true;
            }

            if (nestedKind is Avm1NestedCodeUnitKind.WithBody)
            {
                if (instruction.Kind is not Avm1StackLirInstructionKind.With ||
                    !instruction.WithSite.IsValid ||
                    instruction.WithSite.Value >= parent.StackLir.WithSites.Count)
                {
                    return false;
                }

                var site = parent.StackLir[instruction.WithSite].MirWithSite;
                var artifact = parent.NestedWithRegions.FirstOrDefault(candidate =>
                    candidate.Plan.Site == site);
                if (artifact is null)
                    return false;

                result = new NestedUnit(
                    CreateUnit(artifact.Body),
                    Avm1CompiledCodeUnitKind.With);
                return true;
            }

            if (instruction.Kind is not Avm1StackLirInstructionKind.Try ||
                !instruction.TrySite.IsValid ||
                instruction.TrySite.Value >= parent.StackLir.TrySites.Count)
            {
                return false;
            }

            var trySite = parent.StackLir[instruction.TrySite].MirTrySite;
            var tryArtifact = parent.NestedTryRegions.FirstOrDefault(candidate =>
                candidate.Plan.Site == trySite);
            if (tryArtifact is null)
                return false;

            Avm1CompiledTryComponentArtifact? component;
            Avm1CompiledCodeUnitKind kind;
            switch (nestedKind)
            {
                case Avm1NestedCodeUnitKind.TryBody:
                    component = tryArtifact.TryBody;
                    kind = Avm1CompiledCodeUnitKind.Try;
                    break;
                case Avm1NestedCodeUnitKind.CatchBody:
                    component = tryArtifact.CatchBody;
                    kind = Avm1CompiledCodeUnitKind.Catch;
                    break;
                case Avm1NestedCodeUnitKind.FinallyBody:
                    component = tryArtifact.FinallyBody;
                    kind = Avm1CompiledCodeUnitKind.Finally;
                    break;
                default:
                    return false;
            }

            if (component is null)
                return false;
            result = new NestedUnit(CreateUnit(component), kind);
            return true;
        }

        private static Unit CreateUnit(Avm1CompiledFunctionArtifact artifact) =>
            new(
                artifact.Source,
                artifact.Mir,
                artifact.StackLir,
                artifact.InstructionMap,
                artifact.NestedFunctions,
                artifact.NestedWithRegions,
                artifact.NestedTryRegions);

        private static Unit CreateUnit(
            Avm1CompiledWithComponentArtifact artifact) =>
            new(
                artifact.Source,
                artifact.Mir,
                artifact.StackLir,
                artifact.InstructionMap,
                artifact.NestedFunctions,
                artifact.NestedWithRegions,
                artifact.NestedTryRegions);

        private static Unit CreateUnit(
            Avm1CompiledTryComponentArtifact artifact) =>
            new(
                artifact.Source,
                artifact.Mir,
                artifact.StackLir,
                artifact.InstructionMap,
                artifact.NestedFunctions,
                artifact.NestedWithRegions,
                artifact.NestedTryRegions);
    }

    private readonly record struct NestedUnit(
        Unit Source,
        Avm1CompiledCodeUnitKind Kind);

    private readonly record struct Unit(
        Avm1SourceMethod Source,
        Avm1MirMethod Mir,
        Avm1StackLirMethod StackLir,
        IReadOnlyList<Avm1CompiledInstructionMap> InstructionMap,
        IReadOnlyList<Avm1CompiledFunctionArtifact> NestedFunctions,
        IReadOnlyList<Avm1CompiledWithArtifact> NestedWithRegions,
        IReadOnlyList<Avm1CompiledTryArtifact> NestedTryRegions);
}
