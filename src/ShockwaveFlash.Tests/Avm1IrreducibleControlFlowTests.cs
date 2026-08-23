using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1IrreducibleControlFlowTests
{
    [Fact]
    public void Multi_entry_scc_is_preserved_as_an_incomplete_opaque_method()
    {
        var body = AssembleIrreducibleMethod();

        var decompilation = Avm1Decompiler.DecompileMethod(
            body.Bytes,
            swfVersion: 7);

        decompilation.IrreducibleControlFlow.IsIrreducible.ShouldBeTrue();
        var region = decompilation.IrreducibleControlFlow.Regions.ShouldHaveSingleItem();
        region.Entries.Count.ShouldBe(2);
        region.Blocks.Count.ShouldBeGreaterThan(1);
        decompilation.Instructions.RootBytecode.Span.SequenceEqual(
            body.Bytes.Span).ShouldBeTrue();

        var source = decompilation.ProjectSource();

        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC005" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("multi-entry", StringComparison.Ordinal));
        var opaque = source.Arena.OpaqueRegions.ShouldHaveSingleItem();
        opaque.Kind.ShouldBe(Avm1SourceOpaqueKind.RecoveryStatement);
        source.Arena.GetBytecode(opaque).Span.SequenceEqual(body.Bytes.Span).ShouldBeTrue();

        var text = decompilation.GetStructuredAs2Text();
        text.ShouldContain("irreducible AVM1 control flow");
        text.ShouldNotContain("nextFrame");
        text.ShouldNotContain("prevFrame");
        text.ShouldBe(source.GetAs2Text());
    }

    [Fact]
    public void Natural_loop_with_one_entry_remains_reducible()
    {
        var codeUnit = new Avm1CodeUnit();
        var loop = codeUnit.DefineLabel();
        var exit = codeUnit.DefineLabel();
        codeUnit.MarkLabel(loop);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        codeUnit.EmitIf(exit);
        codeUnit.Emit(new ActionNextFrame());
        codeUnit.EmitJump(loop);
        codeUnit.MarkLabel(exit);
        codeUnit.Emit(new ActionEnd());
        var body = codeUnit.Assemble(RootOptions());
        body.Succeeded.ShouldBeTrue(Describe(body));

        var decompilation = Avm1Decompiler.DecompileMethod(
            body.Bytes,
            swfVersion: 7);

        decompilation.IrreducibleControlFlow.IsIrreducible.ShouldBeFalse();
        decompilation.IrreducibleControlFlow.Regions.ShouldBeEmpty();
    }

    [Fact]
    public void Instruction_table_separates_decoded_length_from_original_input()
    {
        ReadOnlyMemory<byte> bytecode = new byte[]
        {
            (byte)ActionOpcode.End,
            0xFF,
            0xFF
        };

        var core = Avm1Decompiler.BuildMethodCore(bytecode, swfVersion: 7);

        core.Instructions.ByteLength.ShouldBe(bytecode.Length);
        core.Instructions.Instructions[^1].Action
            .ShouldBeOfType<ActionTrailingData>();
        core.Instructions.RootBytecode.Span.SequenceEqual(bytecode.Span).ShouldBeTrue();
    }

    private static Avm1ActionBody AssembleIrreducibleMethod()
    {
        var codeUnit = new Avm1CodeUnit();
        var left = codeUnit.DefineLabel();
        var right = codeUnit.DefineLabel();
        var dispatch = codeUnit.DefineLabel();

        codeUnit.Emit(new ActionPush([PushValue.Boolean(true)]));
        codeUnit.EmitIf(right);
        codeUnit.MarkLabel(left);
        codeUnit.Emit(new ActionNextFrame());
        codeUnit.EmitJump(dispatch);
        codeUnit.MarkLabel(right);
        codeUnit.Emit(new ActionPreviousFrame());
        codeUnit.EmitJump(dispatch);
        codeUnit.MarkLabel(dispatch);
        codeUnit.Emit(new ActionPush([PushValue.Boolean(false)]));
        codeUnit.EmitIf(left);
        codeUnit.EmitJump(right);
        codeUnit.Emit(new ActionEnd());

        var body = codeUnit.Assemble(RootOptions());
        body.Succeeded.ShouldBeTrue(Describe(body));
        return body;
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
