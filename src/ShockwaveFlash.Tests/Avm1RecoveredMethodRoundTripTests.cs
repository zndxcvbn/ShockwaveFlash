using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compatibility;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1RecoveredMethodRoundTripTests
{
    [Fact]
    public void Round_trips_a_legacy_function_with_an_outer_constant_pool()
    {
        var input = new Avm1MethodCompatibilityInput(
            "legacy",
            Encode(
                new ActionPush([PushValue.Constant8(0)]),
                new ActionReturn()),
            swfVersion: 7)
        {
            InitialConstantPool = ["resolved-value"]
        };

        var result = Avm1RecoveredMethodRoundTripAnalyzer.Analyze(input);

        result.Status.ShouldBe(
            Avm1RecoveredMethodRoundTripStatus.Equivalent,
            Describe(result));
        result.ReferenceVerification.ShouldNotBeNull().Succeeded.ShouldBeTrue();
        result.Source.ShouldNotBeNull().IsComplete.ShouldBeTrue();
        result.Compilation.ShouldNotBeNull().Succeeded.ShouldBeTrue();
        result.Candidate.ShouldNotBeNull().RegisterCount.ShouldBeNull();
        result.Compatibility.ShouldNotBeNull().IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void Round_trips_define_function2_preloads_and_parameter_registers()
    {
        var flags = FunctionFlags.PreloadThis |
            FunctionFlags.SuppressArguments |
            FunctionFlags.SuppressSuper;
        var input = new Avm1MethodCompatibilityInput(
            "modern",
            Encode(
                new ActionPush(
                [
                    PushValue.Register(1),
                    PushValue.String("value")
                ]),
                new ActionGetMember(),
                new ActionPush([PushValue.Register(2)]),
                new ActionAdd2(),
                new ActionReturn()),
            swfVersion: 7)
        {
            FunctionContext = new FunctionContext(
                flags,
                [new FunctionParameter(2, "value")]),
            RegisterCount = 3
        };

        var result = Avm1RecoveredMethodRoundTripAnalyzer.Analyze(input);

        result.Status.ShouldBe(
            Avm1RecoveredMethodRoundTripStatus.Equivalent,
            Describe(result));
        result.Candidate.ShouldNotBeNull().RegisterCount.ShouldBe(byte.MaxValue);
        result.Candidate.FunctionContext.ShouldNotBeNull().Flags.ShouldBe(flags);
        result.Candidate.FunctionContext.Parameters
            .ShouldHaveSingleItem().Register.ShouldBe((byte)0);
        result.Compatibility.ShouldNotBeNull().IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void Round_trips_nested_source_register_declared_in_both_if_branches()
    {
        var source = CreateNestedBranchLocalMethod();
        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7)
            {
                SourceRegisterAllocationMode =
                    Avm1SourceRegisterAllocationMode.PreserveSourceSymbols
            });

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic => diagnostic.Message)));
        artifact.NestedFunctions.ShouldHaveSingleItem()
            .Plan.RegisterAllocations.ShouldContain(allocation =>
                allocation.Symbol.IsValid);

        var result = Avm1RecoveredMethodRoundTripAnalyzer.Analyze(
            new Avm1MethodCompatibilityInput(
                "nested-branch-local",
                artifact.Bytecode,
                swfVersion: 7));

        result.Status.ShouldBe(
            Avm1RecoveredMethodRoundTripStatus.Equivalent,
            Describe(result));
        result.Compatibility.ShouldNotBeNull().IsCompatible.ShouldBeTrue();
        result.Compatibility.CandidateSource.ShouldNotBeNull().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Rejects_invalid_reference_before_source_projection()
    {
        ReadOnlyMemory<byte> truncatedPush = new byte[]
        {
            (byte)ActionOpcode.Push,
            5,
            0,
            7,
            1
        };

        var result = Avm1RecoveredMethodRoundTripAnalyzer.Analyze(
            new Avm1MethodCompatibilityInput(
                "invalid",
                truncatedPush,
                swfVersion: 7));

        result.Status.ShouldBe(Avm1RecoveredMethodRoundTripStatus.ReferenceInvalid);
        result.ReferenceVerification.ShouldNotBeNull().Succeeded.ShouldBeFalse();
        result.Source.ShouldBeNull();
        result.Compilation.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Stage ==
                Avm1RecoveredMethodRoundTripStage.ReferenceVerification);
    }

    [Fact]
    public void Stops_before_compilation_for_a_class_abi_action_stream()
    {
        var result = Avm1RecoveredMethodRoundTripAnalyzer.Analyze(
            new Avm1MethodCompatibilityInput(
                "class-initializer",
                Encode(
                    new ActionPush([PushValue.String("Child")]),
                    new ActionGetVariable(),
                    new ActionPush([PushValue.String("Base")]),
                    new ActionGetVariable(),
                    new ActionExtends()),
                swfVersion: 7));

        result.Status.ShouldBe(Avm1RecoveredMethodRoundTripStatus.SourceIncomplete);
        result.Source.ShouldNotBeNull().IsComplete.ShouldBeFalse();
        result.Compilation.ShouldBeNull();
        result.Compatibility.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Stage == Avm1RecoveredMethodRoundTripStage.SourceProjection &&
            diagnostic.Code == "AVM1SRC006");
    }

    private static ReadOnlyMemory<byte> Encode(
        params ShockwaveFlash.Avm1.Action[] actions) =>
        ShockwaveFlash.Avm1.Action.EncodeCollection(actions, swfVersion: 7);

    private static Avm1SourceMethod CreateNestedBranchLocalMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var condition = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "condition",
            SourceTypeIndex.Invalid,
            unknown,
            Avm1SourceSymbolFlags.DeclarationProvided);
        var value = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            unknown);

        SourceExpressionIndex AddInteger(int number) => builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: number));
        SourceStatementIndex DeclareValue(int number) => builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddInteger(number),
            symbol: value);

        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: builder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: condition),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: [DeclareValue(1)]),
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: [DeclareValue(2)])
            ]);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: builder.AddExpression(
                Avm1SourceExpressionKind.SymbolReference,
                symbol: value));
        var function = builder.AddFunction(
            SourceSymbolIndex.Invalid,
            builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: [conditional, returnStatement]),
            Avm1SourceFunctionFlags.None,
            SourceOriginIndex.Invalid,
            parameters: [condition]);
        var functionLiteral = builder.AddExpression(
            Avm1SourceExpressionKind.FunctionLiteral,
            function: function);
        var assignment = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children:
                [
                    builder.AddExpression(
                        Avm1SourceExpressionKind.DynamicName,
                        name: builder.InternString("handler")),
                    functionLiteral
                ]));
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [assignment]);
        return new Avm1SourceMethod(builder.ToArena(), body, [], []);
    }

    private static string Describe(Avm1RecoveredMethodRoundTripResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage} {diagnostic.Code}: {diagnostic.Message}"));
}
