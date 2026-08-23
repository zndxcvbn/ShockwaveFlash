using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1DynamicStackVerifierTests
{
    [Fact]
    public void Verifier_accepts_known_counts_for_variadic_actions()
    {
        (string Name, Avm1Action[] Actions, int MaximumStackDepth)[] cases =
        [
            (
                "CallFunction",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("right"),
                        PushValue.String("left"),
                        PushValue.Integer(2),
                        PushValue.String("fn")
                    ]),
                    new ActionCallFunction(),
                    new ActionPop()),
                4),
            (
                "CallMethod",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("right"),
                        PushValue.String("left"),
                        PushValue.Integer(2),
                        PushValue.String("object"),
                        PushValue.String("method")
                    ]),
                    new ActionCallMethod(),
                    new ActionPop()),
                5),
            (
                "NewObject",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("right"),
                        PushValue.String("left"),
                        PushValue.Integer(2),
                        PushValue.String("Type")
                    ]),
                    new ActionNewObject(),
                    new ActionPop()),
                4),
            (
                "NewMethod",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("right"),
                        PushValue.String("left"),
                        PushValue.Integer(2),
                        PushValue.String("object"),
                        PushValue.String("Type")
                    ]),
                    new ActionNewMethod(),
                    new ActionPop()),
                5),
            (
                "InitArray",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("second"),
                        PushValue.String("first"),
                        PushValue.Integer(2)
                    ]),
                    new ActionInitArray(),
                    new ActionPop()),
                3),
            (
                "InitObject",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("second"),
                        PushValue.Integer(2),
                        PushValue.String("first"),
                        PushValue.Integer(1),
                        PushValue.Integer(2)
                    ]),
                    new ActionInitObject(),
                    new ActionPop()),
                5),
            (
                "ImplementsOp",
                WithEnd(
                    new ActionPush(
                    [
                        PushValue.String("IFirst"),
                        PushValue.String("ISecond"),
                        PushValue.Integer(2),
                        PushValue.String("Class")
                    ]),
                    new ActionImplementsOp()),
                4)
        ];

        foreach (var fixture in cases)
        {
            var result = Verify(fixture.Actions);

            result.Succeeded.ShouldBeTrue(Describe(fixture.Name, result));
            result.MaximumStackDepth.ShouldBe(fixture.MaximumStackDepth);
            result.MaximumStackDepthIsExact.ShouldBeTrue();
        }
    }

    [Fact]
    public void Verifier_accepts_both_start_drag_stack_shapes()
    {
        var unconstrained = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Boolean(false),
                    PushValue.Boolean(false),
                    PushValue.String("target")
                ]),
                new ActionStartDrag()));
        var constrained = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Integer(0),
                    PushValue.Integer(0),
                    PushValue.Integer(100),
                    PushValue.Integer(100),
                    PushValue.Boolean(true),
                    PushValue.Boolean(false),
                    PushValue.String("target")
                ]),
                new ActionStartDrag()));

        unconstrained.Succeeded.ShouldBeTrue(
            Describe("unconstrained StartDrag", unconstrained));
        unconstrained.MaximumStackDepth.ShouldBe(3);
        constrained.Succeeded.ShouldBeTrue(
            Describe("constrained StartDrag", constrained));
        constrained.MaximumStackDepth.ShouldBe(7);
    }

    [Fact]
    public void Verifier_rejects_an_unknown_start_drag_constraint()
    {
        var result = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Register(1),
                    PushValue.Boolean(false),
                    PushValue.String("target")
                ]),
                new ActionStartDrag()));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER014" && diagnostic.ActionIndex == 1);
    }

    [Fact]
    public void Verifier_rejects_unknown_and_negative_variadic_counts()
    {
        var unknown = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Undefined(),
                    PushValue.String("fn")
                ]),
                new ActionCallFunction(),
                new ActionPop()));
        var negative = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Integer(-1),
                    PushValue.String("Type")
                ]),
                new ActionNewObject(),
                new ActionPop()));

        unknown.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER012" && diagnostic.ActionIndex == 1);
        negative.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER012" && diagnostic.ActionIndex == 1);
    }

    [Fact]
    public void Verifier_accepts_integral_floating_variadic_counts()
    {
        var doubleCount = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Double(0),
                    PushValue.String("fn")
                ]),
                new ActionCallFunction(),
                new ActionPop()));
        var floatCount = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.String("argument"),
                    PushValue.Float(1),
                    PushValue.String("fn")
                ]),
                new ActionCallFunction(),
                new ActionPop()));

        doubleCount.Succeeded.ShouldBeTrue(
            Describe("double count", doubleCount));
        floatCount.Succeeded.ShouldBeTrue(
            Describe("float count", floatCount));
    }

    [Fact]
    public void Verifier_reports_underflow_from_a_known_variadic_count()
    {
        var result = Verify(
            WithEnd(
                new ActionPush(
                [
                    PushValue.Integer(2),
                    PushValue.String("fn")
                ]),
                new ActionCallFunction(),
                new ActionPop()));

        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER004" &&
            diagnostic.ActionIndex == 1 &&
            diagnostic.Message.Contains("required 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Cfg_join_preserves_an_identical_variadic_count()
    {
        var body = AssembleCallAfterCountJoin(alternativeCount: 1);

        body.Succeeded.ShouldBeTrue(Describe("identical count join", body));
        body.MaximumStackDepth.ShouldBe(3);
        body.MaximumStackDepthIsExact.ShouldBeTrue();
    }

    [Fact]
    public void Cfg_join_rejects_conflicting_variadic_counts()
    {
        var body = AssembleCallAfterCountJoin(alternativeCount: 0);

        body.Succeeded.ShouldBeFalse();
        body.Bytes.IsEmpty.ShouldBeTrue();
        body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER012");
    }

    [Fact]
    public void Verifier_accepts_a_symbolic_enumeration_loop()
    {
        var assembler = new Avm1ActionAssembler();
        var loop = assembler.DefineLabel();
        var exit = assembler.DefineLabel();
        assembler.Emit(new ActionPush([PushValue.String("object")]));
        assembler.Emit(new ActionGetVariable());
        assembler.Emit(new ActionEnumerate2());
        assembler.MarkLabel(loop);
        assembler.Emit(new ActionStoreRegister(1));
        assembler.Emit(new ActionPush([PushValue.Null()]));
        assembler.Emit(new ActionEquals2());
        assembler.EmitIf(exit);
        assembler.EmitJump(loop);
        assembler.MarkLabel(exit);
        assembler.Emit(new ActionEnd());

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7)
        {
            RequireEndAction = true
        });

        body.Succeeded.ShouldBeTrue(Describe("enumeration loop", body));
        body.MaximumStackDepth.ShouldBe(2);
        body.MaximumStackDepthIsExact.ShouldBeFalse();
    }

    [Fact]
    public void Verifier_accepts_an_inverted_enumeration_cleanup_loop()
    {
        var assembler = new Avm1ActionAssembler();
        var cleanup = assembler.DefineLabel();
        assembler.Emit(new ActionPush([PushValue.String("object")]));
        assembler.Emit(new ActionGetVariable());
        assembler.Emit(new ActionEnumerate2());
        assembler.MarkLabel(cleanup);
        assembler.Emit(new ActionPush([PushValue.Null()]));
        assembler.Emit(new ActionEquals2());
        assembler.Emit(new ActionNot());
        assembler.EmitIf(cleanup);
        assembler.Emit(new ActionEnd());

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7)
        {
            RequireEndAction = true
        });

        body.Succeeded.ShouldBeTrue(Describe("enumeration cleanup", body));
        body.MaximumStackDepthIsExact.ShouldBeFalse();
    }

    [Fact]
    public void Verifier_rejects_an_unstructured_enumeration_consumer()
    {
        var result = Verify(
            WithEnd(
                new ActionPush([PushValue.String("object")]),
                new ActionGetVariable(),
                new ActionEnumerate2(),
                new ActionPop()));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER013" && diagnostic.ActionIndex == 3);
        result.MaximumStackDepthIsExact.ShouldBeFalse();
    }

    [Fact]
    public void Dynamic_stack_depth_propagates_from_a_nested_code_unit()
    {
        var nested = new Avm1CodeUnit();
        var loop = nested.DefineLabel();
        var exit = nested.DefineLabel();
        nested.Emit(new ActionPush([PushValue.String("object")]));
        nested.Emit(new ActionGetVariable());
        nested.Emit(new ActionEnumerate2());
        nested.MarkLabel(loop);
        nested.Emit(new ActionStoreRegister(1));
        nested.Emit(new ActionPush([PushValue.Null()]));
        nested.Emit(new ActionEquals2());
        nested.EmitIf(exit);
        nested.EmitJump(loop);
        nested.MarkLabel(exit);

        var root = new Avm1CodeUnit();
        root.Emit(new ActionPush([PushValue.Undefined()]));
        root.EmitWith(nested);
        root.Emit(new ActionEnd());

        var body = root.Assemble(new Avm1AssemblyOptions(SwfVersion: 7)
        {
            RequireEndAction = true
        });

        body.Succeeded.ShouldBeTrue(Describe("nested enumeration", body));
        body.MaximumStackDepth.ShouldBe(2);
        body.MaximumStackDepthIsExact.ShouldBeFalse();
        body.NestedBodies.ShouldHaveSingleItem().Body
            .MaximumStackDepthIsExact.ShouldBeFalse();
    }

    private static Avm1ActionBody AssembleCallAfterCountJoin(
        int alternativeCount)
    {
        var assembler = new Avm1ActionAssembler();
        var alternative = assembler.DefineLabel();
        var merge = assembler.DefineLabel();
        assembler.Emit(new ActionPush(
        [
            PushValue.String("argument"),
            PushValue.Boolean(true)
        ]));
        assembler.EmitIf(alternative);
        assembler.Emit(new ActionPush([PushValue.Integer(1)]));
        assembler.EmitJump(merge);
        assembler.MarkLabel(alternative);
        assembler.Emit(new ActionPush([PushValue.Integer(alternativeCount)]));
        assembler.MarkLabel(merge);
        assembler.Emit(new ActionPush([PushValue.String("fn")]));
        assembler.Emit(new ActionCallFunction());
        assembler.Emit(new ActionPop());
        assembler.Emit(new ActionEnd());
        return assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7)
        {
            RequireEndAction = true
        });
    }

    private static Avm1BytecodeVerificationResult Verify(
        IReadOnlyList<Avm1Action> actions) =>
        Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7)
            {
                RequireEndAction = true
            });

    private static Avm1Action[] WithEnd(params Avm1Action[] actions) =>
        [.. actions, new ActionEnd()];

    private static string Describe(
        string name,
        Avm1BytecodeVerificationResult result) =>
        name + Environment.NewLine + string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));

    private static string Describe(string name, Avm1ActionBody body) =>
        name + Environment.NewLine + string.Join(
            Environment.NewLine,
            body.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));
}
