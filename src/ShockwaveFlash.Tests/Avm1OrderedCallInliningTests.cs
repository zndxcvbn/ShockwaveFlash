using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1OrderedCallInliningTests
{
    [Fact]
    public void Reconsiders_call_argument_after_nested_member_inlining()
    {
        var text = Avm1Decompiler.DecompileMethod(
            BuildCall(includeProbe: false),
            swfVersion: 7).GetStructuredAs2Text();

        text.ShouldBe(
            "receiver.method(entries[index], state);" + Environment.NewLine);
    }

    [Fact]
    public void Keeps_call_argument_temporary_across_an_independent_call()
    {
        var text = Avm1Decompiler.DecompileMethod(
            BuildCall(includeProbe: true),
            swfVersion: 7).GetStructuredAs2Text();

        text.ShouldContain("var v1 = state;");
        text.ShouldContain("probe();");
        text.ShouldContain("receiver.method(entries[index], v1);");
        text.IndexOf("var v1", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("probe()", StringComparison.Ordinal));
        text.IndexOf("probe()", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("receiver.method", StringComparison.Ordinal));
    }

    private static IReadOnlyList<Avm1Action> BuildCall(bool includeProbe)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("state")]),
            new ActionGetVariable()
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
            new ActionPush([PushValue.String("entries")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("index")]),
            new ActionGetVariable(),
            new ActionGetMember(),
            new ActionPush([
                PushValue.Integer(2),
                PushValue.String("receiver")
            ]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("method")]),
            new ActionCallMethod(),
            new ActionPop()
        ]);
        return actions;
    }
}
