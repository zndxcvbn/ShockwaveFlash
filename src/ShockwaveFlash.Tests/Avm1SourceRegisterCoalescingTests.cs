using System.Text.RegularExpressions;
using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SourceRegisterCoalescingTests
{
    private static int GetEncodedLength(Avm1Action action) =>
        Avm1Action.EncodeCollection([action], swfVersion: 7).Length;

    private static void SetBranch(
        List<Avm1Action> actions,
        int branchIndex,
        int targetIndex)
    {
        var offsets = new int[actions.Count];
        var offset = 0;
        for (var index = 0; index < actions.Count; index++)
        {
            offsets[index] = offset;
            offset += GetEncodedLength(actions[index]);
        }

        var branchOffset = checked((short)(
            offsets[targetIndex] -
            (offsets[branchIndex] + GetEncodedLength(actions[branchIndex]))));
        actions[branchIndex] = new ActionIf(branchOffset);
    }

    private static void SetJump(
        List<Avm1Action> actions,
        int branchIndex,
        int targetIndex)
    {
        var offsets = new int[actions.Count];
        var offset = 0;
        for (var index = 0; index < actions.Count; index++)
        {
            offsets[index] = offset;
            offset += GetEncodedLength(actions[index]);
        }

        var branchOffset = checked((short)(
            offsets[targetIndex] -
            (offsets[branchIndex] + GetEncodedLength(actions[branchIndex]))));
        actions[branchIndex] = new ActionJump(branchOffset);
    }

    [Fact]
    public void Source_projection_coalesces_noninterfering_register_live_ranges()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("first")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("second")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();

        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc2_ = first;",
            "before = _loc2_;",
            "_loc2_ = second;",
            "after = _loc2_;",
            string.Empty
        ]));
        source.Arena.Symbols.Count(symbol =>
            source.Arena[symbol.Name] == "_loc2_").ShouldBe(1);
    }

    [Fact]
    public void Source_projection_coalesces_noninterfering_register_zero_live_ranges()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("first")]),
            new ActionGetVariable(),
            new ActionStoreRegister(0),
            new ActionPop(),
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Register(0)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("second")]),
            new ActionGetVariable(),
            new ActionStoreRegister(0),
            new ActionPop(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(0)]),
            new ActionSetVariable()
        ], swfVersion: 7);

        var source = decompilation.ProjectSource();
        var text = source.GetAs2Text();

        text.ShouldNotMatch(@"\b_loc0_v\d+\b");
        text.ShouldContain("var _loc0_ = first;");
        text.ShouldContain("_loc0_ = second;");
        source.Arena.Symbols.Count(symbol =>
            source.Arena[symbol.Name] == "_loc0_").ShouldBe(1);
    }

    [Fact]
    public void Scope_analysis_keeps_generated_register_names_distinct_from_named_locals()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([
                PushValue.Integer(1),
                PushValue.String("_loc1_")
            ]),
            new ActionStackSwap(),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("Math")]),
            new ActionGetVariable(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([
                PushValue.String("result"),
                PushValue.Register(1)
            ]),
            new ActionSetVariable()
        ], swfVersion: 7);

        var write = decompilation.RegisterSsa.Accesses.Single(access =>
            access.Kind is Avm1.Decompilation.Ssa.Avm1RegisterAccessKind.Write &&
            access.Register == 1);
        decompilation.SymbolTable.TryGetRegisterSymbol(
            write.Register,
            write.Version,
            out var registerSymbol).ShouldBeTrue();

        registerSymbol.Name.ShouldNotBe("_loc1_");
        decompilation.ProjectSource().GetAs2Text().ShouldContain("var _loc1_ = 1;");
    }

    [Fact]
    public void Source_projection_keeps_register_live_ranges_that_interfere_on_the_stack()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("first")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionPush([PushValue.String("second")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        ], swfVersion: 7);

        var text = decompilation.ProjectSource().GetAs2Text();

        Regex.Matches(text, @"_loc2_v\d+")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(2);
        text.ShouldNotContain("result = _loc2_ + 1;");
    }

    [Fact]
    public void Source_projection_coalesces_after_earlier_effect_is_materialized()
    {
        var decompilation = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("first")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Register(2)]),
            new ActionPush([PushValue.String("value")]),
            new ActionCallMethod(),
            new ActionPush([PushValue.String("detached")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Register(2)]),
            new ActionPush([PushValue.String("value")]),
            new ActionCallMethod(),
            new ActionAdd2(),
            new ActionReturn()
        ], swfVersion: 7);

        var text = decompilation.ProjectSource().GetAs2Text();
        var expression = Regex.Match(
            text,
            @"var (?<temporary>v\d+) = _loc2_\.value\(1\);\s*" +
            @"_loc2_ = detached;\s*" +
            @"return \k<temporary> \+ _loc2_\.value\(2\);");

        expression.Success.ShouldBeTrue(text);
        text.ShouldNotMatch(@"\b_loc2_v\d+\b");
    }

    [Fact]
    public void Source_projection_coalesces_live_ranges_in_mutually_exclusive_branches()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("first")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("trueBefore")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("second")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("trueAfter")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("falseValue")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };
        SetBranch(actions, branchIndex: 7, targetIndex: 20);

        var source = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7)
            .ProjectSource();
        var text = source.GetAs2Text();

        text.ShouldNotMatch(@"\b_loc2_v\d+\b");
        Regex.Matches(text, @"\bvar _loc2_\b").Count.ShouldBe(1);
        text.ShouldContain("_loc2_ = second;");
        source.Arena.Symbols.Count(symbol =>
            source.Arena[symbol.Name] == "_loc2_").ShouldBe(1);
    }

    [Fact]
    public void Scope_analysis_does_not_coalesce_through_an_unread_outer_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("Selection")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("left")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.String("right")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };
        SetBranch(actions, branchIndex: 7, targetIndex: 24);
        SetBranch(actions, branchIndex: 11, targetIndex: 17);
        SetJump(actions, branchIndex: 16, targetIndex: 21);

        var decompilation = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var writes = decompilation.RegisterSsa.Accesses
            .Where(access =>
                access.Kind is Avm1.Decompilation.Ssa.Avm1RegisterAccessKind.Write &&
                access.Register == 2)
            .ToArray();
        writes.Length.ShouldBe(3);

        decompilation.SymbolTable.TryGetRegisterSymbol(
            writes[0].Register,
            writes[0].Version,
            out var transport).ShouldBeTrue();
        decompilation.SymbolTable.TryGetRegisterSymbol(
            writes[1].Register,
            writes[1].Version,
            out var left).ShouldBeTrue();
        decompilation.SymbolTable.TryGetRegisterSymbol(
            writes[2].Register,
            writes[2].Version,
            out var right).ShouldBeTrue();

        ReferenceEquals(transport, left).ShouldBeFalse();
        ReferenceEquals(left, right).ShouldBeTrue();
    }
}
