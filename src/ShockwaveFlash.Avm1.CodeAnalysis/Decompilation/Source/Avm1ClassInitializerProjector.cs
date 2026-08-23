using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Decompilation;

internal static class Avm1ClassInitializerProjector
{
    public static Avm1SourceClassInitializer Project(
        Avm1MethodDecompilation initializer,
        string className,
        IReadOnlyList<Avm1SourceClassMember> members,
        IReadOnlyDictionary<(int Register, int Version), string> registerAliases)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(registerAliases);

        var statementMap = new RecoveryStatementMap(initializer.StructuredAst);
        var resolver = new Avm1ClassMetadataAnalysis.QualifiedNameResolver(
            initializer.Core,
            registerAliases);
        var memberLookup = BuildMemberLookup(members);
        var pending = new List<PendingStep>();
        var structuralStatements = new HashSet<int>();

        foreach (var instruction in initializer.TacIr.Instructions)
        {
            if (!TryClassifyStructuralOperation(
                    initializer,
                    instruction,
                    className,
                    resolver,
                    memberLookup,
                    out var kind,
                    out var member))
            {
                continue;
            }

            var statement = statementMap.Find(instruction.Action);
            if (statement.IsValid)
                structuralStatements.Add(statement.Value);
            pending.Add(new PendingStep(
                instruction.Action.Value,
                kind,
                member,
                statement));
        }

        var residualStatements = new HashSet<int>();
        foreach (var instruction in initializer.TacIr.Instructions)
        {
            if (!Avm1TacEffectAnalysis.IsSourceStatementRoot(
                    instruction,
                    initializer.ValueAnalysis) ||
                instruction.Op is Avm1TacOp.StoreRegister)
                continue;

            var statement = statementMap.Find(instruction.Action);
            if (!statement.IsValid ||
                initializer.StructuredAst[statement].OriginAction != instruction.Action ||
                initializer.StructuredAst[statement].Kind is
                    Avm1AstNodeKind.AssignRegister or
                    Avm1AstNodeKind.AssignTemp ||
                structuralStatements.Contains(statement.Value) ||
                !residualStatements.Add(statement.Value))
            {
                continue;
            }

            pending.Add(new PendingStep(
                instruction.Action.Value,
                Avm1SourceClassInitializationStepKind.ResidualStatement,
                Member: null,
                statement));
        }

        var includedStatements = BuildIncludedStatementNodes(
            initializer.StructuredAst,
            residualStatements);
        var residualBody = Avm1SourceProjector.ProjectMethod(
            initializer,
            registerAliases,
            includedStatements,
            classAbiActionsHandledExternally: true);
        var projectedStatements = BuildProjectedStatementMap(residualBody);
        var seen = new HashSet<StepIdentity>();
        var steps = pending
            .OrderBy(item => item.Action)
            .ThenBy(item => item.Kind)
            .Select(item => new Avm1SourceClassInitializationStep(
                item.Kind,
                item.Member,
                item.Kind is Avm1SourceClassInitializationStepKind.ResidualStatement &&
                    projectedStatements.TryGetValue(
                        item.RecoveryStatement.Value,
                        out var projectedStatement)
                    ? projectedStatement
                    : SourceStatementIndex.Invalid))
            .Where(item => seen.Add(new StepIdentity(
                item.Kind,
                item.Member,
                item.Statement)))
            .ToArray();
        return new Avm1SourceClassInitializer(residualBody, steps);
    }

    private static HashSet<int> BuildIncludedStatementNodes(
        Avm1AstArena ast,
        IReadOnlySet<int> residualStatements)
    {
        var parents = new int[ast.Nodes.Count];
        Array.Fill(parents, -1);
        var pending = new Stack<AstIndex>();
        var visited = new HashSet<int>();
        pending.Push(ast.Root);
        while (pending.Count > 0)
        {
            var index = pending.Pop();
            if (!index.IsValid || !visited.Add(index.Value))
                continue;
            var node = ast[index];
            for (var i = 0; i < node.Children.Count; i++)
            {
                var child = ast.GetChild(node, i);
                if (child.IsValid && parents[child.Value] < 0)
                    parents[child.Value] = index.Value;
                pending.Push(child);
            }
        }

        var result = new HashSet<int> { ast.Root.Value };
        foreach (var statement in residualStatements)
        {
            for (var current = statement; current >= 0; current = parents[current])
            {
                if (!result.Add(current))
                    break;
            }
        }

        foreach (var indexValue in result.ToArray())
        {
            var node = ast[new AstIndex(indexValue)];
            if (node.Kind is Avm1AstNodeKind.For && node.Children.Count >= 3)
            {
                IncludeSubtree(ast.GetChild(node, 0));
                IncludeSubtree(ast.GetChild(node, 2));
            }
            else if (node.Kind is Avm1AstNodeKind.Try)
            {
                for (var i = 0; i < node.Children.Count; i++)
                    result.Add(ast.GetChild(node, i).Value);
            }
        }
        return result;

        void IncludeSubtree(AstIndex root)
        {
            var worklist = new Stack<AstIndex>();
            worklist.Push(root);
            while (worklist.Count > 0)
            {
                var current = worklist.Pop();
                if (!current.IsValid || !result.Add(current.Value))
                    continue;
                var currentNode = ast[current];
                for (var i = 0; i < currentNode.Children.Count; i++)
                    worklist.Push(ast.GetChild(currentNode, i));
            }
        }
    }

    private static Dictionary<int, SourceStatementIndex> BuildProjectedStatementMap(
        Avm1SourceMethod source)
    {
        var result = new Dictionary<int, SourceStatementIndex>();
        foreach (var statement in source.Arena.Statements)
        {
            if (!statement.Origin.IsValid)
                continue;
            var origin = source.Arena[statement.Origin];
            if (origin.RecoveryUnit == 0 && origin.RecoveryNode >= 0)
                result.TryAdd(origin.RecoveryNode, statement.Index);
        }
        return result;
    }

    private static Dictionary<(bool IsStatic, string Name), Avm1SourceClassMember>
        BuildMemberLookup(IReadOnlyList<Avm1SourceClassMember> members)
    {
        var result = new Dictionary<(bool IsStatic, string Name), Avm1SourceClassMember>();
        foreach (var member in members)
        {
            var isStatic = member.Modifiers.HasFlag(Avm1SourceDeclarationModifiers.Static);
            Add((isStatic, member.Name), member);
            if (!string.IsNullOrWhiteSpace(member.Origin.RuntimeName))
                Add((isStatic, member.Origin.RuntimeName!), member);
        }
        return result;

        void Add(
            (bool IsStatic, string Name) key,
            Avm1SourceClassMember candidate)
        {
            if (!result.TryGetValue(key, out var existing) ||
                existing is not Avm1SourceMethodDeclaration &&
                candidate is Avm1SourceMethodDeclaration)
            {
                result[key] = candidate;
            }
        }
    }

    private static bool TryClassifyStructuralOperation(
        Avm1MethodDecompilation initializer,
        Avm1TacInstruction instruction,
        string className,
        Avm1ClassMetadataAnalysis.QualifiedNameResolver resolver,
        IReadOnlyDictionary<(bool IsStatic, string Name), Avm1SourceClassMember> members,
        out Avm1SourceClassInitializationStepKind kind,
        out Avm1SourceClassMember? member)
    {
        kind = default;
        member = null;
        var normalizedClassName = Avm1ClassMetadataAnalysis.NormalizeQualifiedName(className);

        if (instruction.Op is Avm1TacOp.Extends)
        {
            var derived = Normalize(resolver.Resolve(instruction.Operand0));
            if (!Avm1ClassMetadataAnalysis.NamesIdentifySameClass(
                    derived,
                    normalizedClassName))
            {
                return false;
            }

            kind = Avm1SourceClassInitializationStepKind.Inheritance;
            return true;
        }

        if (instruction.Op is Avm1TacOp.Implements)
        {
            var implementingClass = Normalize(resolver.Resolve(instruction.Operand0));
            if (!Avm1ClassMetadataAnalysis.NamesIdentifySameClass(
                    implementingClass,
                    normalizedClassName))
            {
                return false;
            }

            kind = Avm1SourceClassInitializationStepKind.Interfaces;
            return true;
        }

        if (instruction.Op is Avm1TacOp.SetVariable or Avm1TacOp.DefineLocal)
        {
            var target = Normalize(resolver.Resolve(instruction.Operand0));
            if (Avm1ClassMetadataAnalysis.NamesIdentifySameClass(
                    target,
                    normalizedClassName) &&
                IsFunctionLiteral(initializer, instruction.Operand1))
            {
                kind = Avm1SourceClassInitializationStepKind.ClassDefinition;
                member = members.Values.OfType<Avm1SourceMethodDeclaration>().FirstOrDefault(
                    candidate => candidate.Kind is Avm1SourceMethodKind.Constructor);
                return true;
            }

            if (IsPackagePrefix(target, normalizedClassName))
            {
                kind = Avm1SourceClassInitializationStepKind.PackageDefinition;
                return true;
            }
            return false;
        }

        if (instruction.Op is Avm1TacOp.SetMember)
        {
            var owner = Normalize(resolver.Resolve(instruction.Operand0));
            var memberName = resolver.Resolve(instruction.Operand1);
            if (string.IsNullOrWhiteSpace(memberName))
                return false;
            memberName = NormalizeMemberName(memberName);

            var target = owner.Length == 0 ? string.Empty : owner + "." + memberName;
            if (Avm1ClassMetadataAnalysis.NamesIdentifySameClass(
                    target,
                    normalizedClassName) &&
                IsFunctionLiteral(initializer, instruction.Operand2))
            {
                kind = Avm1SourceClassInitializationStepKind.ClassDefinition;
                member = members.Values.OfType<Avm1SourceMethodDeclaration>().FirstOrDefault(
                    candidate => candidate.Kind is Avm1SourceMethodKind.Constructor);
                return true;
            }

            if (IsPackagePrefix(target, normalizedClassName))
            {
                kind = Avm1SourceClassInitializationStepKind.PackageDefinition;
                return true;
            }

            if (IsClassName(owner, normalizedClassName))
            {
                if (memberName is "prototype" or "__constructor__")
                {
                    kind = Avm1SourceClassInitializationStepKind.ClassDefinition;
                    return true;
                }
                if (members.TryGetValue((true, memberName), out member))
                {
                    kind = Avm1SourceClassInitializationStepKind.MemberDefinition;
                    return true;
                }
                return false;
            }

            if (!IsPrototypeName(owner, normalizedClassName))
                return false;
            if (memberName is "constructor" or "__constructor__")
            {
                kind = Avm1SourceClassInitializationStepKind.ClassDefinition;
                return true;
            }
            if (!members.TryGetValue((false, memberName), out member))
                return false;

            kind = Avm1SourceClassInitializationStepKind.MemberDefinition;
            return true;
        }

        if (instruction.Op is Avm1TacOp.CallMethod)
        {
            var methodName = NormalizeMemberName(resolver.Resolve(instruction.Operand0));
            var target = Normalize(resolver.Resolve(instruction.Operand1));
            if (methodName == "addProperty" && IsPrototypeName(target, normalizedClassName))
            {
                var propertyName = NormalizeMemberName(
                    resolver.Resolve(GetCallArgument(initializer, instruction, 0)));
                if (!members.TryGetValue((false, propertyName), out member) ||
                    member is not Avm1SourceMethodDeclaration
                    {
                        Kind: Avm1SourceMethodKind.Getter or Avm1SourceMethodKind.Setter
                    })
                {
                    return false;
                }

                kind = Avm1SourceClassInitializationStepKind.AccessorDefinition;
                return true;
            }
            if (methodName == "registerClass" && GetSimpleName(target) == "Object")
            {
                var registeredClass = Normalize(
                    resolver.Resolve(GetCallArgument(initializer, instruction, 1)));
                if (!IsClassName(registeredClass, normalizedClassName))
                    return false;

                kind = Avm1SourceClassInitializationStepKind.Linkage;
                return true;
            }
            return false;
        }

        if (instruction.Op is Avm1TacOp.CallFunction)
        {
            var functionName = GetSimpleName(Normalize(resolver.Resolve(instruction.Operand0)));
            if (functionName == "ASSetPropFlags")
            {
                var flagsTarget = Normalize(
                    resolver.Resolve(GetCallArgument(initializer, instruction, 0)));
                if (!IsClassName(flagsTarget, normalizedClassName) &&
                    !IsPrototypeName(flagsTarget, normalizedClassName))
                {
                    return false;
                }

                kind = Avm1SourceClassInitializationStepKind.PropertyFlags;
                return true;
            }
        }

        return false;
    }

    private static ValueIndex GetCallArgument(
        Avm1MethodDecompilation initializer,
        Avm1TacInstruction instruction,
        int argumentIndex)
    {
        if (!instruction.Operand2.IsValid ||
            argumentIndex < 0 ||
            argumentIndex >= instruction.OperandCount)
        {
            return ValueIndex.Invalid;
        }

        var operandIndex = instruction.Operand2.Value + argumentIndex;
        return operandIndex < initializer.TacIr.CallArguments.Count
            ? initializer.TacIr.CallArguments[operandIndex]
            : ValueIndex.Invalid;
    }

    private static bool IsFunctionLiteral(
        Avm1MethodDecompilation initializer,
        ValueIndex value) =>
        value.IsValid &&
        initializer.ValueOrigins[value].Kind is Avm1ValueOriginKind.FunctionLiteral;

    private static bool IsClassName(string candidate, string className) =>
        Avm1ClassMetadataAnalysis.NamesIdentifySameClass(candidate, className);

    private static bool IsPrototypeName(string candidate, string className)
    {
        const string suffix = ".prototype";
        return candidate.EndsWith(suffix, StringComparison.Ordinal) &&
            IsClassName(candidate[..^suffix.Length], className);
    }

    private static bool IsPackagePrefix(string candidate, string className) =>
        candidate.Length > 0 &&
        className.StartsWith(candidate + ".", StringComparison.Ordinal);

    private static string Normalize(string? name) =>
        Avm1ClassMetadataAnalysis.NormalizeQualifiedName(name);

    private static string NormalizeMemberName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var normalized = Normalize(name);
        return GetSimpleName(normalized);
    }

    private static string GetSimpleName(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private readonly record struct PendingStep(
        int Action,
        Avm1SourceClassInitializationStepKind Kind,
        Avm1SourceClassMember? Member,
        AstIndex RecoveryStatement);

    private readonly record struct StepIdentity(
        Avm1SourceClassInitializationStepKind Kind,
        Avm1SourceClassMember? Member,
        SourceStatementIndex Statement);

    private sealed class RecoveryStatementMap
    {
        private readonly Avm1AstArena _ast;
        private readonly int[] _parents;
        private readonly Dictionary<int, AstIndex> _statementsByOriginAction = [];

        public RecoveryStatementMap(Avm1AstArena ast)
        {
            _ast = ast;
            _parents = new int[ast.Nodes.Count];
            Array.Fill(_parents, -1);
            foreach (var node in ast.Nodes)
            {
                if (IsStatement(node.Kind) && node.OriginAction.IsValid)
                    _statementsByOriginAction.TryAdd(node.OriginAction.Value, node.Index);
            }
            var pending = new Stack<AstIndex>();
            var visited = new HashSet<int>();
            pending.Push(ast.Root);
            while (pending.Count > 0)
            {
                var index = pending.Pop();
                if (!index.IsValid || !visited.Add(index.Value))
                    continue;
                var node = ast[index];
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = ast.GetChild(node, i);
                    if (child.IsValid && _parents[child.Value] < 0)
                        _parents[child.Value] = index.Value;
                    pending.Push(child);
                }
            }
        }

        public AstIndex Find(ActionIndex action)
        {
            if (_statementsByOriginAction.TryGetValue(action.Value, out var statement))
                return statement;

            var best = AstIndex.Invalid;
            var bestExact = false;
            var bestSpan = int.MaxValue;
            foreach (var node in _ast.Nodes)
            {
                var exact = node.EndAction == action || node.StartAction == action;
                var start = node.StartAction.Value;
                var end = node.EndAction.Value;
                var contains = start >= 0 && end >= start &&
                    action.Value >= start && action.Value <= end;
                if (!exact && !contains)
                    continue;

                var span = contains ? end - start : 0;
                if (best.IsValid &&
                    (bestExact && !exact || bestExact == exact && span >= bestSpan))
                {
                    continue;
                }

                best = node.Index;
                bestExact = exact;
                bestSpan = span;
            }
            while (best.IsValid && !IsStatement(_ast[best].Kind))
                best = new AstIndex(_parents[best.Value]);
            return best;
        }

        private static bool IsStatement(Avm1AstNodeKind kind) =>
            kind is
                Avm1AstNodeKind.Root or
                Avm1AstNodeKind.Block or
                Avm1AstNodeKind.If or
                Avm1AstNodeKind.While or
                Avm1AstNodeKind.DoWhile or
                Avm1AstNodeKind.For or
                Avm1AstNodeKind.ForIn or
                Avm1AstNodeKind.With or
                Avm1AstNodeKind.Try or
                Avm1AstNodeKind.Switch or
                Avm1AstNodeKind.Break or
                Avm1AstNodeKind.Continue or
                Avm1AstNodeKind.AssignRegister or
                Avm1AstNodeKind.AssignTemp or
                Avm1AstNodeKind.CompoundAssignRegister or
                Avm1AstNodeKind.AssignVariable or
                Avm1AstNodeKind.CompoundAssignVariable or
                Avm1AstNodeKind.AssignMember or
                Avm1AstNodeKind.CompoundAssignMember or
                Avm1AstNodeKind.DeclareVariable or
                Avm1AstNodeKind.ExpressionStatement or
                Avm1AstNodeKind.Return or
                Avm1AstNodeKind.Throw;
    }
}
