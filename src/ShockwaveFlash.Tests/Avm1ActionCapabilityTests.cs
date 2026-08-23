using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf2;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1ActionCapabilityTests
{
    [Fact]
    public void Capability_table_covers_every_declared_opcode_at_the_specified_version()
    {
        var expectedOpcodes = new List<ActionOpcode>();

        foreach (var (minimumSwfVersion, opcodes) in OpcodeVersionGroups)
        {
            foreach (var opcode in opcodes)
            {
                expectedOpcodes.Add(opcode);
                Avm1ActionCapabilities.TryGetMinimumSwfVersion(
                    opcode,
                    out var actualVersion).ShouldBeTrue();
                actualVersion.ShouldBe(minimumSwfVersion);
                Avm1ActionCapabilities.GetMinimumSwfVersion(opcode)
                    .ShouldBe(minimumSwfVersion);
            }
        }

        expectedOpcodes.Count.ShouldBe(expectedOpcodes.Distinct().Count());
        expectedOpcodes.OrderBy(opcode => (byte)opcode).ToArray().ShouldBe(
            Enum.GetValues<ActionOpcode>()
                .OrderBy(opcode => (byte)opcode)
                .ToArray());
    }

    [Fact]
    public void Unknown_opcode_is_not_reported_as_supported()
    {
        var opcode = (ActionOpcode)0xFF;

        Avm1ActionCapabilities.TryGetMinimumSwfVersion(opcode, out _)
            .ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Avm1ActionCapabilities.GetMinimumSwfVersion(opcode));
    }

    [Fact]
    public void Push_value_forms_have_independent_version_requirements()
    {
        (PushValue Value, byte MinimumVersion)[] forms =
        [
            (PushValue.String("value"), 4),
            (PushValue.Float(1.5f), 4),
            (PushValue.Null(), 5),
            (PushValue.Undefined(), 5),
            (PushValue.Register(0), 5),
            (PushValue.Boolean(true), 5),
            (PushValue.Double(1.5), 5),
            (PushValue.Integer(1), 5),
            (PushValue.Constant8(0), 5),
            (PushValue.Constant16(0), 5)
        ];

        foreach (var (value, minimumVersion) in forms)
        {
            Avm1ActionCapabilities.GetMinimumSwfVersion(value)
                .ShouldBe(minimumVersion);
            Avm1ActionCapabilities.GetMinimumSwfVersion(
                new ActionPush([value])).ShouldBe(minimumVersion);
        }

        var mixed = new ActionPush([
            PushValue.String("value"),
            PushValue.Integer(1)
        ]);
        Avm1ActionCapabilities.GetMinimumSwfVersion(mixed).ShouldBe((byte)5);
        Avm1ActionCapabilities.IsSupported(mixed, swfVersion: 4).ShouldBeFalse();
        Avm1ActionCapabilities.IsSupported(mixed, swfVersion: 5).ShouldBeTrue();
    }

    [Fact]
    public void Verifier_enforces_action_version_boundaries()
    {
        var rejected = Verify(1, new ActionStopSounds(), new ActionEnd());
        var accepted = Verify(2, new ActionStopSounds(), new ActionEnd());

        rejected.Succeeded.ShouldBeFalse();
        rejected.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.ActionIndex == 0 &&
            diagnostic.Message.Contains("requires SWF 2", StringComparison.Ordinal));
        accepted.Succeeded.ShouldBeTrue(Describe(accepted));
    }

    [Fact]
    public void Swf4_accepts_original_push_forms_and_rejects_swf5_forms()
    {
        var accepted = Verify(
            4,
            new ActionPush([
                PushValue.String("value"),
                PushValue.Float(1.5f)
            ]),
            new ActionPop(),
            new ActionPop(),
            new ActionEnd());
        var rejected = Verify(
            4,
            new ActionPush([PushValue.Integer(1)]),
            new ActionPop(),
            new ActionEnd());
        var acceptedInSwf5 = Verify(
            5,
            new ActionPush([PushValue.Integer(1)]),
            new ActionPop(),
            new ActionEnd());

        accepted.Succeeded.ShouldBeTrue(Describe(accepted));
        rejected.Succeeded.ShouldBeFalse();
        rejected.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Message.Contains("integer value form", StringComparison.Ordinal));
        acceptedInSwf5.Succeeded.ShouldBeTrue(Describe(acceptedInSwf5));
    }

    [Fact]
    public void Swf7_action_is_rejected_by_swf6_and_accepted_by_swf7()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionThrow(),
            new ActionEnd()
        ];

        var rejected = Verify(6, actions);
        var accepted = Verify(7, actions);

        rejected.Succeeded.ShouldBeFalse();
        rejected.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.ActionIndex == 1 &&
            diagnostic.Message.Contains("Throw requires SWF 7", StringComparison.Ordinal));
        accepted.Succeeded.ShouldBeTrue(Describe(accepted));
    }

    [Fact]
    public void Assembler_rejects_versioned_nested_action_without_returning_bytes()
    {
        var functionBody = new Avm1CodeUnit();
        functionBody.Emit(new ActionPush([PushValue.Undefined()]));
        functionBody.Emit(new ActionReturn());

        var root = new Avm1CodeUnit();
        root.EmitDefineFunction2("factory", 0, 0, [], functionBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(AssemblyOptions(swfVersion: 6));

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Message.Contains(
                "DefineFunction2 requires SWF 7",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Assembler_propagates_unsupported_action_from_nested_body()
    {
        var functionBody = new Avm1CodeUnit();
        functionBody.Emit(new ActionPush([PushValue.Integer(1)]));
        functionBody.Emit(new ActionThrow());

        var root = new Avm1CodeUnit();
        root.EmitDefineFunction("throwValue", [], functionBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(AssemblyOptions(swfVersion: 6));

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1ASM010" &&
            diagnostic.Message.Contains("AVM1VER019", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Throw requires SWF 7", StringComparison.Ordinal));
    }

    [Fact]
    public void Assembler_checks_actions_synthesized_from_symbolic_control_flow()
    {
        var unit = new Avm1CodeUnit();
        var exit = unit.DefineLabel();
        unit.EmitJump(exit);
        unit.MarkLabel(exit);
        unit.Emit(new ActionEnd());

        var result = unit.Assemble(AssemblyOptions(swfVersion: 3));

        result.Succeeded.ShouldBeFalse();
        result.Bytes.IsEmpty.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Message.Contains("Jump requires SWF 4", StringComparison.Ordinal));
    }

    private static readonly (byte MinimumVersion, ActionOpcode[] Opcodes)[]
        OpcodeVersionGroups =
        [
            (1,
            [
                ActionOpcode.End,
                ActionOpcode.NextFrame,
                ActionOpcode.PreviousFrame,
                ActionOpcode.Play,
                ActionOpcode.Stop,
                ActionOpcode.ToggleQuality,
                ActionOpcode.GotoFrame,
                ActionOpcode.GetURL
            ]),
            (2,
            [
                ActionOpcode.StopSounds
            ]),
            (3,
            [
                ActionOpcode.WaitForFrame,
                ActionOpcode.SetTarget,
                ActionOpcode.GoToLabel
            ]),
            (4,
            [
                ActionOpcode.Add,
                ActionOpcode.Subtract,
                ActionOpcode.Multiply,
                ActionOpcode.Divide,
                ActionOpcode.Equals,
                ActionOpcode.Less,
                ActionOpcode.And,
                ActionOpcode.Or,
                ActionOpcode.Not,
                ActionOpcode.StringEquals,
                ActionOpcode.StringLength,
                ActionOpcode.StringExtract,
                ActionOpcode.Pop,
                ActionOpcode.ToInteger,
                ActionOpcode.GetVariable,
                ActionOpcode.SetVariable,
                ActionOpcode.SetTarget2,
                ActionOpcode.StringAdd,
                ActionOpcode.GetProperty,
                ActionOpcode.SetProperty,
                ActionOpcode.CloneSprite,
                ActionOpcode.RemoveSprite,
                ActionOpcode.Trace,
                ActionOpcode.StartDrag,
                ActionOpcode.EndDrag,
                ActionOpcode.StringLess,
                ActionOpcode.RandomNumber,
                ActionOpcode.MBStringLength,
                ActionOpcode.CharToAscii,
                ActionOpcode.AsciiToChar,
                ActionOpcode.GetTime,
                ActionOpcode.MBStringExtract,
                ActionOpcode.MBCharToAscii,
                ActionOpcode.MBAsciiToChar,
                ActionOpcode.WaitForFrame2,
                ActionOpcode.Push,
                ActionOpcode.Jump,
                ActionOpcode.GetURL2,
                ActionOpcode.If,
                ActionOpcode.Call,
                ActionOpcode.GotoFrame2
            ]),
            (5,
            [
                ActionOpcode.Delete,
                ActionOpcode.Delete2,
                ActionOpcode.DefineLocal,
                ActionOpcode.CallFunction,
                ActionOpcode.Return,
                ActionOpcode.Modulo,
                ActionOpcode.NewObject,
                ActionOpcode.DefineLocal2,
                ActionOpcode.InitArray,
                ActionOpcode.InitObject,
                ActionOpcode.TypeOf,
                ActionOpcode.TargetPath,
                ActionOpcode.Enumerate,
                ActionOpcode.Add2,
                ActionOpcode.Less2,
                ActionOpcode.Equals2,
                ActionOpcode.ToNumber,
                ActionOpcode.ToString,
                ActionOpcode.PushDuplicate,
                ActionOpcode.StackSwap,
                ActionOpcode.GetMember,
                ActionOpcode.SetMember,
                ActionOpcode.Increment,
                ActionOpcode.Decrement,
                ActionOpcode.CallMethod,
                ActionOpcode.NewMethod,
                ActionOpcode.BitAnd,
                ActionOpcode.BitOr,
                ActionOpcode.BitXor,
                ActionOpcode.BitLShift,
                ActionOpcode.BitRShift,
                ActionOpcode.BitURShift,
                ActionOpcode.StoreRegister,
                ActionOpcode.ConstantPool,
                ActionOpcode.DefineFunction,
                ActionOpcode.With
            ]),
            (6,
            [
                ActionOpcode.InstanceOf,
                ActionOpcode.Enumerate2,
                ActionOpcode.StrictEquals,
                ActionOpcode.Greater,
                ActionOpcode.StringGreater
            ]),
            (7,
            [
                ActionOpcode.Throw,
                ActionOpcode.CastOp,
                ActionOpcode.ImplementsOp,
                ActionOpcode.Extends,
                ActionOpcode.DefineFunction2,
                ActionOpcode.Try
            ])
        ];

    private static Avm1BytecodeVerificationResult Verify(
        byte swfVersion,
        params Avm1Action[] actions) =>
        Avm1BytecodeVerifier.Verify(
            actions,
            new Avm1BytecodeVerificationOptions(swfVersion)
            {
                RequireEndAction = true
            });

    private static Avm1AssemblyOptions AssemblyOptions(byte swfVersion) =>
        new(swfVersion)
        {
            RequireEndAction = true
        };

    private static string Describe(Avm1BytecodeVerificationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));
}
