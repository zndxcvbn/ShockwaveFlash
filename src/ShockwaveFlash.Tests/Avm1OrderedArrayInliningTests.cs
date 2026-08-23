using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1OrderedArrayInliningTests
{
    [Fact]
    public void Inlines_array_elements_in_source_order()
    {
        Avm1Action[] actions =
        [
            .. ReadMember("target", "height"),
            .. ReadMember("target", "width"),
            .. ReadMember("target", "y"),
            .. ReadMember("target", "x"),
            new ActionPush([PushValue.Integer(4)]),
            new ActionInitArray(),
            new ActionReturn()
        ];
        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "return [target.x, target.y, target.width, target.height];" +
            Environment.NewLine);
        decompilation.ProjectSource().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Keeps_array_element_temporary_across_an_independent_call()
    {
        var actions = new List<Avm1Action>();
        actions.AddRange(ReadMember("target", "height"));
        actions.Add(new ActionPush([
            PushValue.Integer(0),
            PushValue.String("mutate")
        ]));
        actions.Add(new ActionCallFunction());
        actions.Add(new ActionPop());
        actions.AddRange(ReadMember("target", "x"));
        actions.Add(new ActionPush([PushValue.Integer(2)]));
        actions.Add(new ActionInitArray());
        actions.Add(new ActionReturn());

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7)
            .GetStructuredAs2Text();

        text.ShouldContain("var v3 = target.height;");
        text.ShouldContain("mutate();");
        text.ShouldContain("return [target.x, v3];");
        text.IndexOf("var v3", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("mutate()", StringComparison.Ordinal));
        text.IndexOf("mutate()", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("return [target.x, v3]", StringComparison.Ordinal));
    }

    [Fact]
    public void Inlines_array_element_across_a_structured_conditional()
    {
        var actions = new List<Avm1Action>();
        actions.AddRange(ReadMember("obj", "value"));
        actions.AddRange([
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("off")]),
            new ActionJump(0),
            new ActionPush([PushValue.String("on")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionInitArray(),
            new ActionReturn()
        ]);
        SetBranch(actions, index: 6, targetIndex: 9);
        SetBranch(actions, index: 8, targetIndex: 10);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "return [flag ? \"on\" : \"off\", obj.value];" +
            Environment.NewLine);
        decompilation.ProjectSource().IsComplete.ShouldBeTrue();
    }

    private static Avm1Action[] ReadMember(string target, string member) =>
    [
        new ActionPush([PushValue.String(target)]),
        new ActionGetVariable(),
        new ActionPush([PushValue.String(member)]),
        new ActionGetMember()
    ];

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
