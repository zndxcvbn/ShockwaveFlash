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

public sealed class Avm1FunctionCompilerTests
{
    [Fact]
    public void Anonymous_function_compiles_to_define_function2_child_code_unit()
    {
        var fixture = new SourceFixtureBuilder();
        var functionOrigin = fixture.Origin(0, 0, 4, 48);
        var childOrigin = fixture.Origin(0, 1, 25, 37);
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var function = fixture.Function(
            fixture.Block(fixture.Return(
                fixture.ReferenceAt(parameter, childOrigin))),
            parameters: [parameter]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(
                fixture.FunctionLiteralAt(function, functionOrigin))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.FunctionSites.ShouldHaveSingleItem().IsDeclaration
            .ShouldBeFalse();
        artifact.StackLir.ShouldNotBeNull().Functions.ShouldHaveSingleItem()
            .IsDeclaration.ShouldBeFalse();
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.DefineFunction2,
                ActionOpcode.Pop
            ]);

        var action = artifact.ActionBody.Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Name.ShouldBeEmpty();
        action.RegisterCount.ShouldBe((byte)2);
        action.Flags.ShouldBe(
            FunctionFlags.SuppressThis |
            FunctionFlags.SuppressArguments |
            FunctionFlags.SuppressSuper);
        action.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(1, "value"));

        var nestedBody = artifact.ActionBody.NestedBodies.ShouldHaveSingleItem();
        nestedBody.Kind.ShouldBe(
            Avm1NestedCodeUnitKind.DefineFunction2Body);
        nestedBody.Body.Succeeded.ShouldBeTrue();
        action.Body.Span.SequenceEqual(nestedBody.Body.Bytes.Span).ShouldBeTrue();
        nestedBody.Body.Actions.Select(child => child.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.Return
        ]);
        GetPushedRegister(nestedBody.Body.Actions[0]).ShouldBe((byte)1);

        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.Function.ShouldBe(function);
        child.Plan.IsDeclaration.ShouldBeFalse();
        child.Plan.Name.ShouldBeEmpty();
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(1, "value"));
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(parameter, 1)
        ]);

        artifact.SourceMap.CodeUnits.Select(unit => unit.Kind).ShouldBe([
            Avm1CompiledCodeUnitKind.Method,
            Avm1CompiledCodeUnitKind.Function
        ]);
        var childUnit = artifact.SourceMap.CodeUnits[1];
        childUnit.Parent.ShouldBe(artifact.SourceMap.CodeUnits[0].Index);
        childUnit.ByteOffset.ShouldBe(nestedBody.ByteOffset);
        childUnit.ByteLength.ShouldBe(nestedBody.Body.Bytes.Length);
        var childEntries = artifact.SourceMap.Entries
            .Where(entry => entry.CodeUnit == childUnit.Index)
            .ToArray();
        childEntries.ShouldNotBeEmpty();
        childEntries.ShouldAllBe(entry => entry.Origin == childOrigin);
        artifact.SourceMap.TryGetEntry(
            childEntries[0].ByteOffset,
            out var resolved).ShouldBeTrue();
        resolved.CodeUnit.ShouldBe(childUnit.Index);
        resolved.Origin.ShouldBe(childOrigin);
    }

    [Fact]
    public void Basic_optimization_runs_in_nested_function_code_units()
    {
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Binary(
                Avm1SourceOperator.Add,
                fixture.IntegerLiteral(2),
                fixture.IntegerLiteral(3)))));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.Constant,
            Avm1MirInstructionKind.Return
        ]);
        var body = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        body.Actions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.Return
        ]);
        GetPushedInteger(body.Actions[0]).ShouldBe(5);
    }

    [Fact]
    public void Adobe_evaluation_mode_propagates_to_nested_function_code_units()
    {
        var fixture = new SourceFixtureBuilder();
        var invocation = fixture.Call(
            fixture.DynamicName("sink"),
            fixture.DynamicName("left"),
            fixture.DynamicName("right"));
        var function = fixture.Function(
            fixture.Block(fixture.Return(invocation)));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                ExpressionEvaluationMode =
                    Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.NestedFunctions.ShouldHaveSingleItem().Mir.CallSites
            .ShouldHaveSingleItem().ArgumentEvaluationOrder.ShouldBe(
                Avm1MirEvaluationOrder.Reverse);
        artifact.NestedFunctions.ShouldHaveSingleItem().Mir.CallSites
            .ShouldHaveSingleItem().TargetEvaluationOrder.ShouldBe(
                Avm1MirInvocationTargetOrder.AfterArguments);
    }

    [Fact]
    public void Basic_constant_pool_is_owned_by_the_nested_function_code_unit()
    {
        const string name =
            "very_long_dynamic_name_repeated_in_a_nested_function";
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(fixture.Block(
            fixture.ExpressionStatement(fixture.DynamicName(name)),
            fixture.ExpressionStatement(fixture.DynamicName(name)),
            fixture.ExpressionStatement(fixture.DynamicName(name)),
            fixture.Return(fixture.DynamicName(name))));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action is ActionConstantPool);
        var body = artifact.ActionBody.NestedBodies
            .ShouldHaveSingleItem().Body;
        body.Actions[0].ShouldBeOfType<ActionConstantPool>()
            .Constants.ShouldBe([name]);
        body.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .Count(value => value is PushValue.PushValueConstant8)
            .ShouldBe(4);

        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals[name] = new Avm1Number(27);

        machine.Execute(body.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(27d);
    }

    [Fact]
    public void Basic_optimization_prunes_nested_function_phi_edges()
    {
        var fixture = new SourceFixtureBuilder();
        var conditional = fixture.Conditional(
            fixture.IntegerLiteral(1),
            fixture.IntegerLiteral(2),
            fixture.Call(fixture.DynamicName("unreachable")));
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Binary(
                Avm1SourceOperator.Add,
                conditional,
                fixture.IntegerLiteral(3)))));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Mir.PhiSites.ShouldBeEmpty();
        child.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Invoke ||
            instruction.Kind == Avm1MirInstructionKind.Binary ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        var body = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        body.Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.CallFunction ||
            action.Opcode == ActionOpcode.Add2);
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(body.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(5d);
    }

    [Fact]
    public void Named_function_declaration_has_no_stack_result()
    {
        var fixture = new SourceFixtureBuilder();
        var symbol = fixture.AddSymbol(Avm1SourceSymbolKind.Function, "helper");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.IntegerLiteral(42))),
            Avm1SourceFunctionFlags.Declaration,
            nameSymbol: symbol);
        var source = fixture.Build(fixture.Block(
            fixture.FunctionDeclaration(symbol, function)));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().MaximumStackDepth.ShouldBe(0);
        var action = artifact.ActionBody.ShouldNotBeNull().Actions
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Name.ShouldBe("helper");
        artifact.ActionBody.MaximumStackDepth.ShouldBe(1);
        artifact.NestedFunctions.ShouldHaveSingleItem().Plan.IsDeclaration
            .ShouldBeTrue();
    }

    [Fact]
    public void Captured_outer_local_remains_in_the_named_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var outer = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "outer");
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Reference(outer))));
        var source = fixture.Build(fixture.Block(
            fixture.Declare(outer, fixture.IntegerLiteral(9)),
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[outer]
            .RequiresNamedActivation.ShouldBeTrue();
        var childBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        var childActions = childBody.Actions;
        childActions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.GetVariable,
            ActionOpcode.Return
        ]);
        GetPushedString(childActions[0]).ShouldBe("outer");
    }

    [Fact]
    public void Nested_function_tree_is_compiled_and_verified_recursively()
    {
        var fixture = new SourceFixtureBuilder();
        var inner = fixture.Function(fixture.Block(
            fixture.Return(fixture.IntegerLiteral(1))));
        var outer = fixture.Function(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(inner)),
            fixture.Return(fixture.IntegerLiteral(2))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(outer))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var outerArtifact = artifact.NestedFunctions.ShouldHaveSingleItem();
        outerArtifact.Plan.Function.ShouldBe(outer);
        outerArtifact.NestedFunctions.ShouldHaveSingleItem().Plan.Function
            .ShouldBe(inner);

        var outerBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body;
        outerBody.Actions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.DefineFunction2,
            ActionOpcode.Pop,
            ActionOpcode.Push,
            ActionOpcode.Return
        ]);
        var innerBody = outerBody.NestedBodies.ShouldHaveSingleItem().Body;
        innerBody.Succeeded.ShouldBeTrue();
        innerBody.Actions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.Return
        ]);
    }

    [Fact]
    public void Child_control_flow_allocates_private_temporary_registers()
    {
        var fixture = new SourceFixtureBuilder();
        var value = fixture.Conditional(
            fixture.DynamicName("condition"),
            fixture.IntegerLiteral(7),
            fixture.IntegerLiteral(9));
        var function = fixture.Function(fixture.Block(fixture.Return(value)));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.ShouldBe([1]);
        child.Plan.RegisterCount.ShouldBe((byte)2);
        var functionAction = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        functionAction.RegisterCount.ShouldBe((byte)2);
        artifact.ActionBody.NestedBodies.ShouldHaveSingleItem().Body.Actions
            .OfType<ActionStoreRegister>()
            .Select(action => action.RegisterNumber).ShouldBe([1, 1]);
    }

    [Fact]
    public void Register_eligible_local_uses_private_register_storage()
    {
        var fixture = new SourceFixtureBuilder();
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var function = fixture.Function(fixture.Block(
            fixture.Declare(local, fixture.IntegerLiteral(4)),
            fixture.Return(fixture.Reference(local))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(local, 1)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)2);
        child.Mir.LValues.ShouldContain(lValue =>
            lValue.Kind == Avm1MirLValueKind.Register &&
            lValue.Symbol == local &&
            lValue.Register == 1);

        var actions = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions;
        actions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.StoreRegister,
            ActionOpcode.Pop,
            ActionOpcode.Push,
            ActionOpcode.Return
        ]);
        actions[1].ShouldBeOfType<ActionStoreRegister>().RegisterNumber
            .ShouldBe((byte)1);
        GetPushedRegister(actions[3]).ShouldBe((byte)1);

        var machine = new Avm1Machine(swfVersion: 7);
        machine.Execute(actions, strict: true);
        machine.ReturnValue.AsNumber.ShouldBe(4d);
    }

    [Fact]
    public void Disjoint_source_locals_reuse_register_and_clear_undefined()
    {
        var fixture = new SourceFixtureBuilder();
        var first = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "first");
        var second = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "second");
        var function = fixture.Function(fixture.Block(
            fixture.Declare(first, fixture.IntegerLiteral(4)),
            fixture.ExpressionStatement(fixture.Reference(first)),
            fixture.Declare(second),
            fixture.Return(fixture.Reference(second))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(first, 1),
            new Avm1SourceRegisterAllocation(second, 1)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)2);

        var actions = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions;
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Execute(actions, strict: true);
        machine.ReturnValue.IsUndefined.ShouldBeTrue();
    }

    [Fact]
    public void Source_preserving_mode_does_not_coalesce_disjoint_locals()
    {
        var fixture = new SourceFixtureBuilder();
        var first = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "first");
        var second = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "second");
        var function = fixture.Function(fixture.Block(
            fixture.Declare(first, fixture.IntegerLiteral(4)),
            fixture.ExpressionStatement(fixture.Reference(first)),
            fixture.Declare(second, fixture.IntegerLiteral(5)),
            fixture.Return(fixture.Reference(second))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                SourceRegisterAllocationMode =
                    Avm1SourceRegisterAllocationMode.PreserveSourceSymbols
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(first, 1),
            new Avm1SourceRegisterAllocation(second, 2)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)3);
    }

    [Fact]
    public void Overlapping_source_locals_use_distinct_registers()
    {
        var fixture = new SourceFixtureBuilder();
        var left = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "left");
        var right = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "right");
        var function = fixture.Function(fixture.Block(
            fixture.Declare(left, fixture.IntegerLiteral(4)),
            fixture.Declare(right, fixture.IntegerLiteral(5)),
            fixture.Return(fixture.Binary(
                Avm1SourceOperator.Add,
                fixture.Reference(left),
                fixture.Reference(right)))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(left, 1),
            new Avm1SourceRegisterAllocation(right, 2)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)3);
    }

    [Fact]
    public void Mutually_exclusive_branch_locals_do_not_reuse_parameter_register()
    {
        var fixture = new SourceFixtureBuilder();
        var condition = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "condition");
        var whenTrue = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "whenTrue");
        var whenFalse = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "whenFalse");
        var function = fixture.Function(
            fixture.Block(fixture.If(
                fixture.Reference(condition),
                fixture.Block(
                    fixture.Declare(whenTrue, fixture.IntegerLiteral(1)),
                    fixture.Return(fixture.Reference(whenTrue))),
                fixture.Block(
                    fixture.Declare(whenFalse, fixture.IntegerLiteral(2)),
                    fixture.Return(fixture.Reference(whenFalse))))),
            parameters: [condition]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(condition, 1),
            new Avm1SourceRegisterAllocation(whenTrue, 2),
            new Avm1SourceRegisterAllocation(whenFalse, 2)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)3);
        child.Plan.Parameters.ShouldHaveSingleItem().ShouldBe(
            new FunctionParameter(1, "condition"));
    }

    [Fact]
    public void Loop_live_out_prevents_unsafe_source_register_reuse()
    {
        var fixture = new SourceFixtureBuilder();
        var retained = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "retained");
        var iteration = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "iteration");
        var function = fixture.Function(fixture.Block(
            fixture.Declare(retained, fixture.IntegerLiteral(7)),
            fixture.While(
                fixture.DynamicName("condition"),
                fixture.Block(
                    fixture.Declare(iteration, fixture.IntegerLiteral(1)),
                    fixture.ExpressionStatement(fixture.Reference(iteration)))),
            fixture.Return(fixture.Reference(retained))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(retained, 1),
            new Avm1SourceRegisterAllocation(iteration, 2)
        ]);
        child.Plan.RegisterCount.ShouldBe((byte)3);
    }

    [Fact]
    public void For_in_body_liveness_reaches_values_declared_in_the_preheader()
    {
        var fixture = new SourceFixtureBuilder();
        var sourceObject = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "source");
        var first = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "first");
        var second = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "second");
        var third = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "third");
        var key = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "key");
        var loop = fixture.ForIn(
            key,
            fixture.Member(fixture.Reference(sourceObject), "items"),
            fixture.Block(fixture.Return(fixture.Array(
                fixture.Reference(first),
                fixture.Reference(second),
                fixture.Reference(third)))));
        var function = fixture.Function(
            fixture.Block(
                fixture.Declare(
                    first,
                    fixture.Member(fixture.Reference(sourceObject), "first")),
                fixture.Declare(
                    second,
                    fixture.Member(fixture.Reference(sourceObject), "second")),
                fixture.Declare(
                    third,
                    fixture.Member(fixture.Reference(sourceObject), "third")),
                loop),
            parameters: [sourceObject]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        var registers = child.Plan.RegisterAllocations
            .Where(allocation =>
                allocation.Symbol == first ||
                allocation.Symbol == second ||
                allocation.Symbol == third)
            .Select(allocation => allocation.Register)
            .ToArray();
        registers.Length.ShouldBe(3);
        registers.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Source_registers_follow_measured_stack_lir_scratch_registers()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "selected");
        var initializer = fixture.Conditional(
            fixture.DynamicName("condition"),
            fixture.Reference(parameter),
            fixture.IntegerLiteral(9));
        var function = fixture.Function(
            fixture.Block(
                fixture.Declare(local, initializer),
                fixture.Return(fixture.Reference(local))),
            parameters: [parameter]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.ShouldBe([1]);
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(parameter, 2),
            new Avm1SourceRegisterAllocation(local, 3)
        ]);
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(2, "value"));
        child.Plan.RegisterCount.ShouldBe((byte)4);
    }

    [Fact]
    public void Arguments_observation_keeps_parameters_in_named_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var function = fixture.Function(
            fixture.Block(
                fixture.ExpressionStatement(fixture.DynamicName("arguments")),
                fixture.Return(fixture.Reference(parameter))),
            parameters: [parameter]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[parameter]
            .RequiresNamedActivation.ShouldBeTrue();
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(0, "value"));
        GetPushedStrings(artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions)
            .ShouldContain("value");
    }

    [Fact]
    public void Eval_keeps_addressable_locals_in_named_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var evalCall = fixture.Call(
            fixture.DynamicName("eval"),
            fixture.StringLiteral("local"));
        var function = fixture.Function(fixture.Block(
            fixture.Declare(local, fixture.IntegerLiteral(4)),
            fixture.Return(evalCall)));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[local]
            .RequiresNamedActivation.ShouldBeTrue();
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        child.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.EvaluateName);
        child.Mir.CallSites.ShouldBeEmpty();
        var actions = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions;
        GetPushedStrings(actions).ShouldContain("local");
        actions.ShouldContain(action => action is ActionGetVariable);
        actions.ShouldNotContain(action => action is ActionCallFunction);
    }

    [Fact]
    public void Eval_in_a_nested_function_keeps_an_outer_local_addressable()
    {
        var fixture = new SourceFixtureBuilder();
        var outer = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "outer");
        var evalCall = fixture.Call(
            fixture.DynamicName("eval"),
            fixture.StringLiteral("outer"));
        var innerFunction = fixture.Function(fixture.Block(
            fixture.Return(evalCall)));
        var outerFunction = fixture.Function(fixture.Block(
            fixture.Declare(outer, fixture.IntegerLiteral(9)),
            fixture.ExpressionStatement(
                fixture.FunctionLiteral(innerFunction))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(
                fixture.FunctionLiteral(outerFunction))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[outer]
            .RequiresNamedActivation.ShouldBeTrue();
        var outerArtifact = artifact.NestedFunctions.ShouldHaveSingleItem();
        outerArtifact.Plan.RegisterAllocations.ShouldBeEmpty();
        var outerBody = artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .ShouldHaveSingleItem().Body;
        outerBody.Actions.ShouldContain(action =>
            action is ActionDefineLocal);
        var nestedActions = outerBody.NestedBodies
            .ShouldHaveSingleItem().Body.Actions;
        nestedActions.ShouldContain(action => action is ActionGetVariable);
        nestedActions.ShouldNotContain(action => action is ActionCallFunction);
    }

    [Fact]
    public void Member_named_eval_remains_an_ordinary_indirect_call()
    {
        var fixture = new SourceFixtureBuilder();
        var local = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "local");
        var memberCall = fixture.Call(
            fixture.Member(fixture.DynamicName("object"), "eval"),
            fixture.StringLiteral("local"));
        var function = fixture.Function(fixture.Block(
            fixture.Declare(local, fixture.IntegerLiteral(4)),
            fixture.ExpressionStatement(memberCall),
            fixture.Return(fixture.Reference(local))));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        artifact.ClosureAnalysis.ShouldNotBeNull()[child.Plan.CodeUnit]
            .Flags.HasFlag(Avm1ClosureCodeUnitFlags.InvokesEval)
            .ShouldBeFalse();
        child.Plan.RegisterAllocations.ShouldContain(allocation =>
            allocation.Symbol == local);
        child.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.EvaluateName);
        artifact.ActionBody.ShouldNotBeNull().NestedBodies
            .ShouldHaveSingleItem().Body.Actions.ShouldContain(action =>
                action is ActionCallMethod);
    }

    [Fact]
    public void Dynamic_scope_capture_keeps_bound_parameter_named()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.Reference(parameter))),
            Avm1SourceFunctionFlags.CapturesDynamicScope,
            parameters: [parameter]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[parameter]
            .RequiresNamedActivation.ShouldBeTrue();
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(0, "value"));
    }

    [Fact]
    public void Register_invoked_parameter_uses_ordered_value_target_lowering()
    {
        var fixture = new SourceFixtureBuilder();
        var callback = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "callback");
        var function = fixture.Function(
            fixture.Block(fixture.Return(
                fixture.Call(
                    fixture.Reference(callback),
                    fixture.DynamicName("argument")))),
            parameters: [callback]);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[callback].Uses
            .ShouldBe(Avm1SourceExpressionUse.Invoke);
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.ShouldBe([1, 2]);
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(callback, 3)
        ]);
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(3, "callback"));
        child.Plan.RegisterCount.ShouldBe((byte)4);
        child.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Value);
        child.Mir.CallSites.ShouldHaveSingleItem().Symbol.ShouldBe(callback);

        var actions = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions;
        actions.Select(action => action.Opcode).ShouldBe([
            ActionOpcode.Push,
            ActionOpcode.Push,
            ActionOpcode.GetVariable,
            ActionOpcode.StoreRegister,
            ActionOpcode.Pop,
            ActionOpcode.StoreRegister,
            ActionOpcode.Pop,
            ActionOpcode.Push,
            ActionOpcode.Push,
            ActionOpcode.Push,
            ActionOpcode.Push,
            ActionOpcode.CallMethod,
            ActionOpcode.Return
        ]);
        GetPushedRegister(actions[0]).ShouldBe((byte)3);
        GetPushedString(actions[1]).ShouldBe("argument");
        actions[3].ShouldBeOfType<ActionStoreRegister>().RegisterNumber
            .ShouldBe((byte)2);
        actions[5].ShouldBeOfType<ActionStoreRegister>().RegisterNumber
            .ShouldBe((byte)1);
        GetPushedRegister(actions[7]).ShouldBe((byte)2);
        GetPushedInteger(actions[8]).ShouldBe(1);
        GetPushedRegister(actions[9]).ShouldBe((byte)1);
        GetPushedString(actions[10]).ShouldBeEmpty();

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        Avm1SourceEquivalence.Compare(source, projected).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            source.GetAs2Text() + Environment.NewLine + projected.GetAs2Text());
    }

    [Fact]
    public void Register_constructor_parameter_uses_blank_new_method_form()
    {
        var fixture = new SourceFixtureBuilder();
        var constructor = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "constructor");
        var function = fixture.Function(
            fixture.Block(fixture.Return(
                fixture.Construct(fixture.Reference(constructor)))),
            parameters: [constructor]);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[constructor].Uses
            .ShouldBe(Avm1SourceExpressionUse.Construct);
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.ShouldBe([1]);
        child.Plan.RegisterAllocations.ShouldBe([
            new Avm1SourceRegisterAllocation(constructor, 2)
        ]);
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(2, "constructor"));
        child.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Value);
        artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions
            .ShouldContain(action => action.Opcode == ActionOpcode.NewMethod);

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        Avm1SourceEquivalence.Compare(source, projected).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            source.GetAs2Text() + Environment.NewLine + projected.GetAs2Text());
    }

    [Fact]
    public void Invocation_scratch_spills_the_last_capacity_target_to_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var parameters = Enumerable.Range(0, byte.MaxValue)
            .Select(index => fixture.AddSymbol(
                Avm1SourceSymbolKind.Parameter,
                $"p{index}"))
            .ToArray();
        var statements = parameters[..^1]
            .Select(parameter => fixture.ExpressionStatement(
                fixture.Reference(parameter)))
            .Append(fixture.Return(
                fixture.Call(fixture.Reference(parameters[^1]))))
            .ToArray();
        var function = fixture.Function(
            fixture.Block(statements),
            parameters: parameters);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.ShouldBeEmpty();
        child.Plan.RegisterAllocations.Count.ShouldBe(byte.MaxValue - 1);
        child.Plan.Parameters[^2].ShouldBe(new FunctionParameter(254, "p253"));
        child.Plan.Parameters[^1].ShouldBe(new FunctionParameter(0, "p254"));
        child.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Name);
        child.Plan.RegisterCount.ShouldBe(byte.MaxValue);
    }

    [Fact]
    public void Oversized_register_target_shape_retries_as_named_invocation()
    {
        var fixture = new SourceFixtureBuilder();
        var callback = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "callback");
        var arguments = Enumerable.Range(0, byte.MaxValue)
            .Select(fixture.IntegerLiteral)
            .ToArray();
        var function = fixture.Function(
            fixture.Block(fixture.Return(
                fixture.Call(fixture.Reference(callback), arguments))),
            parameters: [callback]);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        child.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(0, "callback"));
        child.StackLir.TemporaryRegisters.Count.ShouldBe(byte.MaxValue - 1);
        child.StackLir.TemporaryRegisters[0].ShouldBe((byte)1);
        child.StackLir.TemporaryRegisters[^1].ShouldBe((byte)254);
        child.StackLir.TemporaryStorages.Count(storage =>
            storage.Kind is Avm1StackLirTemporaryStorageKind.ActivationName)
            .ShouldBe(1);
        child.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Name);
        child.Mir.CallSites.ShouldHaveSingleItem().Symbol.ShouldBe(callback);
        child.Plan.RegisterCount.ShouldBe(byte.MaxValue);
        artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions
            .ShouldContain(action => action.Opcode == ActionOpcode.CallFunction);
    }

    [Fact]
    public void Compiler_temporary_overflow_spills_inside_define_function2()
    {
        var fixture = new SourceFixtureBuilder();
        var callback = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "callback");
        var arguments = Enumerable.Range(0, byte.MaxValue + 1)
            .Select(fixture.IntegerLiteral)
            .ToArray();
        var function = fixture.Function(
            fixture.Block(fixture.Return(
                fixture.Call(fixture.Reference(callback), arguments))),
            parameters: [callback]);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.StackLir.TemporaryRegisters.Count.ShouldBe(byte.MaxValue - 1);
        child.StackLir.TemporaryStorages.Where(storage =>
                storage.Kind is Avm1StackLirTemporaryStorageKind.ActivationName)
            .Select(storage => child.StackLir[storage.Name])
            .ShouldBe(["__avm1_spill_254", "__avm1_spill_255"]);
        child.Plan.RegisterCount.ShouldBe(byte.MaxValue);
        artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions
            .ShouldContain(action => action is ActionDefineLocal);
    }

    [Fact]
    public void Source_register_exhaustion_spills_remaining_parameters_to_activation()
    {
        var fixture = new SourceFixtureBuilder();
        var parameters = Enumerable.Range(0, byte.MaxValue + 1)
            .Select(index => fixture.AddSymbol(
                Avm1SourceSymbolKind.Parameter,
                $"p{index}"))
            .ToArray();
        var statements = parameters
            .Select(parameter => fixture.ExpressionStatement(
                fixture.Reference(parameter)))
            .Append(fixture.Return())
            .ToArray();
        var function = fixture.Function(
            fixture.Block(statements),
            parameters: parameters);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.Count.ShouldBe(byte.MaxValue - 1);
        child.Plan.RegisterAllocations[0].ShouldBe(
            new Avm1SourceRegisterAllocation(parameters[0], 1));
        child.Plan.RegisterAllocations[^1].ShouldBe(
            new Avm1SourceRegisterAllocation(parameters[253], 254));
        child.Plan.Parameters[253].ShouldBe(new FunctionParameter(254, "p253"));
        child.Plan.Parameters[254].ShouldBe(new FunctionParameter(0, "p254"));
        child.Plan.Parameters[255].ShouldBe(new FunctionParameter(0, "p255"));
        child.Plan.RegisterCount.ShouldBe(byte.MaxValue);
    }

    [Fact]
    public void More_than_255_disjoint_locals_share_one_source_register()
    {
        const int localCount = 300;
        var fixture = new SourceFixtureBuilder();
        var locals = Enumerable.Range(0, localCount)
            .Select(index => fixture.AddSymbol(
                Avm1SourceSymbolKind.Local,
                $"local{index}"))
            .ToArray();
        var statements = locals
            .SelectMany((local, index) => new[]
            {
                fixture.Declare(local, fixture.IntegerLiteral(index)),
                fixture.ExpressionStatement(fixture.Reference(local))
            })
            .Append(fixture.Return())
            .ToArray();
        var function = fixture.Function(fixture.Block(statements));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.Count.ShouldBe(localCount);
        child.Plan.RegisterAllocations[0].ShouldBe(
            new Avm1SourceRegisterAllocation(locals[0], 1));
        child.Plan.RegisterAllocations[^1].ShouldBe(
            new Avm1SourceRegisterAllocation(locals[^1], 1));
        child.Plan.RegisterAllocations
            .Select(allocation => allocation.Register)
            .Distinct()
            .ShouldBe([1]);
        child.Plan.RegisterCount.ShouldBe((byte)2);
    }

    [Fact]
    public void Invocation_fallback_spills_a_local_when_parameters_reserve_the_register_file()
    {
        var fixture = new SourceFixtureBuilder();
        var parameters = Enumerable.Range(0, byte.MaxValue)
            .Select(index => fixture.AddSymbol(
                Avm1SourceSymbolKind.Parameter,
                $"p{index}"))
            .ToArray();
        var trailing = fixture.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "trailing");
        var statements = parameters[..^1]
            .Select(parameter => fixture.ExpressionStatement(
                fixture.Reference(parameter)))
            .Append(fixture.ExpressionStatement(
                fixture.Call(fixture.Reference(parameters[^1]))))
            .Append(fixture.Declare(trailing, fixture.IntegerLiteral(7)))
            .Append(fixture.Return(fixture.Reference(trailing)))
            .ToArray();
        var function = fixture.Function(
            fixture.Block(statements),
            parameters: parameters);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.Count.ShouldBe(byte.MaxValue - 1);
        child.Plan.RegisterAllocations.ShouldNotContain(allocation =>
            allocation.Symbol == trailing);
        child.Plan.Parameters[^2].ShouldBe(new FunctionParameter(254, "p253"));
        child.Plan.Parameters[^1].ShouldBe(new FunctionParameter(0, "p254"));
        child.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Name);
        child.Plan.RegisterCount.ShouldBe(byte.MaxValue);
        var actions = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body.Actions;
        actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.CallFunction);
        actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.DefineLocal);
    }

    [Fact]
    public void Parameter_captured_by_grandchild_stays_named()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var inner = fixture.Function(fixture.Block(
            fixture.Return(fixture.Reference(parameter))));
        var outer = fixture.Function(
            fixture.Block(
                fixture.ExpressionStatement(fixture.FunctionLiteral(inner)),
                fixture.Return()),
            parameters: [parameter]);
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(outer))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[parameter]
            .RequiresNamedActivation.ShouldBeTrue();
        var outerArtifact = artifact.NestedFunctions.ShouldHaveSingleItem();
        outerArtifact.Plan.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(0, "value"));
        var innerBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body
            .NestedBodies.ShouldHaveSingleItem().Body;
        GetPushedString(innerBody.Actions[0]).ShouldBe("value");
        innerBody.Actions[1].ShouldBeOfType<ActionGetVariable>();
    }

    [Fact]
    public void Repeated_function_site_reuses_compiled_child_artifact()
    {
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(fixture.Block(fixture.Return()));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function)),
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.NestedFunctions.Count.ShouldBe(1);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionDefineFunction2>().Count().ShouldBe(2);
        artifact.ActionBody.NestedBodies.Count.ShouldBe(2);
        ReferenceEquals(
            artifact.ActionBody.NestedBodies[0].Body,
            artifact.ActionBody.NestedBodies[1].Body).ShouldBeTrue();
    }

    [Fact]
    public void Function_compilation_requires_swf7_define_function2()
    {
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(fixture.Block(fixture.Return()));
        var source = fixture.Build(fixture.Block(
            fixture.ExpressionStatement(fixture.FunctionLiteral(function))));

        var artifact = Compile(source, swfVersion: 6);

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP120" &&
            diagnostic.Message.Contains("SWF 7", StringComparison.Ordinal));
    }

    [Fact]
    public void Named_function_expression_uses_preloaded_recursive_self_binding()
    {
        var fixture = new SourceFixtureBuilder();
        var self = fixture.AddSymbol(Avm1SourceSymbolKind.Function, "self");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.Call(
                fixture.Reference(self)))),
            nameSymbol: self);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var action = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Name.ShouldBeEmpty();
        action.Flags.ShouldBe(
            FunctionFlags.SuppressThis |
            FunctionFlags.PreloadArguments |
            FunctionFlags.SuppressSuper);
        action.RegisterCount.ShouldBe((byte)2);
        action.Parameters.ShouldBeEmpty();

        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Plan.RegisterAllocations.ShouldBeEmpty();
        child.Mir.Instructions[0].Kind.ShouldBe(
            Avm1MirInstructionKind.LoadCurrentFunction);
        child.Mir.Instructions[1].Kind.ShouldBe(
            Avm1MirInstructionKind.DeclareLocal);
        var childBody = artifact.ActionBody.NestedBodies
            .ShouldHaveSingleItem().Body;
        childBody.Actions.ShouldContain(
            nested => nested is ActionGetMember);
        childBody.Actions.ShouldContain(
            nested => nested is ActionDefineLocal);

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);
        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: " +
                    diagnostic.Message)) +
            Environment.NewLine + "Expected:" + Environment.NewLine +
            source.GetAs2Text() +
            Environment.NewLine + "Actual:" + Environment.NewLine +
            projected.GetAs2Text());
    }

    [Fact]
    public void Named_function_preload_does_not_collide_with_arguments_parameter()
    {
        var fixture = new SourceFixtureBuilder();
        var self = fixture.AddSymbol(Avm1SourceSymbolKind.Function, "self");
        var arguments = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "arguments");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.Reference(arguments))),
            parameters: [arguments],
            nameSymbol: self);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var action = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Flags.ShouldBe(
            FunctionFlags.SuppressThis |
            FunctionFlags.PreloadArguments |
            FunctionFlags.SuppressSuper);
        var parameterRegister = action.Parameters.ShouldHaveSingleItem().Register;
        parameterRegister.ShouldBeGreaterThan((byte)1);
        artifact.NestedFunctions.ShouldHaveSingleItem()
            .Plan.RegisterAllocations.ShouldBe([
                new Avm1SourceRegisterAllocation(arguments, parameterRegister)
            ]);

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        Avm1SourceEquivalence.Compare(source, projected).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            "The hidden arguments preload must not replace the source parameter.");
    }

    [Fact]
    public void Named_function_self_binding_is_visible_to_nested_closure()
    {
        var fixture = new SourceFixtureBuilder();
        var self = fixture.AddSymbol(Avm1SourceSymbolKind.Function, "self");
        var nested = fixture.Function(fixture.Block(
            fixture.Return(fixture.Reference(self))));
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.FunctionLiteral(nested))),
            nameSymbol: self);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[self].Flags.HasFlag(
            Avm1ClosureSymbolFlags.CapturedByClosure).ShouldBeTrue();
        var outer = artifact.NestedFunctions.ShouldHaveSingleItem();
        outer.NestedFunctions.ShouldHaveSingleItem();
        var nestedBody = artifact.ActionBody.ShouldNotBeNull()
            .NestedBodies.ShouldHaveSingleItem().Body
            .NestedBodies.ShouldHaveSingleItem().Body;
        GetPushedString(nestedBody.Actions[0]).ShouldBe("self");
        nestedBody.Actions[1].ShouldBeOfType<ActionGetVariable>();

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        Avm1SourceEquivalence.Compare(source, projected).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent);
    }

    [Fact]
    public void Special_values_use_fixed_define_function2_preload_prefix()
    {
        var fixture = new SourceFixtureBuilder();
        var specials = new[]
        {
            (Avm1SourceSymbolKind.This, "this", FunctionFlags.PreloadThis, (byte)1),
            (Avm1SourceSymbolKind.Arguments, "arguments", FunctionFlags.PreloadArguments, (byte)2),
            (Avm1SourceSymbolKind.Super, "super", FunctionFlags.PreloadSuper, (byte)3),
            (Avm1SourceSymbolKind.Root, "_root", FunctionFlags.PreloadRoot, (byte)4),
            (Avm1SourceSymbolKind.Parent, "_parent", FunctionFlags.PreloadParent, (byte)5),
            (Avm1SourceSymbolKind.Global, "_global", FunctionFlags.PreloadGlobal, (byte)6)
        };
        var symbols = specials
            .Select(special => fixture.AddSymbol(special.Item1, special.Item2))
            .ToArray();
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Array(
                symbols.Select(fixture.Reference).ToArray()))));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var action = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Flags.ShouldBe(specials.Aggregate(
            (FunctionFlags)0,
            (flags, special) => flags | special.Item3));
        action.RegisterCount.ShouldBeGreaterThanOrEqualTo((byte)7);

        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        for (var i = 0; i < specials.Length; i++)
        {
            child.Mir.LValues.ShouldContain(lValue =>
                lValue.Symbol == symbols[i] &&
                lValue.Kind == Avm1MirLValueKind.Register &&
                lValue.Register == specials[i].Item4);
        }

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        Avm1SourceEquivalence.Compare(source, projected).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent);
    }

    [Fact]
    public void Dynamic_arguments_lookup_preserves_runtime_special_bindings()
    {
        var fixture = new SourceFixtureBuilder();
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.DynamicName("arguments"))));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var action = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Flags.ShouldBe((FunctionFlags)0);
        artifact.ActionBody.NestedBodies.ShouldHaveSingleItem().Body.Actions
            .ShouldContain(action => action is ActionGetVariable);
    }

    [Fact]
    public void Named_function_self_binding_follows_this_preload()
    {
        var fixture = new SourceFixtureBuilder();
        var self = fixture.AddSymbol(Avm1SourceSymbolKind.Function, "self");
        var thisSymbol = fixture.AddSymbol(Avm1SourceSymbolKind.This, "this");
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.Array(
                fixture.Reference(self),
                fixture.Reference(thisSymbol),
                fixture.Reference(parameter)))),
            parameters: [parameter],
            nameSymbol: self);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var action = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Flags.ShouldBe(
            FunctionFlags.PreloadThis |
            FunctionFlags.PreloadArguments |
            FunctionFlags.SuppressSuper);
        action.Parameters.ShouldHaveSingleItem().Register
            .ShouldBeGreaterThan((byte)2);
        var child = artifact.NestedFunctions.ShouldHaveSingleItem();
        child.Mir.Instructions[0].Kind.ShouldBe(
            Avm1MirInstructionKind.LoadCurrentFunction);
        child.Mir[child.Mir.Instructions[0].LValue].Register.ShouldBe((byte)2);
        child.Mir.LValues.ShouldContain(lValue =>
            lValue.Symbol == thisSymbol &&
            lValue.Register == 1);

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        Avm1SourceEquivalence.Compare(source, projected).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent);
    }

    [Fact]
    public void Try_components_reuse_the_owning_function_preload_prefix()
    {
        var fixture = new SourceFixtureBuilder();
        var thisSymbol = fixture.AddSymbol(Avm1SourceSymbolKind.This, "this");
        var tryStatement = fixture.Try(
            fixture.Block(fixture.ExpressionStatement(
                fixture.IntegerLiteral(1))),
            fixture.Finally(fixture.Block(
                fixture.Return(fixture.Reference(thisSymbol)))));
        var function = fixture.Function(fixture.Block(tryStatement));
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));

        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var action = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionDefineFunction2>();
        action.Flags.ShouldBe(
            FunctionFlags.PreloadThis |
            FunctionFlags.SuppressArguments |
            FunctionFlags.SuppressSuper);
        var finallyBody = artifact.NestedFunctions.ShouldHaveSingleItem()
            .NestedTryRegions.ShouldHaveSingleItem().FinallyBody.ShouldNotBeNull();
        finallyBody.Mir.LValues.ShouldContain(lValue =>
            lValue.Symbol == thisSymbol &&
            lValue.Kind == Avm1MirLValueKind.Register &&
            lValue.Register == 1);
    }

    [Fact]
    public void Function_compile_decompile_projection_is_hir_equivalent()
    {
        var fixture = new SourceFixtureBuilder();
        var parameter = fixture.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.Reference(parameter))),
            parameters: [parameter]);
        var source = fixture.Build(fixture.Block(
            fixture.Return(fixture.FunctionLiteral(function))));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: " +
                    diagnostic.Message)) +
            Environment.NewLine + "Expected:" + Environment.NewLine +
            source.GetAs2Text() +
            Environment.NewLine + "Actual:" + Environment.NewLine +
            projected.GetAs2Text() +
            Environment.NewLine + "Projection diagnostics:" + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                projected.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Capturing_function_roundtrips_through_source_hir()
    {
        var fixture = new SourceFixtureBuilder();
        var outer = fixture.AddSymbol(Avm1SourceSymbolKind.Local, "outer");
        var function = fixture.Function(fixture.Block(
            fixture.Return(fixture.Reference(outer))));
        var source = fixture.Build(fixture.Block(
            fixture.Declare(outer, fixture.IntegerLiteral(9)),
            fixture.Return(fixture.FunctionLiteral(function))));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: " +
                    diagnostic.Message)) +
            Environment.NewLine + "Expected:" + Environment.NewLine +
            source.GetAs2Text() +
            Environment.NewLine + "Actual:" + Environment.NewLine +
            projected.GetAs2Text());
    }

    [Fact]
    public void Function_declaration_roundtrips_through_source_hir()
    {
        var fixture = new SourceFixtureBuilder();
        var symbol = fixture.AddSymbol(Avm1SourceSymbolKind.Function, "helper");
        var function = fixture.Function(
            fixture.Block(fixture.Return(fixture.IntegerLiteral(42))),
            Avm1SourceFunctionFlags.Declaration,
            nameSymbol: symbol);
        var source = fixture.Build(fixture.Block(
            fixture.FunctionDeclaration(symbol, function)));
        var artifact = Compile(source);
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: " +
                    diagnostic.Message)) +
            Environment.NewLine + "Expected:" + Environment.NewLine +
            source.GetAs2Text() +
            Environment.NewLine + "Actual:" + Environment.NewLine +
            projected.GetAs2Text());
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

    private static string Describe(Avm1MethodArtifact artifact) =>
        string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string GetPushedString(Avm1Action action) =>
        action.ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueString>()
            .Value;

    private static byte GetPushedRegister(Avm1Action action) =>
        action.ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueRegister>()
            .RegisterIndex;

    private static int GetPushedInteger(Avm1Action action) =>
        action.ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueInteger>()
            .Value;

    private static string[] GetPushedStrings(
        IReadOnlyList<Avm1Action> actions) =>
        actions.OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .OfType<PushValue.PushValueString>()
            .Select(value => value.Value)
            .ToArray();

    private sealed class SourceFixtureBuilder
    {
        private readonly SourceTypeIndex _unknownType;

        public SourceFixtureBuilder()
        {
            _unknownType = Builder.GetBuiltInType(
                Avm1SourceTypeKind.Unknown);
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
            string name) =>
            Builder.AddSymbol(
                kind,
                name,
                SourceTypeIndex.Invalid,
                _unknownType);

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

        public SourceExpressionIndex Array(
            params SourceExpressionIndex[] values) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.ArrayLiteral,
                children: values);

        public SourceExpressionIndex Call(
            SourceExpressionIndex target,
            params SourceExpressionIndex[] arguments) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children: [target, .. arguments]);

        public SourceExpressionIndex Member(
            SourceExpressionIndex receiver,
            string name) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.MemberAccess,
                children: [receiver, StringLiteral(name)]);

        public SourceExpressionIndex Construct(
            SourceExpressionIndex target,
            params SourceExpressionIndex[] arguments) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.New,
                children: [target, .. arguments]);

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

        public SourceExpressionIndex FunctionLiteralAt(
            SourceFunctionIndex function,
            SourceOriginIndex origin) =>
            Builder.AddExpression(
                Avm1SourceExpressionKind.FunctionLiteral,
                function: function,
                origin: origin);

        public SourceFunctionIndex Function(
            SourceStatementIndex body,
            Avm1SourceFunctionFlags flags = Avm1SourceFunctionFlags.None,
            IReadOnlyList<SourceSymbolIndex>? parameters = null,
            SourceSymbolIndex? nameSymbol = null) =>
            Builder.AddFunction(
                nameSymbol ?? SourceSymbolIndex.Invalid,
                body,
                flags,
                SourceOriginIndex.Invalid,
                parameters);

        public SourceStatementIndex FunctionDeclaration(
            SourceSymbolIndex symbol,
            SourceFunctionIndex function) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.FunctionDeclaration,
                expression: FunctionLiteral(function),
                symbol: symbol);

        public SourceStatementIndex Declare(
            SourceSymbolIndex symbol,
            SourceExpressionIndex? initializer = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: initializer,
                symbol: symbol);

        public SourceStatementIndex ExpressionStatement(
            SourceExpressionIndex expression) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression);

        public SourceStatementIndex Return(
            SourceExpressionIndex? expression = null) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expression);

        public SourceStatementIndex If(
            SourceExpressionIndex condition,
            SourceStatementIndex whenTrue,
            SourceStatementIndex whenFalse) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: condition,
                children: [whenTrue, whenFalse]);

        public SourceStatementIndex Try(
            SourceStatementIndex tryBody,
            SourceStatementIndex finallyClause) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Try,
                children: [tryBody, finallyClause]);

        public SourceStatementIndex Finally(SourceStatementIndex body) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.FinallyClause,
                children: [body]);

        public SourceStatementIndex While(
            SourceExpressionIndex condition,
            SourceStatementIndex body) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.While,
                expression: condition,
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

        public SourceStatementIndex Block(
            params SourceStatementIndex[] statements) =>
            Builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: statements);

        public Avm1SourceMethod Build(SourceStatementIndex body) =>
            new(Builder.ToArena(), body, [], []);
    }
}
