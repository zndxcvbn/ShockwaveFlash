using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1OrderedObjectConditionalInliningTests
{
    [Fact]
    public void Inlines_object_value_across_a_structured_conditional_element()
    {
        var decompilation = Avm1Decompiler.DecompileMethod(
            BuildConditionalObject(includeProbe: false),
            swfVersion: 7);

        decompilation.GetStructuredAs2Text().ShouldBe(
            "result = {first: obj.value, state: flag ? \"on\" : \"off\"};" +
            Environment.NewLine);
        decompilation.ProjectSource().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Keeps_object_value_before_a_call_and_structured_conditional()
    {
        var text = Avm1Decompiler.DecompileMethod(
            BuildConditionalObject(includeProbe: true),
            swfVersion: 7).GetStructuredAs2Text();

        text.ShouldContain("var v");
        text.ShouldContain("probe();");
        text.ShouldContain("result = {first: v");
        text.ShouldContain("state: flag ? \"on\" : \"off\"");
        text.IndexOf("var v", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("probe()", StringComparison.Ordinal));
        text.IndexOf("probe()", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("result =", StringComparison.Ordinal));
    }

    private static IReadOnlyList<Avm1Action> BuildConditionalObject(bool includeProbe)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("first")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember()
        };
        if (includeProbe)
        {
            actions.Add(new ActionPush([
                PushValue.Integer(0),
                PushValue.String("probe")
            ]));
            actions.Add(new ActionCallFunction());
            actions.Add(new ActionPop());
        }

        actions.AddRange([
            new ActionPush([PushValue.String("state")]),
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("off")]),
            new ActionJump(0),
            new ActionPush([PushValue.String("on")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionInitObject(),
            new ActionSetVariable()
        ]);

        var branchIndex = includeProbe ? 12 : 9;
        var jumpIndex = branchIndex + 2;
        var trueIndex = jumpIndex + 1;
        var mergeIndex = trueIndex + 1;
        SetBranch(actions, branchIndex, trueIndex);
        SetBranch(actions, jumpIndex, mergeIndex);
        return actions;
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
