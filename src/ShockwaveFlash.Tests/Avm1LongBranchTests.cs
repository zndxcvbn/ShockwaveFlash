using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1LongBranchTests
{
    private const int FillerActionPairs = 80;
    private static readonly string FillerPayload = new('x', 1024);

    [Fact]
    public void Conditional_long_branch_uses_guarded_trampoline_chain()
    {
        var codeUnit = new Avm1CodeUnit();
        var target = codeUnit.DefineLabel();
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        var conditional = codeUnit.EmitIf(target);
        EmitFiller(codeUnit);
        codeUnit.MarkLabel(target);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.ActionLocations.Count.ShouldBe(result.Actions.Count);
        result.Actions.Count.ShouldBeGreaterThan(codeUnit.InstructionCount);
        var synthetic = result.ActionLocations
            .Where(location =>
                location.SyntheticKind is not Avm1SyntheticActionKind.None)
            .ToArray();
        synthetic.Length.ShouldBeGreaterThanOrEqualTo(4);
        synthetic.Count(location => location.SyntheticKind is
            Avm1SyntheticActionKind.TrampolineGuard).ShouldBe(
                synthetic.Length / 2);
        synthetic.Count(location => location.SyntheticKind is
            Avm1SyntheticActionKind.TrampolineJump).ShouldBe(
                synthetic.Length / 2);
        synthetic.ShouldAllBe(location => location.Owner == conditional);

        var source = result.GetLocation(conditional);
        result.ActionLocations[source.ActionIndex].SyntheticKind.ShouldBe(
            Avm1SyntheticActionKind.None);
        GetBranchTarget(result, source.ActionIndex).SyntheticKind.ShouldBe(
            Avm1SyntheticActionKind.TrampolineJump);

        foreach (var guard in synthetic.Where(location =>
            location.SyntheticKind is Avm1SyntheticActionKind.TrampolineGuard))
        {
            GetBranchTarget(result, guard.ActionIndex).SyntheticKind.ShouldBe(
                Avm1SyntheticActionKind.None);
        }
    }

    [Fact]
    public void Backward_long_jump_uses_trampolines_without_changing_stack_depth()
    {
        var codeUnit = new Avm1CodeUnit();
        var loop = codeUnit.DefineLabel();
        codeUnit.MarkLabel(loop);
        EmitFiller(codeUnit);
        var backEdge = codeUnit.EmitJump(loop);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.MaximumStackDepth.ShouldBe(1);
        result.GetLabelOffset(loop).ShouldBe(0);
        var synthetic = result.ActionLocations
            .Where(location =>
                location.SyntheticKind is not Avm1SyntheticActionKind.None)
            .ToArray();
        synthetic.Length.ShouldBeGreaterThanOrEqualTo(4);
        synthetic.ShouldAllBe(location => location.Owner == backEdge);
        GetBranchTarget(
            result,
            result.GetLocation(backEdge).ActionIndex).SyntheticKind.ShouldBe(
                Avm1SyntheticActionKind.TrampolineJump);
    }

    [Fact]
    public void Forward_long_branches_share_a_compatible_trampoline_chain()
    {
        var codeUnit = new Avm1CodeUnit();
        var target = codeUnit.DefineLabel();
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        var first = codeUnit.EmitIf(target);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(true)]));
        var second = codeUnit.EmitIf(target);
        EmitFiller(codeUnit);
        codeUnit.MarkLabel(target);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var firstTarget = GetBranchTarget(
            result,
            result.GetLocation(first).ActionIndex);
        var secondTarget = GetBranchTarget(
            result,
            result.GetLocation(second).ActionIndex);
        secondTarget.ActionIndex.ShouldBe(firstTarget.ActionIndex);
        firstTarget.SyntheticKind.ShouldBe(
            Avm1SyntheticActionKind.TrampolineJump);

        var synthetic = result.ActionLocations
            .Where(location =>
                location.SyntheticKind is not Avm1SyntheticActionKind.None)
            .ToArray();
        synthetic.Length.ShouldBe(4);
        synthetic.ShouldAllBe(location => location.Owner == first);
        synthetic.ShouldNotContain(location => location.Owner == second);
    }

    [Fact]
    public void Backward_long_branches_share_a_compatible_trampoline_chain()
    {
        var codeUnit = new Avm1CodeUnit();
        var target = codeUnit.DefineLabel();
        codeUnit.MarkLabel(target);
        EmitFiller(codeUnit);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        var first = codeUnit.EmitIf(target);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(true)]));
        var second = codeUnit.EmitIf(target);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var firstTarget = GetBranchTarget(
            result,
            result.GetLocation(first).ActionIndex);
        var secondTarget = GetBranchTarget(
            result,
            result.GetLocation(second).ActionIndex);
        secondTarget.ActionIndex.ShouldBe(firstTarget.ActionIndex);
        firstTarget.SyntheticKind.ShouldBe(
            Avm1SyntheticActionKind.TrampolineJump);

        var synthetic = result.ActionLocations
            .Where(location =>
                location.SyntheticKind is not Avm1SyntheticActionKind.None)
            .ToArray();
        synthetic.Length.ShouldBe(4);
        synthetic.ShouldAllBe(location => location.Owner == first);
        synthetic.ShouldNotContain(location => location.Owner == second);
    }

    [Fact]
    public void Long_branches_with_different_targets_do_not_share_trampolines()
    {
        var codeUnit = new Avm1CodeUnit();
        var firstTarget = codeUnit.DefineLabel();
        var secondTarget = codeUnit.DefineLabel();
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        var first = codeUnit.EmitIf(firstTarget);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(true)]));
        var second = codeUnit.EmitIf(secondTarget);
        EmitFiller(codeUnit);
        codeUnit.MarkLabel(firstTarget);
        codeUnit.Emit(new ActionNextFrame());
        codeUnit.MarkLabel(secondTarget);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        GetBranchTarget(result, result.GetLocation(first).ActionIndex)
            .ActionIndex.ShouldNotBe(
                GetBranchTarget(result, result.GetLocation(second).ActionIndex)
                    .ActionIndex);
        result.ActionLocations.ShouldContain(location =>
            location.SyntheticKind == Avm1SyntheticActionKind.TrampolineJump &&
            location.Owner == first);
        result.ActionLocations.ShouldContain(location =>
            location.SyntheticKind == Avm1SyntheticActionKind.TrampolineJump &&
            location.Owner == second);
        result.ActionLocations.Count(location =>
            location.SyntheticKind != Avm1SyntheticActionKind.None).ShouldBe(8);
    }

    [Fact]
    public void Short_branch_does_not_create_synthetic_actions()
    {
        var codeUnit = new Avm1CodeUnit();
        var target = codeUnit.DefineLabel();
        codeUnit.EmitJump(target);
        codeUnit.Emit(new ActionNextFrame());
        codeUnit.MarkLabel(target);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Actions.Count.ShouldBe(codeUnit.InstructionCount);
        result.ActionLocations.ShouldAllBe(location =>
            location.SyntheticKind == Avm1SyntheticActionKind.None);
    }

    [Fact]
    public void Wait_skip_is_relocated_after_trampoline_insertion()
    {
        var codeUnit = new Avm1CodeUnit();
        var target = codeUnit.DefineLabel();
        codeUnit.EmitWaitForFrame(frame: 1, target);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        codeUnit.EmitIf(target);
        EmitFiller(codeUnit);
        codeUnit.MarkLabel(target);
        codeUnit.Emit(new ActionEnd());

        var result = codeUnit.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        var wait = result.Actions[0].ShouldBeOfType<ActionWaitForFrame>();
        var physicalTarget = result.GetLabelActionIndex(target);
        wait.SkipCount.ShouldBe((byte)(physicalTarget - 1));
        wait.SkipCount.ShouldBeGreaterThan(
            (byte)(codeUnit.InstructionCount - 2));
        result.ActionLocations.ShouldContain(location =>
            location.SyntheticKind == Avm1SyntheticActionKind.TrampolineJump);
    }

    private static void EmitFiller(Avm1CodeUnit codeUnit)
    {
        for (var i = 0; i < FillerActionPairs; i++)
        {
            codeUnit.Emit(new ActionPush([PushValue.String(FillerPayload)]));
            codeUnit.Emit(new ActionPop());
        }
    }

    private static Avm1AssemblyActionLocation GetBranchTarget(
        Avm1ActionBody body,
        int actionIndex)
    {
        var source = body.ActionLocations[actionIndex];
        var displacement = body.Actions[actionIndex] switch
        {
            ActionJump jump => jump.BranchOffset,
            ActionIf conditional => conditional.BranchOffset,
            _ => throw new InvalidOperationException(
                $"Action {actionIndex} is not a byte-relative branch.")
        };
        var targetOffset = source.ByteOffset + source.ByteLength +
            displacement;
        return body.ActionLocations
            .Where(location => location.ByteOffset == targetOffset)
            .ShouldHaveSingleItem();
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
