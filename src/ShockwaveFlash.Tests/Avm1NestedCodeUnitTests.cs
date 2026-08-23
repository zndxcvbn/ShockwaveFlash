using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1NestedCodeUnitTests
{
    [Fact]
    public void Code_unit_assembles_define_function_with_independent_labels()
    {
        var functionBody = new Avm1CodeUnit();
        var exit = functionBody.DefineLabel();
        functionBody.Emit(new ActionPush([PushValue.Boolean(false)]));
        functionBody.EmitIf(exit);
        functionBody.Emit(new ActionPush([PushValue.String("unused")]));
        functionBody.Emit(new ActionTrace());
        functionBody.MarkLabel(exit);
        var returnValue = functionBody.Emit(
            new ActionPush([PushValue.Undefined()]));
        functionBody.Emit(new ActionReturn());

        var parameters = new List<string> { "value" };
        var root = new Avm1CodeUnit();
        var owner = root.EmitDefineFunction("readValue", parameters, functionBody);
        root.Emit(new ActionEnd());
        parameters[0] = "mutated";

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var function = result.Actions[0].ShouldBeOfType<ActionDefineFunction>();
        function.Name.ShouldBe("readValue");
        function.Parameters.ShouldBe(["value"]);
        var nested = result.NestedBodies.ShouldHaveSingleItem();
        nested.Owner.ShouldBe(owner);
        nested.Kind.ShouldBe(Avm1NestedCodeUnitKind.DefineFunctionBody);
        nested.ByteOffset.ShouldBe(
            result.GetLocation(owner).ByteOffset +
            result.GetLocation(owner).ByteLength - function.Body.Length);
        nested.Body.Succeeded.ShouldBeTrue(Describe(nested.Body));
        nested.Body.GetLabelOffset(exit).ShouldBe(
            nested.Body.GetLocation(returnValue).ByteOffset);
        function.Body.Span.SequenceEqual(nested.Body.Bytes.Span).ShouldBeTrue();
        Avm1Action.DecodeCollection(function.Body, swfVersion: 7, strict: true)
            .Count.ShouldBe(6);
    }

    [Fact]
    public void Code_unit_assembles_define_function2_metadata_and_body()
    {
        var functionBody = new Avm1CodeUnit();
        functionBody.Emit(new ActionPush([PushValue.Integer(42)]));
        functionBody.Emit(new ActionReturn());

        var parameters = new List<FunctionParameter>
        {
            new(2, "value")
        };
        var root = new Avm1CodeUnit();
        var owner = root.EmitDefineFunction2(
            string.Empty,
            registerCount: 3,
            FunctionFlags.PreloadThis,
            parameters,
            functionBody);
        root.Emit(new ActionPop());
        root.Emit(new ActionEnd());
        parameters.Clear();

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var function = result.Actions[0].ShouldBeOfType<ActionDefineFunction2>();
        function.Name.ShouldBeEmpty();
        function.RegisterCount.ShouldBe((byte)3);
        function.Flags.ShouldBe(FunctionFlags.PreloadThis);
        var parameter = function.Parameters.ShouldHaveSingleItem();
        parameter.ShouldBe(new FunctionParameter(2, "value"));
        var nested = result.NestedBodies.ShouldHaveSingleItem();
        nested.Owner.ShouldBe(owner);
        nested.Kind.ShouldBe(Avm1NestedCodeUnitKind.DefineFunction2Body);
        nested.ByteOffset.ShouldBe(
            result.GetLocation(owner).ByteOffset +
            result.GetLocation(owner).ByteLength - function.Body.Length);
        nested.Body.MaximumStackDepth.ShouldBe(1);
        function.Body.Span.SequenceEqual(nested.Body.Bytes.Span).ShouldBeTrue();
    }

    [Fact]
    public void Reused_with_code_unit_is_assembled_once_per_root_assembly()
    {
        var withBody = TraceUnit("inside");
        var root = new Avm1CodeUnit();
        root.Emit(new ActionPush([PushValue.Undefined()]));
        var firstOwner = root.EmitWith(withBody);
        root.Emit(new ActionPush([PushValue.Undefined()]));
        var secondOwner = root.EmitWith(withBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.NestedBodies.Count.ShouldBe(2);
        result.NestedBodies[0].Owner.ShouldBe(firstOwner);
        result.NestedBodies[1].Owner.ShouldBe(secondOwner);
        result.NestedBodies.ShouldAllBe(nested =>
            nested.Kind == Avm1NestedCodeUnitKind.WithBody);
        ReferenceEquals(
            result.NestedBodies[0].Body,
            result.NestedBodies[1].Body).ShouldBeTrue();
        result.Actions.Count(action => action is ActionWith).ShouldBe(2);
    }

    [Fact]
    public void Code_unit_assembles_try_catch_and_finally_components()
    {
        var root = new Avm1CodeUnit();
        var owner = root.EmitTry(
            TryFlags.CatchBlock | TryFlags.FinallyBlock,
            catchRegister: 0,
            catchVariable: "error",
            TraceUnit("try"),
            TraceUnit("catch"),
            TraceUnit("finally"));
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var actionTry = result.Actions[0].ShouldBeOfType<ActionTry>();
        actionTry.Flags.ShouldBe(TryFlags.CatchBlock | TryFlags.FinallyBlock);
        actionTry.CatchVariable.ShouldBe("error");
        actionTry.TryBody.IsEmpty.ShouldBeFalse();
        actionTry.CatchBody.IsEmpty.ShouldBeFalse();
        actionTry.FinallyBody.IsEmpty.ShouldBeFalse();
        result.NestedBodies.Select(nested => nested.Kind).ShouldBe(
            [
                Avm1NestedCodeUnitKind.TryBody,
                Avm1NestedCodeUnitKind.CatchBody,
                Avm1NestedCodeUnitKind.FinallyBody
            ]);
        result.NestedBodies.ShouldAllBe(nested => nested.Owner == owner);
        var location = result.GetLocation(owner);
        var tryOffset = location.ByteOffset + location.ByteLength -
            actionTry.TryBody.Length - actionTry.CatchBody.Length -
            actionTry.FinallyBody.Length;
        result.NestedBodies.Select(nested => nested.ByteOffset).ShouldBe([
            tryOffset,
            tryOffset + actionTry.TryBody.Length,
            tryOffset + actionTry.TryBody.Length + actionTry.CatchBody.Length
        ]);
        result.MaximumStackDepth.ShouldBe(1);
    }

    [Fact]
    public void Instruction_table_resolves_a_try_branch_in_the_parent_region()
    {
        var skippedPush = new ActionPush([PushValue.Integer(1)]);
        var skippedLength = Avm1Action.EncodeCollection(
            [skippedPush],
            swfVersion: 7).Length;
        var tryBody = Avm1Action.EncodeCollection(
            [new ActionJump(checked((short)skippedLength))],
            swfVersion: 7);
        var table = Avm1InstructionTable.Build(
            [
                new ActionTry(
                    0,
                    catchRegister: 0,
                    catchVariable: string.Empty,
                    tryBody,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty),
                skippedPush,
                new ActionPop(),
                new ActionEnd()
            ],
            swfVersion: 7);
        var jump = table.Instructions.Single(instruction =>
            instruction.Action is ActionJump).Index;
        var pop = table.Instructions.Single(instruction =>
            instruction.Action is ActionPop).Index;

        table.GetBranchTargetAction(jump).ShouldBe(pop);
    }

    [Fact]
    public void Try_body_parent_branch_is_relocated_to_the_final_parent_label()
    {
        var root = new Avm1CodeUnit();
        var exit = root.DefineLabel();
        var tryBody = new Avm1CodeUnit();
        var branch = tryBody.EmitParentJump(root, exit);
        var owner = root.EmitTry(
            0,
            catchRegister: 0,
            catchVariable: string.Empty,
            tryBody);
        root.Emit(new ActionPush([PushValue.String("skipped")]));
        root.Emit(new ActionPop());
        root.MarkLabel(exit);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var actionTry = result.Actions[result.GetLocation(owner).ActionIndex]
            .ShouldBeOfType<ActionTry>();
        var nested = result.NestedBodies.ShouldHaveSingleItem();
        var nestedBranch = nested.Body.Actions[
            nested.Body.GetLocation(branch).ActionIndex]
            .ShouldBeOfType<ActionJump>();
        var nestedBase = result.GetLocation(owner).ByteOffset +
            result.GetLocation(owner).ByteLength - actionTry.TryBody.Length;
        var branchLocation = nested.Body.GetLocation(branch);
        var targetOffset = nestedBase + branchLocation.ByteOffset +
            branchLocation.ByteLength + nestedBranch.BranchOffset;
        targetOffset.ShouldBe(result.GetLabelOffset(exit));
        actionTry.TryBody.Span.SequenceEqual(nested.Body.Bytes.Span).ShouldBeTrue();
    }

    [Fact]
    public void Try_body_parent_branch_can_target_an_earlier_parent_label()
    {
        var root = new Avm1CodeUnit();
        var loop = root.DefineLabel();
        root.MarkLabel(loop);
        var tryBody = new Avm1CodeUnit();
        tryBody.Emit(new ActionPush([PushValue.Boolean(false)]));
        var branch = tryBody.EmitParentIf(root, loop);
        var owner = root.EmitTry(
            0,
            catchRegister: 0,
            catchVariable: string.Empty,
            tryBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var actionTry = result.Actions[result.GetLocation(owner).ActionIndex]
            .ShouldBeOfType<ActionTry>();
        var nested = result.NestedBodies.ShouldHaveSingleItem();
        var nestedBranch = nested.Body.Actions[
            nested.Body.GetLocation(branch).ActionIndex]
            .ShouldBeOfType<ActionIf>();
        var nestedBase = result.GetLocation(owner).ByteOffset +
            result.GetLocation(owner).ByteLength - actionTry.TryBody.Length;
        var branchLocation = nested.Body.GetLocation(branch);
        var targetOffset = nestedBase + branchLocation.ByteOffset +
            branchLocation.ByteLength + nestedBranch.BranchOffset;
        targetOffset.ShouldBe(result.GetLabelOffset(loop));
        nestedBranch.BranchOffset.ShouldBeLessThan((short)0);
    }

    [Fact]
    public void Parent_branch_cannot_be_assembled_without_its_parent_try()
    {
        var parent = new Avm1CodeUnit();
        var exit = parent.DefineLabel();
        parent.MarkLabel(exit);
        var child = new Avm1CodeUnit();
        var branch = child.EmitParentJump(parent, exit);
        child.Emit(new ActionEnd());

        var result = child.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM016" &&
            diagnostic.Instruction == branch);
    }

    [Fact]
    public void Function_body_cannot_branch_to_its_parent_code_unit()
    {
        var root = new Avm1CodeUnit();
        var exit = root.DefineLabel();
        var functionBody = new Avm1CodeUnit();
        functionBody.EmitParentJump(root, exit);
        var owner = root.EmitDefineFunction(
            "invalid",
            [],
            functionBody);
        root.MarkLabel(exit);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM010" &&
            diagnostic.Instruction == owner &&
            diagnostic.Message.Contains("AVM1ASM016", StringComparison.Ordinal));
    }

    [Fact]
    public void Try_body_parent_branch_reports_signed_offset_overflow()
    {
        var root = new Avm1CodeUnit();
        var exit = root.DefineLabel();
        var tryBody = new Avm1CodeUnit();
        var owner = root.EmitTry(
            0,
            catchRegister: 0,
            catchVariable: string.Empty,
            tryBody);
        tryBody.EmitParentJump(root, exit);
        root.Emit(new ActionPush([PushValue.String(new string('x', 33000))]));
        root.Emit(new ActionPop());
        root.MarkLabel(exit);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM017" &&
            diagnostic.Instruction == owner);
    }

    [Fact]
    public void Invalid_nested_body_stops_parent_bytecode_emission()
    {
        var invalidBody = new Avm1CodeUnit();
        invalidBody.Emit(new ActionPop());

        var root = new Avm1CodeUnit();
        root.Emit(new ActionPush([PushValue.Undefined()]));
        var owner = root.EmitWith(invalidBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM010" &&
            diagnostic.Instruction == owner &&
            diagnostic.Message.Contains("AVM1VER004", StringComparison.Ordinal));
        var nested = result.NestedBodies.ShouldHaveSingleItem();
        nested.Body.Succeeded.ShouldBeFalse();
        nested.Body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER004");
    }

    [Fact]
    public void Indirect_code_unit_cycle_is_reported_without_recursion()
    {
        var outer = new Avm1CodeUnit();
        var inner = new Avm1CodeUnit();
        outer.EmitWith(inner);
        inner.EmitWith(outer);

        var result = outer.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM010" &&
            diagnostic.Message.Contains("AVM1ASM011", StringComparison.Ordinal));
        var innerResult = result.NestedBodies.ShouldHaveSingleItem().Body;
        var cycleResult = innerResult.NestedBodies.ShouldHaveSingleItem().Body;
        cycleResult.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM011");
    }

    [Fact]
    public void Try_flags_must_match_nested_code_unit_presence()
    {
        var root = new Avm1CodeUnit();
        var owner = root.EmitTry(
            TryFlags.CatchBlock,
            catchRegister: 0,
            catchVariable: "error",
            TraceUnit("try"));
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.NestedBodies.ShouldBeEmpty();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM012" &&
            diagnostic.Instruction == owner &&
            diagnostic.Message.Contains("CatchBlock", StringComparison.Ordinal));
    }

    private static Avm1CodeUnit TraceUnit(string value)
    {
        var result = new Avm1CodeUnit();
        result.Emit(new ActionPush([PushValue.String(value)]));
        result.Emit(new ActionTrace());
        return result;
    }

    private static Avm1AssemblyOptions RootOptions() =>
        new(SwfVersion: 7)
        {
            RequireEndAction = true
        };

    private static string Describe(Avm1ActionBody body) =>
        string.Join(
            Environment.NewLine,
            body.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));
}
