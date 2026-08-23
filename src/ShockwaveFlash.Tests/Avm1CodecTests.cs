using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Exceptions;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1CodecTests
{
    [Fact]
    public void Strict_decode_throws_on_an_unknown_push_value_type()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x96, 0x01, 0x00, 0x0A };

        Should.Throw<SwfFormatException>(() => Avm1Action.DecodeCollection(bytecode, swfVersion: 6, strict: true));
    }

    [Fact]
    public void Strict_decode_throws_when_an_action_is_shorter_than_its_declared_length()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x81, 0x03, 0x00, 0x05, 0x00, 0xFF };

        Should.Throw<SwfFormatException>(() => Avm1Action.DecodeCollection(bytecode, swfVersion: 6, strict: true));
    }

    [Fact]
    public void Lenient_decode_skips_an_unknown_push_value_type()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x96, 0x01, 0x00, 0x0A };

        var actions = Avm1Action.DecodeCollection(bytecode, swfVersion: 6);

        actions.Count.ShouldBe(1);
        actions[0].ShouldBeOfType<ActionPush>().PushValues.ShouldBeEmpty();
    }

    [Fact]
    public void Lenient_decode_tolerates_a_trailing_length_mismatch()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x81, 0x03, 0x00, 0x05, 0x00, 0xFF };

        var actions = Avm1Action.DecodeCollection(bytecode, swfVersion: 6);

        actions.Count.ShouldBe(1);
        actions[0].ShouldBeOfType<ActionGotoFrame>();
    }

    [Fact]
    public void With_owns_its_body_and_round_trips()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x94, 0x02, 0x00, 0x02, 0x00, 0x06, 0x17 };

        var actions = Avm1Action.DecodeCollection(bytecode, swfVersion: 6);

        actions.Count.ShouldBe(1);
        actions[0].ShouldBeOfType<ActionWith>();

        Avm1Action.EncodeCollection(actions, swfVersion: 6).ToArray().ShouldBe(bytecode.ToArray());
    }

    [Fact]
    public void DefineFunction2_owns_its_body_and_round_trips()
    {
        var body = new byte[] { 0x06, 0x17 };
        var function = new ActionDefineFunction2("f", 0, (FunctionFlags)0, [], body);

        var encoded = Avm1Action.EncodeCollection([function], swfVersion: 6);
        var decoded = Avm1Action.DecodeCollection(encoded, swfVersion: 6);

        decoded.Count.ShouldBe(1);
        var roundTripped = decoded[0].ShouldBeOfType<ActionDefineFunction2>();
        roundTripped.Body.ToArray().ShouldBe(body);

        Avm1Action.EncodeCollection(decoded, swfVersion: 6).ToArray().ShouldBe(encoded.ToArray());
    }

    [Fact]
    public void Strict_decode_throws_on_invalid_utf8_in_a_string_push()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x96, 0x03, 0x00, 0x00, 0xFF, 0x00 };

        Should.Throw<SwfFormatException>(() => Avm1Action.DecodeCollection(bytecode, swfVersion: 6, strict: true));
    }

    [Fact]
    public void Lenient_decode_replaces_invalid_utf8_in_a_string_push()
    {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x96, 0x03, 0x00, 0x00, 0xFF, 0x00 };

        var actions = Avm1Action.DecodeCollection(bytecode, swfVersion: 6);

        actions[0].ShouldBeOfType<ActionPush>().PushValues.Count.ShouldBe(1);
    }

    [Fact]
    public void Recovery_decode_preserves_a_malformed_action_and_continues()
    {
        ReadOnlyMemory<byte> bytecode = new byte[]
        {
            0x88, 0x04, 0x00,
            0x02, 0x00, 0xFF, 0x00,
            0x06,
            0x00
        };

        var actions = Avm1Action.DecodeCollection(
            bytecode,
            swfVersion: 6,
            Avm1ActionDecodeMode.RecoverMalformed);

        actions.Count.ShouldBe(3);
        var malformed = actions[0].ShouldBeOfType<ActionMalformed>();
        malformed.Opcode.ShouldBe(ActionOpcode.ConstantPool);
        malformed.Data.ToArray().ShouldBe(new byte[] { 0x02, 0x00, 0xFF, 0x00 });
        actions[1].ShouldBeOfType<ActionPlay>();
        actions[2].ShouldBeOfType<ActionEnd>();
        Avm1Action.EncodeCollection(actions, swfVersion: 6).ToArray()
            .ShouldBe(bytecode.ToArray());
    }

    [Fact]
    public void Recovery_decode_does_not_guess_a_malformed_trailer_length()
    {
        ReadOnlyMemory<byte> bytecode = new byte[]
        {
            0x94, 0x01, 0x00, 0x02,
            0x06, 0x00
        };

        Should.Throw<SwfTruncatedException>(() => Avm1Action.DecodeCollection(
            bytecode,
            swfVersion: 6,
            Avm1ActionDecodeMode.RecoverMalformed));
    }

    [Fact]
    public void Decompiler_projects_past_a_malformed_payload_action()
    {
        ReadOnlyMemory<byte> bytecode = new byte[]
        {
            0x88, 0x04, 0x00,
            0x02, 0x00, 0xFF, 0x00,
            0x06,
            0x00
        };

        var method = Avm1Decompiler.DecompileMethod(bytecode, swfVersion: 6);

        method.Instructions.Instructions[0].Action
            .ShouldBeOfType<ActionMalformed>();
        method.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        Should.NotThrow(() => method.ProjectSource().GetAs2Text());
    }

    [Fact]
    public void Recovery_decode_preserves_bytes_after_action_end()
    {
        ReadOnlyMemory<byte> bytecode = new byte[]
        {
            0x06,
            0x00,
            0x96, 0x01, 0x00, 0x0A
        };

        var actions = Avm1Action.DecodeCollection(
            bytecode,
            swfVersion: 6,
            Avm1ActionDecodeMode.RecoverMalformed);

        actions.Count.ShouldBe(3);
        actions[0].ShouldBeOfType<ActionPlay>();
        actions[1].ShouldBeOfType<ActionEnd>();
        actions[2].ShouldBeOfType<ActionTrailingData>().Data.ToArray()
            .ShouldBe(new byte[] { 0x96, 0x01, 0x00, 0x0A });
        Avm1Action.EncodeCollection(actions, swfVersion: 6).ToArray()
            .ShouldBe(bytecode.ToArray());
        Should.Throw<SwfFormatException>(() => Avm1Action.DecodeCollection(
            bytecode,
            swfVersion: 6,
            strict: true));
    }

    [Fact]
    public void Emitting_a_too_deeply_nested_value_tree_throws()
    {
        var root = new Avm1Object();
        var current = root;

        for (var i = 0; i < 300; i++)
        {
            var child = new Avm1Object();
            current["x"] = child;
            current = child;
        }

        Should.Throw<SwfFormatException>(() => Avm1Emitter.Emit(root));
    }
}
