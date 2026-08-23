using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Source;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1ClosureAnalysisTests
{
    [Fact]
    public void Builds_code_unit_tree_and_infers_direct_capture()
    {
        var fixture = new SourceFixtureBuilder();
        var captured = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "captured");
        var declaration = fixture.Declare(captured);
        var nestedBody = fixture.Block(fixture.Return(fixture.Reference(captured)));
        var function = fixture.Function(nestedBody, captures: [captured]);
        var rootBody = fixture.Block(
            declaration,
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var nestedCodeUnit = analysis.ScopeAnalysis.GetCodeUnit(function);
        var nested = analysis[nestedCodeUnit];
        var capture = analysis.GetCapture(nested, 0);

        analysis.IsComplete.ShouldBeTrue();
        analysis.CodeUnits.Count.ShouldBe(2);
        nested.Parent.ShouldBe(analysis.RootCodeUnit);
        nested.Captures.Count.ShouldBe(1);
        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.CapturesLexicalSymbols).ShouldBeTrue();
        analysis[analysis.RootCodeUnit].Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.OwnsCapturedSymbols).ShouldBeTrue();
        capture.Symbol.ShouldBe(captured);
        capture.DeclarationCodeUnit.ShouldBe(analysis.RootCodeUnit);
        capture.Uses.HasFlag(Avm1SourceExpressionUse.Read).ShouldBeTrue();
        capture.Flags.ShouldBe(
            Avm1ClosureCaptureFlags.Referenced |
            Avm1ClosureCaptureFlags.DeclaredMetadata);
        analysis[captured].RequiresNamedActivation.ShouldBeTrue();
        analysis[captured].IsRegisterEligible.ShouldBeFalse();
        analysis.CaptureMetadataMismatchCount.ShouldBe(0);
    }

    [Fact]
    public void Attributes_a_transitive_capture_to_the_referencing_code_unit_only()
    {
        var fixture = new SourceFixtureBuilder();
        var captured = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "captured");
        var innerBody = fixture.Block(fixture.Return(fixture.Reference(captured)));
        var innerFunction = fixture.Function(innerBody, captures: [captured]);
        var outerBody = fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(innerFunction)));
        var outerFunction = fixture.Function(outerBody);
        var rootBody = fixture.Block(
            fixture.Declare(captured),
            fixture.ExpressionStatement(fixture.FunctionLiteral(outerFunction)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var outer = analysis[analysis.ScopeAnalysis.GetCodeUnit(outerFunction)];
        var inner = analysis[analysis.ScopeAnalysis.GetCodeUnit(innerFunction)];

        analysis.IsComplete.ShouldBeTrue();
        outer.Captures.Count.ShouldBe(0);
        inner.Captures.Count.ShouldBe(1);
        inner.Parent.ShouldBe(outer.Index);
        analysis.GetCapture(inner, 0).Symbol.ShouldBe(captured);
        analysis[captured].Flags.HasFlag(
            Avm1ClosureSymbolFlags.CapturedByClosure).ShouldBeTrue();
    }

    [Fact]
    public void Preserves_declared_capture_metadata_conservatively()
    {
        var fixture = new SourceFixtureBuilder();
        var captured = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "captured");
        var function = fixture.Function(
            fixture.Block(fixture.Return()),
            captures: [captured]);
        var rootBody = fixture.Block(
            fixture.Declare(captured),
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var nested = analysis[analysis.ScopeAnalysis.GetCodeUnit(function)];
        var capture = analysis.GetCapture(nested, 0);

        analysis.IsComplete.ShouldBeTrue();
        capture.Flags.ShouldBe(Avm1ClosureCaptureFlags.DeclaredMetadata);
        capture.Uses.ShouldBe(Avm1SourceExpressionUse.None);
        analysis.CaptureMetadataMismatchCount.ShouldBe(1);
        analysis[captured].RequiresNamedActivation.ShouldBeTrue();
    }

    [Fact]
    public void Leaves_unobserved_parameters_and_locals_register_eligible()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "parameter");
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var rootBody = fixture.Block(
            fixture.Declare(local),
            fixture.ExpressionStatement(fixture.Reference(parameter)),
            fixture.ExpressionStatement(fixture.Reference(local)));

        var analysis = Avm1ClosureAnalysis.Analyze(
            fixture.Build(rootBody, parameter));

        analysis[parameter].IsRegisterEligible.ShouldBeTrue();
        analysis[local].IsRegisterEligible.ShouldBeTrue();
        analysis[parameter].RequiresNamedActivation.ShouldBeFalse();
        analysis[local].RequiresNamedActivation.ShouldBeFalse();
    }

    [Fact]
    public void Arguments_requires_named_parameter_storage_only()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "parameter");
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var arguments = fixture.AddSymbol(
            Avm1SourceSymbolKind.Arguments,
            "arguments");
        var rootBody = fixture.Block(
            fixture.Declare(local),
            fixture.ExpressionStatement(fixture.Reference(arguments)));

        var analysis = Avm1ClosureAnalysis.Analyze(
            fixture.Build(rootBody, parameter));

        analysis[analysis.RootCodeUnit].Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesArguments).ShouldBeTrue();
        analysis[parameter].Flags.HasFlag(
            Avm1ClosureSymbolFlags.ObservedThroughArguments).ShouldBeTrue();
        analysis[parameter].RequiresNamedActivation.ShouldBeTrue();
        analysis[local].IsRegisterEligible.ShouldBeTrue();
    }

    [Fact]
    public void Dynamic_arguments_lookup_also_protects_parameters()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "parameter");
        var rootBody = fixture.Block(
            fixture.ExpressionStatement(fixture.DynamicName("arguments")));

        var analysis = Avm1ClosureAnalysis.Analyze(
            fixture.Build(rootBody, parameter));

        analysis[analysis.RootCodeUnit].Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesArguments).ShouldBeTrue();
        analysis[parameter].RequiresNamedActivation.ShouldBeTrue();
    }

    [Fact]
    public void Function_binding_is_always_named_and_can_be_captured()
    {
        var fixture = new SourceFixtureBuilder();
        var helperSymbol = fixture.AddSymbol(
            Avm1SourceSymbolKind.Function,
            "helper");
        var helperFunction = fixture.Function(
            fixture.Block(fixture.Return()),
            Avm1SourceFunctionFlags.Declaration,
            nameSymbol: helperSymbol);
        var callbackFunction = fixture.Function(
            fixture.Block(fixture.Return(fixture.Reference(helperSymbol))),
            captures: [helperSymbol]);
        var rootBody = fixture.Block(
            fixture.FunctionDeclaration(helperSymbol, helperFunction),
            fixture.ExpressionStatement(
                fixture.FunctionLiteral(callbackFunction)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var callback = analysis[
            analysis.ScopeAnalysis.GetCodeUnit(callbackFunction)];

        callback.Captures.Count.ShouldBe(1);
        analysis.GetCapture(callback, 0).Symbol.ShouldBe(helperSymbol);
        analysis[helperSymbol].RequiresNamedActivation.ShouldBeTrue();
        analysis[helperSymbol].IsRegisterEligible.ShouldBeFalse();
    }

    [Fact]
    public void Eval_marks_current_and_ancestor_activation_symbols_addressable()
    {
        var fixture = new SourceFixtureBuilder();
        var outer = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "outer");
        var inner = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "inner");
        var evalCall = fixture.Call(
            fixture.DynamicName("eval"),
            fixture.StringLiteral("outer + inner"));
        var function = fixture.Function(fixture.Block(
            fixture.Declare(inner),
            fixture.ExpressionStatement(evalCall)));
        var rootBody = fixture.Block(
            fixture.Declare(outer),
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var nested = analysis[analysis.ScopeAnalysis.GetCodeUnit(function)];

        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.InvokesEval).ShouldBeTrue();
        analysis[outer].Flags.HasFlag(
            Avm1ClosureSymbolFlags.AddressableByEval).ShouldBeTrue();
        analysis[inner].Flags.HasFlag(
            Avm1ClosureSymbolFlags.AddressableByEval).ShouldBeTrue();
        analysis[outer].RequiresNamedActivation.ShouldBeTrue();
        analysis[inner].RequiresNamedActivation.ShouldBeTrue();
    }

    [Fact]
    public void With_marks_bound_references_as_dynamic_scope_sensitive()
    {
        var fixture = new SourceFixtureBuilder();
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var withBody = fixture.Block(
            fixture.ExpressionStatement(fixture.Reference(local)));
        var withStatement = fixture.StatementBuilder.AddStatement(
            Avm1SourceStatementKind.With,
            expression: fixture.DynamicName("scopeObject"),
            children: [withBody]);
        var rootBody = fixture.Block(fixture.Declare(local), withStatement);

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));

        analysis[analysis.RootCodeUnit].Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.ContainsWith).ShouldBeTrue();
        analysis[analysis.RootCodeUnit].Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope).ShouldBeTrue();
        analysis[local].Flags.HasFlag(
            Avm1ClosureSymbolFlags.DynamicScopeSensitive).ShouldBeTrue();
        analysis[local].RequiresNamedActivation.ShouldBeTrue();
    }

    [Fact]
    public void Nested_this_belongs_to_its_function_and_is_not_a_capture()
    {
        var fixture = new SourceFixtureBuilder();
        var nestedThis = fixture.AddSymbol(Avm1SourceSymbolKind.This, "this");
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Reference(nestedThis))));
        var rootBody = fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var nested = analysis[analysis.ScopeAnalysis.GetCodeUnit(function)];

        nested.Captures.Count.ShouldBe(0);
        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesThis).ShouldBeTrue();
        analysis[nestedThis].Flags.ShouldBe(Avm1ClosureSymbolFlags.None);
        analysis.ScopeAnalysis.GetCodeUnit(nestedThis).ShouldBe(nested.Index);
    }

    [Fact]
    public void Tracks_root_parent_and_global_usage_per_code_unit()
    {
        var fixture = new SourceFixtureBuilder();
        var root = fixture.AddSymbol(Avm1SourceSymbolKind.Root, "_root");
        var parent = fixture.AddSymbol(Avm1SourceSymbolKind.Parent, "_parent");
        var global = fixture.AddSymbol(Avm1SourceSymbolKind.Global, "_global");
        var function = fixture.Function(fixture.Block(
            fixture.ExpressionStatement(fixture.Reference(root)),
            fixture.ExpressionStatement(fixture.Reference(parent)),
            fixture.Return(fixture.Reference(global))));
        var rootBody = fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var nested = analysis[analysis.ScopeAnalysis.GetCodeUnit(function)];

        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesRoot).ShouldBeTrue();
        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesParent).ShouldBeTrue();
        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.UsesGlobal).ShouldBeTrue();
        nested.Captures.Count.ShouldBe(0);
    }

    [Fact]
    public void Carries_dynamic_scope_capture_flag_into_the_code_unit_plan()
    {
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(
            fixture.Block(fixture.Return()),
            Avm1SourceFunctionFlags.CapturesDynamicScope);
        var rootBody = fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));

        var analysis = Avm1ClosureAnalysis.Analyze(fixture.Build(rootBody));
        var nested = analysis[analysis.ScopeAnalysis.GetCodeUnit(function)];

        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.CapturesDynamicScope).ShouldBeTrue();
        nested.Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope).ShouldBeTrue();
    }

    [Fact]
    public void Compiler_artifact_carries_closure_analysis_into_function_lowering()
    {
        var fixture = new SourceFixtureBuilder();
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Reference(local))));
        var rootBody = fixture.Block(
            fixture.Declare(local),
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)));
        var source = fixture.Build(rootBody);

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(
            string.Join(Environment.NewLine, artifact.Diagnostics));
        artifact.ClosureAnalysis.ShouldNotBeNull();
        artifact.ClosureAnalysis!.IsComplete.ShouldBeTrue();
        artifact.ClosureAnalysis[local].RequiresNamedActivation.ShouldBeTrue();
        artifact.NestedFunctions.Count.ShouldBe(1);
    }

    private sealed class SourceFixtureBuilder
    {
        private readonly SourceTypeIndex _unknownType;

        public SourceFixtureBuilder()
        {
            _unknownType = StatementBuilder.GetBuiltInType(
                Avm1SourceTypeKind.Unknown);
        }

        public Avm1SourceArena.Builder StatementBuilder { get; } = new();

        public SourceSymbolIndex AddSymbol(
            Avm1SourceSymbolKind kind,
            string name) =>
            StatementBuilder.AddSymbol(
                kind,
                name,
                SourceTypeIndex.Invalid,
                _unknownType);

        public SourceExpressionIndex Reference(SourceSymbolIndex symbol) =>
            StatementBuilder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: symbol);

        public SourceExpressionIndex DynamicName(string name) =>
            StatementBuilder.AddExpression(
                Avm1SourceExpressionKind.DynamicName,
                name: StatementBuilder.InternString(name));

        public SourceExpressionIndex StringLiteral(string value)
        {
            var literal = StatementBuilder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: StatementBuilder.InternString(value));
            return StatementBuilder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: literal);
        }

        public SourceExpressionIndex Call(
            SourceExpressionIndex target,
            params SourceExpressionIndex[] arguments) =>
            StatementBuilder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children: [target, .. arguments]);

        public SourceExpressionIndex FunctionLiteral(SourceFunctionIndex function) =>
            StatementBuilder.AddExpression(
                Avm1SourceExpressionKind.FunctionLiteral,
                function: function);

        public SourceFunctionIndex Function(
            SourceStatementIndex body,
            Avm1SourceFunctionFlags flags = Avm1SourceFunctionFlags.None,
            IReadOnlyList<SourceSymbolIndex>? parameters = null,
            IReadOnlyList<SourceSymbolIndex>? captures = null,
            SourceSymbolIndex? nameSymbol = null) =>
            StatementBuilder.AddFunction(
                nameSymbol ?? SourceSymbolIndex.Invalid,
                body,
                flags,
                SourceOriginIndex.Invalid,
                parameters,
                captures);

        public SourceStatementIndex FunctionDeclaration(
            SourceSymbolIndex symbol,
            SourceFunctionIndex function) =>
            StatementBuilder.AddStatement(
                Avm1SourceStatementKind.FunctionDeclaration,
                expression: FunctionLiteral(function),
                symbol: symbol);

        public SourceStatementIndex Declare(SourceSymbolIndex symbol) =>
            StatementBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                symbol: symbol);

        public SourceStatementIndex ExpressionStatement(
            SourceExpressionIndex expression) =>
            StatementBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression);

        public SourceStatementIndex Return(
            SourceExpressionIndex? expression = null) =>
            StatementBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expression);

        public SourceStatementIndex Block(params SourceStatementIndex[] statements) =>
            StatementBuilder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: statements);

        public Avm1SourceMethod Build(
            SourceStatementIndex body,
            params SourceSymbolIndex[] parameters) =>
            new(StatementBuilder.ToArena(), body, parameters, []);
    }
}
