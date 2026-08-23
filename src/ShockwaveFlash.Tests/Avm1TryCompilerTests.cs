using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1TryCompilerTests
{
    [Fact]
    public void Throw_compiles_as_a_terminal_action()
    {
        var fixture = new SourceFixtureBuilder();
        var source = fixture.Build(fixture.Block(
            fixture.Throw(fixture.IntegerLiteral(42))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.Constant,
            Avm1MirInstructionKind.Throw
        ]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Throw
            ]);
        artifact.ActionBody.Actions[1].ShouldBeOfType<ActionThrow>();
    }

    [Fact]
    public void Try_catch_finally_compiles_as_three_nested_code_units()
    {
        var fixture = new SourceFixtureBuilder();
        var tryOrigin = fixture.Origin(0, 0, 10, 22);
        var catchOrigin = fixture.Origin(0, 1, 38, 43);
        var finallyOrigin = fixture.Origin(0, 2, 58, 62);
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var result = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "result");
        var cleanup = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "cleanup");
        var statement = fixture.Try(
            fixture.Block(fixture.Throw(
                fixture.StringLiteralAt("boom", tryOrigin))),
            fixture.Catch(
                error,
                fixture.Block(fixture.Assign(
                    result,
                    fixture.ReferenceAt(error, catchOrigin)))),
            fixture.Finally(
                fixture.Block(fixture.Assign(
                    cleanup,
                    fixture.BooleanLiteralAt(true, finallyOrigin)))));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TrySites.ShouldHaveSingleItem();
        artifact.StackLir.ShouldNotBeNull().TrySites.ShouldHaveSingleItem();
        var action = artifact.ActionBody.ShouldNotBeNull().Actions
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ActionTry>();
        action.Flags.ShouldBe(TryFlags.CatchBlock | TryFlags.FinallyBlock);
        action.CatchVariable.ShouldBe("error");
        action.CatchRegister.ShouldBe((byte)0);
        artifact.ActionBody.NestedBodies.Select(body => body.Kind).ShouldBe([
            Avm1NestedCodeUnitKind.TryBody,
            Avm1NestedCodeUnitKind.CatchBody,
            Avm1NestedCodeUnitKind.FinallyBody
        ]);
        artifact.ActionBody.NestedBodies[0].Body.Actions
            .Select(child => child.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Throw
            ]);

        var region = artifact.NestedTryRegions.ShouldHaveSingleItem();
        region.Plan.Flags.ShouldBe(action.Flags);
        region.Plan.CatchVariable.ShouldBe("error");
        region.CatchBody.ShouldNotBeNull();
        region.FinallyBody.ShouldNotBeNull();

        artifact.SourceMap.CodeUnits.Select(unit => unit.Kind).ShouldBe([
            Avm1CompiledCodeUnitKind.Method,
            Avm1CompiledCodeUnitKind.Try,
            Avm1CompiledCodeUnitKind.Catch,
            Avm1CompiledCodeUnitKind.Finally
        ]);
        var expectedOrigins = new Dictionary<
            Avm1CompiledCodeUnitKind,
            SourceOriginIndex>
        {
            [Avm1CompiledCodeUnitKind.Try] = tryOrigin,
            [Avm1CompiledCodeUnitKind.Catch] = catchOrigin,
            [Avm1CompiledCodeUnitKind.Finally] = finallyOrigin
        };
        foreach (var (kind, origin) in expectedOrigins)
        {
            var unit = artifact.SourceMap.CodeUnits.Single(candidate =>
                candidate.Kind == kind);
            var entry = artifact.SourceMap.Entries.First(candidate =>
                candidate.CodeUnit == unit.Index && candidate.Origin == origin);
            artifact.SourceMap.TryGetEntry(entry.ByteOffset, out var resolved)
                .ShouldBeTrue();
            resolved.CodeUnit.ShouldBe(unit.Index);
            resolved.Origin.ShouldBe(origin);
        }
    }

    [Fact]
    public void Basic_constant_pool_is_restored_after_a_try_region()
    {
        const string parentName =
            "very_long_parent_dynamic_name_used_around_a_try_region";
        const string childName =
            "very_long_child_dynamic_name_used_inside_a_try_region";
        var fixture = new SourceFixtureBuilder();
        var tryStatement = fixture.Try(
            fixture.Block(
                fixture.ExpressionStatement(fixture.DynamicName(childName)),
                fixture.ExpressionStatement(fixture.DynamicName(childName)),
                fixture.ExpressionStatement(fixture.DynamicName(childName)),
                fixture.ExpressionStatement(fixture.DynamicName(childName))),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.DynamicName(parentName)),
            fixture.ExpressionStatement(fixture.DynamicName(parentName)),
            tryStatement,
            fixture.ExpressionStatement(fixture.DynamicName(parentName)),
            fixture.Return(fixture.DynamicName(parentName))));

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var actions = artifact.ActionBody.ShouldNotBeNull().Actions;
        actions[0].ShouldBeOfType<ActionConstantPool>()
            .Constants.ShouldBe([parentName]);
        var tryIndex = actions.ToList().FindIndex(action => action is ActionTry);
        tryIndex.ShouldBeGreaterThanOrEqualTo(0);
        actions[tryIndex + 1].ShouldBeOfType<ActionConstantPool>()
            .Constants.ShouldBe([parentName]);
        actions.OfType<ActionConstantPool>().Count().ShouldBe(2);

        var tryBody = artifact.ActionBody.NestedBodies
            .Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody)
            .Body;
        tryBody.Actions[0].ShouldBeOfType<ActionConstantPool>()
            .Constants.ShouldBe([childName]);
        tryBody.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .Count(value => value is PushValue.PushValueConstant8)
            .ShouldBe(4);
    }

    [Fact]
    public void Return_inside_try_function_uses_native_finally_completion()
    {
        var fixture = new SourceFixtureBuilder();
        var cleanup = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "cleanup");
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Return(fixture.IntegerLiteral(7))),
            catchClause: null,
            fixture.Finally(
                fixture.Block(fixture.Assign(cleanup, fixture.BooleanLiteral(true)))));
        var function = fixture.Function(fixture.Block(tryStatement));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.NestedTryRegions.ShouldHaveSingleItem();
        var functionBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        functionBody.Actions.ShouldHaveSingleItem().ShouldBeOfType<ActionTry>();
        functionBody.NestedBodies.Select(body => body.Kind).ShouldBe([
            Avm1NestedCodeUnitKind.TryBody,
            Avm1NestedCodeUnitKind.FinallyBody
        ]);
        functionBody.NestedBodies[0].Body.Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
    }

    [Fact]
    public void Nested_try_regions_compile_recursively()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var inner = fixture.Try(
            fixture.Block(fixture.Throw(fixture.IntegerLiteral(1))),
            fixture.Catch(error, fixture.Block()),
            finallyClause: null);
        var outer = fixture.Try(
            fixture.Block(inner),
            catchClause: null,
            fixture.Finally(fixture.Block()));

        var artifact = Compile(fixture.Build(fixture.Block(outer)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var outerArtifact = artifact.NestedTryRegions.ShouldHaveSingleItem();
        outerArtifact.TryBody.NestedTryRegions.ShouldHaveSingleItem();
        var outerTryBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody)
            .Body;
        outerTryBody.Actions.ShouldHaveSingleItem().ShouldBeOfType<ActionTry>();
        outerTryBody.NestedBodies.Count.ShouldBe(2);
    }

    [Fact]
    public void Function_declared_inside_try_keeps_its_lexical_code_unit_owner()
    {
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.IntegerLiteral(1))));
        var statement = fixture.Try(
            fixture.Block(fixture.ExpressionStatement(
                fixture.FunctionLiteral(function))),
            catchClause: null,
            fixture.Finally(fixture.Block()));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var tryBody = artifact.NestedTryRegions.ShouldHaveSingleItem().TryBody;
        tryBody.NestedFunctions.ShouldHaveSingleItem().Plan.Function
            .ShouldBe(function);
        var assembledTryBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody)
            .Body;
        assembledTryBody.Actions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.DefineFunction2,
            ActionOpcode.Pop
        ]);
        assembledTryBody.NestedBodies.ShouldHaveSingleItem().Kind.ShouldBe(
            Avm1NestedCodeUnitKind.DefineFunction2Body);
    }

    [Fact]
    public void Function_register_count_includes_try_component_temporaries()
    {
        var fixture = new SourceFixtureBuilder();
        var conditional = fixture.Conditional(
            fixture.DynamicName("condition"),
            fixture.IntegerLiteral(1),
            fixture.IntegerLiteral(2));
        var statement = fixture.Try(
            fixture.Block(fixture.Return(conditional)),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var function = fixture.Function(fixture.Block(statement));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.ShouldBeEmpty();
        child.NestedTryRegions.ShouldHaveSingleItem().TryBody.StackLir
            .TemporaryRegisters.ShouldBe([1]);
        child.Plan.RegisterCount.ShouldBe((byte)2);
        artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>().RegisterCount.ShouldBe((byte)2);
    }

    [Fact]
    public void Function_catch_payload_uses_planned_private_register()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Throw(fixture.StringLiteral("boom"))),
            fixture.Catch(
                error,
                fixture.Block(fixture.Return(fixture.Reference(error)))),
            finallyClause: null);
        var function = fixture.Function(fixture.Block(tryStatement));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(error, 1)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)2);
        var region = child.NestedTryRegions.ShouldHaveSingleItem();
        region.Plan.Flags.ShouldBe(
            TryFlags.CatchBlock | TryFlags.CatchInRegister);
        region.Plan.CatchVariable.ShouldBeEmpty();
        region.Plan.CatchRegister.ShouldBe((byte)1);

        var functionBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        var action = functionBody.Actions.ShouldHaveSingleItem()
            .ShouldBeOfType<ActionTry>();
        action.Flags.ShouldBe(region.Plan.Flags);
        action.CatchVariable.ShouldBeEmpty();
        action.CatchRegister.ShouldBe((byte)1);
        var catchBody = functionBody.NestedBodies.Single(body =>
            body.Kind is Avm1NestedCodeUnitKind.CatchBody).Body;
        catchBody.Actions.Select(childAction => childAction.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.Return
        ]);
        GetPushedRegister(catchBody.Actions[0]).ShouldBe((byte)1);

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);
        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Catch_register_does_not_clobber_outer_live_value()
    {
        var fixture = new SourceFixtureBuilder();
        var outer = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "outer");
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Throw(fixture.StringLiteral("boom"))),
            fixture.Catch(error, fixture.Block(
                fixture.ExpressionStatement(fixture.Reference(error)))),
            finallyClause: null);
        var function = fixture.Function(fixture.Block(
            fixture.Declare(outer, fixture.IntegerLiteral(7)),
            tryStatement,
            fixture.Return(fixture.Reference(outer))));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(outer, 1),
            new Avm1SourceRegisterAllocation(error, 2)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)3);
        var region = child.NestedTryRegions.ShouldHaveSingleItem();
        region.Plan.Flags.ShouldBe(
            TryFlags.CatchBlock | TryFlags.CatchInRegister);
        region.Plan.CatchRegister.ShouldBe((byte)2);
    }

    [Fact]
    public void Catch_register_follows_component_scratch_prefix()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var catchValue = fixture.Conditional(
            fixture.DynamicName("condition"),
            fixture.Reference(error),
            fixture.IntegerLiteral(0));
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Throw(fixture.StringLiteral("boom"))),
            fixture.Catch(error, fixture.Block(fixture.Return(catchValue))),
            finallyClause: null);
        var function = fixture.Function(fixture.Block(tryStatement));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(error, 2)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)3);
        var region = child.NestedTryRegions.ShouldHaveSingleItem();
        region.Plan.CatchRegister.ShouldBe((byte)2);
        region.CatchBody.ShouldNotBeNull().StackLir.TemporaryRegisters
            .ShouldBe([1]);

        var functionBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        functionBody.Actions.ShouldHaveSingleItem()
            .ShouldBeOfType<ActionTry>().CatchRegister.ShouldBe((byte)2);
        var pushedRegisters = functionBody.NestedBodies.Single(body =>
                body.Kind is Avm1NestedCodeUnitKind.CatchBody)
            .Body.Actions.OfType<ActionPush>()
            .SelectMany(push => push.PushValues)
            .OfType<PushValue.PushValueRegister>()
            .Select(value => value.RegisterIndex)
            .ToArray();
        pushedRegisters.ShouldContain((byte)2);
    }

    [Fact]
    public void Captured_catch_symbol_stays_in_named_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var inner = fixture.Function(
            fixture.Block(fixture.Return(fixture.Reference(error))),
            captures: [error]);
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Throw(fixture.StringLiteral("boom"))),
            fixture.Catch(
                error,
                fixture.Block(fixture.Return(fixture.FunctionLiteral(inner)))),
            finallyClause: null);
        var outer = fixture.Function(fixture.Block(tryStatement));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(outer))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[error]
            .RequiresNamedActivation.ShouldBeTrue();
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        child.NestedTryRegions.ShouldHaveSingleItem().CatchBody.ShouldNotBeNull()
            .NestedFunctions.ShouldHaveSingleItem().Plan.Function.ShouldBe(inner);
        var action = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions.ShouldHaveSingleItem()
            .ShouldBeOfType<ActionTry>();
        action.Flags.ShouldBe(TryFlags.CatchBlock);
        action.CatchVariable.ShouldBe("error");
        action.CatchRegister.ShouldBe((byte)0);
    }

    [Fact]
    public void Eval_keeps_catch_symbol_in_named_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var evalCall = fixture.Call(
            fixture.DynamicName("eval"),
            fixture.StringLiteral("error"));
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Throw(fixture.StringLiteral("boom"))),
            fixture.Catch(error, fixture.Block(
                fixture.ExpressionStatement(evalCall),
                fixture.Return(fixture.Reference(error)))),
            finallyClause: null);
        var function = fixture.Function(fixture.Block(tryStatement));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[error]
            .RequiresNamedActivation.ShouldBeTrue();
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        var action = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions.ShouldHaveSingleItem()
            .ShouldBeOfType<ActionTry>();
        action.Flags.ShouldBe(TryFlags.CatchBlock);
        action.CatchVariable.ShouldBe("error");
    }

    [Fact]
    public void Dynamic_scope_keeps_catch_symbol_in_named_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var tryStatement = fixture.Try(
            fixture.Block(fixture.Throw(fixture.StringLiteral("boom"))),
            fixture.Catch(
                error,
                fixture.Block(fixture.Return(fixture.Reference(error)))),
            finallyClause: null);
        var function = fixture.Function(
            fixture.Block(tryStatement),
            Avm1SourceFunctionFlags.CapturesDynamicScope);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[error]
            .RequiresNamedActivation.ShouldBeTrue();
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        var action = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions.ShouldHaveSingleItem()
            .ShouldBeOfType<ActionTry>();
        action.Flags.ShouldBe(TryFlags.CatchBlock);
        action.CatchVariable.ShouldBe("error");
    }

    [Fact]
    public void Break_crosses_a_try_component_boundary_through_completion_dispatch()
    {
        var fixture = new SourceFixtureBuilder();
        var cleanup = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "cleanup");
        var protectedBreak = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block(
                fixture.Assign(cleanup, fixture.BooleanLiteral(true)))));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var rootSite = artifact.Mir.TrySites.ShouldHaveSingleItem();
        rootSite.VisibleControlScopes.Count.ShouldBe(1);
        var visibleScope = artifact.Mir.GetVisibleControlScope(rootSite, 0);
        visibleScope.Statement.ShouldBe(loop);
        visibleScope.Kind.ShouldBe(Avm1MirControlTargetKind.Loop);

        var completionVariable = artifact.Mir[rootSite.CompletionStorage.Name];
        var tryBody = artifact.NestedTryRegions.ShouldHaveSingleItem().TryBody;
        var completion = tryBody.Mir.CompletionSites.ShouldHaveSingleItem();
        completion.Kind.ShouldBe(Avm1MirCompletionKind.Break);
        completion.TargetStatement.ShouldBe(loop);
        tryBody.Mir[completion.Storage.Name].ShouldBe(completionVariable);
        tryBody.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1MirInstructionKind.SetPendingCompletion);
        tryBody.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldNotContain(Avm1MirInstructionKind.Break);

        var opcodes = artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .ToArray();
        opcodes.ShouldContain(ActionOpcode.DefineLocal);
        opcodes.ShouldContain(ActionOpcode.Try);
        opcodes.ShouldContain(ActionOpcode.StrictEquals);
        opcodes.ShouldContain(ActionOpcode.If);
        artifact.ActionBody.NestedBodies
            .Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody)
            .Body.Actions.Select(action => action.Opcode)
            .ShouldContain(ActionOpcode.SetVariable);
    }

    [Fact]
    public void Adobe_mode_emits_a_physical_parent_branch_without_completion_storage()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.DynamicName("condition"),
            fixture.Block(protectedBreak));
        var options = new Avm1CompilationOptions(7)
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic,
            ProtectedRegionExitMode =
                Avm1ProtectedRegionExitMode.AdobePhysicalBranch
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var rootSite = artifact.Mir.TrySites.ShouldHaveSingleItem();
        rootSite.UsesPhysicalParentBranches.ShouldBeTrue();
        rootSite.CompletionStorage.Kind.ShouldBe(
            Avm1MirCompletionStorageKind.None);
        var tryBody = artifact.NestedTryRegions.ShouldHaveSingleItem().TryBody;
        tryBody.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1MirInstructionKind.ExternalControlBranch);
        tryBody.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldNotContain(Avm1MirInstructionKind.SetPendingCompletion);
        var nested = artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody);
        nested.Body.Actions.ShouldContain(action => action is ActionJump);
        artifact.ActionBody.Actions.Any(action =>
            action is ActionPush push && push.PushValues.Any(value =>
                value is PushValue.PushValueString text &&
                text.Value.StartsWith(
                    "__avm1_completion",
                    StringComparison.Ordinal))).ShouldBeFalse();
    }

    [Fact]
    public void Adobe_mode_forwards_a_physical_branch_through_nested_try_units()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.Try(
            fixture.Block(inner),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.DynamicName("condition"),
            fixture.Block(outer));
        var options = new Avm1CompilationOptions(7)
        {
            ProtectedRegionExitMode =
                Avm1ProtectedRegionExitMode.AdobePhysicalBranch
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var outerArtifact = artifact.NestedTryRegions.ShouldHaveSingleItem();
        outerArtifact.TryBody.NestedTryRegions.ShouldHaveSingleItem()
            .TryBody.Mir.Instructions.Any(instruction =>
                instruction.Kind is
                    Avm1MirInstructionKind.ExternalControlBranch).ShouldBeTrue();
        var outerBody = artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody).Body;
        outerBody.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            outerBody.Diagnostics.Select(diagnostic => diagnostic.Message)));
        outerBody.NestedBodies.Any(body =>
            body.Kind is Avm1NestedCodeUnitKind.TryBody &&
            body.Body.Actions.Any(action => action is ActionJump)).ShouldBeTrue();
        outerBody.Actions.Count(action => action is ActionJump)
            .ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Adobe_mode_retains_semantic_completion_for_enumeration_cleanup()
    {
        var fixture = new SourceFixtureBuilder();
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var protectedBreak = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.ForIn(
            key,
            fixture.DynamicName("source"),
            fixture.Block(protectedBreak));
        var options = new Avm1CompilationOptions(7)
        {
            ProtectedRegionExitMode =
                Avm1ProtectedRegionExitMode.AdobePhysicalBranch
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var site = artifact.Mir.TrySites.ShouldHaveSingleItem();
        site.UsesPhysicalParentBranches.ShouldBeFalse();
        site.CompletionStorage.Kind.ShouldNotBe(
            Avm1MirCompletionStorageKind.None);
        artifact.NestedTryRegions.ShouldHaveSingleItem().TryBody.Mir.Instructions
            .Any(instruction => instruction.Kind is
                Avm1MirInstructionKind.SetPendingCompletion).ShouldBeTrue();
    }

    [Fact]
    public void Adobe_mode_retains_semantic_completion_across_with_scope()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var dynamicBody = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(protectedBreak));
        var loop = fixture.While(
            fixture.DynamicName("condition"),
            fixture.Block(dynamicBody));
        var options = new Avm1CompilationOptions(7)
        {
            ProtectedRegionExitMode =
                Avm1ProtectedRegionExitMode.AdobePhysicalBranch
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var tryArtifact = artifact.NestedWithRegions.ShouldHaveSingleItem().Body
            .NestedTryRegions.ShouldHaveSingleItem();
        tryArtifact.TryBody.Mir.Instructions.Any(instruction =>
            instruction.Kind is Avm1MirInstructionKind.SetPendingCompletion)
            .ShouldBeTrue();
        tryArtifact.TryBody.Mir.Instructions.Any(instruction =>
            instruction.Kind is Avm1MirInstructionKind.ExternalControlBranch)
            .ShouldBeFalse();
    }

    [Fact]
    public void Break_through_try_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block(
                fixture.Assign(
                    fixture.DynamicName("cleanup"),
                    fixture.BooleanLiteral(true)))));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Continue_crosses_a_try_component_boundary_with_a_distinct_token()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedContinue = fixture.Try(
            fixture.Block(fixture.Continue()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedContinue));

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var region = artifact.NestedTryRegions.ShouldHaveSingleItem();
        var completion = region.TryBody.Mir.CompletionSites.ShouldHaveSingleItem();
        completion.Kind.ShouldBe(Avm1MirCompletionKind.Continue);
        completion.TargetStatement.ShouldBe(loop);
        completion.Token.ShouldBeGreaterThan(0);
        region.TryBody.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldNotContain(Avm1MirInstructionKind.Continue);
    }

    [Fact]
    public void Continue_through_try_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedContinue = fixture.Try(
            fixture.Block(fixture.Continue()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedContinue));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Conditional_break_through_try_round_trips_with_normal_completion()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.Try(
            fixture.Block(
                fixture.If(
                    fixture.DynamicName("stop"),
                    fixture.Block(fixture.Break())),
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("work")))),
            catchClause: null,
            fixture.Finally(fixture.Block(
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("cleanup"))))));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Finally_completion_override_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block(fixture.Continue())));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(statement));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Labeled_break_propagates_through_nested_try_regions()
    {
        var fixture = new SourceFixtureBuilder();
        var outerLabel = fixture.AddLabel("outer");
        var inner = fixture.Try(
            fixture.Block(fixture.Break(outerLabel)),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.Try(
            fixture.Block(inner),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(outer),
            outerLabel);

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var outerSite = artifact.Mir.TrySites.ShouldHaveSingleItem();
        var outerVariable = artifact.Mir[outerSite.CompletionStorage.Name];
        var outerRegion = artifact.NestedTryRegions.ShouldHaveSingleItem();
        var innerSite = outerRegion.TryBody.Mir.TrySites.ShouldHaveSingleItem();
        var innerVariable = outerRegion.TryBody.Mir[innerSite.CompletionStorage.Name];
        innerVariable.ShouldNotBe(outerVariable);

        var innerRegion = outerRegion.TryBody.NestedTryRegions.ShouldHaveSingleItem();
        var innerWrite = innerRegion.TryBody.Mir.CompletionSites.ShouldHaveSingleItem();
        innerWrite.Kind.ShouldBe(Avm1MirCompletionKind.Break);
        innerWrite.TargetStatement.ShouldBe(loop);
        innerRegion.TryBody.Mir[innerWrite.Storage.Name].ShouldBe(innerVariable);

        var propagation = outerRegion.TryBody.Mir.CompletionSites.Single(site =>
            site.Kind is Avm1MirCompletionKind.Break);
        propagation.Kind.ShouldBe(Avm1MirCompletionKind.Break);
        propagation.TargetStatement.ShouldBe(loop);
        propagation.Token.ShouldBe(innerWrite.Token);
        outerRegion.TryBody.Mir[propagation.Storage.Name].ShouldBe(outerVariable);
        outerRegion.TryBody.Mir.CompletionSites.ShouldContain(site =>
            site.Kind == Avm1MirCompletionKind.Continue &&
            site.TargetStatement == loop);
    }

    [Fact]
    public void Labeled_break_through_nested_try_regions_round_trips()
    {
        var fixture = new SourceFixtureBuilder();
        var outerLabel = fixture.AddLabel("outer");
        var inner = fixture.Try(
            fixture.Block(fixture.Break(outerLabel)),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.Try(
            fixture.Block(inner),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(outer),
            outerLabel);
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Finally_control_completion_overrides_the_protected_completion()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block(fixture.Continue())));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(statement));

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var rootSite = artifact.Mir.TrySites.ShouldHaveSingleItem();
        var completionVariable = artifact.Mir[rootSite.CompletionStorage.Name];
        var region = artifact.NestedTryRegions.ShouldHaveSingleItem();
        var protectedCompletion = region.TryBody.Mir.CompletionSites
            .ShouldHaveSingleItem();
        var finallyCompletion = region.FinallyBody.ShouldNotBeNull()
            .Mir.CompletionSites.ShouldHaveSingleItem();
        protectedCompletion.Kind.ShouldBe(Avm1MirCompletionKind.Break);
        finallyCompletion.Kind.ShouldBe(Avm1MirCompletionKind.Continue);
        protectedCompletion.TargetStatement.ShouldBe(loop);
        finallyCompletion.TargetStatement.ShouldBe(loop);
        protectedCompletion.Token.ShouldNotBe(finallyCompletion.Token);
        region.TryBody.Mir[protectedCompletion.Storage.Name]
            .ShouldBe(completionVariable);
        region.FinallyBody.Mir[finallyCompletion.Storage.Name]
            .ShouldBe(completionVariable);
    }

    [Fact]
    public void Nested_try_in_finally_uses_separate_completion_storage()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.Try(
            fixture.Block(),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block(inner)));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(outer));

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var outerSite = artifact.Mir.TrySites.ShouldHaveSingleItem();
        var outerVariable = artifact.Mir[outerSite.CompletionStorage.Name];
        var outerRegion = artifact.NestedTryRegions.ShouldHaveSingleItem();
        var finallyBody = outerRegion.FinallyBody.ShouldNotBeNull();
        var innerSite = finallyBody.Mir.TrySites.ShouldHaveSingleItem();
        var innerVariable = finallyBody.Mir[innerSite.CompletionStorage.Name];
        innerVariable.ShouldNotBe(outerVariable);
        finallyBody.NestedTryRegions.ShouldHaveSingleItem();
    }

    [Fact]
    public void Break_through_try_cleans_the_enclosing_for_in_stream()
    {
        var fixture = new SourceFixtureBuilder();
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var protectedBreak = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.ForIn(
            key,
            fixture.DynamicName("source"),
            fixture.Block(protectedBreak));
        var options = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = 3,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(1, 2)
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.EndEnumeration).ShouldBe(1);
        var completion = artifact.NestedTryRegions.ShouldHaveSingleItem()
            .TryBody.Mir.CompletionSites.ShouldHaveSingleItem();
        completion.Kind.ShouldBe(Avm1MirCompletionKind.Break);
        completion.TargetStatement.ShouldBe(loop);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
    }

    [Fact]
    public void Completion_storage_does_not_collide_with_source_names()
    {
        var fixture = new SourceFixtureBuilder();
        fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "__avm1_completion");
        var statement = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(statement));

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var site = artifact.Mir.TrySites.ShouldHaveSingleItem();
        var variable = artifact.Mir[site.CompletionStorage.Name];
        variable.ShouldStartWith("__avm1_completion_1_", Case.Sensitive);
        variable.ShouldNotBe("__avm1_completion");
    }

    [Fact]
    public void Loop_fully_inside_try_can_break_locally()
    {
        var fixture = new SourceFixtureBuilder();
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(fixture.Break()));
        var statement = fixture.Try(
            fixture.Block(loop),
            catchClause: null,
            fixture.Finally(fixture.Block()));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.NestedTryRegions.ShouldHaveSingleItem()
            .TryBody.Mir.ControlTargets.ShouldHaveSingleItem();
    }

    [Fact]
    public void Throw_from_for_in_cleans_the_enumeration_stream_first()
    {
        var fixture = new SourceFixtureBuilder();
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var loop = fixture.ForIn(
            key,
            fixture.DynamicName("source"),
            fixture.Block(fixture.Throw(fixture.Reference(key))));
        var options = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = 3,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(1, 2)
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(2);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.EndEnumeration).ShouldBe(1);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Throw).ShouldBe(1);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .ShouldContain(action => action is ActionThrow);
    }

    [Fact]
    public void Try_component_temporaries_do_not_clobber_parent_for_in_state()
    {
        var fixture = new SourceFixtureBuilder();
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var result = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "result");
        var protectedAssignment = fixture.Try(
            fixture.Block(fixture.Assign(
                result,
                fixture.Conditional(
                    fixture.DynamicName("condition"),
                    fixture.IntegerLiteral(1),
                    fixture.IntegerLiteral(2)))),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var loop = fixture.ForIn(
            key,
            fixture.DynamicName("source"),
            fixture.Block(protectedAssignment));
        var options = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = 3,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(1, 2)
        };

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        artifact.NestedTryRegions.ShouldHaveSingleItem().TryBody.StackLir
            .TemporaryRegisters.ShouldBe([2]);
    }

    [Fact]
    public void Finally_return_is_emitted_as_an_overriding_native_completion()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.Try(
            fixture.Block(fixture.Return(fixture.IntegerLiteral(1))),
            catchClause: null,
            fixture.Finally(
                fixture.Block(fixture.Return(fixture.IntegerLiteral(2)))));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var nestedBodies = artifact.ActionBody.ShouldNotBeNull().NestedBodies;
        nestedBodies.Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody)
            .Body.Actions.Last().ShouldBeOfType<ShockwaveFlash.Avm1.Swf5.ActionReturn>();
        nestedBodies.Single(body => body.Kind is Avm1NestedCodeUnitKind.FinallyBody)
            .Body.Actions.Last().ShouldBeOfType<ShockwaveFlash.Avm1.Swf5.ActionReturn>();
    }

    [Fact]
    public void Try_compile_decompile_projection_is_hir_equivalent()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var statement = fixture.Try(
            fixture.Block(fixture.ExpressionStatement(
                fixture.Call(fixture.DynamicName("mayFail")))),
            fixture.Catch(
                error,
                fixture.Block(fixture.Return(fixture.Reference(error)))),
            finallyClause: null);
        var source = fixture.Build(fixture.Block(statement));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Try_compilation_requires_swf7()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.Try(
            fixture.Block(),
            catchClause: null,
            fixture.Finally(fixture.Block()));

        var artifact = Compile(
            fixture.Build(fixture.Block(statement)),
            swfVersion: 6);

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP121" &&
            diagnostic.Message.Contains("SWF 7", StringComparison.Ordinal));
    }

    [Fact]
    public void Standalone_throw_is_rejected_by_the_target_capability_gate()
    {
        var fixture = new SourceFixtureBuilder();
        var source = fixture.Build(fixture.Block(
            fixture.Throw(fixture.IntegerLiteral(1))));

        var artifact = Compile(source, swfVersion: 6);

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldNotBeNull().Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Message.Contains(
                "Throw requires SWF 7",
                StringComparison.Ordinal));
    }

    [Fact]
    public void With_compiles_as_a_nested_dynamic_scope_code_unit()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.ExpressionStatement(
                fixture.DynamicName("value"))));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.WithSites.ShouldHaveSingleItem();
        artifact.StackLir.ShouldNotBeNull().WithSites.ShouldHaveSingleItem();
        artifact.ActionBody.ShouldNotBeNull().Actions.Last()
            .ShouldBeOfType<ActionWith>();
        artifact.ActionBody.NestedBodies.ShouldHaveSingleItem().Kind.ShouldBe(
            Avm1NestedCodeUnitKind.WithBody);
        artifact.NestedWithRegions.ShouldHaveSingleItem().Body.Mir.Instructions
            .ShouldContain(instruction =>
                instruction.Effects.HasFlag(Avm1MirEffect.ReadsDynamicScope));
    }

    [Fact]
    public void Nested_with_regions_compile_recursively()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.With(
            fixture.DynamicName("inner"),
            fixture.Block());
        var outer = fixture.With(
            fixture.DynamicName("outer"),
            fixture.Block(inner));

        var artifact = Compile(fixture.Build(fixture.Block(outer)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var outerArtifact = artifact.NestedWithRegions.ShouldHaveSingleItem();
        outerArtifact.Body.NestedWithRegions.ShouldHaveSingleItem();
        artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .ShouldHaveSingleItem().Body.Actions.Last()
            .ShouldBeOfType<ActionWith>();
    }

    [Fact]
    public void With_body_does_not_spill_temporaries_through_dynamic_lookup()
    {
        var fixture = new SourceFixtureBuilder();
        var call = fixture.Call(
            fixture.DynamicName("fn"),
            fixture.IntegerLiteral(1),
            fixture.IntegerLiteral(2));
        var statement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.ExpressionStatement(call)));

        var artifact = Compile(
            fixture.Build(fixture.Block(statement)),
            RegisterOptions(registerCount: 0));

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP107" &&
            diagnostic.Message.Contains(
                "temporary storage",
                StringComparison.Ordinal), Describe(artifact));
        artifact.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1CMP103");
    }

    [Fact]
    public void Try_body_uses_a_distinct_activation_spill_namespace()
    {
        var fixture = new SourceFixtureBuilder();
        var call = fixture.Call(
            fixture.DynamicName("fn"),
            fixture.IntegerLiteral(1),
            fixture.IntegerLiteral(2));
        var statement = fixture.Try(
            fixture.Block(fixture.ExpressionStatement(call)),
            catchClause: null,
            fixture.Finally(fixture.Block()));

        var artifact = Compile(
            fixture.Build(fixture.Block(statement)),
            RegisterOptions(registerCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var tryBody = artifact.NestedTryRegions.ShouldHaveSingleItem().TryBody;
        tryBody.StackLir.TemporaryRegisters.ShouldBeEmpty();
        var names = tryBody.StackLir.TemporaryStorages.Select(storage =>
        {
            storage.Kind.ShouldBe(
                Avm1StackLirTemporaryStorageKind.ActivationName);
            return tryBody.StackLir[storage.Name];
        }).ToArray();
        names.ShouldBe([
            "__avm1_spill_try0_0",
            "__avm1_spill_try0_1"
        ]);
        artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .Single(body => body.Kind is Avm1NestedCodeUnitKind.TryBody)
            .Body.Actions.ShouldContain(action => action is ActionDefineLocal);
    }

    [Fact]
    public void Break_crosses_with_boundary_through_register_completion_storage()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.Break()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));
        var options = RegisterOptions(registerCount: 2);

        var artifact = Compile(fixture.Build(fixture.Block(loop)), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var site = artifact.Mir.WithSites.ShouldHaveSingleItem();
        site.CompletionStorage.Kind.ShouldBe(
            Avm1MirCompletionStorageKind.Register);
        site.CompletionStorage.Register.ShouldBe((byte)1);
        var completion = artifact.NestedWithRegions.ShouldHaveSingleItem()
            .Body.Mir.CompletionSites.ShouldHaveSingleItem();
        completion.Kind.ShouldBe(Avm1MirCompletionKind.Break);
        completion.TargetStatement.ShouldBe(loop);
        completion.Storage.ShouldBe(site.CompletionStorage);
        artifact.StackLir.ShouldNotBeNull().Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1StackLirInstructionKind.StoreRegister &&
            instruction.Register == 1);
        artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .Single(body => body.Kind is Avm1NestedCodeUnitKind.WithBody)
            .Body.Actions.ShouldContain(action => action is ActionStoreRegister);
    }

    [Fact]
    public void Break_through_with_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.Break()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source, RegisterOptions(registerCount: 2));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Conditional_break_through_with_round_trips_with_normal_completion()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(
                fixture.If(
                    fixture.DynamicName("stop"),
                    fixture.Block(fixture.Break())),
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("work")))));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source, RegisterOptions(registerCount: 2));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Continue_through_with_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedContinue = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.Continue()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedContinue));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source, RegisterOptions(registerCount: 2));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Try_inside_with_completion_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(inner));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(outer));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source, RegisterOptions(registerCount: 2));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Break_through_catch_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var error = fixture.AddSymbol(Avm1SourceSymbolKind.Catch, "error");
        var statement = fixture.Try(
            fixture.Block(fixture.ExpressionStatement(
                fixture.Call(fixture.DynamicName("mayFail")))),
            fixture.Catch(
                error,
                fixture.Block(fixture.Break())),
            fixture.Finally(fixture.Block(fixture.ExpressionStatement(
                fixture.Call(fixture.DynamicName("cleanup"))))));
        var loop = fixture.While(
            fixture.DynamicName("again"),
            fixture.Block(statement));
        var source = fixture.Build(fixture.Block(loop));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Break_through_try_to_switch_round_trips_to_structured_source()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.Try(
            fixture.Block(
                fixture.If(
                    fixture.DynamicName("stop"),
                    fixture.Block(fixture.Break())),
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("work")))),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var statement = fixture.Switch(
            fixture.DynamicName("kind"),
            fixture.SwitchCase(
                fixture.IntegerLiteral(1),
                protectedBreak,
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("afterCase"))),
                fixture.Break()),
            fixture.SwitchCase(
                fixture.IntegerLiteral(2),
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("alternative"))),
                fixture.Break()),
            fixture.SwitchDefault(
                fixture.ExpressionStatement(
                    fixture.Call(fixture.DynamicName("fallback"))),
                fixture.Break()));
        var source = fixture.Build(fixture.Block(statement));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Completion_transport_with_an_extra_read_is_not_folded()
    {
        var fixture = new SourceFixtureBuilder();
        var completion = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "__avm1_completion_4",
            Avm1SourceSymbolFlags.CompilerGenerated);
        var protectedStatement = fixture.Try(
            fixture.Block(
                fixture.Assign(completion, fixture.IntegerLiteral(10)),
                fixture.ExpressionStatement(fixture.Call(
                    fixture.DynamicName("observe"),
                    fixture.Reference(completion)))),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var guard = fixture.If(
            fixture.Binary(
                Avm1SourceOperator.StrictEqual,
                fixture.Reference(completion),
                fixture.IntegerLiteral(10)),
            fixture.Block(fixture.Break()));
        var source = fixture.Build(fixture.Block(fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(
                fixture.Declare(completion, fixture.IntegerLiteral(0)),
                protectedStatement,
                guard))));

        var normalized = Avm1SourceNormalizer.Normalize(
            source,
            CancellationToken.None);

        normalized.Diagnostics.ShouldBeEmpty();
        normalized.Method.GetAs2Text().ShouldContain("__avm1_completion_4");
        normalized.Method.GetAs2Text().ShouldContain("observe");
    }

    [Fact]
    public void Completion_transport_with_mismatched_token_kind_is_not_folded()
    {
        var fixture = new SourceFixtureBuilder();
        var completion = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "__avm1_completion_4",
            Avm1SourceSymbolFlags.CompilerGenerated);
        var protectedStatement = fixture.Try(
            fixture.Block(
                fixture.Assign(completion, fixture.IntegerLiteral(11))),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var guard = fixture.If(
            fixture.Binary(
                Avm1SourceOperator.StrictEqual,
                fixture.Reference(completion),
                fixture.IntegerLiteral(11)),
            fixture.Block(fixture.Break()));
        var source = fixture.Build(fixture.Block(fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(
                fixture.Declare(completion, fixture.IntegerLiteral(0)),
                protectedStatement,
                guard))));

        var normalized = Avm1SourceNormalizer.Normalize(
            source,
            CancellationToken.None);

        normalized.Diagnostics.ShouldBeEmpty();
        normalized.Method.GetAs2Text().ShouldContain("__avm1_completion_4");
        normalized.Method.GetAs2Text().ShouldContain("=== 11");
    }

    [Fact]
    public void With_completion_uses_the_automatic_legacy_register_plan()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(fixture.With(
                fixture.DynamicName("scope"),
                fixture.Block(fixture.Break()))));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.WithSites.ShouldHaveSingleItem()
            .CompletionStorage.Register.ShouldBe((byte)0);
        artifact.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1CMP122" ||
            diagnostic.Code == "AVM1CMP124");
    }

    [Fact]
    public void Try_inside_with_uses_the_next_completion_register()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(inner));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(outer));

        var artifact = Compile(
            fixture.Build(fixture.Block(loop)),
            RegisterOptions(registerCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var withSite = artifact.Mir.WithSites.ShouldHaveSingleItem();
        withSite.CompletionStorage.Register.ShouldBe((byte)1);
        var withBody = artifact.NestedWithRegions.ShouldHaveSingleItem().Body;
        var trySite = withBody.Mir.TrySites.ShouldHaveSingleItem();
        trySite.CompletionStorage.Kind.ShouldBe(
            Avm1MirCompletionStorageKind.Register);
        trySite.CompletionStorage.Register.ShouldBe((byte)2);
        withBody.Mir.CompletionSites.ShouldContain(site =>
            site.Storage.Kind == Avm1MirCompletionStorageKind.Register &&
            site.Storage.Register == 1);
    }

    [Fact]
    public void With_completion_register_does_not_clobber_for_in_state()
    {
        var fixture = new SourceFixtureBuilder();
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var protectedBreak = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.Break()));
        var loop = fixture.ForIn(
            key,
            fixture.DynamicName("source"),
            fixture.Block(protectedBreak));

        var artifact = Compile(
            fixture.Build(fixture.Block(loop)),
            RegisterOptions(registerCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([2]);
        artifact.Mir.WithSites.ShouldHaveSingleItem()
            .CompletionStorage.Register.ShouldBe((byte)1);
    }

    [Fact]
    public void Register_plan_reserves_descendant_completion_prefix_before_parent_temporaries()
    {
        var fixture = new SourceFixtureBuilder();
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var inner = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(inner));
        var loop = fixture.ForIn(
            key,
            fixture.DynamicName("source"),
            fixture.Block(outer));
        var source = fixture.Build(fixture.Block(loop));
        var options = RegisterOptions(registerCount: 3);

        var artifact = Compile(source, options);
        var repeated = Compile(source, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        repeated.Succeeded.ShouldBeTrue(Describe(repeated));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([3]);
        artifact.Mir.WithSites.ShouldHaveSingleItem()
            .CompletionStorage.Register.ShouldBe((byte)1);
        artifact.NestedWithRegions.ShouldHaveSingleItem().Body.Mir.TrySites
            .ShouldHaveSingleItem().CompletionStorage.Register.ShouldBe((byte)2);
        artifact.Bytecode.ToArray().ShouldBe(repeated.Bytecode.ToArray());
    }

    [Fact]
    public void Nested_function_register_count_uses_the_compact_automatic_plan()
    {
        var fixture = new SourceFixtureBuilder();
        var protectedBreak = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.Break()));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(protectedBreak));
        var function = fixture.Function(fixture.Block(loop));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterCount.ShouldBe((byte)2);
        child.Mir.WithSites.ShouldHaveSingleItem()
            .CompletionStorage.Register.ShouldBe((byte)1);
        artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>().RegisterCount.ShouldBe((byte)2);
    }

    [Fact]
    public void Nested_dynamic_completion_reports_register_depth_exhaustion()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.Try(
            fixture.Block(fixture.Break()),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var outer = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(inner));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(outer));

        var artifact = Compile(
            fixture.Build(fixture.Block(loop)),
            RegisterOptions(registerCount: 1));

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP124" &&
            diagnostic.Message.Contains(
                "requires 2 concurrent completion register",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Automatic_legacy_plan_reports_more_than_four_completion_levels()
    {
        var fixture = new SourceFixtureBuilder();
        var fifth = fixture.With(
            fixture.DynamicName("fifth"),
            fixture.Block(fixture.Break()));
        var fourth = fixture.Try(
            fixture.Block(fifth),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var third = fixture.With(
            fixture.DynamicName("third"),
            fixture.Block(fourth));
        var second = fixture.Try(
            fixture.Block(third),
            catchClause: null,
            fixture.Finally(fixture.Block()));
        var first = fixture.With(
            fixture.DynamicName("first"),
            fixture.Block(second));
        var loop = fixture.While(
            fixture.BooleanLiteral(true),
            fixture.Block(first));

        var artifact = Compile(fixture.Build(fixture.Block(loop)));

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP124" &&
            diagnostic.Message.Contains(
                "requires 5 concurrent completion register",
                StringComparison.Ordinal) &&
            diagnostic.Message.Contains("0..3 provides 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Function_inside_with_retains_dynamic_scope_effects()
    {
        var fixture = new SourceFixtureBuilder();
        var value = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "value");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.Reference(value))),
            Avm1SourceFunctionFlags.CapturesDynamicScope);
        var statement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.ExpressionStatement(
                fixture.FunctionLiteral(function))));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedWithRegions.ShouldHaveSingleItem().Body
            .NestedFunctions.ShouldHaveSingleItem();
        child.Mir.Instructions.Single(instruction =>
                instruction.Kind is Avm1MirInstructionKind.LoadLValue)
            .Effects.HasFlag(Avm1MirEffect.ReadsDynamicScope).ShouldBeTrue();
        artifact.ClosureAnalysis.ShouldNotBeNull()[child.Plan.CodeUnit].Flags
            .HasFlag(Avm1ClosureCodeUnitFlags.ExecutesInDynamicScope)
            .ShouldBeTrue();
    }

    [Fact]
    public void Bound_reference_inside_with_has_dynamic_scope_effects()
    {
        var fixture = new SourceFixtureBuilder();
        var value = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "value");
        var statement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.ExpressionStatement(
                fixture.Reference(value))));

        var artifact = Compile(fixture.Build(fixture.Block(statement)));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var load = artifact.NestedWithRegions.ShouldHaveSingleItem().Body.Mir
            .Instructions.Single(instruction =>
                instruction.Kind is Avm1MirInstructionKind.LoadLValue);
        load.Effects.HasFlag(Avm1MirEffect.ReadsDynamicScope).ShouldBeTrue();
        load.Effects.HasFlag(Avm1MirEffect.MayInvokeUserCode).ShouldBeTrue();
        load.Effects.HasFlag(Avm1MirEffect.MayThrow).ShouldBeTrue();
    }

    [Fact]
    public void With_compile_decompile_projection_is_hir_equivalent()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.ExpressionStatement(
                fixture.DynamicName("value"))));
        var source = fixture.Build(fixture.Block(statement));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(source, projected, equivalence));
    }

    [Fact]
    public void Basic_optimization_runs_in_with_and_try_component_code_units()
    {
        var fixture = new SourceFixtureBuilder();
        var withStatement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block(fixture.ExpressionStatement(fixture.Binary(
                Avm1SourceOperator.Add,
                fixture.IntegerLiteral(1),
                fixture.IntegerLiteral(2)))));
        var tryStatement = fixture.Try(
            fixture.Block(fixture.ExpressionStatement(fixture.Binary(
                Avm1SourceOperator.Multiply,
                fixture.IntegerLiteral(3),
                fixture.IntegerLiteral(4)))),
            catchClause: null,
            fixture.Finally(fixture.Block(
                fixture.ExpressionStatement(fixture.Binary(
                    Avm1SourceOperator.Subtract,
                    fixture.IntegerLiteral(7),
                    fixture.IntegerLiteral(5))))));

        var artifact = Compile(
            fixture.Build(fixture.Block(withStatement, tryStatement)),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.NestedWithRegions.ShouldHaveSingleItem()
            .Body.Mir.Instructions.ShouldBeEmpty();
        var tryArtifact = artifact.NestedTryRegions.ShouldHaveSingleItem();
        tryArtifact.TryBody.Mir.Instructions.ShouldBeEmpty();
        tryArtifact.FinallyBody.ShouldNotBeNull()
            .Mir.Instructions.ShouldBeEmpty();
    }

    [Fact]
    public void With_compilation_requires_swf5()
    {
        var fixture = new SourceFixtureBuilder();
        var statement = fixture.With(
            fixture.DynamicName("scope"),
            fixture.Block());

        var artifact = Compile(
            fixture.Build(fixture.Block(statement)),
            swfVersion: 4);

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP123" &&
            diagnostic.Message.Contains("SWF 5", StringComparison.Ordinal));
    }

    private static Avm1MethodArtifact Compile(
        Avm1SourceMethod source,
        byte swfVersion = 7) =>
        new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(swfVersion));

    private static Avm1MethodArtifact Compile(
        Avm1SourceMethod source,
        Avm1CompilationOptions options) =>
        new Avm1Compiler().CompileMethod(source, options);

    private static Avm1CompilationOptions RegisterOptions(byte registerCount) =>
        new(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = registerCount == 0
                ? (byte)0
                : checked((byte)(registerCount + 1)),
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(
                1,
                registerCount)
        };

    private static string Describe(Avm1MethodArtifact artifact) =>
        string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string DescribeEquivalence(
        Avm1SourceMethod expected,
        Avm1SourceMethod actual,
        Avm1SourceEquivalenceResult equivalence) =>
        string.Join(
            Environment.NewLine,
            equivalence.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}")) +
        Environment.NewLine + "Expected:" + Environment.NewLine +
        expected.GetAs2Text() +
        Environment.NewLine + "Actual:" + Environment.NewLine +
        actual.GetAs2Text();

    private sealed class SourceFixtureBuilder
    {
        private readonly SourceTypeIndex _unknownType;

        public SourceFixtureBuilder()
        {
            _unknownType = Builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        }

        private Avm1SourceArena.Builder Builder { get; } = new();

        public SourceOriginIndex Origin(
            int recoveryUnit,
            int recoveryNode,
            int startOffset,
            int endOffset) =>
            Builder.AddOrigin(
                recoveryUnit,
                recoveryNode,
                startOffset,
                endOffset);

        public SourceSymbolIndex AddSymbol(
            Avm1SourceSymbolKind kind,
            string name,
            Avm1SourceSymbolFlags flags = Avm1SourceSymbolFlags.None) =>
            Builder.AddSymbol(
                kind,
                name,
                SourceTypeIndex.Invalid,
                _unknownType,
                flags);

        public SourceExpressionIndex Reference(SourceSymbolIndex symbol) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: symbol);

        public SourceExpressionIndex ReferenceAt(
            SourceSymbolIndex symbol,
            SourceOriginIndex origin) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: symbol,
                origin: origin);

        public SourceExpressionIndex DynamicName(string name) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.DynamicName,
                name: Builder.InternString(name));

        public SourceExpressionIndex Call(
            SourceExpressionIndex target,
            params SourceExpressionIndex[] arguments)
        {
            var children = new SourceExpressionIndex[arguments.Length + 1];
            children[0] = target;
            arguments.CopyTo(children, 1);
            return Builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children: children);
        }

        public SourceExpressionIndex IntegerLiteral(int value) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: Builder.AddLiteral(
                    Avm1SourceLiteralKind.Integer,
                    integerValue: value));

        public SourceExpressionIndex StringLiteral(string value) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: Builder.AddLiteral(
                    Avm1SourceLiteralKind.String,
                    stringValue: Builder.InternString(value)));

        public SourceExpressionIndex StringLiteralAt(
            string value,
            SourceOriginIndex origin) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: Builder.AddLiteral(
                    Avm1SourceLiteralKind.String,
                    stringValue: Builder.InternString(value)),
                origin: origin);

        public SourceExpressionIndex BooleanLiteral(bool value) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: Builder.AddLiteral(
                    Avm1SourceLiteralKind.Boolean,
                    booleanValue: value));

        public SourceExpressionIndex BooleanLiteralAt(
            bool value,
            SourceOriginIndex origin) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: Builder.AddLiteral(
                    Avm1SourceLiteralKind.Boolean,
                    booleanValue: value),
                origin: origin);

        public SourceExpressionIndex Conditional(
            SourceExpressionIndex condition,
            SourceExpressionIndex whenTrue,
            SourceExpressionIndex whenFalse) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Conditional,
                children: [condition, whenTrue, whenFalse]);

        public SourceExpressionIndex Binary(
            Avm1SourceOperator @operator,
            SourceExpressionIndex left,
            SourceExpressionIndex right) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                @operator,
                children: [left, right]);

        public SourceExpressionIndex FunctionLiteral(
            SourceFunctionIndex function) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.FunctionLiteral,
                function: function);

        public SourceFunctionIndex Function(
            SourceStatementIndex body,
            Avm1SourceFunctionFlags flags = Avm1SourceFunctionFlags.None,
            IReadOnlyList<SourceSymbolIndex>? captures = null) =>
            Builder.AddFunction(
                SourceSymbolIndex.Invalid,
                body,
                flags,
                SourceOriginIndex.Invalid,
                captures: captures);

        public SourceStatementIndex ExpressionStatement(
            SourceExpressionIndex expression) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression);

        public SourceStatementIndex Declare(
            SourceSymbolIndex symbol,
            SourceExpressionIndex? initializer = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: initializer,
                symbol: symbol);

        public SourceStatementIndex Assign(
            SourceSymbolIndex target,
            SourceExpressionIndex value) =>
            Assign(Reference(target), value);

        public SourceStatementIndex Assign(
            SourceExpressionIndex target,
            SourceExpressionIndex value) =>
            ExpressionStatement(Builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children: [target, value]));

        public SourceStatementIndex Return(
            SourceExpressionIndex? expression = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expression);

        public SourceStatementIndex Throw(SourceExpressionIndex expression) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Throw,
                expression: expression);

        public SourceLabelIndex AddLabel(string name) => Builder.AddLabel(name);

        public SourceStatementIndex Break(SourceLabelIndex? label = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Break,
                label: label);

        public SourceStatementIndex Continue(SourceLabelIndex? label = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Continue,
                label: label);

        public SourceStatementIndex If(
            SourceExpressionIndex condition,
            SourceStatementIndex whenTrue,
            SourceStatementIndex? whenFalse = null)
        {
            var children = whenFalse.HasValue
                ? new[] { whenTrue, whenFalse.Value }
                : new[] { whenTrue };
            return Builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: condition,
                children: children);
        }

        public SourceStatementIndex While(
            SourceExpressionIndex condition,
            SourceStatementIndex body,
            SourceLabelIndex? label = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.While,
                expression: condition,
                label: label,
                children: [body]);

        public SourceStatementIndex ForIn(
            SourceSymbolIndex key,
            SourceExpressionIndex collection,
            SourceStatementIndex body) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.ForIn,
                expression: Reference(key),
                secondaryExpression: collection,
                flags: Avm1SourceStatementFlags.ForInDeclaresKey,
                children: [body]);

        public SourceStatementIndex Switch(
            SourceExpressionIndex selector,
            params SourceStatementIndex[] clauses) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Switch,
                expression: selector,
                children: clauses);

        public SourceStatementIndex SwitchCase(
            SourceExpressionIndex value,
            params SourceStatementIndex[] statements) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.SwitchCase,
                expression: value,
                children: statements);

        public SourceStatementIndex SwitchDefault(
            params SourceStatementIndex[] statements) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.SwitchDefault,
                children: statements);

        public SourceStatementIndex With(
            SourceExpressionIndex scopeObject,
            SourceStatementIndex body) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.With,
                expression: scopeObject,
                children: [body]);

        public SourceStatementIndex Try(
            SourceStatementIndex tryBody,
            SourceStatementIndex? catchClause,
            SourceStatementIndex? finallyClause)
        {
            var children = new List<SourceStatementIndex> { tryBody };
            if (catchClause.HasValue)
                children.Add(catchClause.Value);
            if (finallyClause.HasValue)
                children.Add(finallyClause.Value);
            return Builder.AddStatement(
                Avm1SourceStatementKind.Try,
                children: children);
        }

        public SourceStatementIndex Catch(
            SourceSymbolIndex symbol,
            SourceStatementIndex body) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.CatchClause,
                symbol: symbol,
                children: [body]);

        public SourceStatementIndex Finally(SourceStatementIndex body) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.FinallyClause,
                children: [body]);

        public SourceStatementIndex Block(
            params SourceStatementIndex[] statements) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: statements);

        public Avm1SourceMethod Build(SourceStatementIndex body) =>
            new(Builder.ToArena(), body, [], []);
    }

    private static byte GetPushedRegister(Avm1Action action) =>
        action.ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueRegister>()
            .RegisterIndex;
}
