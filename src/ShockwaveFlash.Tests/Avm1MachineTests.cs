using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1MachineTests
{
    [Fact]
    public void InitArray_preserves_element_order()
    {
        var machine = new Avm1Machine();

        machine.Execute(
        [
            new ActionPush([PushValue.String("arr")]),
            new ActionPush([PushValue.Integer(30)]),
            new ActionPush([PushValue.Integer(20)]),
            new ActionPush([PushValue.Integer(10)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionInitArray(),
            new ActionSetVariable(),
            new ActionEnd(),
        ]);

        var array = machine.Globals["arr"].AsArray;

        array.Items.Select(item => item.AsNumber).ShouldBe([10d, 20d, 30d]);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void If_and_jump_follow_encoded_action_boundaries(
        bool condition,
        int expected)
    {
        var codeUnit = new Avm1CodeUnit();
        var trueLabel = codeUnit.DefineLabel();
        var endLabel = codeUnit.DefineLabel();
        codeUnit.Emit(new ActionPush([PushValue.Boolean(condition)]));
        codeUnit.EmitIf(trueLabel);
        EmitAssignment(codeUnit, "result", 1);
        codeUnit.EmitJump(endLabel);
        codeUnit.MarkLabel(trueLabel);
        EmitAssignment(codeUnit, "result", 2);
        codeUnit.MarkLabel(endLabel);
        var body = codeUnit.Assemble(new Avm1AssemblyOptions(7)
        {
            RequireEndAction = false
        });
        body.Succeeded.ShouldBeTrue();
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(body.Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(expected);
    }

    [Fact]
    public void Branch_to_a_non_action_boundary_is_rejected()
    {
        var machine = new Avm1Machine(swfVersion: 7);

        var exception = Should.Throw<InvalidOperationException>(() =>
            machine.Execute([new ActionJump(1)], strict: true));

        exception.Message.ShouldContain("non-action boundary");
    }

    [Fact]
    public void DefineLocal_actions_initialize_the_activation_scope()
    {
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(
        [
            new ActionPush([PushValue.String("value"), PushValue.Integer(7)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("empty")]),
            new ActionDefineLocal2()
        ], strict: true);

        machine.Globals["value"].AsNumber.ShouldBe(7d);
        machine.Globals.Members.ShouldContainKey("empty");
        machine.Globals["empty"].ShouldBe(Avm1Value.Undefined);
    }

    [Fact]
    public void Increment_and_decrement_apply_numeric_coercion()
    {
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(
        [
            new ActionPush([PushValue.String("incremented"), PushValue.String("4")]),
            new ActionIncrement(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("decremented"), PushValue.String("4")]),
            new ActionDecrement(),
            new ActionSetVariable()
        ], strict: true);

        machine.Globals["incremented"].AsNumber.ShouldBe(5d);
        machine.Globals["decremented"].AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void Enumerate2_pushes_each_object_key_and_a_null_sentinel()
    {
        var source = new Avm1Object();
        source.Members["first"] = new Avm1Number(1);
        source.Members["second"] = new Avm1Number(2);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["source"] = source;
        var codeUnit = new Avm1CodeUnit();
        var loop = codeUnit.DefineLabel();
        var exit = codeUnit.DefineLabel();
        codeUnit.Emit(new ActionPush([PushValue.String("source")]));
        codeUnit.Emit(new ActionGetVariable());
        codeUnit.Emit(new ActionEnumerate2());
        codeUnit.MarkLabel(loop);
        codeUnit.Emit(new ActionStoreRegister(1));
        codeUnit.Emit(new ActionPush([PushValue.Null()]));
        codeUnit.Emit(new ActionEquals2());
        codeUnit.EmitIf(exit);
        codeUnit.Emit(new ActionPush([PushValue.String("lastKey")]));
        codeUnit.Emit(new ActionPush([PushValue.Register(1)]));
        codeUnit.Emit(new ActionSetVariable());
        codeUnit.EmitJump(loop);
        codeUnit.MarkLabel(exit);
        codeUnit.Emit(new ActionEnd());
        var body = codeUnit.Assemble(new Avm1AssemblyOptions(7));
        body.Succeeded.ShouldBeTrue();

        machine.Execute(body.Actions, strict: true);

        machine.Globals["lastKey"].AsString.ShouldBe("second");
        machine.UnsupportedOpcodes.ShouldBeEmpty();
    }

    private static void EmitAssignment(
        Avm1CodeUnit codeUnit,
        string name,
        int value)
    {
        codeUnit.Emit(new ActionPush([
            PushValue.String(name),
            PushValue.Integer(value)
        ]));
        codeUnit.Emit(new ActionSetVariable());
    }
}
