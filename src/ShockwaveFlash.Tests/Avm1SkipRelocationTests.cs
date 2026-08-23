using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SkipRelocationTests
{
    [Fact]
    public void Wait_for_frame_counts_nested_function_as_one_outer_action()
    {
        var functionBody = new Avm1CodeUnit();
        for (var i = 0; i < 64; i++)
            functionBody.Emit(new ActionNextFrame());

        var root = new Avm1CodeUnit();
        var target = root.DefineLabel();
        var wait = root.EmitWaitForFrame(frame: 17, target);
        root.EmitDefineFunction("largeBody", [], functionBody);
        root.MarkLabel(target);
        var end = root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var waitAction = result.Actions[0].ShouldBeOfType<ActionWaitForFrame>();
        waitAction.Frame.ShouldBe((ushort)17);
        waitAction.SkipCount.ShouldBe((byte)1);
        result.GetLabelActionIndex(target).ShouldBe(2);
        result.GetLabelOffset(target).ShouldBe(result.GetLocation(end).ByteOffset);
        result.GetLocation(wait).ActionIndex.ShouldBe(0);
        var function = result.Actions[1].ShouldBeOfType<ActionDefineFunction>();
        function.Body.Length.ShouldBe(64);
    }

    [Fact]
    public void Wait_for_frame2_preserves_stack_depth_on_both_successors()
    {
        var root = new Avm1CodeUnit();
        var target = root.DefineLabel();
        root.Emit(new ActionPush([PushValue.Integer(10)]));
        root.EmitWaitForFrame2(target);
        root.Emit(new ActionPush([PushValue.String("loaded")]));
        root.Emit(new ActionTrace());
        root.MarkLabel(target);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.MaximumStackDepth.ShouldBe(1);
        result.Actions[1].ShouldBeOfType<ActionWaitForFrame2>()
            .SkipCount.ShouldBe((byte)2);
        result.GetLabelActionIndex(target).ShouldBe(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Wait_for_frame_supports_ui8_skip_boundaries(int skippedActionCount)
    {
        var assembler = new Avm1ActionAssembler();
        var target = assembler.DefineLabel();
        assembler.EmitWaitForFrame(frame: 1, target);
        for (var i = 0; i < skippedActionCount; i++)
            assembler.Emit(new ActionNextFrame());
        assembler.MarkLabel(target);
        assembler.Emit(new ActionEnd());

        var result = assembler.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Actions[0].ShouldBeOfType<ActionWaitForFrame>()
            .SkipCount.ShouldBe((byte)skippedActionCount);
        result.GetLabelActionIndex(target).ShouldBe(skippedActionCount + 1);
    }

    [Fact]
    public void Wait_for_frame_rejects_skip_count_overflow()
    {
        var assembler = new Avm1ActionAssembler();
        var target = assembler.DefineLabel();
        var wait = assembler.EmitWaitForFrame(frame: 1, target);
        for (var i = 0; i < 256; i++)
            assembler.Emit(new ActionNextFrame());
        assembler.MarkLabel(target);
        assembler.Emit(new ActionEnd());

        var result = assembler.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM013" &&
            diagnostic.Instruction == wait &&
            diagnostic.Message.Contains("SkipCount 256", StringComparison.Ordinal));
    }

    [Fact]
    public void Wait_for_frame_rejects_backward_skip_target()
    {
        var assembler = new Avm1ActionAssembler();
        var target = assembler.DefineLabel();
        assembler.MarkLabel(target);
        var wait = assembler.EmitWaitForFrame(frame: 1, target);
        assembler.Emit(new ActionEnd());

        var result = assembler.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM013" &&
            diagnostic.Instruction == wait &&
            diagnostic.Message.Contains("SkipCount -1", StringComparison.Ordinal));
    }

    [Fact]
    public void Assembler_rejects_concrete_wait_for_frame_actions()
    {
        var assembler = new Avm1ActionAssembler();

        Should.Throw<ArgumentException>(() =>
            assembler.Emit(new ActionWaitForFrame(frame: 1, skipCount: 0)));
        Should.Throw<ArgumentException>(() =>
            assembler.Emit(new ActionWaitForFrame2(skipCount: 0)));
    }

    [Fact]
    public void Verifier_rejects_skip_target_past_code_unit_end()
    {
        Avm1Action[] actions =
        [
            new ActionWaitForFrame(frame: 1, skipCount: 2),
            new ActionEnd()
        ];

        var result = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER011" && diagnostic.ActionIndex == 0);
    }

    [Fact]
    public void Verifier_checks_stack_join_between_fallthrough_and_skip_path()
    {
        Avm1Action[] actions =
        [
            new ActionWaitForFrame(frame: 1, skipCount: 1),
            new ActionPush([PushValue.Integer(42)]),
            new ActionEnd()
        ];

        var result = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER005" && diagnostic.ActionIndex == 2);
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
