using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1OrderedMemberInliningTests
{
    [Fact]
    public void Inlines_ordered_receiver_and_key_member_reads()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("first")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("keys")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("index")]),
            new ActionGetMember(),
            new ActionGetMember(),
            new ActionReturn()
        ], swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "return target.first[keys.index];" + Environment.NewLine);
        decompilation.ProjectSource().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Keeps_receiver_temporary_across_an_independent_call()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("first")]),
            new ActionGetMember(),
            new ActionPush([
                PushValue.Integer(0),
                PushValue.String("mutate")
            ]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("index")]),
            new ActionGetMember(),
            new ActionReturn()
        ], swfVersion: 7);

        var text = decompilation.GetStructuredAs2Text();
        text.ShouldContain("var v3 = target.first;");
        text.ShouldContain("mutate();");
        text.ShouldContain("return v3.index;");
        text.IndexOf("var v3", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("mutate()", StringComparison.Ordinal));
        text.IndexOf("mutate()", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("return v3.index", StringComparison.Ordinal));
    }

    [Fact]
    public void Inlines_member_receiver_across_a_conditional_nested_in_the_value()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("first")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.String("prefix")]),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("off")]),
            new ActionJump(0),
            new ActionPush([PushValue.String("on")]),
            new ActionAdd2(),
            new ActionSetMember()
        };
        SetBranch(actions, index: 8, targetIndex: 11);
        SetBranch(actions, index: 10, targetIndex: 12);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "target.first.field = \"prefix\" + (condition ? \"on\" : \"off\");" +
            Environment.NewLine);
        decompilation.ProjectSource().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Does_not_materialize_an_inlined_receiver_repeated_in_the_value()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("initial")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("child")]),
            new ActionGetMember(),
            new ActionPush([
                PushValue.String("prefix"),
                PushValue.String("key")
            ]),
            new ActionGetVariable(),
            new ActionAdd2(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("child")]),
            new ActionGetMember(),
            new ActionPush([
                PushValue.Integer(0),
                PushValue.String("value")
            ]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("toString")]),
            new ActionCallMethod(),
            new ActionPush([
                PushValue.Integer(2),
                PushValue.String("types")
            ]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Value")]),
            new ActionNewMethod(),
            new ActionStoreRegister(2),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(2)]),
            new ActionReturn()
        ], swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "initial;" +
            Environment.NewLine +
            "var _loc2_;" +
            Environment.NewLine +
            "obj.child[\"prefix\" + key] = _loc2_ = new types.Value(value.toString(), obj.child);" +
            Environment.NewLine +
            "return _loc2_;" +
            Environment.NewLine);
    }

    [Fact]
    public void Inlines_left_member_operand_across_a_conditional_right_operand()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("size")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(3)]),
            new ActionMultiply(),
            new ActionReturn()
        };
        SetBranch(actions, index: 6, targetIndex: 9);
        SetBranch(actions, index: 8, targetIndex: 10);

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7)
            .GetStructuredAs2Text();

        text.ShouldBe(
            "return target.size * (condition ? 3 : 2);" + Environment.NewLine);
    }

    [Fact]
    public void Inlines_ordered_receiver_and_key_for_delete()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("holder")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("keys")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("index")]),
            new ActionGetMember(),
            new ActionDelete(),
            new ActionPop()
        ], swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "delete target.holder[keys.index];" + Environment.NewLine);
    }

    private static void SetBranch(
        List<Avm1Action> actions,
        int index,
        int targetIndex)
    {
        var offsets = ComputeOffsets(actions);
        var displacement = checked((short)(
            offsets[targetIndex] - offsets[index] - Length(actions[index])));
        actions[index] = actions[index] switch
        {
            ActionIf => new ActionIf(displacement),
            ActionJump => new ActionJump(displacement),
            _ => throw new ArgumentException("Action is not a branch.", nameof(index))
        };
    }

    private static int[] ComputeOffsets(IReadOnlyList<Avm1Action> actions)
    {
        var offsets = new int[actions.Count];
        for (var i = 1; i < actions.Count; i++)
            offsets[i] = offsets[i - 1] + Length(actions[i - 1]);
        return offsets;
    }

    private static int Length(Avm1Action action) =>
        Avm1Action.EncodeCollection([action], swfVersion: 7).Length;
}
