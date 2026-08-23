using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Verification;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1RegisterAndConstantPoolVerifierTests
{
    [Fact]
    public void Standalone_function2_body_uses_the_public_register_context()
    {
        var bytecode = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Register(4)]),
                new ActionReturn()
            ],
            swfVersion: 7);

        var valid = Avm1BytecodeVerifier.Verify(
            bytecode,
            new Avm1BytecodeVerificationOptions(7)
            {
                RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
                RegisterCount = 5
            });
        var invalid = Avm1BytecodeVerifier.Verify(
            bytecode,
            new Avm1BytecodeVerificationOptions(7)
            {
                RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
                RegisterCount = 4
            });

        valid.Succeeded.ShouldBeTrue(Describe(valid));
        invalid.Succeeded.ShouldBeFalse();
        invalid.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER016");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Try_body_accepts_declared_physical_region_exits(
        bool skipFinally)
    {
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(1)]),
                new ActionPop()
            ],
            swfVersion: 7);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(2)]),
                new ActionPop()
            ],
            swfVersion: 7);
        var displacement = checked((short)(
            catchBody.Length + (skipFinally ? finallyBody.Length : 0)));
        var tryBody = Avm1Action.EncodeCollection(
            [new ActionJump(displacement)],
            swfVersion: 7);
        var result = Verify(WithEnd(
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                finallyBody)));

        result.Succeeded.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Try_body_accepts_a_parent_code_unit_action_boundary()
    {
        Avm1Action[] skippedActions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionPop()
        ];
        var skippedLength = Avm1Action.EncodeCollection(
            skippedActions,
            swfVersion: 7).Length;
        var tryBody = Avm1Action.EncodeCollection(
            [new ActionJump(checked((short)skippedLength))],
            swfVersion: 7);
        var result = Verify(WithEnd(
            new ActionTry(
                0,
                catchRegister: 0,
                catchVariable: string.Empty,
                tryBody,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty),
            skippedActions[0],
            skippedActions[1]));

        result.Succeeded.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Root_code_unit_accepts_global_registers_zero_through_three()
    {
        var result = Verify(WithEnd(
            new ActionPush([
                PushValue.Register(0),
                PushValue.Register(3)
            ]),
            new ActionPop(),
            new ActionPop()));

        result.Succeeded.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Root_code_unit_rejects_register_four_for_reads_and_writes()
    {
        var result = Verify(WithEnd(
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(4),
            new ActionPop(),
            new ActionPush([PushValue.Register(4)]),
            new ActionPop()));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.Count(diagnostic =>
            diagnostic.Code == "AVM1VER016").ShouldBe(2);
    }

    [Fact]
    public void Function2_body_uses_its_declared_private_register_file()
    {
        var body = new Avm1CodeUnit();
        body.Emit(new ActionPush([PushValue.Register(4)]));
        body.Emit(new ActionReturn());

        var root = new Avm1CodeUnit();
        root.EmitDefineFunction2(
            "readValue",
            registerCount: 5,
            FunctionFlags.PreloadThis,
            [new FunctionParameter(2, "value")],
            body);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.NestedBodies.ShouldHaveSingleItem().Body.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Function2_accepts_allocated_register_zero_and_rejects_registers_at_or_above_count()
    {
        var functionBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([
                    PushValue.Register(0),
                    PushValue.Register(4)
                ]),
                new ActionPop(),
                new ActionReturn()
            ],
            swfVersion: 7);
        var result = Verify(WithEnd(
            new ActionDefineFunction2(
                "invalid",
                registerCount: 3,
                0,
                [],
                functionBody)));

        result.Succeeded.ShouldBeFalse();
        var registerDiagnostics = result.Diagnostics.Where(diagnostic =>
            diagnostic.Code == "AVM1VER010" &&
            diagnostic.Message.Contains("AVM1VER016", StringComparison.Ordinal))
            .ToArray();
        registerDiagnostics.ShouldHaveSingleItem().Message.ShouldContain(
            "register 4");
    }

    [Fact]
    public void Function2_metadata_rejects_reserved_conflicting_and_overlapping_fields()
    {
        var flags = FunctionFlags.PreloadThis |
            FunctionFlags.SuppressThis |
            FunctionFlags.PreloadRoot |
            (FunctionFlags)0x8000;
        var result = Verify(WithEnd(
            new ActionDefineFunction2(
                "invalid",
                registerCount: 2,
                flags,
                [
                    new FunctionParameter(1, "overlap"),
                    new FunctionParameter(2, "outside")
                ],
                ReadOnlyMemory<byte>.Empty)));

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.Count(diagnostic =>
            diagnostic.Code == "AVM1VER017").ShouldBe(5);
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains("Reserved", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains("cannot both", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains("preload register", StringComparison.Ordinal));
    }

    [Fact]
    public void Register_catch_is_checked_in_the_owning_function_context()
    {
        var tryBody = new Avm1CodeUnit();
        var catchBody = new Avm1CodeUnit();
        var functionBody = new Avm1CodeUnit();
        functionBody.EmitTry(
            TryFlags.CatchBlock | TryFlags.CatchInRegister,
            catchRegister: 4,
            catchVariable: string.Empty,
            tryBody,
            catchBody);
        functionBody.Emit(new ActionPush([PushValue.Undefined()]));
        functionBody.Emit(new ActionReturn());

        var root = new Avm1CodeUnit();
        root.EmitDefineFunction2(
            "invalidCatch",
            registerCount: 3,
            0,
            [],
            functionBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains("AVM1VER016", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("catch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constant_reference_requires_an_active_in_range_pool_entry()
    {
        var missing = Verify(WithEnd(
            new ActionPush([PushValue.Constant8(0)]),
            new ActionPop()));
        var outside = Verify(WithEnd(
            new ActionConstantPool(["only"]),
            new ActionPush([PushValue.Constant8(1)]),
            new ActionPop()));

        missing.Succeeded.ShouldBeFalse();
        missing.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER015" &&
            diagnostic.Message.Contains("without a pool", StringComparison.Ordinal));
        outside.Succeeded.ShouldBeFalse();
        outside.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER015" &&
            diagnostic.Message.Contains("pool size 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Constant8_and_constant16_accept_pool_boundaries()
    {
        var constants = Enumerable.Range(0, 257)
            .Select(index => "constant" + index)
            .ToArray();
        var result = Verify(WithEnd(
            new ActionConstantPool(constants),
            new ActionPush([
                PushValue.Constant8(255),
                PushValue.Constant16(256)
            ]),
            new ActionPop(),
            new ActionPop()));

        result.Succeeded.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Verifier_accepts_an_explicit_inherited_constant_pool()
    {
        var result = Avm1BytecodeVerifier.Verify(
            WithEnd(
                new ActionPush([PushValue.Constant16(300)]),
                new ActionPop()),
            new Avm1BytecodeVerificationOptions(SwfVersion: 7)
            {
                RequireEndAction = true,
                InitialConstantPoolCount = 301
            });

        result.Succeeded.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Constant_pool_join_uses_the_minimum_size_guaranteed_on_all_paths()
    {
        var valid = AssemblePoolJoin(PushValue.Constant8(0));
        var invalid = AssemblePoolJoin(PushValue.Constant8(1));

        valid.Succeeded.ShouldBeTrue(Describe(valid));
        invalid.Succeeded.ShouldBeFalse();
        invalid.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER015" &&
            diagnostic.Message.Contains("pool size 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Constant_pool_join_rejects_a_path_without_a_pool()
    {
        var result = AssemblePoolJoin(
            PushValue.Constant8(0),
            omitAlternatePool: true);

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER015" &&
            diagnostic.Message.Contains("every incoming", StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_function_captures_the_pool_active_at_definition()
    {
        var functionBody = new Avm1CodeUnit();
        functionBody.Emit(new ActionPush([PushValue.Constant8(0)]));
        functionBody.Emit(new ActionReturn());

        var root = new Avm1CodeUnit();
        root.Emit(new ActionConstantPool(["captured"]));
        root.EmitDefineFunction("read", [], functionBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.NestedBodies.ShouldHaveSingleItem().Body.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void With_pool_replacement_does_not_escape_its_child_activation()
    {
        var withBody = new Avm1CodeUnit();
        withBody.Emit(new ActionConstantPool(["local"]));

        var root = new Avm1CodeUnit();
        root.Emit(new ActionConstantPool(["first", "second"]));
        root.Emit(new ActionPush([PushValue.Undefined()]));
        root.EmitWith(withBody);
        root.Emit(new ActionPush([PushValue.Constant8(1)]));
        root.Emit(new ActionPop());
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Try_pool_replacement_constrains_the_continuing_activation()
    {
        var tryBody = new Avm1CodeUnit();
        tryBody.Emit(new ActionConstantPool(["replacement"]));

        var root = new Avm1CodeUnit();
        root.Emit(new ActionConstantPool(["first", "second"]));
        root.EmitTry(
            0,
            catchRegister: 0,
            catchVariable: string.Empty,
            tryBody);
        root.Emit(new ActionPush([PushValue.Constant8(1)]));
        root.Emit(new ActionPop());
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER015" &&
            diagnostic.Message.Contains("pool size 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_code_unit_cache_includes_function_register_context()
    {
        var sharedBody = new Avm1CodeUnit();
        sharedBody.Emit(new ActionPush([PushValue.Register(4)]));
        sharedBody.Emit(new ActionReturn());

        var root = new Avm1CodeUnit();
        root.EmitDefineFunction2("five", 5, 0, [], sharedBody);
        root.EmitDefineFunction2("six", 6, 0, [], sharedBody);
        root.Emit(new ActionEnd());

        var result = root.Assemble(RootOptions());

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.NestedBodies.Count.ShouldBe(2);
        ReferenceEquals(
            result.NestedBodies[0].Body,
            result.NestedBodies[1].Body).ShouldBeFalse();
    }

    private static Avm1ActionBody AssemblePoolJoin(
        PushValue constant,
        bool omitAlternatePool = false)
    {
        var unit = new Avm1CodeUnit();
        var alternate = unit.DefineLabel();
        var join = unit.DefineLabel();
        unit.Emit(new ActionPush([PushValue.Register(0)]));
        unit.EmitIf(alternate);
        unit.Emit(new ActionConstantPool(["zero", "one"]));
        unit.EmitJump(join);
        unit.MarkLabel(alternate);
        if (!omitAlternatePool)
            unit.Emit(new ActionConstantPool(["zero"]));
        unit.MarkLabel(join);
        unit.Emit(new ActionPush([constant]));
        unit.Emit(new ActionPop());
        unit.Emit(new ActionEnd());
        return unit.Assemble(RootOptions());
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

    private static Avm1AssemblyOptions RootOptions() =>
        new(SwfVersion: 7)
        {
            RequireEndAction = true
        };

    private static string Describe(Avm1BytecodeVerificationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));

    private static string Describe(Avm1ActionBody body) =>
        string.Join(
            Environment.NewLine,
            body.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));
}
