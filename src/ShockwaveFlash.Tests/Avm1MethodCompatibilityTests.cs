using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compatibility;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1MethodCompatibilityTests
{
    [Fact]
    public void Extracts_top_level_function_code_units_with_their_context()
    {
        var legacy = new ActionDefineFunction(
            "legacy",
            ["value"],
            EncodeReturn(PushValue.Integer(1)));
        var function2 = new ActionDefineFunction2(
            "modern",
            registerCount: 4,
            FunctionFlags.PreloadThis,
            [new FunctionParameter(2, "value")],
            EncodeReturn(PushValue.Integer(2)));
        var bytecode = ShockwaveFlash.Avm1.Action.EncodeCollection(
            [legacy, function2],
            swfVersion: 7);

        var functions = Avm1FunctionBodyExtractor.ExtractTopLevel(
            bytecode,
            swfVersion: 7);

        functions.Count.ShouldBe(2);
        functions[0].Name.ShouldBe("legacy");
        functions[0].UsesDefineFunction2.ShouldBeFalse();
        functions[0].RegisterCount.ShouldBeNull();
        functions[0].Parameters.ShouldHaveSingleItem().Register.ShouldBe((byte)0);
        functions[1].Name.ShouldBe("modern");
        functions[1].UsesDefineFunction2.ShouldBeTrue();
        functions[1].RegisterCount.ShouldBe((byte)4);
        functions[1].FunctionContext.PreloadThis.ShouldBeTrue();
        functions[1].ToCompatibilityInput().RegisterCount.ShouldBe((byte)4);
    }

    [Fact]
    public void Extracted_functions_capture_the_active_constant_pool_snapshot()
    {
        var first = new ActionDefineFunction(
            "first",
            [],
            EncodeReturn(PushValue.Constant8(0)));
        var second = new ActionDefineFunction(
            "second",
            [],
            EncodeReturn(PushValue.Constant8(0)));
        var bytecode = ShockwaveFlash.Avm1.Action.EncodeCollection(
            [
                new ActionConstantPool(["first-value"]),
                first,
                new ActionConstantPool(["second-value"]),
                second
            ],
            swfVersion: 7);

        var functions = Avm1FunctionBodyExtractor.ExtractTopLevel(
            bytecode,
            swfVersion: 7);

        functions[0].InitialConstantPool.ShouldBe(["first-value"]);
        functions[1].InitialConstantPool.ShouldBe(["second-value"]);
    }

    [Fact]
    public void Compares_an_extracted_constant_reference_using_its_outer_pool()
    {
        var function = new ActionDefineFunction(
            "reference",
            [],
            EncodeReturn(PushValue.Constant8(0)));
        var outerBytecode = ShockwaveFlash.Avm1.Action.EncodeCollection(
            [new ActionConstantPool(["resolved-value"]), function],
            swfVersion: 7);
        var extracted = Avm1FunctionBodyExtractor.ExtractTopLevel(
            outerBytecode,
            swfVersion: 7).ShouldHaveSingleItem();

        var result = Avm1MethodCompatibilityAnalyzer.Compare(
            extracted.ToCompatibilityInput(),
            new Avm1MethodCompatibilityInput(
                "candidate",
                EncodeReturn(PushValue.String("resolved-value")),
                7));

        result.IsCompatible.ShouldBeTrue(Describe(result));
        result.ReferenceVerification.Succeeded.ShouldBeTrue();
        result.ReferenceSource.ShouldNotBeNull();
    }

    [Fact]
    public void Compares_normalized_source_without_requiring_identical_bytecode()
    {
        var reference = EncodeReturn(PushValue.Integer(42));
        var candidate = EncodeReturn(PushValue.Double(42));

        var result = Avm1MethodCompatibilityAnalyzer.Compare(
            new Avm1MethodCompatibilityInput("reference", reference, 7),
            new Avm1MethodCompatibilityInput("candidate", candidate, 7));

        result.IsCompatible.ShouldBeTrue(Describe(result));
        result.SourceEquivalence.ShouldNotBeNull().IsEquivalent.ShouldBeTrue();
        result.ReferenceProfile.ByteLength.ShouldNotBe(
            result.CandidateProfile.ByteLength);
        result.ReferenceProfile.ActionCount.ShouldBe(2);
        result.ReferenceProfile.OpcodeCounts.ShouldContain(count =>
            count.Opcode == ActionOpcode.Push && count.Count == 1);
    }

    [Fact]
    public void Canonical_comparison_coalesces_noninterfering_register_versions()
    {
        var reference = ShockwaveFlash.Avm1.Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("first")]),
                new ActionGetVariable(),
                new ActionStoreRegister(2),
                new ActionPop(),
                new ActionPush([
                    PushValue.String("before"),
                    PushValue.Register(2)
                ]),
                new ActionSetVariable(),
                new ActionPush([PushValue.String("second")]),
                new ActionGetVariable(),
                new ActionStoreRegister(2),
                new ActionPop(),
                new ActionPush([PushValue.Register(2)]),
                new ActionReturn()
            ],
            swfVersion: 7);
        var referenceInput = new Avm1MethodCompatibilityInput(
            "reference",
            reference,
            7)
        {
            RegisterCount = 3
        };
        var canonical = Avm1MethodCompatibilityAnalyzer.Compare(
            referenceInput,
            referenceInput,
            new Avm1MethodCompatibilityOptions
            {
                CoalesceNonInterferingRegisterVersions = true
            });
        var versioned = Avm1MethodCompatibilityAnalyzer.Compare(
            referenceInput,
            referenceInput,
            new Avm1MethodCompatibilityOptions
            {
                CoalesceNonInterferingRegisterVersions = false
            });

        canonical.IsCompatible.ShouldBeTrue(Describe(canonical));
        versioned.IsCompatible.ShouldBeTrue(Describe(versioned));
        CountRegisterSymbols(canonical.ReferenceSource.ShouldNotBeNull()).ShouldBe(1);
        CountRegisterSymbols(versioned.ReferenceSource.ShouldNotBeNull()).ShouldBe(2);

        static int CountRegisterSymbols(Avm1SourceMethod source) =>
            source.Arena.Symbols.Count(symbol =>
                symbol.Kind == Avm1SourceSymbolKind.Local &&
                source.Arena[symbol.Name].StartsWith("_loc2", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_a_semantic_source_difference()
    {
        var result = Avm1MethodCompatibilityAnalyzer.Compare(
            new Avm1MethodCompatibilityInput(
                "reference",
                EncodeReturn(PushValue.Integer(1)),
                7),
            new Avm1MethodCompatibilityInput(
                "candidate",
                EncodeReturn(PushValue.Integer(2)),
                7));

        result.BytecodeIsValid.ShouldBeTrue(Describe(result));
        result.IsCompatible.ShouldBeFalse();
        result.SourceEquivalence.ShouldNotBeNull().Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Different);
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Side == Avm1CompatibilitySide.Comparison &&
            diagnostic.Code == "AVM1EQ100");
    }

    [Fact]
    public void Stops_before_source_comparison_when_bytecode_is_invalid()
    {
        ReadOnlyMemory<byte> truncatedPush = new byte[]
        {
            (byte)ActionOpcode.Push,
            5,
            0,
            7,
            1
        };

        var result = Avm1MethodCompatibilityAnalyzer.Compare(
            new Avm1MethodCompatibilityInput(
                "reference",
                EncodeReturn(PushValue.Integer(1)),
                7),
            new Avm1MethodCompatibilityInput(
                "candidate",
                truncatedPush,
                7));

        result.BytecodeIsValid.ShouldBeFalse();
        result.IsCompatible.ShouldBeFalse();
        result.SourceEquivalence.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Side == Avm1CompatibilitySide.Candidate &&
            diagnostic.Severity ==
                Avm1CompilationDiagnosticSeverity.Error);
    }

    [Fact]
    public void Does_not_compare_incomplete_class_abi_method_projections()
    {
        var bytecode = ShockwaveFlash.Avm1.Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("IFirst")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.Integer(1)]),
                new ActionPush([PushValue.String("Child")]),
                new ActionGetVariable(),
                new ActionImplementsOp()
            ],
            swfVersion: 7);
        var input = new Avm1MethodCompatibilityInput(
            "class-initializer",
            bytecode,
            swfVersion: 7);

        var result = Avm1MethodCompatibilityAnalyzer.Compare(input, input);

        result.BytecodeIsValid.ShouldBeTrue(Describe(result));
        result.ReferenceSource.ShouldNotBeNull().IsComplete.ShouldBeFalse();
        result.CandidateSource.ShouldNotBeNull().IsComplete.ShouldBeFalse();
        result.SourceEquivalence.ShouldBeNull();
        result.IsCompatible.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC006" &&
            diagnostic.Severity == Avm1CompilationDiagnosticSeverity.Error);
    }

    private static ReadOnlyMemory<byte> EncodeReturn(PushValue value) =>
        ShockwaveFlash.Avm1.Action.EncodeCollection(
            [new ActionPush([value]), new ActionReturn()],
            swfVersion: 7);

    private static string Describe(Avm1MethodCompatibilityResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Side} {diagnostic.Code}: " +
                diagnostic.Message));
}
