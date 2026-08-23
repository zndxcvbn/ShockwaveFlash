using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;

namespace ShockwaveFlash.Avm1.Decompilation.Analysis;

public sealed class Avm1ClassMetadataAnalysis
{
    private readonly string[] _interfaceNames;

    private Avm1ClassMetadataAnalysis(string? baseClassName, string[] interfaceNames)
    {
        BaseClassName = baseClassName;
        _interfaceNames = interfaceNames;
    }

    public string? BaseClassName { get; }

    public IReadOnlyList<string> InterfaceNames => _interfaceNames;

    public static Avm1ClassMetadataAnalysis Build(Avm1MethodCore initializer, string className)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        var normalizedClassName = NormalizeQualifiedName(className);
        var resolver = new QualifiedNameResolver(initializer);
        string? baseClassName = null;
        var interfaceNames = new List<string>();
        var seenInterfaces = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instruction in initializer.TacIr.Instructions)
        {
            if (instruction.Op is Avm1TacOp.Extends)
            {
                var derivedClass = NormalizeQualifiedName(resolver.Resolve(instruction.Operand0));
                if (!NamesIdentifySameClass(derivedClass, normalizedClassName))
                    continue;

                var candidate = NormalizeQualifiedName(resolver.Resolve(instruction.Operand1));
                if (candidate.Length > 0 && !NamesIdentifySameClass(candidate, normalizedClassName))
                    baseClassName = candidate;
                continue;
            }

            if (instruction.Op is not Avm1TacOp.Implements)
                continue;

            var implementingClass = NormalizeQualifiedName(resolver.Resolve(instruction.Operand0));
            if (!NamesIdentifySameClass(implementingClass, normalizedClassName) ||
                !instruction.Operand2.IsValid ||
                instruction.OperandCount <= 0)
            {
                continue;
            }

            var start = instruction.Operand2.Value;
            if (start >= initializer.TacIr.ValueOperands.Count)
                continue;
            var count = Math.Min(instruction.OperandCount, initializer.TacIr.ValueOperands.Count - start);
            for (var i = count - 1; i >= 0; i--)
            {
                var interfaceName = NormalizeQualifiedName(
                    resolver.Resolve(initializer.TacIr.ValueOperands[start + i]));
                if (interfaceName.Length > 0 && seenInterfaces.Add(interfaceName))
                    interfaceNames.Add(interfaceName);
            }
        }

        return new Avm1ClassMetadataAnalysis(baseClassName, interfaceNames.ToArray());
    }

    internal static string NormalizeQualifiedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var result = name.Trim();
        while (true)
        {
            if (result.StartsWith("_global.", StringComparison.Ordinal))
            {
                result = result["_global.".Length..];
                continue;
            }

            if (result.StartsWith("__Packages.", StringComparison.Ordinal))
            {
                result = result["__Packages.".Length..];
                continue;
            }

            return result;
        }
    }

    internal static bool NamesIdentifySameClass(string candidate, string className)
    {
        if (candidate.Length == 0 || className.Length == 0)
            return false;
        if (string.Equals(candidate, className, StringComparison.Ordinal))
            return true;

        var candidateIsQualified = candidate.Contains('.');
        var classIsQualified = className.Contains('.');
        return (!candidateIsQualified || !classIsQualified) &&
            string.Equals(GetSimpleName(candidate), GetSimpleName(className), StringComparison.Ordinal);
    }

    private static string GetSimpleName(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    internal sealed class QualifiedNameResolver
    {
        private readonly Avm1TacIr _tac;
        private readonly Avm1ValueOriginAnalysis _origins;
        private readonly IrIndex[] _definitionByValue;
        private readonly Dictionary<IrIndex, Avm1RegisterAccess> _accessByInstruction = [];
        private readonly Dictionary<(int Register, int Version), ValueIndex> _writeSourceByVersion = [];
        private readonly Dictionary<(int Register, int Version), Avm1RegisterPhiNode> _phiByVersion = [];
        private readonly HashSet<(int Register, int Version)> _activeRegisters = [];
        private readonly IReadOnlyDictionary<(int Register, int Version), string> _registerAliases;
        private readonly string?[] _cache;
        private readonly byte[] _states;

        public QualifiedNameResolver(
            Avm1MethodCore initializer,
            IReadOnlyDictionary<(int Register, int Version), string>? registerAliases = null)
        {
            _tac = initializer.TacIr;
            _origins = initializer.ValueOrigins;
            _definitionByValue = new IrIndex[_origins.Origins.Count];
            Array.Fill(_definitionByValue, IrIndex.Invalid);
            _cache = new string?[_definitionByValue.Length];
            _states = new byte[_definitionByValue.Length];
            _registerAliases = registerAliases ??
                new Dictionary<(int Register, int Version), string>();

            foreach (var instruction in _tac.Instructions)
            {
                if (instruction.Result.IsValid &&
                    instruction.Result.Value < _definitionByValue.Length &&
                    !_definitionByValue[instruction.Result.Value].IsValid)
                {
                    _definitionByValue[instruction.Result.Value] = instruction.Index;
                }
            }

            foreach (var access in initializer.RegisterSsa.Accesses)
            {
                _accessByInstruction[access.Instruction] = access;
                if (access.Kind is Avm1RegisterAccessKind.Write)
                    _writeSourceByVersion[(access.Register, access.Version)] = access.Source;
            }

            foreach (var phi in initializer.RegisterSsa.PhiNodes)
                _phiByVersion[(phi.Register, phi.Version)] = phi;
        }

        public string? Resolve(ValueIndex value)
        {
            if (!value.IsValid || value.Value >= _definitionByValue.Length)
                return null;
            if (_states[value.Value] is 2)
                return _cache[value.Value];
            if (_states[value.Value] is 1)
                return null;

            _states[value.Value] = 1;
            var result = ResolveOrigin(_origins[value]);
            var definition = _definitionByValue[value.Value];
            if (result is null && definition.IsValid)
                result = ResolveInstruction(_tac[definition]);

            _cache[value.Value] = result;
            _states[value.Value] = 2;
            return result;
        }

        private string? ResolveInstruction(Avm1TacInstruction instruction)
        {
            return instruction.Op switch
            {
                Avm1TacOp.GetVariable => Resolve(instruction.Operand0),
                Avm1TacOp.GetMember => Combine(
                    Resolve(instruction.Operand0),
                    Resolve(instruction.Operand1)),
                Avm1TacOp.Copy or Avm1TacOp.Cast => Resolve(instruction.Operand0),
                Avm1TacOp.Phi => ResolveValuePhi(instruction),
                Avm1TacOp.LoadRegister => ResolveRegister(instruction),
                _ => null
            };
        }

        private string? ResolveValuePhi(Avm1TacInstruction instruction)
        {
            if (!instruction.Operand2.IsValid || instruction.OperandCount <= 0)
                return null;

            string? merged = null;
            var end = Math.Min(
                instruction.Operand2.Value + instruction.OperandCount,
                _tac.ValueOperands.Count);
            for (var i = instruction.Operand2.Value; i < end; i++)
            {
                var incoming = NormalizeQualifiedName(Resolve(_tac.ValueOperands[i]));
                if (incoming.Length == 0)
                    return null;
                if (merged is not null && !string.Equals(merged, incoming, StringComparison.Ordinal))
                    return null;
                merged = incoming;
            }

            return merged;
        }

        private string? ResolveRegister(Avm1TacInstruction instruction)
        {
            if (!_accessByInstruction.TryGetValue(instruction.Index, out var access) ||
                access.Kind is not Avm1RegisterAccessKind.Read)
            {
                return null;
            }

            return ResolveRegisterVersion(access.Register, access.Version);
        }

        private string? ResolveRegisterVersion(int register, int version)
        {
            var key = (register, version);
            if (!_activeRegisters.Add(key))
                return null;

            try
            {
                if (_registerAliases.TryGetValue(key, out var alias))
                    return alias;
                if (_writeSourceByVersion.TryGetValue(key, out var source))
                    return Resolve(source);
                if (!_phiByVersion.TryGetValue(key, out var phi))
                    return null;

                string? merged = null;
                foreach (var incomingVersion in phi.IncomingVersions)
                {
                    var incoming = NormalizeQualifiedName(
                        ResolveRegisterVersion(register, incomingVersion));
                    if (incoming.Length == 0)
                        return null;
                    if (merged is not null && !string.Equals(merged, incoming, StringComparison.Ordinal))
                        return null;
                    merged = incoming;
                }

                return merged;
            }
            finally
            {
                _activeRegisters.Remove(key);
            }
        }

        private static string? ResolveOrigin(Avm1ValueOrigin origin)
        {
            return origin.Kind switch
            {
                Avm1ValueOriginKind.StringLiteral or Avm1ValueOriginKind.Variable => origin.Name,
                Avm1ValueOriginKind.StaticMember => Combine(origin.Name, origin.Member),
                _ => null
            };
        }

        private static string? Combine(string? target, string? member)
        {
            return string.IsNullOrEmpty(target) || string.IsNullOrEmpty(member)
                ? null
                : target + "." + member;
        }
    }
}
