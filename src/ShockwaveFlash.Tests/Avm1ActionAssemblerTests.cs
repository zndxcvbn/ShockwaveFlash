using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1ActionAssemblerTests
{
    [Fact]
    public void Assembler_resolves_forward_and_backward_branches_deterministically()
    {
        var assembler = new Avm1ActionAssembler();
        var loop = assembler.DefineLabel();
        var exit = assembler.DefineLabel();

        assembler.MarkLabel(loop);
        var push = assembler.Emit(new ActionPush([PushValue.Boolean(true)]));
        var conditional = assembler.EmitIf(exit);
        var backEdge = assembler.EmitJump(loop);
        assembler.MarkLabel(exit);
        var end = assembler.Emit(new ActionEnd());

        var options = new Avm1AssemblyOptions(SwfVersion: 7)
        {
            RequireEndAction = true
        };
        var first = assembler.Assemble(options);
        var second = assembler.Assemble(options);

        first.Succeeded.ShouldBeTrue(Describe(first));
        first.Diagnostics.ShouldBeEmpty();
        first.MaximumStackDepth.ShouldBe(1);
        first.Actions.Count.ShouldBe(4);
        first.InstructionLocations.Count.ShouldBe(4);
        first.GetLabelOffset(loop).ShouldBe(0);
        first.GetLabelOffset(exit).ShouldBe(first.GetLocation(end).ByteOffset);
        first.GetLocation(push).ActionIndex.ShouldBe(0);

        var conditionalAction = first.Actions[1].ShouldBeOfType<ActionIf>();
        conditionalAction.BranchOffset.ShouldBe(checked((short)(
            first.GetLabelOffset(exit) -
            (first.GetLocation(conditional).ByteOffset +
                first.GetLocation(conditional).ByteLength))));
        var backAction = first.Actions[2].ShouldBeOfType<ActionJump>();
        backAction.BranchOffset.ShouldBe(checked((short)(
            first.GetLabelOffset(loop) -
            (first.GetLocation(backEdge).ByteOffset +
                first.GetLocation(backEdge).ByteLength))));

        first.Bytes.Span.SequenceEqual(second.Bytes.Span).ShouldBeTrue();
        Avm1Action.EncodeCollection(first.Actions, swfVersion: 7).Span
            .SequenceEqual(first.Bytes.Span).ShouldBeTrue();
        Avm1Action.DecodeCollection(first.Bytes, swfVersion: 7, strict: true)
            .Count.ShouldBe(first.Actions.Count);
    }

    [Fact]
    public void Assembler_allows_branch_to_code_unit_end_boundary()
    {
        var assembler = new Avm1ActionAssembler();
        var exit = assembler.DefineLabel();
        assembler.Emit(new ActionPush([PushValue.Boolean(true)]));
        assembler.EmitIf(exit);
        assembler.MarkLabel(exit);

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7));

        body.Succeeded.ShouldBeTrue(Describe(body));
        body.GetLabelOffset(exit).ShouldBe(body.Bytes.Length);
    }

    [Fact]
    public void Assembler_rejects_referenced_but_unmarked_label()
    {
        var assembler = new Avm1ActionAssembler();
        var missing = assembler.DefineLabel();
        assembler.EmitJump(missing);

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7));

        body.Succeeded.ShouldBeFalse();
        body.Bytes.IsEmpty.ShouldBeTrue();
        body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM001");
    }

    [Fact]
    public void Assembler_rejects_duplicate_label_mark()
    {
        var assembler = new Avm1ActionAssembler();
        var label = assembler.DefineLabel();
        assembler.MarkLabel(label);
        assembler.Emit(new ActionEnd());
        assembler.MarkLabel(label);

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7));

        body.Succeeded.ShouldBeFalse();
        body.Bytes.IsEmpty.ShouldBeTrue();
        body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM002");
    }

    [Fact]
    public void Assembler_rejects_long_branch_across_unbridgeable_action_payload()
    {
        var assembler = new Avm1ActionAssembler();
        var target = assembler.DefineLabel();
        assembler.EmitJump(target);
        assembler.Emit(new ActionPush([PushValue.String(new string('x', 32_768))]));
        assembler.Emit(new ActionPop());
        assembler.MarkLabel(target);
        assembler.Emit(new ActionEnd());

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7));

        body.Succeeded.ShouldBeFalse();
        body.Bytes.IsEmpty.ShouldBeTrue();
        body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM014" &&
            diagnostic.Message.Contains("No action boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Assembler_rejects_direct_concrete_branch_actions()
    {
        var assembler = new Avm1ActionAssembler();

        Should.Throw<ArgumentException>(() => assembler.Emit(new ActionJump(0)));
        Should.Throw<ArgumentException>(() => assembler.Emit(new ActionIf(0)));
    }

    [Fact]
    public void Verifier_rejects_stack_underflow_and_assembler_returns_no_bytes()
    {
        var assembler = new Avm1ActionAssembler();
        var pop = assembler.Emit(new ActionPop());
        assembler.Emit(new ActionEnd());

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7));

        body.Succeeded.ShouldBeFalse();
        body.Bytes.IsEmpty.ShouldBeTrue();
        body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER004" &&
            diagnostic.Instruction == pop);
    }

    [Fact]
    public void Bytecode_verifier_can_model_runtime_compatible_stack_underflow()
    {
        Avm1Action[] actions =
        [
            new ActionPop(),
            new ActionEnd()
        ];

        var result = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7)
            {
                AllowRuntimeStackUnderflow = true
            });

        result.Succeeded.ShouldBeTrue();
        result.MaximumStackDepthIsExact.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER020" &&
            diagnostic.Severity == Avm1CompilationDiagnosticSeverity.Warning &&
            diagnostic.ActionIndex == 0);
    }

    [Fact]
    public void Bytecode_verifier_can_model_a_pop_that_normalizes_a_join()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Boolean(true)]),
            new ActionIf(5),
            new ActionJump(8),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPop(),
            new ActionEnd()
        ];

        var strict = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7));
        var compatible = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7)
            {
                AllowRuntimeStackUnderflow = true
            });

        strict.Succeeded.ShouldBeFalse();
        strict.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER005");
        compatible.Succeeded.ShouldBeTrue();
        compatible.MaximumStackDepthIsExact.ShouldBeFalse();
        compatible.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER021" &&
            diagnostic.Severity == Avm1CompilationDiagnosticSeverity.Warning &&
            diagnostic.ActionIndex == 4);
    }

    [Fact]
    public void Verifier_rejects_inconsistent_stack_depth_at_join()
    {
        var assembler = new Avm1ActionAssembler();
        var emptyPath = assembler.DefineLabel();
        var merge = assembler.DefineLabel();
        assembler.Emit(new ActionPush([PushValue.Boolean(true)]));
        assembler.EmitIf(emptyPath);
        assembler.Emit(new ActionPush([PushValue.Integer(1)]));
        assembler.EmitJump(merge);
        assembler.MarkLabel(emptyPath);
        assembler.MarkLabel(merge);
        assembler.Emit(new ActionEnd());

        var body = assembler.Assemble(new Avm1AssemblyOptions(SwfVersion: 7));

        body.Succeeded.ShouldBeFalse();
        body.Bytes.IsEmpty.ShouldBeTrue();
        body.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER005");
    }

    [Fact]
    public void Bytecode_verifier_rejects_branch_into_action_payload()
    {
        Avm1Action[] actions =
        [
            new ActionJump(-1),
            new ActionEnd()
        ];

        var result = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER003" && diagnostic.ActionIndex == 0);
    }

    [Fact]
    public void Bytecode_verifier_rejects_bytes_after_action_end()
    {
        ReadOnlyMemory<byte> bytes = new byte[]
        {
            (byte)ActionOpcode.End,
            (byte)ActionOpcode.NextFrame
        };

        var result = Avm1BytecodeVerifier.Verify(
            bytes,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER001" &&
            diagnostic.Message.Contains("after ActionEnd", StringComparison.Ordinal));
    }

    [Fact]
    public void Bytecode_verifier_recursively_checks_nested_action_bodies()
    {
        var invalidNestedBody = Avm1Action.EncodeCollection(
            [new ActionPop()],
            swfVersion: 7);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Undefined()]),
            new ActionWith(invalidNestedBody),
            new ActionEnd()
        ];

        var result = Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(SwfVersion: 7));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER010" &&
            diagnostic.Message.Contains("AVM1VER004", StringComparison.Ordinal));
    }

    private static string Describe(Avm1ActionBody body) =>
        string.Join(
            Environment.NewLine,
            body.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));
}
