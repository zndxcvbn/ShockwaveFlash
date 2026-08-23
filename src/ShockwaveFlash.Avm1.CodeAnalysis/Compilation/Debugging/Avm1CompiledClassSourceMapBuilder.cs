using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf7;

namespace ShockwaveFlash.Avm1.Compilation;

internal readonly record struct Avm1ClassMethodAssemblySite(
    Avm1SourceMethodDeclaration Declaration,
    Avm1AssemblyInstructionIndex Instruction);

internal static class Avm1CompiledClassSourceMapBuilder
{
    public static Avm1CompiledClassSourceMap BuildCompiled(
        IReadOnlyList<Avm1CompiledClassMember> methods,
        Avm1ActionBody initializerBody,
        IReadOnlyList<Avm1ClassMethodAssemblySite> sites)
    {
        ArgumentNullException.ThrowIfNull(initializerBody);
        if (!initializerBody.Succeeded)
            return Avm1CompiledClassSourceMap.Empty(methods);
        if (sites.Count != methods.Count)
        {
            throw new InvalidOperationException(
                $"Class initializer emitted {sites.Count} mapped method bodies " +
                $"for {methods.Count} compiled methods.");
        }

        var indices = methods
            .Select((method, index) => (method.Declaration, index))
            .ToDictionary(item => item.Declaration, item => item.index);
        var placements = new Avm1CompiledClassMethodSourceMap[methods.Count];
        var assigned = new bool[methods.Count];
        foreach (var site in sites)
        {
            if (!indices.TryGetValue(site.Declaration, out var methodIndex) ||
                assigned[methodIndex])
            {
                throw new InvalidOperationException(
                    "A class method has an invalid or duplicate initializer site.");
            }

            var location = initializerBody.GetLocation(site.Instruction);
            placements[methodIndex] = CreatePlacement(
                methods[methodIndex],
                methodIndex,
                location.ActionIndex,
                initializerBody,
                Avm1CompiledClassMethodBodyKind.Compiled,
                requireIdenticalBody: true);
            assigned[methodIndex] = true;
        }

        if (assigned.Any(value => !value))
        {
            throw new InvalidOperationException(
                "A compiled class method has no initializer site.");
        }

        return Build(methods, placements);
    }

    public static Avm1CompiledClassSourceMap BuildOrigin(
        IReadOnlyList<Avm1CompiledClassMember> methods,
        Avm1ActionBody initializerBody)
    {
        ArgumentNullException.ThrowIfNull(initializerBody);
        if (!initializerBody.Succeeded)
            return Avm1CompiledClassSourceMap.Empty(methods);

        var placements = new Avm1CompiledClassMethodSourceMap[methods.Count];
        var actionIndices = new HashSet<int>();
        for (var methodIndex = 0; methodIndex < methods.Count; methodIndex++)
        {
            var method = methods[methodIndex];
            var origin = method.Declaration.Origin.Bytecode;
            if (origin is null)
            {
                if (!method.OriginBodyReused)
                {
                    throw new InvalidOperationException(
                        $"Recompiled method {method.Declaration.Name} has no " +
                        "bytecode placement.");
                }

                placements[methodIndex] = new Avm1CompiledClassMethodSourceMap(
                    methodIndex,
                    InitializerActionIndex: -1,
                    ByteOffset: -1,
                    ByteLength: 0,
                    Avm1CompiledClassMethodBodyKind.Unplaced);
                continue;
            }
            if (!actionIndices.Add(origin.InitializerActionIndex))
            {
                throw new InvalidOperationException(
                    $"Initializer action {origin.InitializerActionIndex} owns more " +
                    "than one source method.");
            }

            var identical = GetFunction(
                    initializerBody,
                    origin.InitializerActionIndex)
                .Body.Span.SequenceEqual(method.Method.Bytecode.Span);
            if (!identical && !method.OriginBodyReused)
            {
                throw new InvalidOperationException(
                    $"Recompiled method {method.Declaration.Name} does not match " +
                    "its initializer function body.");
            }

            var kind = identical
                ? method.OriginBodyReused
                    ? Avm1CompiledClassMethodBodyKind.ReusedIdentical
                    : Avm1CompiledClassMethodBodyKind.Compiled
                : Avm1CompiledClassMethodBodyKind.ReusedUnmapped;
            placements[methodIndex] = CreatePlacement(
                method,
                methodIndex,
                origin.InitializerActionIndex,
                initializerBody,
                kind,
                requireIdenticalBody: false);
        }

        return Build(methods, placements);
    }

    private static Avm1CompiledClassMethodSourceMap CreatePlacement(
        Avm1CompiledClassMember method,
        int methodIndex,
        int initializerActionIndex,
        Avm1ActionBody initializerBody,
        Avm1CompiledClassMethodBodyKind kind,
        bool requireIdenticalBody)
    {
        var function = GetFunction(initializerBody, initializerActionIndex);
        if (requireIdenticalBody &&
            !function.Body.Span.SequenceEqual(method.Method.Bytecode.Span))
        {
            throw new InvalidOperationException(
                $"Compiled method {method.Declaration.Name} does not match its " +
                "initializer function body.");
        }

        var location = initializerBody.ActionLocations[initializerActionIndex];
        var byteOffset = checked(
            location.ByteOffset + location.ByteLength - function.Body.Length);
        if (byteOffset < location.ByteOffset ||
            byteOffset + function.Body.Length > initializerBody.Bytes.Length ||
            !initializerBody.Bytes.Span
                .Slice(byteOffset, function.Body.Length)
                .SequenceEqual(function.Body.Span))
        {
            throw new InvalidOperationException(
                $"Function body for {method.Declaration.Name} has an invalid " +
                "physical initializer range.");
        }

        return new Avm1CompiledClassMethodSourceMap(
            methodIndex,
            initializerActionIndex,
            byteOffset,
            function.Body.Length,
            kind);
    }

    private static ActionDefineFunction2 GetFunction(
        Avm1ActionBody initializerBody,
        int actionIndex)
    {
        if (actionIndex < 0 ||
            actionIndex >= initializerBody.Actions.Count ||
            initializerBody.Actions[actionIndex] is not ActionDefineFunction2 function)
        {
            throw new InvalidOperationException(
                $"Initializer action {actionIndex} is not ActionDefineFunction2.");
        }
        return function;
    }

    private static Avm1CompiledClassSourceMap Build(
        IReadOnlyList<Avm1CompiledClassMember> methods,
        Avm1CompiledClassMethodSourceMap[] placements)
    {
        var methodMaps = methods
            .Select(method => method.Method.SourceMap)
            .ToArray();
        var entries = new List<Avm1CompiledClassSourceMapEntry>();
        foreach (var placement in placements)
        {
            if (!placement.IsMapped)
                continue;
            foreach (var methodEntry in methodMaps[placement.MethodIndex].Entries)
            {
                entries.Add(new Avm1CompiledClassSourceMapEntry(
                    placement.MethodIndex,
                    checked(placement.ByteOffset + methodEntry.ByteOffset),
                    methodEntry));
            }
        }

        return new Avm1CompiledClassSourceMap(
            placements,
            entries
                .OrderBy(entry => entry.ByteOffset)
                .ThenBy(entry => entry.MethodIndex)
                .ThenBy(entry => entry.MethodEntry.ActionIndex)
                .ToArray(),
            methodMaps);
    }
}
