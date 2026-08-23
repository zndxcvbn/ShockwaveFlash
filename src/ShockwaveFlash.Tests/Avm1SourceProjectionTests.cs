using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SourceProjectionTests
{
    [Fact]
    public void Source_projection_allocations_stay_bounded_for_large_literal_data()
    {
        const long allocationBudget = 32 * 1024 * 1024;
        var actions = CreateLargeLiteralDataActions();

        var method = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        _ = method.ProjectSource();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var source = method.ProjectSource();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        source.IsComplete.ShouldBeTrue(string.Join(
            Environment.NewLine,
            source.Diagnostics.Select(diagnostic => diagnostic.Message)));
        source.Arena.Expressions.Count.ShouldBeGreaterThan(32_000);
        allocated.ShouldBeLessThan(
            allocationBudget,
            $"Source projection allocated {allocated:N0} bytes for " +
                $"{source.Arena.Expressions.Count:N0} expressions.");
    }

    [Fact]
    public void Method_core_allocations_stay_bounded_for_large_literal_data()
    {
        const long allocationBudget = 32 * 1024 * 1024;
        var actions = CreateLargeLiteralDataActions();
        _ = Avm1Decompiler.BuildMethodCore(actions, swfVersion: 6);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var core = Avm1Decompiler.BuildMethodCore(actions, swfVersion: 6);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        core.StackIr.Diagnostics.ShouldBeEmpty();
        core.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
        core.StackIr.Values.Count.ShouldBeGreaterThan(32_000);
        allocated.ShouldBeLessThan(
            allocationBudget,
            $"Method core allocated {allocated:N0} bytes for " +
                $"{core.StackIr.Values.Count:N0} stack values.");
    }

    [Fact]
    public void Class_abi_actions_require_class_aware_source_projection()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("_global")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Child")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("Base")]),
            new ActionGetVariable(),
            new ActionExtends(),
            new ActionPush([PushValue.String("IFirst")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.String("_global")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Child")]),
            new ActionGetMember(),
            new ActionImplementsOp()
        ], swfVersion: 7);

        var methodSource = decompilation.ProjectSource();

        methodSource.IsComplete.ShouldBeFalse();
        methodSource.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC006" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error);
        methodSource.Arena.OpaqueRegions.ShouldHaveSingleItem();
        methodSource.GetAs2Text().ShouldNotContain("var v");

        var initializer = Avm1ClassInitializerProjector.Project(
            decompilation,
            "Child",
            [],
            new Dictionary<(int Register, int Version), string>());

        initializer.IsComplete.ShouldBeTrue(string.Join(
            Environment.NewLine,
            initializer.Body.Diagnostics.Select(diagnostic => diagnostic.Message)));
        initializer.Steps.ShouldContain(step =>
            step.Kind == Avm1SourceClassInitializationStepKind.Inheritance);
        initializer.Steps.ShouldContain(step =>
            step.Kind == Avm1SourceClassInitializationStepKind.Interfaces);
        initializer.Body.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1SRC006");
    }

    [Fact]
    public void Unreachable_class_abi_action_does_not_hide_a_complete_method()
    {
        var source = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Integer(7)]),
            new ActionReturn(),
            new ActionPush([PushValue.String("Child")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Base")]),
            new ActionGetVariable(),
            new ActionExtends()
        ], swfVersion: 7).ProjectSource();

        source.IsComplete.ShouldBeTrue(string.Join(
            Environment.NewLine,
            source.Diagnostics.Select(diagnostic => diagnostic.Message)));
        source.GetAs2Text().ShouldBe("return 7;" + Environment.NewLine);
        source.Arena.OpaqueRegions.ShouldBeEmpty();
        source.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1SRC006");
    }

    [Fact]
    public void Projects_a_computed_variable_lookup_as_eval()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("name")]),
            new ActionGetVariable(),
            new ActionGetVariable(),
            new ActionReturn()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            "return eval(name);" + Environment.NewLine);
        decompilation.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());
        var statement = GetOnlyEffectiveStatement(source.Arena, source.Body);
        var expression = source.Arena[statement.Expression];
        expression.Kind.ShouldBe(
            Avm1SourceExpressionKind.ComputedDynamicName);
        source.Arena[source.Arena.GetChild(expression, 0)].Kind.ShouldBe(
            Avm1SourceExpressionKind.DynamicName);
    }

    [Fact]
    public void Projects_an_undefined_method_name_as_a_direct_callable_target()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("callback")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Undefined()]),
            new ActionCallMethod(),
            new ActionPop()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe("callback();" + Environment.NewLine);
    }

    [Fact]
    public void Projects_linear_expression_into_self_contained_source_hir()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("y")]),
            new ActionGetVariable(),
            new ActionAdd2(),
            new ActionSetVariable()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.Diagnostics.ShouldBeEmpty();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.GetAs2Text().ShouldBe("result = x + y;" + Environment.NewLine);

        var arena = source.Arena;
        var root = arena[source.Body];
        root.Kind.ShouldBe(Avm1SourceStatementKind.Block);
        root.Children.Count.ShouldBe(1);

        var statement = GetOnlyEffectiveStatement(arena, source.Body);
        statement.Kind.ShouldBe(Avm1SourceStatementKind.Expression);
        var assignment = arena[statement.Expression];
        assignment.Kind.ShouldBe(Avm1SourceExpressionKind.Assignment);
        assignment.Operator.ShouldBe(Avm1SourceOperator.Assign);

        var target = arena[arena.GetChild(assignment, 0)];
        target.Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
        arena[target.Name].ShouldBe("result");

        var value = arena[arena.GetChild(assignment, 1)];
        value.Kind.ShouldBe(Avm1SourceExpressionKind.Binary);
        value.Operator.ShouldBe(Avm1SourceOperator.Add);
        var left = arena[arena.GetChild(value, 0)];
        var right = arena[arena.GetChild(value, 1)];
        left.Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
        right.Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
        arena[left.Name].ShouldBe("x");
        arena[right.Name].ShouldBe("y");
    }

    [Fact]
    public void Projects_member_assignment_without_conflating_dynamic_names_and_members()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([
                PushValue.String("disableInput"),
                PushValue.Boolean(true)
            ]),
            new ActionSetMember()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe("obj.disableInput = true;" + Environment.NewLine);

        var arena = source.Arena;
        var statement = GetOnlyEffectiveStatement(arena, source.Body);
        var assignment = arena[statement.Expression];
        var member = arena[arena.GetChild(assignment, 0)];
        member.Kind.ShouldBe(Avm1SourceExpressionKind.MemberAccess);
        member.Flags.ShouldBe(Avm1SourceExpressionFlags.None);

        var receiver = arena[arena.GetChild(member, 0)];
        receiver.Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
        arena[receiver.Name].ShouldBe("obj");

        var memberName = arena[arena.GetChild(member, 1)];
        arena[arena[memberName.Literal].StringValue].ShouldBe("disableInput");
    }

    [Fact]
    public void Projects_variable_declaration_as_a_bound_symbol()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([
                PushValue.String("answer"),
                PushValue.Integer(42)
            ]),
            new ActionDefineLocal()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe("var answer = 42;" + Environment.NewLine);

        var arena = source.Arena;
        var declaration = GetOnlyEffectiveStatement(arena, source.Body);
        declaration.Kind.ShouldBe(Avm1SourceStatementKind.VariableDeclaration);
        var symbol = arena[declaration.Symbol];
        symbol.Kind.ShouldBe(Avm1SourceSymbolKind.Local);
        arena[symbol.Name].ShouldBe("answer");
        ReadIntegerLiteral(arena, declaration.Expression).ShouldBe(42);
    }

    [Fact]
    public void Binds_function_scoped_local_before_its_declaration()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([
                PushValue.String("value"),
                PushValue.Integer(1)
            ]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionDefineLocal2()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        var arena = source.Arena;
        var statements = GetEffectiveStatements(arena, source.Body);
        statements.Count.ShouldBe(2);

        var assignment = arena[statements[0].Expression];
        var target = arena[arena.GetChild(assignment, 0)];
        target.Kind.ShouldBe(Avm1SourceExpressionKind.SymbolReference);

        var declaration = statements[1];
        declaration.Kind.ShouldBe(Avm1SourceStatementKind.VariableDeclaration);
        target.Symbol.ShouldBe(declaration.Symbol);
        arena[arena[target.Symbol].Name].ShouldBe("value");
    }

    [Fact]
    public void Projects_if_else_with_structured_text_parity()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };
        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);

        var offsets = ComputeOffsets(actions);
        var jumpIndex = 3 + thenBody.Length;
        var elseStart = jumpIndex + 1;
        SetBranch(actions, index: 2, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[^1] + ActionLength(actions[^1]));

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var ifStatement = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.If);
        ifStatement.Children.Count.ShouldBe(2);
        ifStatement.Expression.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Projects_while_and_unlabeled_continue()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + ActionLength(actions[^1]);
        SetBranch(actions, index: 3, exitOffset);
        SetBranch(actions, index: 6, offsets[0]);
        SetBranch(actions, index: 10, offsets[0]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var statements = GetAllStatements(source.Arena, source.Body);
        statements.Count(statement => statement.Kind is Avm1SourceStatementKind.While).ShouldBe(1);
        var @continue = statements.Single(
            statement => statement.Kind is Avm1SourceStatementKind.Continue);
        @continue.Label.ShouldBe(SourceLabelIndex.Invalid);
    }

    [Fact]
    public void Projects_do_while_and_unlabeled_break()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("done")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[9]);
        SetBranch(actions, index: 8, offsets[0]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var statements = GetAllStatements(source.Arena, source.Body);
        statements.Count(statement => statement.Kind is Avm1SourceStatementKind.DoWhile).ShouldBe(1);
        var @break = statements.Single(statement => statement.Kind is Avm1SourceStatementKind.Break);
        @break.Label.ShouldBe(SourceLabelIndex.Invalid);
    }

    [Fact]
    public void Binds_outer_loop_continue_to_the_same_source_label_as_its_target()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[19]);
        SetBranch(actions, index: 7, offsets[15]);
        SetBranch(actions, index: 10, offsets[0]);
        SetBranch(actions, index: 14, offsets[4]);
        SetBranch(actions, index: 18, offsets[0]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var statements = GetAllStatements(source.Arena, source.Body);
        var outerLoop = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.While && statement.Label.IsValid);
        var @continue = statements.Single(
            statement => statement.Kind is Avm1SourceStatementKind.Continue);
        @continue.Label.ShouldBe(outerLoop.Label);
        source.Arena[source.Arena[outerLoop.Label].Name].ShouldBe("loop_b0");
    }

    [Fact]
    public void Projects_single_clause_for_into_list_valued_source_hir()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 7, offsets[17]);
        SetBranch(actions, index: 16, offsets[3]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var @for = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.For);
        @for.Initializers.Count.ShouldBe(1);
        @for.Expressions.Count.ShouldBe(1);
        @for.Expression.IsValid.ShouldBeTrue();
        source.GetAs2Text().ShouldContain("result = _loc5_;");
        source.Arena.Symbols.Count(symbol =>
            source.Arena[symbol.Name] == "_loc5_").ShouldBe(1);
    }

    [Fact]
    public void Recovers_multiple_for_initializers_and_updates_in_source_order()
    {
        var actions = BuildTwoCounterLoop(includeInitializerBarrier: false);
        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 10, offsets[^1] + ActionLength(actions[^1]));
        SetBranch(actions, index: 24, offsets[6]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        var recoveryFor = decompilation.StructuredAst.Nodes.Single(
            node => node.Kind is Avm1AstNodeKind.For);
        var recoveryInitializers = decompilation.StructuredAst[
            decompilation.StructuredAst.GetChild(recoveryFor, 0)];
        var recoveryUpdates = decompilation.StructuredAst[
            decompilation.StructuredAst.GetChild(recoveryFor, 2)];
        recoveryInitializers.Kind.ShouldBe(Avm1AstNodeKind.ForInitializerList);
        recoveryInitializers.Children.Count.ShouldBe(2);
        recoveryUpdates.Kind.ShouldBe(Avm1AstNodeKind.ForUpdateList);
        recoveryUpdates.Children.Count.ShouldBe(2);

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc5_ = 0, _loc6_ = 0; _loc5_ < 3; _loc5_ += 1, _loc6_ += 1)",
            "{",
            "    y = _loc6_;",
            "}",
            string.Empty
        ]));

        var sourceFor = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.For);
        sourceFor.Initializers.Count.ShouldBe(2);
        sourceFor.Expressions.Count.ShouldBe(2);
    }

    [Fact]
    public void Rejects_multiple_for_promotion_across_initializer_side_effect()
    {
        var actions = BuildTwoCounterLoop(includeInitializerBarrier: true);
        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 12, offsets[^1] + ActionLength(actions[^1]));
        SetBranch(actions, index: 26, offsets[8]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        decompilation.StructuredAst.Nodes.ShouldNotContain(
            node => node.Kind == Avm1AstNodeKind.For);
        decompilation.StructuredAst.Nodes.ShouldContain(
            node => node.Kind == Avm1AstNodeKind.While);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.GetAs2Text().ShouldContain("trace(\"barrier\");");
        source.GetAs2Text().ShouldContain("while (_loc5_ < 3)");
    }

    [Fact]
    public void Groups_multiple_declared_variables_under_one_for_var_clause()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("i"), PushValue.Integer(0)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("c"), PushValue.Integer(0)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.String("c")]),
            new ActionGetVariable(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("i")]),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("c")]),
            new ActionPush([PushValue.String("c")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 9, offsets[^1] + ActionLength(actions[^1]));
        SetBranch(actions, index: 26, offsets[4]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var i = 0, c = 0; i < 3; i += 1, c += 1)",
            "{",
            "    y = c;",
            "}",
            string.Empty
        ]));
        var @for = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.For);
        @for.Initializers.Count.ShouldBe(2);
        @for.Expressions.Count.ShouldBe(2);
        for (var i = 0; i < @for.Initializers.Count; i++)
        {
            source.Arena[source.Arena.GetInitializer(@for, i)].Kind
                .ShouldBe(Avm1SourceStatementKind.VariableDeclaration);
        }
    }

    [Fact]
    public void Projects_for_in_with_a_declared_register_key()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[11]);
        SetBranch(actions, index: 10, offsets[3]);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var forIn = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.ForIn);
        forIn.Flags.ShouldBe(Avm1SourceStatementFlags.ForInDeclaresKey);
        forIn.Expression.IsValid.ShouldBeTrue();
        forIn.SecondaryExpression.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Projects_grouped_switch_labels_and_fallthrough_in_source_order()
    {
        var actions = BuildSourceSwitch(
            bodyByCase: [0, 0, 1],
            bodies: [
                new SourceSwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(12)]),
                    new ActionSetVariable()
                ], JumpsToMerge: false),
                new SourceSwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(3)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true)
            ],
            defaultBody: new SourceSwitchBodySpec([
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(4)]),
                new ActionSetVariable()
            ], JumpsToMerge: true));

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "    case \"b\":",
            "        x = 12;",
            "    case \"c\":",
            "        x = 3;",
            "        break;",
            "    default:",
            "        x = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));

        var @switch = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Switch);
        @switch.Children.Count.ShouldBe(4);
        var labels = Enumerable.Range(0, @switch.Children.Count)
            .Select(index => source.Arena[source.Arena.GetChild(@switch, index)])
            .ToArray();
        labels.Select(label => label.Kind).ShouldBe([
            Avm1SourceStatementKind.SwitchCase,
            Avm1SourceStatementKind.SwitchCase,
            Avm1SourceStatementKind.SwitchCase,
            Avm1SourceStatementKind.SwitchDefault
        ]);
        labels[0].Children.Count.ShouldBe(0);
        GetAllStatements(source.Arena, labels[1].Index)
            .ShouldNotContain(statement => statement.Kind == Avm1SourceStatementKind.Break);
        GetAllStatements(source.Arena, labels[2].Index)
            .ShouldContain(statement => statement.Kind == Avm1SourceStatementKind.Break);
    }

    [Fact]
    public void Preserves_outer_loop_label_identity_through_switch_cases()
    {
        var decompilation = Avm1Decompiler.DecompileMethod(
            BuildSwitchInsideWhile(includeOuterLoopExits: true),
            swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var statements = GetAllStatements(source.Arena, source.Body);
        var outerLoop = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.While && statement.Label.IsValid);
        var labeledBreak = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.Break && statement.Label.IsValid);
        labeledBreak.Label.ShouldBe(outerLoop.Label);
        source.Arena[source.Arena[outerLoop.Label].Name].ShouldBe("loop_b0");
        statements.ShouldContain(statement =>
            statement.Kind == Avm1SourceStatementKind.Switch && !statement.Label.IsValid);
    }

    [Fact]
    public void Treats_lexical_name_actions_inside_with_as_dynamic_scope_lookups()
    {
        var withBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("x"), PushValue.Integer(0)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            new ActionWith(withBody),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var x = 0;",
            "with (scope)",
            "{",
            "    x = 1;",
            "}",
            "after = x;",
            string.Empty
        ]));

        var @with = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.With);
        var withBodyStatement = source.Arena.GetChild(@with, 0);
        var dynamicAssignment = GetAllStatements(source.Arena, withBodyStatement)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var dynamicTarget = source.Arena[source.Arena.GetChild(
            source.Arena[dynamicAssignment.Expression],
            0)];
        dynamicTarget.Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
        source.Arena[dynamicTarget.Name].ShouldBe("x");

        var outsideAssignment = GetEffectiveStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var outsideValue = source.Arena[source.Arena.GetChild(
            source.Arena[outsideAssignment.Expression],
            1)];
        outsideValue.Kind.ShouldBe(Avm1SourceExpressionKind.SymbolReference);
        source.Arena[source.Arena[outsideValue.Symbol].Name].ShouldBe("x");
    }

    [Fact]
    public void Projects_nested_with_boundaries_without_flattening_dynamic_scopes()
    {
        var innerBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x"), PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var outerBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("innerScope")]),
            new ActionGetVariable(),
            new ActionWith(innerBody),
            new ActionPush([PushValue.String("y"), PushValue.Integer(2)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("outerScope")]),
            new ActionGetVariable(),
            new ActionWith(outerBody),
            new ActionPush([PushValue.String("z"), PushValue.Integer(3)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var withStatements = GetAllStatements(source.Arena, source.Body)
            .Where(statement => statement.Kind is Avm1SourceStatementKind.With)
            .ToArray();
        withStatements.Length.ShouldBe(2);
        var nestedBody = source.Arena.GetChild(withStatements[0], 0);
        GetAllStatements(source.Arena, nestedBody)
            .ShouldContain(statement => statement.Index == withStatements[1].Index);
    }

    [Fact]
    public void Keeps_explicit_register_access_bound_inside_with()
    {
        var withBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("inside"), PushValue.Register(5)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("seed")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            new ActionWith(withBody),
            new ActionPush([PushValue.String("after"), PushValue.Register(5)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var @with = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.With);
        var update = GetAllStatements(source.Arena, source.Arena.GetChild(@with, 0))
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var assignment = source.Arena[update.Expression];
        var target = source.Arena[source.Arena.GetChild(assignment, 0)];
        var value = source.Arena[source.Arena.GetChild(assignment, 1)];
        target.Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
        value.Kind.ShouldBe(Avm1SourceExpressionKind.SymbolReference);
    }

    [Fact]
    public void Projects_named_catch_and_finally_as_explicit_clauses()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x"), PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("caught")]),
            new ActionPush([PushValue.String("error")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        ], swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("cleanup"), PushValue.Integer(3)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                finallyBody)
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var @try = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Try);
        @try.Children.Count.ShouldBe(3);
        var @catch = source.Arena[source.Arena.GetChild(@try, 1)];
        var @finally = source.Arena[source.Arena.GetChild(@try, 2)];
        @catch.Kind.ShouldBe(Avm1SourceStatementKind.CatchClause);
        @finally.Kind.ShouldBe(Avm1SourceStatementKind.FinallyClause);
        var catchSymbol = source.Arena[@catch.Symbol];
        catchSymbol.Kind.ShouldBe(Avm1SourceSymbolKind.Catch);
        source.Arena[catchSymbol.Name].ShouldBe("error");

        var catchAssignment = GetAllStatements(
                source.Arena,
                source.Arena.GetChild(@catch, 0))
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var catchValue = source.Arena[source.Arena.GetChild(
            source.Arena[catchAssignment.Expression],
            1)];
        catchValue.Kind.ShouldBe(Avm1SourceExpressionKind.SymbolReference);
        catchValue.Symbol.ShouldBe(@catch.Symbol);

        var region = decompilation.Instructions.TryRegions.ShouldHaveSingleItem();
        decompilation.ControlFlowGraph.TryGetBlockForAction(
            region.CatchBodyStartAction,
            out var catchBlock).ShouldBeTrue();
        decompilation.ControlFlowGraph.TryGetBlockForAction(
            region.FinallyBodyStartAction,
            out var finallyBlock).ShouldBeTrue();
        decompilation.Reachability.IsReachable(catchBlock).ShouldBeTrue();
        decompilation.Reachability.IsReachable(finallyBlock).ShouldBeTrue();
    }

    [Fact]
    public void Projects_register_catch_as_one_scoped_catch_symbol()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x"), PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("caught"), PushValue.Register(7)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.CatchInRegister,
                catchRegister: 7,
                catchVariable: string.Empty,
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var @catch = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.CatchClause);
        var catchSymbol = source.Arena[@catch.Symbol];
        catchSymbol.Kind.ShouldBe(Avm1SourceSymbolKind.Catch);
        source.Arena[catchSymbol.Name].ShouldBe("_loc7_");
        var catchAssignment = GetAllStatements(
                source.Arena,
                source.Arena.GetChild(@catch, 0))
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var value = source.Arena[source.Arena.GetChild(
            source.Arena[catchAssignment.Expression],
            1)];
        value.Kind.ShouldBe(Avm1SourceExpressionKind.SymbolReference);
        value.Symbol.ShouldBe(@catch.Symbol);
    }

    [Fact]
    public void Catch_symbol_shadows_function_local_only_inside_catch_body()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x"), PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("caught")]),
            new ActionPush([PushValue.String("error")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("error"), PushValue.String("outer")]),
            new ActionDefineLocal(),
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.String("error")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var statements = GetAllStatements(source.Arena, source.Body);
        var outerDeclaration = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.VariableDeclaration);
        var @catch = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.CatchClause);
        @catch.Symbol.ShouldNotBe(outerDeclaration.Symbol);

        var catchAssignment = GetAllStatements(
                source.Arena,
                source.Arena.GetChild(@catch, 0))
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var catchValue = source.Arena[source.Arena.GetChild(
            source.Arena[catchAssignment.Expression],
            1)];
        catchValue.Symbol.ShouldBe(@catch.Symbol);

        var outsideAssignment = GetEffectiveStatements(source.Arena, source.Body)
            .Last(statement => statement.Kind is Avm1SourceStatementKind.Expression);
        var outsideValue = source.Arena[source.Arena.GetChild(
            source.Arena[outsideAssignment.Expression],
            1)];
        outsideValue.Symbol.ShouldBe(outerDeclaration.Symbol);
    }

    [Fact]
    public void Projects_finally_only_after_terminal_try_body()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn()
        ], swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("cleanup"), PushValue.Boolean(true)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                tryBody,
                ReadOnlyMemory<byte>.Empty,
                finallyBody)
        ], swfVersion: 6);
        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var @try = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Try);
        @try.Children.Count.ShouldBe(2);
        source.Arena[source.Arena.GetChild(@try, 1)].Kind
            .ShouldBe(Avm1SourceStatementKind.FinallyClause);
        GetAllStatements(source.Arena, source.Arena.GetChild(@try, 0))
            .ShouldContain(statement => statement.Kind == Avm1SourceStatementKind.Return);
    }

    [Fact]
    public void Projects_define_function2_as_self_contained_function_hir()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Register(1)]),
            new ActionReturn()
        ], swfVersion: 7);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("onPress")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 2,
                flags: (FunctionFlags)0,
                parameters: [new FunctionParameter(1, "value")],
                body: functionBody),
            new ActionSetMember()
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.Arena.Functions.Count.ShouldBe(1);
        var functionExpression = source.Arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.FunctionLiteral);
        var function = source.Arena[functionExpression.Function];
        function.NameSymbol.IsValid.ShouldBeFalse();
        function.Parameters.Count.ShouldBe(1);
        function.Captures.Count.ShouldBe(0);
        var parameter = source.Arena.GetParameter(function, 0);
        source.Arena[parameter].Kind.ShouldBe(Avm1SourceSymbolKind.Parameter);
        source.Arena[source.Arena[parameter].Name].ShouldBe("value");
        var @return = GetAllStatements(source.Arena, function.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Return);
        var value = source.Arena[@return.Expression];
        value.Kind.ShouldBe(Avm1SourceExpressionKind.SymbolReference);
        value.Symbol.ShouldBe(parameter);
        source.Arena[functionExpression.Origin].RecoveryUnit.ShouldBe(0);
        source.Arena[@return.Origin].RecoveryUnit.ShouldBe(1);
    }

    [Fact]
    public void Projects_reused_parameter_register_as_a_new_local_symbol()
    {
        var context = new FunctionContext(
            (FunctionFlags)0,
            [new FunctionParameter(1, "value")]);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("before"), PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Integer(0), PushValue.String("getValue")]),
            new ActionCallFunction(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.String("after"), PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(1)]),
            new ActionReturn()
        ], swfVersion: 7, context);

        var source = decompilation.ProjectSource();
        var text = source.GetAs2Text();
        text.ShouldBe(string.Join(Environment.NewLine, [
            "before = value;",
            "var _loc1_ = getValue();",
            "after = _loc1_;",
            "return _loc1_;",
            string.Empty
        ]));
        var statements = GetEffectiveStatements(source.Arena, source.Body);
        var parameter = source.Parameters.Single();
        var declaration = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.VariableDeclaration);
        var @return = statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.Return);

        declaration.Symbol.ShouldNotBe(parameter);
        source.Arena[@return.Expression].Symbol.ShouldBe(declaration.Symbol);
    }

    [Fact]
    public void Records_outer_lexical_capture_by_symbol_identity()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("captured")]),
            new ActionGetVariable(),
            new ActionReturn()
        ], swfVersion: 7);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("captured"), PushValue.Integer(7)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("callback")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: functionBody),
            new ActionSetMember()
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var declaration = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.VariableDeclaration);
        var function = source.Arena[source.Arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.FunctionLiteral).Function];
        function.Captures.Count.ShouldBe(1);
        source.Arena.GetCapture(function, 0).ShouldBe(declaration.Symbol);
        var @return = GetAllStatements(source.Arena, function.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Return);
        source.Arena[@return.Expression].Symbol.ShouldBe(declaration.Symbol);
    }

    [Fact]
    public void Function_parameter_and_this_shadow_outer_function_bindings()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("nestedThis"), PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(2)]),
            new ActionReturn()
        ], swfVersion: 7);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("value"), PushValue.Integer(7)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("outerThis"), PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("callback")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 3,
                flags: FunctionFlags.PreloadThis,
                parameters: [new FunctionParameter(2, "value")],
                body: functionBody),
            new ActionSetVariable()
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var function = source.Arena[source.Arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.FunctionLiteral).Function];
        function.Captures.Count.ShouldBe(0);
        var parameter = source.Arena.GetParameter(function, 0);
        var @return = GetAllStatements(source.Arena, function.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Return);
        source.Arena[@return.Expression].Symbol.ShouldBe(parameter);

        var thisSymbols = source.Arena.Symbols
            .Where(symbol => symbol.Kind is Avm1SourceSymbolKind.This)
            .Select(symbol => symbol.Index)
            .ToArray();
        thisSymbols.Length.ShouldBe(2);
        thisSymbols[0].ShouldNotBe(thisSymbols[1]);
    }

    [Fact]
    public void Recursively_projects_nested_function_and_transitive_capture()
    {
        var innerBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("captured")]),
            new ActionGetVariable(),
            new ActionReturn()
        ], swfVersion: 7);
        var outerBody = Avm1Action.EncodeCollection([
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: innerBody),
            new ActionReturn()
        ], swfVersion: 7);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("captured"), PushValue.Integer(7)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("callback")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: outerBody),
            new ActionSetVariable()
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        source.Arena.Functions.Count.ShouldBe(2);
        var declaration = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.VariableDeclaration);
        var capturingFunction = source.Arena.Functions.Single(function => function.Captures.Count == 1);
        source.Arena.GetCapture(capturingFunction, 0).ShouldBe(declaration.Symbol);
        source.Arena.Functions.Single(function => function.Captures.Count == 0)
            .Index.ShouldNotBe(capturingFunction.Index);
    }

    [Fact]
    public void Function_created_inside_with_retains_dynamic_scope_boundary()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionReturn()
        ], swfVersion: 7);
        var withBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("callback")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: functionBody),
            new ActionSetVariable()
        ], swfVersion: 7);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("x"), PushValue.Integer(7)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            new ActionWith(withBody)
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(decompilation.GetStructuredAs2Text());
        var function = source.Arena.Functions.Single();
        function.Flags.ShouldBe(Avm1SourceFunctionFlags.CapturesDynamicScope);
        function.Captures.Count.ShouldBe(0);
        var @return = GetAllStatements(source.Arena, function.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Return);
        source.Arena[@return.Expression].Kind.ShouldBe(Avm1SourceExpressionKind.DynamicName);
    }

    [Fact]
    public void Projects_legacy_named_function_as_a_declaration_with_self_binding()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionReturn()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionDefineFunction("inner", [], functionBody),
            new ActionPop()
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        var declaration = GetAllStatements(source.Arena, source.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.FunctionDeclaration);
        var functionExpression = source.Arena[declaration.Expression];
        var function = source.Arena[functionExpression.Function];
        function.Flags.ShouldBe(Avm1SourceFunctionFlags.Declaration);
        function.NameSymbol.ShouldBe(declaration.Symbol);
        source.Arena[function.NameSymbol].Kind.ShouldBe(Avm1SourceSymbolKind.Function);
        var @return = GetAllStatements(source.Arena, function.Body)
            .Single(statement => statement.Kind is Avm1SourceStatementKind.Return);
        source.Arena[@return.Expression].Symbol.ShouldBe(function.NameSymbol);
        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "function inner()",
            "{",
            "    return inner;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Reports_invalid_named_function_without_discarding_its_body()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn()
        ], swfVersion: 6);
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionDefineFunction("invalid-name", [], functionBody)
        ], swfVersion: 6);

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.Count.ShouldBe(1);
        source.Diagnostics[0].Code.ShouldBe("AVM1SRC002");
        source.Diagnostics[0].Message.ShouldContain("invalid-name");
        var function = source.Arena.Functions.Single();
        function.NameSymbol.IsValid.ShouldBeFalse();
        GetAllStatements(source.Arena, function.Body)
            .ShouldContain(statement => statement.Kind == Avm1SourceStatementKind.Return);
    }

    private static int ReadIntegerLiteral(
        Avm1SourceArena arena,
        SourceExpressionIndex expressionIndex)
    {
        var expression = arena[expressionIndex];
        expression.Kind.ShouldBe(Avm1SourceExpressionKind.Literal);
        var literal = arena[expression.Literal];
        literal.Kind.ShouldBe(Avm1SourceLiteralKind.Integer);
        return literal.IntegerValue;
    }

    private static int ActionLength(Avm1Action action) =>
        Avm1Action.EncodeCollection([action], swfVersion: 6).Length;

    private static List<Avm1Action> BuildTwoCounterLoop(bool includeInitializerBarrier)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop()
        };
        if (includeInitializerBarrier)
        {
            actions.Add(new ActionPush([PushValue.String("barrier")]));
            actions.Add(new ActionTrace());
        }
        actions.AddRange([
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(6),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(6)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(6)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionStoreRegister(6),
            new ActionPop(),
            new ActionJump(0)
        ]);
        return actions;
    }

    private readonly record struct SourceSwitchBodySpec(
        IReadOnlyList<Avm1Action> Actions,
        bool JumpsToMerge);

    private static List<Avm1Action> BuildSourceSwitch(
        IReadOnlyList<int> bodyByCase,
        IReadOnlyList<SourceSwitchBodySpec> bodies,
        SourceSwitchBodySpec defaultBody)
    {
        var actions = new List<Avm1Action>();
        var ifIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            ifIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        var jumpIndices = new List<int>();
        actions.AddRange(defaultBody.Actions);
        if (defaultBody.JumpsToMerge)
        {
            jumpIndices.Add(actions.Count);
            actions.Add(new ActionJump(0));
        }

        var bodyStarts = new int[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            bodyStarts[i] = actions.Count;
            actions.AddRange(bodies[i].Actions);
            if (bodies[i].JumpsToMerge)
            {
                jumpIndices.Add(actions.Count);
                actions.Add(new ActionJump(0));
            }
        }

        var mergeStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        for (var i = 0; i < ifIndices.Length; i++)
            SetBranch(actions, ifIndices[i], offsets[bodyStarts[bodyByCase[i]]]);
        foreach (var jumpIndex in jumpIndices)
            SetBranch(actions, jumpIndex, offsets[mergeStart]);
        return actions;
    }

    private static List<Avm1Action> BuildSwitchInsideWhile(bool includeOuterLoopExits)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("loop")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0)
        };

        var caseIfIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            caseIfIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ]);
        var defaultJump = actions.Count;
        actions.Add(new ActionJump(0));

        var bodyStarts = new int[3];
        var caseJumps = new int[3];
        var outerLoopControlIfs = new[] { -1, -1 };
        for (var i = 0; i < bodyStarts.Length; i++)
        {
            bodyStarts[i] = actions.Count;
            if (includeOuterLoopExits && i < outerLoopControlIfs.Length)
            {
                actions.Add(new ActionPush([PushValue.String(i == 0 ? "skip" : "done")]));
                actions.Add(new ActionGetVariable());
                outerLoopControlIfs[i] = actions.Count;
                actions.Add(new ActionIf(0));
            }

            actions.AddRange([
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(i + 1)]),
                new ActionSetVariable()
            ]);
            caseJumps[i] = actions.Count;
            actions.Add(new ActionJump(0));
        }

        var switchMerge = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);
        var loopJump = actions.Count;
        actions.Add(new ActionJump(0));

        var loopExit = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(6)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[loopExit]);
        for (var i = 0; i < caseIfIndices.Length; i++)
        {
            SetBranch(actions, caseIfIndices[i], offsets[bodyStarts[i]]);
            SetBranch(actions, caseJumps[i], offsets[switchMerge]);
        }
        SetBranch(actions, defaultJump, offsets[switchMerge]);
        SetBranch(actions, loopJump, offsets[0]);
        if (includeOuterLoopExits)
        {
            SetBranch(actions, outerLoopControlIfs[0], offsets[0]);
            SetBranch(actions, outerLoopControlIfs[1], offsets[loopExit]);
        }
        return actions;
    }

    private static int[] ComputeOffsets(IReadOnlyList<Avm1Action> actions)
    {
        var offsets = new int[actions.Count];
        var offset = 0;
        for (var i = 0; i < actions.Count; i++)
        {
            offsets[i] = offset;
            offset += ActionLength(actions[i]);
        }
        return offsets;
    }

    private static IReadOnlyList<Avm1Action> CreateLargeLiteralDataActions()
    {
        const int objectCount = 128;
        const int propertyCount = 128;
        var actions = new List<Avm1Action>(objectCount * 3);
        for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
        {
            var values = new PushValue[(propertyCount * 2) + 2];
            values[0] = PushValue.String($"data{objectIndex}");
            for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                var valueIndex = 1 + (propertyIndex * 2);
                values[valueIndex] = PushValue.String($"key{propertyIndex}");
                values[valueIndex + 1] = PushValue.String(
                    $"value{objectIndex}_{propertyIndex}");
            }
            values[^1] = PushValue.Integer(propertyCount);
            actions.Add(new ActionPush(values));
            actions.Add(new ActionInitObject());
            actions.Add(new ActionSetVariable());
        }
        return actions;
    }

    private static void SetBranch(
        List<Avm1Action> actions,
        int index,
        int targetOffset)
    {
        var offsets = ComputeOffsets(actions);
        var displacement = checked((short)(targetOffset - offsets[index] - ActionLength(actions[index])));
        actions[index] = actions[index] switch
        {
            ActionIf => new ActionIf(displacement),
            ActionJump => new ActionJump(displacement),
            _ => throw new ArgumentException("Action is not a branch.", nameof(index))
        };
    }

    private static Avm1SourceStatement GetOnlyEffectiveStatement(
        Avm1SourceArena arena,
        SourceStatementIndex index)
    {
        var statement = arena[index];
        while (statement.Kind is Avm1SourceStatementKind.Block)
        {
            statement.Children.Count.ShouldBe(1);
            statement = arena[arena.GetChild(statement, 0)];
        }
        return statement;
    }

    private static IReadOnlyList<Avm1SourceStatement> GetEffectiveStatements(
        Avm1SourceArena arena,
        SourceStatementIndex index)
    {
        var result = new List<Avm1SourceStatement>();
        var pending = new Stack<SourceStatementIndex>();
        pending.Push(index);
        while (pending.Count > 0)
        {
            var statement = arena[pending.Pop()];
            if (statement.Kind is not Avm1SourceStatementKind.Block)
            {
                result.Add(statement);
                continue;
            }

            for (var i = statement.Children.Count - 1; i >= 0; i--)
                pending.Push(arena.GetChild(statement, i));
        }
        return result;
    }

    private static IReadOnlyList<Avm1SourceStatement> GetAllStatements(
        Avm1SourceArena arena,
        SourceStatementIndex root)
    {
        var result = new List<Avm1SourceStatement>();
        var pending = new Stack<SourceStatementIndex>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var statement = arena[pending.Pop()];
            result.Add(statement);
            for (var i = statement.Children.Count - 1; i >= 0; i--)
                pending.Push(arena.GetChild(statement, i));
        }
        return result;
    }
}
