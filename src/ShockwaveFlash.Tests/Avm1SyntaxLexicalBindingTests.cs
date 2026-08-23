using ShockwaveFlash.Avm1.Compilation.Binding;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SyntaxLexicalBindingTests
{
    private static readonly string[] SkyUiSourceRoots =
    [
        @"C:\Users\artem\source\repos\skyui\src\CraftingMenu",
        @"C:\Users\artem\source\repos\skyui\src\Common",
        @"C:\Users\artem\source\repos\skyui\src\CLIK"
    ];

    [Fact]
    public void Lexical_binding_models_hoisting_shadowing_captures_and_function_names()
    {
        const string source = """
            class Test
            {
                function run(arg:Number):Number
                {
                    before = nested(later);
                    var first:Number = arg, later:Number = 1;
                    var arg:Number;
                    function nested(arg:Number):Number
                    {
                        var local:Number = later;
                        return arg + later + local + arguments.length;
                    }
                    var lambda:Function = function self(value:Number):Number
                    {
                        return value <= 0 ? later : self(value - 1);
                    };
                    try { throw 1; }
                    catch (later:Number)
                    {
                        var catchLocal:Number = later;
                        first += later;
                    }
                    return first + later;
                }
            }
            """;
        var (program, binding, file, tree) = BindSingle(source);

        binding.HasErrors.ShouldBeFalse(Describe(binding));
        var methodUnit = GetMemberCodeUnit(program, binding, "Test", "run");
        var methodScope = binding.GetCodeUnit(methodUnit).RootScope;
        var methodSymbols = GetSymbols(binding, methodScope);
        methodSymbols.Keys.ShouldBe([
            "arguments",
            "arg",
            "first",
            "later",
            "nested",
            "lambda",
            "catchLocal"
        ], ignoreOrder: true);
        methodSymbols["arg"].Flags.HasFlag(
            Avm1SyntaxLocalSymbolFlags.Parameter).ShouldBeTrue();
        methodSymbols["arg"].Flags.HasFlag(
            Avm1SyntaxLocalSymbolFlags.Variable).ShouldBeTrue();
        binding.GetDeclarations(methodSymbols["arg"].Index).Length.ShouldBe(2);

        var before = FindIdentifiers(tree, "before").ShouldHaveSingleItem();
        binding.GetIdentifierBinding(file, before.Index).Kind.ShouldBe(
            Avm1SyntaxIdentifierBindingKind.Unresolved);
        var firstLater = FindIdentifiers(tree, "later")
            .OrderBy(expression => expression.Span.Start)
            .First();
        binding.GetIdentifierBinding(file, firstLater.Index).Symbol.ShouldBe(
            methodSymbols["later"].Index);
        var nestedReference = FindIdentifiers(tree, "nested")
            .ShouldHaveSingleItem();
        binding.GetIdentifierBinding(file, nestedReference.Index).Symbol.ShouldBe(
            methodSymbols["nested"].Index);

        var nestedDeclaration = tree.Statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.FunctionDeclaration);
        var nestedUnit = binding.GetFunctionCodeUnit(file, nestedDeclaration.Index);
        var nestedScope = binding.GetCodeUnit(nestedUnit).RootScope;
        var nestedSymbols = GetSymbols(binding, nestedScope);
        nestedSymbols.Keys.ShouldBe(["arguments", "arg", "local"],
            ignoreOrder: true);
        var nestedBody = tree.GetChildren(nestedDeclaration).ToArray()
            .ShouldHaveSingleItem();
        var nestedLater = FindIdentifiers(tree, "later")
            .Where(expression => tree.GetStatement(nestedBody).Span.Start <=
                expression.Span.Start &&
                expression.Span.End <= tree.GetStatement(nestedBody).Span.End)
            .ToArray();
        nestedLater.Length.ShouldBe(2);
        nestedLater.ShouldAllBe(expression =>
            binding.GetIdentifierBinding(file, expression.Index).Symbol ==
            methodSymbols["later"].Index);
        var nestedArg = FindIdentifiers(tree, "arg")
            .Single(expression => tree.GetStatement(nestedBody).Span.Start <=
                expression.Span.Start &&
                expression.Span.End <= tree.GetStatement(nestedBody).Span.End);
        binding.GetIdentifierBinding(file, nestedArg.Index).Symbol.ShouldBe(
            nestedSymbols["arg"].Index);

        var functionExpression = tree.Expressions.Single(expression =>
            expression.Kind == Avm1ExpressionSyntaxKind.FunctionExpression);
        var lambdaUnit = binding.GetFunctionCodeUnit(file, functionExpression.Index);
        var lambda = binding.GetCodeUnit(lambdaUnit);
        var nameScope = binding.GetScope(lambda.RootScope).Parent;
        binding.GetScope(nameScope).Kind.ShouldBe(
            Avm1SyntaxScopeKind.FunctionName);
        var self = GetSymbols(binding, nameScope)["self"];
        self.Flags.HasFlag(
            Avm1SyntaxLocalSymbolFlags.FunctionExpressionName).ShouldBeTrue();
        var selfReference = FindIdentifiers(tree, "self").ShouldHaveSingleItem();
        binding.GetIdentifierBinding(file, selfReference.Index).Symbol
            .ShouldBe(self.Index);

        var catchClause = tree.Statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.CatchClause);
        var catchParameter = tree.GetParameters(catchClause).ToArray()
            .ShouldHaveSingleItem();
        var catchSymbol = binding.GetSymbol(
            binding.GetDeclaredSymbol(file, catchParameter.NameToken));
        catchSymbol.Flags.ShouldBe(Avm1SyntaxLocalSymbolFlags.Catch);
        catchSymbol.Index.ShouldNotBe(methodSymbols["later"].Index);
        FindIdentifiers(tree, "later")
            .Where(expression =>
                binding.GetScope(binding.GetScope(file, expression.Index)).Kind ==
                Avm1SyntaxScopeKind.Catch)
            .ShouldAllBe(expression =>
                binding.GetIdentifierBinding(file, expression.Index).Symbol ==
                catchSymbol.Index);
    }

    [Fact]
    public void Dynamic_scopes_and_direct_eval_are_explicit_and_shadow_aware()
    {
        const string source = """
            class Test
            {
                function dynamicRun(obj:Object):Void
                {
                    var local:Number = 1;
                    with (obj)
                    {
                        local = external;
                        var captured:Function = function named(local:Number):Number
                        {
                            return local;
                        };
                    }
                }
                function directRun():Void { eval("value = 1"); }
                function shadowedRun(eval:Function):Void { eval("value = 2"); }
                function outer():Void
                {
                    var value:Number = 0;
                    var callback:Function = function():Void { eval("value = 3"); };
                }
            }
            """;
        var (program, binding, file, tree) = BindSingle(source);

        binding.HasErrors.ShouldBeFalse(Describe(binding));
        var dynamicUnit = GetMemberCodeUnit(
            program, binding, "Test", "dynamicRun");
        binding.GetCodeUnit(dynamicUnit).Flags.HasFlag(
            Avm1SyntaxCodeUnitFlags.ContainsWith).ShouldBeTrue();
        var dynamicSymbols = GetSymbols(
            binding,
            binding.GetCodeUnit(dynamicUnit).RootScope);
        var objReference = FindIdentifiers(tree, "obj").ShouldHaveSingleItem();
        binding.IsInDynamicScope(file, objReference.Index).ShouldBeFalse();
        binding.GetIdentifierBinding(file, objReference.Index).Kind.ShouldBe(
            Avm1SyntaxIdentifierBindingKind.Lexical);

        var localReferences = FindIdentifiers(tree, "local")
            .OrderBy(expression => expression.Span.Start)
            .ToArray();
        localReferences.Length.ShouldBe(2);
        binding.IsInDynamicScope(file, localReferences[0].Index).ShouldBeTrue();
        binding.GetIdentifierBinding(file, localReferences[0].Index).Kind.ShouldBe(
            Avm1SyntaxIdentifierBindingKind.Dynamic);
        binding.GetIdentifierBinding(file, localReferences[0].Index).Symbol.ShouldBe(
            dynamicSymbols["local"].Index);
        var external = FindIdentifiers(tree, "external").ShouldHaveSingleItem();
        var externalBinding = binding.GetIdentifierBinding(file, external.Index);
        externalBinding.Kind.ShouldBe(Avm1SyntaxIdentifierBindingKind.Dynamic);
        externalBinding.Symbol.IsValid.ShouldBeFalse();

        var namedFunction = tree.Expressions.Single(expression =>
            expression.Kind == Avm1ExpressionSyntaxKind.FunctionExpression &&
            expression.Token.IsValid);
        var namedUnit = binding.GetFunctionCodeUnit(file, namedFunction.Index);
        binding.GetCodeUnit(namedUnit).Flags.HasFlag(
            Avm1SyntaxCodeUnitFlags.DeclaredInDynamicScope).ShouldBeTrue();
        var namedSymbols = GetSymbols(
            binding,
            binding.GetCodeUnit(namedUnit).RootScope);
        binding.IsInDynamicScope(file, localReferences[1].Index).ShouldBeTrue();
        binding.GetIdentifierBinding(file, localReferences[1].Index).Kind.ShouldBe(
            Avm1SyntaxIdentifierBindingKind.Lexical);
        binding.GetIdentifierBinding(file, localReferences[1].Index).Symbol.ShouldBe(
            namedSymbols["local"].Index);

        var directUnit = GetMemberCodeUnit(
            program, binding, "Test", "directRun");
        binding.GetCodeUnit(directUnit).Flags.HasFlag(
            Avm1SyntaxCodeUnitFlags.ContainsDirectEval).ShouldBeTrue();
        var shadowedUnit = GetMemberCodeUnit(
            program, binding, "Test", "shadowedRun");
        binding.GetCodeUnit(shadowedUnit).Flags.HasFlag(
            Avm1SyntaxCodeUnitFlags.ContainsDirectEval).ShouldBeFalse();

        var outerUnit = GetMemberCodeUnit(program, binding, "Test", "outer");
        binding.GetCodeUnit(outerUnit).Flags.HasFlag(
            Avm1SyntaxCodeUnitFlags.HasDescendantDirectEval).ShouldBeTrue();
        var anonymousFunction = tree.Expressions.Single(expression =>
            expression.Kind == Avm1ExpressionSyntaxKind.FunctionExpression &&
            !expression.Token.IsValid);
        binding.GetCodeUnit(binding.GetFunctionCodeUnit(file, anonymousFunction.Index))
            .Flags.HasFlag(Avm1SyntaxCodeUnitFlags.ContainsDirectEval)
            .ShouldBeTrue();
    }

    [Fact]
    public void Control_binding_resolves_implicit_and_labeled_targets()
    {
        const string source = """
            class Test
            {
                function run(items:Array):Void
                {
                    outer: for (var i:Number = 0; i < items.length; i++)
                    {
                        switch (i)
                        {
                            case 0: continue outer;
                            default: break;
                        }
                        inner: while (i > 0)
                        {
                            i--;
                            break inner;
                        }
                    }
                    block: { break block; }
                }
            }
            """;
        var (_, binding, file, tree) = BindSingle(source);

        binding.HasErrors.ShouldBeFalse(Describe(binding));
        var transfers = tree.Statements.Where(statement => statement.Kind is
                Avm1StatementSyntaxKind.Break or
                Avm1StatementSyntaxKind.Continue)
            .OrderBy(statement => statement.Span.Start)
            .ToArray();
        transfers.Length.ShouldBe(4);

        var continueOuter = binding.GetControlTransfer(file, transfers[0].Index);
        continueOuter.Kind.ShouldBe(Avm1SyntaxControlResolutionKind.Bound);
        var outerTarget = binding.GetControlTarget(continueOuter.Target);
        outerTarget.Kind.ShouldBe(Avm1SyntaxControlTargetKind.Label);
        outerTarget.Flags.HasFlag(
            Avm1SyntaxControlTargetFlags.Continuable).ShouldBeTrue();
        tree.GetStatement(outerTarget.TargetStatement).Kind.ShouldBe(
            Avm1StatementSyntaxKind.For);

        var switchBreak = binding.GetControlTarget(
            binding.GetControlTransfer(file, transfers[1].Index).Target);
        switchBreak.Kind.ShouldBe(Avm1SyntaxControlTargetKind.Switch);
        var innerBreak = binding.GetControlTarget(
            binding.GetControlTransfer(file, transfers[2].Index).Target);
        innerBreak.Kind.ShouldBe(Avm1SyntaxControlTargetKind.Label);
        binding.GetName(innerBreak.LabelName).ShouldBe("inner");
        var blockBreak = binding.GetControlTarget(
            binding.GetControlTransfer(file, transfers[3].Index).Target);
        blockBreak.Kind.ShouldBe(Avm1SyntaxControlTargetKind.Label);
        blockBreak.Flags.HasFlag(
            Avm1SyntaxControlTargetFlags.Continuable).ShouldBeFalse();
    }

    [Fact]
    public void Invalid_control_transfers_report_precise_diagnostics()
    {
        const string source = """
            class Test
            {
                function run():Void
                {
                    block: { continue block; }
                    duplicate: duplicate: while (false) { break missing; }
                    break;
                    continue unknown;
                }
            }
            """;
        var (_, binding, file, tree) = BindSingle(source);

        binding.HasErrors.ShouldBeTrue();
        binding.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B2001")
            .ShouldBe(1);
        binding.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B2002")
            .ShouldBe(2);
        binding.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B2003")
            .ShouldBe(1);
        binding.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B2004")
            .ShouldBe(1);

        var transfers = tree.Statements.Where(statement => statement.Kind is
                Avm1StatementSyntaxKind.Break or
                Avm1StatementSyntaxKind.Continue)
            .OrderBy(statement => statement.Span.Start)
            .ToArray();
        binding.GetControlTransfer(file, transfers[0].Index).Kind.ShouldBe(
            Avm1SyntaxControlResolutionKind.Invalid);
        transfers.Skip(1).ShouldAllBe(statement =>
            binding.GetControlTransfer(file, statement.Index).Kind ==
            Avm1SyntaxControlResolutionKind.Unresolved);
    }

    [Fact]
    public void Binds_every_lexical_node_in_the_complete_skyui_target()
    {
        var sourceFiles = SkyUiSourceRoots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.as",
                SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new Avm1SyntaxSourceFile(
                path,
                Avm1SyntaxTree.Parse(File.ReadAllText(path))))
            .ToArray();
        var program = Avm1SyntaxProgram.Create(sourceFiles);

        var binding = Avm1SyntaxLexicalBinding.Bind(program);

        program.HasErrors.ShouldBeFalse();
        binding.HasErrors.ShouldBeFalse(Describe(binding));
        binding.CodeUnits.Count.ShouldBeGreaterThan(500);
        binding.Scopes.Count.ShouldBeGreaterThanOrEqualTo(binding.CodeUnits.Count);
        binding.Symbols.Count.ShouldBeGreaterThan(1000);
        binding.CodeUnits.Count(codeUnit => codeUnit.Flags.HasFlag(
            Avm1SyntaxCodeUnitFlags.ContainsDirectEval)).ShouldBeGreaterThan(0);

        var controlTransferCount = 0;
        for (var fileValue = 0; fileValue < sourceFiles.Length; fileValue++)
        {
            var file = new Avm1SyntaxFileIndex(fileValue);
            var tree = sourceFiles[fileValue].SyntaxTree;
            foreach (var expression in tree.Expressions)
            {
                binding.GetScope(file, expression.Index).IsValid.ShouldBeTrue(
                    sourceFiles[fileValue].Path + ": expression " +
                    expression.Index.Value + " has no scope");
                if (expression.Kind is Avm1ExpressionSyntaxKind.IdentifierName)
                {
                    binding.GetIdentifierBinding(file, expression.Index).Kind
                        .ShouldNotBe(
                            Avm1SyntaxIdentifierBindingKind.NotApplicable);
                }
                if (expression.Kind is Avm1ExpressionSyntaxKind.FunctionExpression)
                {
                    binding.GetFunctionCodeUnit(file, expression.Index).IsValid
                        .ShouldBeTrue();
                }
            }

            foreach (var statement in tree.Statements)
            {
                binding.GetScope(file, statement.Index).IsValid.ShouldBeTrue(
                    sourceFiles[fileValue].Path + ": statement " +
                    statement.Index.Value + " has no scope");
                if (statement.Kind is
                    Avm1StatementSyntaxKind.Break or
                    Avm1StatementSyntaxKind.Continue)
                {
                    controlTransferCount++;
                    binding.GetControlTransfer(file, statement.Index).Kind
                        .ShouldBe(Avm1SyntaxControlResolutionKind.Bound);
                }
                if (statement.Kind is Avm1StatementSyntaxKind.FunctionDeclaration)
                {
                    binding.GetFunctionCodeUnit(file, statement.Index).IsValid
                        .ShouldBeTrue();
                }
            }
        }
        controlTransferCount.ShouldBe(270);
    }

    private static (
        Avm1SyntaxProgram Program,
        Avm1SyntaxLexicalBinding Binding,
        Avm1SyntaxFileIndex File,
        Avm1SyntaxTree Tree) BindSingle(string source)
    {
        var tree = Avm1SyntaxTree.Parse(source);
        tree.HasErrors.ShouldBeFalse();
        var program = Avm1SyntaxProgram.Create([
            new Avm1SyntaxSourceFile("Test.as", tree)
        ]);
        return (
            program,
            Avm1SyntaxLexicalBinding.Bind(program),
            new Avm1SyntaxFileIndex(0),
            tree);
    }

    private static Avm1SyntaxCodeUnitIndex GetMemberCodeUnit(
        Avm1SyntaxProgram program,
        Avm1SyntaxLexicalBinding binding,
        string typeName,
        string memberName)
    {
        program.TryGetType(typeName, out var type).ShouldBeTrue();
        var member = program.GetMemberCandidates(type, memberName)
            .ShouldHaveSingleItem();
        binding.TryGetMemberCodeUnit(member, out var codeUnit).ShouldBeTrue();
        return codeUnit;
    }

    private static Dictionary<string, Avm1SyntaxLocalSymbol> GetSymbols(
        Avm1SyntaxLexicalBinding binding,
        Avm1SyntaxScopeIndex scope) =>
        binding.GetSymbols(scope)
            .ToArray()
            .Select(binding.GetSymbol)
            .ToDictionary(symbol => binding.GetName(symbol.Name));

    private static IEnumerable<Avm1ExpressionSyntax> FindIdentifiers(
        Avm1SyntaxTree tree,
        string name) =>
        tree.Expressions.Where(expression =>
            expression.Kind == Avm1ExpressionSyntaxKind.IdentifierName &&
            tree.GetTokenText(expression.Token).ToString() == name);

    private static string Describe(Avm1SyntaxLexicalBinding binding) =>
        string.Join(
            Environment.NewLine,
            binding.Diagnostics.Select(diagnostic =>
                $"{binding.Program.GetPath(diagnostic.File)}:{diagnostic.Span}: " +
                $"{diagnostic.Code} {diagnostic.Message}"));
}
