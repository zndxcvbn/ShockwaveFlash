using ShockwaveFlash.Avm1.Source;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SourceEquivalenceTests
{
    [Fact]
    public void Ignores_which_mutually_exclusive_branch_declares_a_function_local()
    {
        var expected = CreateBranchSplitDeclarationMethod(declareInTrueBranch: false);
        var actual = CreateBranchSplitDeclarationMethod(declareInTrueBranch: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);
        var expectedNormalized = Avm1SourceNormalizer.Normalize(
            expected,
            CancellationToken.None).Method.GetAs2Text();
        var actualNormalized = Avm1SourceNormalizer.Normalize(
            actual,
            CancellationToken.None).Method.GetAs2Text();

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result) + Environment.NewLine +
                "EXPECTED:" + Environment.NewLine + expectedNormalized +
                "ACTUAL:" + Environment.NewLine + actualNormalized);
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ignores_source_names_types_origins_and_label_names()
    {
        var expected = CreateIdentityLoopMethod("value", "outer", typed: true);
        var actual = CreateIdentityLoopMethod("renamed", "generatedLabel", typed: false);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Treats_integer_and_number_literals_as_the_same_avm1_value()
    {
        var expected = CreateLiteralReturn(Avm1SourceLiteralKind.Integer, integerValue: 42);
        var actual = CreateLiteralReturn(Avm1SourceLiteralKind.Number, numberValue: 42);

        Avm1SourceEquivalence.Compare(expected, actual).IsEquivalent.ShouldBeTrue();
    }

    [Fact]
    public void Canonicalizes_bitwise_literals_to_avm1_int32_operands()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.BitXor,
                    children:
                    [
                        AddDynamicName(expectedBuilder, "value"),
                        expectedBuilder.AddExpression(
                            Avm1SourceExpressionKind.Literal,
                            literal: expectedBuilder.AddLiteral(
                                Avm1SourceLiteralKind.Number,
                                numberValue: 4294967295d))
                    ]))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.BitXor,
                    children:
                    [
                        AddDynamicName(actualBuilder, "value"),
                        AddInteger(actualBuilder, -1)
                    ]))]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_an_inverted_conditional_expression()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Greater,
            children:
            [
                AddDynamicName(expectedBuilder, "value"),
                AddInteger(expectedBuilder, 0)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Conditional,
                    children:
                    [
                        expectedCondition,
                        AddInteger(expectedBuilder, 1),
                        AddInteger(expectedBuilder, -1)
                    ]))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualCondition = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LessOrEqual,
            children:
            [
                AddDynamicName(actualBuilder, "value"),
                AddInteger(actualBuilder, 0)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Conditional,
                    children:
                    [
                        actualCondition,
                        AddInteger(actualBuilder, -1),
                        AddInteger(actualBuilder, 1)
                    ]))]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_a_literal_local_across_an_ordinary_call_barrier()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(expectedBuilder, "work")]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.Add,
                    children: [expectedCall, AddInteger(expectedBuilder, 3)]))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var literal = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "literal",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(actualBuilder, "work")]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(actualBuilder, 3),
                    symbol: literal),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Add,
                        children:
                        [
                            actualCall,
                            AddSymbolReference(actualBuilder, literal)
                        ]))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Does_not_inline_a_literal_local_across_direct_eval()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var literal = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "literal",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedEval = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(expectedBuilder, "eval"),
                AddString(expectedBuilder, "literal = 4")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(expectedBuilder, 3),
                    symbol: literal),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: expectedBuilder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Add,
                        children:
                        [
                            expectedEval,
                            AddSymbolReference(expectedBuilder, literal)
                        ]))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualEval = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(actualBuilder, "eval"),
                AddString(actualBuilder, "literal = 4")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.Add,
                    children: [actualEval, AddInteger(actualBuilder, 3)]))]);

        Avm1SourceEquivalence.Compare(expected, actual).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Different);
    }

    [Fact]
    public void Elides_a_generated_register_alias_across_direct_eval()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedValue = AddParameter(expectedBuilder, "value");
        var expectedFocus = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "Selection"),
                    "getFocus"),
                AddSymbolReference(expectedBuilder, expectedValue)
            ]);
        var expectedEval = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(expectedBuilder, "eval"),
                expectedFocus
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedEval)],
            [expectedValue]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualValue = AddParameter(actualBuilder, "value");
        var transport = AddGeneratedTemporary(actualBuilder, "transport");
        var actualFocus = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "Selection"),
                    "getFocus"),
                AddSymbolReference(actualBuilder, transport)
            ]);
        var actualEval = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(actualBuilder, "eval"),
                actualFocus
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, actualValue),
                    symbol: transport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: actualEval)
            ],
            [actualValue]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_ordered_generated_call_transports_across_call_barriers()
    {
        var expected = CreateOrderedCallTransportMethod(withTransports: false);
        var actual = CreateOrderedCallTransportMethod(withTransports: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_an_adobe_invocation_argument_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedLayout = AddMember(
            expectedBuilder,
            AddDynamicName(expectedBuilder, "itemList"),
            "layout");
        var expectedOptions = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(expectedBuilder, "x"),
                AddInteger(expectedBuilder, 554),
                AddString(expectedBuilder, "layout"),
                expectedLayout
            ]);
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "dialogManager"),
                    "open"),
                AddDynamicName(expectedBuilder, "container"),
                AddString(expectedBuilder, "Dialog"),
                expectedOptions
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedCall)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var layout = AddGeneratedLocal(actualBuilder, "layout");
        var actualOptions = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(actualBuilder, "x"),
                AddInteger(actualBuilder, 554),
                AddString(actualBuilder, "layout"),
                AddSymbolReference(actualBuilder, layout)
            ]);
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "dialogManager"),
                    "open"),
                AddDynamicName(actualBuilder, "container"),
                AddString(actualBuilder, "Dialog"),
                actualOptions
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "itemList"),
                        "layout"),
                    symbol: layout),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: actualCall)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Does_not_inline_an_authored_local_as_an_adobe_invocation_transport()
    {
        var builder = new Avm1SourceArena.Builder();
        var value = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var initializer = AddMember(
            builder,
            AddDynamicName(builder, "object"),
            "value");
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    builder,
                    AddDynamicName(builder, "service"),
                    "accept"),
                AddSymbolReference(builder, value)
            ]);
        var method = CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: initializer,
                    symbol: value),
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: call)
            ]);

        var normalized = Avm1SourceNormalizer.Normalize(
            method,
            CancellationToken.None);

        normalized.Diagnostics.ShouldBeEmpty();
        var text = normalized.Method.GetAs2Text();
        text.ShouldContain("var local0 = object.value;");
        text.ShouldContain("return service.accept(local0);");
    }

    [Fact]
    public void Recognizes_a_canonical_recovery_local_without_provenance_flags()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "service"),
                    "accept"),
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "object"),
                    "value")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedCall)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var recoveryLocal = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc2_",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "service"),
                    "accept"),
                AddSymbolReference(actualBuilder, recoveryLocal)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "object"),
                        "value"),
                    symbol: recoveryLocal),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: actualCall)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_a_canonical_recovery_local_as_a_member_assignment_receiver()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedReceiver = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(expectedBuilder, "getTarget")]);
        var expected = CreateMethod(
            expectedBuilder,
            [AddAssignmentStatement(
                expectedBuilder,
                AddMember(
                    expectedBuilder,
                    expectedReceiver,
                    "key",
                    computed: true),
                AddInteger(expectedBuilder, 1))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var recoveryLocal = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc2_",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children: [AddDynamicName(actualBuilder, "getTarget")]),
                    symbol: recoveryLocal),
                AddAssignmentStatement(
                    actualBuilder,
                    AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, recoveryLocal),
                        "key",
                        computed: true),
                    AddInteger(actualBuilder, 1))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Recognizes_a_versioned_recovery_local_without_provenance_flags()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedResult = AddGeneratedLocal(expectedBuilder, "result");
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children:
                    [
                        AddMember(
                            expectedBuilder,
                            expectedBuilder.AddExpression(
                                Avm1SourceExpressionKind.Call,
                                children: [AddDynamicName(expectedBuilder, "getValue")]),
                            "toString")
                    ]),
                symbol: expectedResult)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var versioned = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc2_v1",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualResult = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc6_",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children: [AddDynamicName(actualBuilder, "getValue")]),
                    symbol: versioned),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children:
                        [
                            AddMember(
                                actualBuilder,
                                AddSymbolReference(actualBuilder, versioned),
                                "toString")
                        ]),
                    symbol: actualResult)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_an_authored_local_return_without_moving_its_initializer()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "object"),
                    "value"))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var resultSymbol = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "object"),
                        "value"),
                    symbol: resultSymbol),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, resultSymbol))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
    }

    [Fact]
    public void Does_not_inline_a_generated_assignment_used_outside_its_branch()
    {
        var expected = CreateMethodWithBranchAssignment(generated: false);
        var actual = CreateMethodWithBranchAssignment(generated: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));

        static Avm1SourceMethod CreateMethodWithBranchAssignment(bool generated)
        {
            var builder = new Avm1SourceArena.Builder();
            var value = AddParameter(builder, "value");
            var result = generated
                ? AddGeneratedLocal(builder, "result")
                : builder.AddSymbol(
                    Avm1SourceSymbolKind.Local,
                    "result",
                    SourceTypeIndex.Invalid,
                    builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
            var branch = AddBlock(
                builder,
                AddAssignmentStatement(
                    builder,
                    AddSymbolReference(builder, result),
                    AddSymbolReference(builder, value)),
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(builder, result)));

            return CreateMethod(
                builder,
                [
                    builder.AddStatement(
                        Avm1SourceStatementKind.VariableDeclaration,
                        expression: AddInteger(builder, 0),
                        symbol: result),
                    builder.AddStatement(
                        Avm1SourceStatementKind.If,
                        expression: AddDynamicName(builder, "condition"),
                        children: [branch]),
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddSymbolReference(builder, result))
                ],
                [value]);
        }
    }

    [Fact]
    public void Inlines_an_authored_local_into_a_stable_this_member_assignment()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedThis = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Object));
        var expectedLeft = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedThis),
                    "x"),
                AddDynamicName(expectedBuilder, "width")
            ]);
        var expectedValue = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Subtract,
            children:
            [
                expectedLeft,
                AddMember(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedThis),
                    "width")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [AddAssignmentStatement(
                expectedBuilder,
                AddMember(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedThis),
                    "x"),
                expectedValue)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualThis = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Object));
        var transport = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "intermediate",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var initializer = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualThis),
                    "x"),
                AddDynamicName(actualBuilder, "width")
            ]);
        var actualValue = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Subtract,
            children:
            [
                AddSymbolReference(actualBuilder, transport),
                AddMember(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualThis),
                    "width")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: initializer,
                    symbol: transport),
                AddAssignmentStatement(
                    actualBuilder,
                    AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, actualThis),
                        "x"),
                    actualValue)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_an_authored_local_used_as_the_next_if_condition()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "object"),
                    "enabled"),
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        expectedBuilder.AddStatement(
                            Avm1SourceStatementKind.Return))
                ])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var conditionSymbol = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "condition",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "object"),
                        "enabled"),
                    symbol: conditionSymbol),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(
                        actualBuilder,
                        conditionSymbol),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
    }

    [Fact]
    public void Inlines_an_authored_local_used_first_in_the_next_if_condition()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedThis = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Object));
        var expectedParameter = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Equal,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedThis),
                    "focused"),
                AddSymbolReference(expectedBuilder, expectedParameter)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: expectedCondition,
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        expectedBuilder.AddStatement(
                            Avm1SourceStatementKind.Return))
                ])],
            [expectedParameter]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualThis = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Object));
        var actualParameter = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var transport = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "focused",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualCondition = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Equal,
            children:
            [
                AddSymbolReference(actualBuilder, transport),
                AddSymbolReference(actualBuilder, actualParameter)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, actualThis),
                        "focused"),
                    symbol: transport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualCondition,
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ],
            [actualParameter]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_a_pure_authored_conditional_into_an_invocation()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCondition = AddParameter(expectedBuilder, "condition");
        var expectedDispatcher = AddParameter(expectedBuilder, "dispatcher");
        var expectedValue = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddSymbolReference(expectedBuilder, expectedCondition),
                AddString(expectedBuilder, "show"),
                AddString(expectedBuilder, "hide")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children:
                    [
                        AddMember(
                            expectedBuilder,
                            AddSymbolReference(
                                expectedBuilder,
                                expectedDispatcher),
                            "send"),
                        expectedValue
                    ]))],
            [expectedCondition, expectedDispatcher]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualCondition = AddParameter(actualBuilder, "condition");
        var actualDispatcher = AddParameter(actualBuilder, "dispatcher");
        var valueSymbol = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualValue = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddSymbolReference(actualBuilder, actualCondition),
                AddString(actualBuilder, "show"),
                AddString(actualBuilder, "hide")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualValue,
                    symbol: valueSymbol),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children:
                        [
                            AddMember(
                                actualBuilder,
                                AddSymbolReference(
                                    actualBuilder,
                                    actualDispatcher),
                                "send"),
                            AddSymbolReference(actualBuilder, valueSymbol)
                        ]))
            ],
            [actualCondition, actualDispatcher]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
    }

    [Fact]
    public void Preserves_ordered_locals_while_removing_call_register_transports()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedX = AddGeneratedLocal(expectedBuilder, "x");
        var expectedY = AddGeneratedLocal(expectedBuilder, "y");
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "point"),
                        "x"),
                    symbol: expectedX),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "point"),
                        "y"),
                    symbol: expectedY),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: expectedBuilder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Multiply,
                        children:
                        [
                            expectedBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children:
                        [
                            AddMember(
                                expectedBuilder,
                                AddDynamicName(expectedBuilder, "Math"),
                                "atan2"),
                            AddSymbolReference(expectedBuilder, expectedY),
                            AddSymbolReference(expectedBuilder, expectedX)
                        ]),
                            AddInteger(expectedBuilder, 2)
                        ]))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualX = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "x",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualY = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "y",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var xTransport = AddGeneratedTemporary(actualBuilder, "xTransport");
        var yTransport = AddGeneratedTemporary(actualBuilder, "yTransport");
        var receiverTransport = AddGeneratedTemporary(actualBuilder, "receiverTransport");
        var receiverAlias = AddGeneratedLocal(actualBuilder, "receiverAlias");
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "point"),
                        "x"),
                    symbol: actualX),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "point"),
                        "y"),
                    symbol: actualY),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, actualX),
                    symbol: xTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, actualY),
                    symbol: yTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "Math"),
                    symbol: receiverTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, receiverTransport),
                    symbol: receiverAlias),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Multiply,
                        children:
                        [
                            actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children:
                        [
                            AddMember(
                                actualBuilder,
                                AddSymbolReference(actualBuilder, receiverAlias),
                                "atan2"),
                            AddSymbolReference(actualBuilder, yTransport),
                            AddSymbolReference(actualBuilder, xTransport)
                        ]),
                            AddInteger(actualBuilder, 2)
                        ]))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);
        var normalizedExpected = Avm1SourceNormalizer.Normalize(
            expected,
            CancellationToken.None).Method.GetAs2Text();
        var normalizedActual = Avm1SourceNormalizer.Normalize(
            actual,
            CancellationToken.None).Method.GetAs2Text();

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result) + Environment.NewLine +
            "EXPECTED:" + Environment.NewLine + normalizedExpected +
            Environment.NewLine + "ACTUAL:" + Environment.NewLine +
            normalizedActual);
        normalizedExpected.ShouldContain("var local0 = point.x;");
        normalizedExpected.ShouldContain("var local1 = point.y;");
        normalizedActual.ShouldContain("var local0 = point.x;");
        normalizedActual.ShouldContain("var local1 = point.y;");
    }

    [Fact]
    public void Inlines_an_authored_local_into_the_immediately_following_declaration()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedResult = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddMember(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "object"),
                        "value"),
                    "toString")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: expectedCall,
                    symbol: expectedResult),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(
                        expectedBuilder,
                        expectedResult))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var staged = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "staged",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualResult = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, staged),
                    "toString")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "object"),
                        "value"),
                    symbol: staged),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualCall,
                    symbol: actualResult),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, actualResult))
            ]);

        Avm1SourceEquivalence.Compare(expected, actual).Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent);
    }

    [Fact]
    public void Normalizes_runtime_qualified_names_and_member_spelling()
    {
        var expected = CreateQualifiedMemberReturn(
            "_global.gfx.controls.Button",
            computed: true);
        var actual = CreateQualifiedMemberReturn(
            "gfx.controls.Button",
            computed: false);

        Avm1SourceEquivalence.Compare(expected, actual).IsEquivalent.ShouldBeTrue();
    }

    [Fact]
    public void Normalizes_direct_eval_to_a_computed_dynamic_name()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var directEval = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(expectedBuilder, "eval"),
                AddDynamicName(expectedBuilder, "name")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: directEval)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var computedName = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ComputedDynamicName,
            children: [AddDynamicName(actualBuilder, "name")]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: computedName)]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Normalizes_inverted_terminal_throw_and_return_branches()
    {
        var expected = CreateTerminalIfMethod(inverted: false);
        var actual = CreateTerminalIfMethod(inverted: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Normalizes_complemented_comparison_with_swapped_branches()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Equal,
            children:
            [
                AddDynamicName(expectedBuilder, "value"),
                AddInteger(expectedBuilder, 1)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: expectedCondition,
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        AddAssignmentStatement(
                            expectedBuilder,
                            AddDynamicName(expectedBuilder, "result"),
                            AddInteger(expectedBuilder, 10))),
                    AddBlock(
                        expectedBuilder,
                        AddAssignmentStatement(
                            expectedBuilder,
                            AddDynamicName(expectedBuilder, "result"),
                            AddInteger(expectedBuilder, 20)))
                ])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualCondition = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.NotEqual,
            children:
            [
                AddDynamicName(actualBuilder, "value"),
                AddInteger(actualBuilder, 1)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: actualCondition,
                children:
                [
                    AddBlock(
                        actualBuilder,
                        AddAssignmentStatement(
                            actualBuilder,
                            AddDynamicName(actualBuilder, "result"),
                            AddInteger(actualBuilder, 20))),
                    AddBlock(
                        actualBuilder,
                        AddAssignmentStatement(
                            actualBuilder,
                            AddDynamicName(actualBuilder, "result"),
                            AddInteger(actualBuilder, 10)))
                ])]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Distinguishes_canonical_symbol_kinds()
    {
        var expected = CreateDeclaredSymbolReturn(Avm1SourceSymbolKind.Local);
        var actual = CreateDeclaredSymbolReturn(Avm1SourceSymbolKind.Temporary);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Different);
        result.Diagnostics.ShouldHaveSingleItem().Path.ShouldBe(".symbols[0]");
    }

    [Fact]
    public void Reports_the_first_structural_difference_with_a_path()
    {
        var expected = CreateLiteralReturn(Avm1SourceLiteralKind.Integer, integerValue: 1);
        var actual = CreateLiteralReturn(Avm1SourceLiteralKind.Integer, integerValue: 2);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Different);
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("AVM1EQ100");
        diagnostic.Path.ShouldContain(".literal");
    }

    [Fact]
    public void Reports_opaque_hir_as_incomplete_instead_of_equivalent()
    {
        var method = CreateOpaqueReturn();

        var result = Avm1SourceEquivalence.Compare(method, method);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Incomplete);
        result.Diagnostics.ShouldNotBeEmpty();
        result.Diagnostics.ShouldAllBe(diagnostic => diagnostic.Code == "AVM1EQ002");
    }

    [Fact]
    public void Compares_nested_function_bodies_and_alpha_renames_parameters()
    {
        var expected = CreateFunctionMethod("value", returnValue: null);
        var actual = CreateFunctionMethod("renamed", returnValue: null);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
    }

    [Fact]
    public void Reports_a_difference_inside_a_nested_function_body()
    {
        var expected = CreateFunctionMethod("value", returnValue: 1);
        var actual = CreateFunctionMethod("value", returnValue: 2);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Different);
        result.Diagnostics.ShouldHaveSingleItem().Path
            .ShouldContain(".functions[0].body");
    }

    [Fact]
    public void Reports_an_invalid_function_handle_as_incomplete()
    {
        var builder = new Avm1SourceArena.Builder();
        var expression = builder.AddExpression(
            Avm1SourceExpressionKind.FunctionLiteral,
            function: new SourceFunctionIndex(99));
        var method = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression)]);

        var result = Avm1SourceEquivalence.Compare(method, method);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Incomplete);
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1EQ003" &&
            diagnostic.Path.Contains(".function", StringComparison.Ordinal));
    }

    [Fact]
    public void Inlines_a_single_use_compiler_transport_into_its_consumer()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedObject = AddDynamicName(expectedBuilder, "object");
        var expectedMember = AddMember(expectedBuilder, expectedObject, "value");
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedMember)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var temporary = AddGeneratedLocal(actualBuilder, "v1");
        var declaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddDynamicName(actualBuilder, "object"),
            symbol: temporary);
        var member = AddMember(
            actualBuilder,
            AddSymbolReference(actualBuilder, temporary),
            "value");
        var actual = CreateMethod(
            actualBuilder,
            [
                declaration,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: member)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_numeric_postfix_transport_before_inlining_aliases()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedSelected = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "selected",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedUpdate = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(expectedBuilder, "index")]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: expectedUpdate,
                    symbol: expectedSelected),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(
                        expectedBuilder,
                        expectedSelected))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var transport = AddGeneratedLocal(actualBuilder, "v1");
        var actualSelected = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "selected",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var conversion = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.IntrinsicCall,
            name: actualBuilder.InternString("Number"),
            children: [AddDynamicName(actualBuilder, "index")]);
        var step = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddSymbolReference(actualBuilder, transport),
                AddInteger(actualBuilder, 1)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: conversion,
                    symbol: transport),
                AddAssignmentStatement(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "index"),
                    step),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, transport),
                    symbol: actualSelected),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, actualSelected))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_an_assignment_expression_result_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedAssignment = AddAssignment(
            expectedBuilder,
            AddDynamicName(expectedBuilder, "target"),
            AddInteger(expectedBuilder, 7));
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedAssignment)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var temporary = AddGeneratedLocal(actualBuilder, "v2");
        var declaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddInteger(actualBuilder, 7),
            symbol: temporary);
        var assignmentStatement = AddAssignmentStatement(
            actualBuilder,
            AddDynamicName(actualBuilder, "target"),
            AddSymbolReference(actualBuilder, temporary));
        var returnStatement = actualBuilder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: AddSymbolReference(actualBuilder, temporary));
        var actual = CreateMethod(
            actualBuilder,
            [declaration, assignmentStatement, returnStatement]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_stable_member_assignment_result_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedAssignment = AddAssignment(
            expectedBuilder,
            AddMember(
                expectedBuilder,
                AddDynamicName(expectedBuilder, "object"),
                "value"),
            AddInteger(expectedBuilder, 1));
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedAssignment)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var receiver = AddGeneratedLocal(actualBuilder, "v1");
        var resultValue = AddGeneratedLocal(
            actualBuilder,
            "__avm1_spill_0");
        var receiverDeclaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddDynamicName(actualBuilder, "object"),
            symbol: receiver);
        var resultDeclaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddInteger(actualBuilder, 1),
            symbol: resultValue);
        var assignment = AddAssignmentStatement(
            actualBuilder,
            AddMember(
                actualBuilder,
                AddSymbolReference(actualBuilder, receiver),
                "value"),
            AddInteger(actualBuilder, 1));
        var actual = CreateMethod(
            actualBuilder,
            [
                receiverDeclaration,
                resultDeclaration,
                assignment,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, resultValue))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Restores_a_logical_and_phi_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var logicalAnd = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddDynamicName(expectedBuilder, "left"),
                AddDynamicName(expectedBuilder, "right")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: logicalAnd)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var temporary = AddGeneratedLocal(actualBuilder, "v3");
        var declaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddDynamicName(actualBuilder, "left"),
            symbol: temporary);
        var assignment = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, temporary),
            AddDynamicName(actualBuilder, "right"));
        var branch = actualBuilder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [assignment]);
        var conditional = actualBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddSymbolReference(actualBuilder, temporary),
            children: [branch]);
        var actual = CreateMethod(
            actualBuilder,
            [
                declaration,
                conditional,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, temporary))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_multi_stage_logical_alias_chain()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedLeft = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children:
            [
                AddDynamicName(expectedBuilder, "first"),
                AddDynamicName(expectedBuilder, "second")
            ]);
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children:
            [
                expectedLeft,
                AddDynamicName(expectedBuilder, "third")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: expectedCondition,
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        expectedBuilder.AddStatement(
                            Avm1SourceStatementKind.Return))
                ])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var condition = AddGeneratedTemporary(actualBuilder, "condition");
        var accumulator = AddGeneratedLocal(actualBuilder, "accumulator");
        var firstUpdate = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, accumulator),
            AddDynamicName(actualBuilder, "second"));
        var secondUpdate = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, accumulator),
            AddDynamicName(actualBuilder, "third"));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "first"),
                    symbol: condition),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, condition),
                    symbol: accumulator),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children:
                        [
                            AddSymbolReference(actualBuilder, condition)
                        ]),
                    children: [AddBlock(actualBuilder, firstUpdate)]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children:
                        [
                            AddSymbolReference(actualBuilder, accumulator)
                        ]),
                    children: [AddBlock(actualBuilder, secondUpdate)]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, accumulator),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restores_a_guarded_multi_stage_logical_transport(bool logicalOr)
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedOperator = logicalOr
            ? Avm1SourceOperator.LogicalOr
            : Avm1SourceOperator.LogicalAnd;
        var expectedLeft = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            expectedOperator,
            children:
            [
                AddDynamicName(expectedBuilder, "first"),
                AddDynamicName(expectedBuilder, "second")
            ]);
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            expectedOperator,
            children:
            [
                expectedLeft,
                AddDynamicName(expectedBuilder, "third")
            ]);
        var expectedFullCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            expectedOperator,
            children:
            [
                expectedCondition,
                AddDynamicName(expectedBuilder, "fourth")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: expectedFullCondition,
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        expectedBuilder.AddStatement(
                            Avm1SourceStatementKind.Return))
                ])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var transport = AddGeneratedLocal(actualBuilder, "transport");
        var assignSecond = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, transport),
            AddDynamicName(actualBuilder, "second"));
        var assignThird = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, transport),
            AddDynamicName(actualBuilder, "third"));
        var assignFourth = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, transport),
            AddDynamicName(actualBuilder, "fourth"));
        SourceStatementIndex AddSelfAssignment() => AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, transport),
            AddSymbolReference(actualBuilder, transport));
        SourceExpressionIndex AddTransportTest()
        {
            var reference = AddSymbolReference(actualBuilder, transport);
            return logicalOr
                ? actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Unary,
                    Avm1SourceOperator.LogicalNot,
                    children: [reference])
                : reference;
        }

        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "first"),
                    symbol: transport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddTransportTest(),
                    children: [AddBlock(actualBuilder, assignSecond)]),
                AddSelfAssignment(),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddTransportTest(),
                    children: [AddBlock(actualBuilder, assignThird)]),
                AddSelfAssignment(),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddTransportTest(),
                    children: [AddBlock(actualBuilder, assignFourth)]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, transport),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removes_empty_transport_nodes_before_guarded_logical_rewrite()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children:
            [
                AddDynamicName(expectedBuilder, "first"),
                AddDynamicName(expectedBuilder, "second")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: expectedCondition,
                    children:
                    [
                        AddBlock(
                            expectedBuilder,
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Expression,
                                expression: expectedBuilder.AddExpression(
                                    Avm1SourceExpressionKind.Call,
                                    children:
                                    [
                                        AddDynamicName(expectedBuilder, "eval"),
                                        AddString(expectedBuilder, "focus")
                                    ])),
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var transport = AddGeneratedLocal(actualBuilder, "transport");
        var unused = AddGeneratedLocal(actualBuilder, "unused");
        var update = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, transport),
            AddDynamicName(actualBuilder, "second"));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "first"),
                    symbol: transport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children:
                        [
                            AddSymbolReference(actualBuilder, transport)
                        ]),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.VariableDeclaration,
                                symbol: unused),
                            update)
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, transport),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Expression,
                                expression: actualBuilder.AddExpression(
                                    Avm1SourceExpressionKind.Call,
                                    children:
                                    [
                                        AddDynamicName(actualBuilder, "eval"),
                                        AddString(actualBuilder, "focus")
                                    ])),
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_the_first_valid_transport_prefix_into_an_array_literal()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedValues = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "values",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedRight = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddDynamicName(expectedBuilder, "right"),
                AddInteger(expectedBuilder, 1)
            ]);
        var expectedLiteral = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children:
            [
                AddDynamicName(expectedBuilder, "left"),
                expectedRight
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: expectedLiteral,
                    symbol: expectedValues),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(
                        expectedBuilder,
                        expectedValues))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var first = AddGeneratedLocal(actualBuilder, "firstTransport");
        var second = AddGeneratedLocal(actualBuilder, "secondTransport");
        var actualValues = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "values",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualRight = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddDynamicName(actualBuilder, "right"),
                AddInteger(actualBuilder, 1)
            ]);
        var transportedLiteral = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children:
            [
                AddSymbolReference(actualBuilder, first),
                AddSymbolReference(actualBuilder, second)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "left"),
                    symbol: first),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualRight,
                    symbol: second),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: transportedLiteral,
                    symbol: actualValues),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, actualValues))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_short_circuit_assignment_condition_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedResult = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedLeft = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Greater,
            children:
            [
                AddDynamicName(expectedBuilder, "value"),
                AddInteger(expectedBuilder, 0)
            ]);
        var expectedValue = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddDynamicName(expectedBuilder, "value"),
                AddInteger(expectedBuilder, 1)
            ]);
        var expectedAssignment = AddAssignment(
            expectedBuilder,
            AddSymbolReference(expectedBuilder, expectedResult),
            expectedValue);
        var expectedRight = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Greater,
            children: [expectedAssignment, AddInteger(expectedBuilder, 0)]);
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children: [expectedLeft, expectedRight]);
        var expectedUpdate = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(expectedBuilder, expectedResult),
                AddInteger(expectedBuilder, 10)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(expectedBuilder, 0),
                    symbol: expectedResult),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: expectedCondition,
                    children:
                    [
                        AddBlock(
                            expectedBuilder,
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Expression,
                                expression: expectedUpdate))
                    ]),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(
                        expectedBuilder,
                        expectedResult))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualResult = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var conditionTransport = AddGeneratedLocal(
            actualBuilder,
            "conditionTransport");
        var valueTransport = AddGeneratedLocal(
            actualBuilder,
            "valueTransport");
        var actualLeft = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Greater,
            children:
            [
                AddDynamicName(actualBuilder, "value"),
                AddInteger(actualBuilder, 0)
            ]);
        var actualValue = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddDynamicName(actualBuilder, "value"),
                AddInteger(actualBuilder, 1)
            ]);
        var branch = AddBlock(
            actualBuilder,
            actualBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: actualValue,
                symbol: valueTransport),
            AddAssignmentStatement(
                actualBuilder,
                AddSymbolReference(actualBuilder, actualResult),
                AddSymbolReference(actualBuilder, valueTransport)),
            AddAssignmentStatement(
                actualBuilder,
                AddSymbolReference(actualBuilder, conditionTransport),
                actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.Greater,
                    children:
                    [
                        AddSymbolReference(actualBuilder, valueTransport),
                        AddInteger(actualBuilder, 0)
                    ])));
        var actualUpdate = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(actualBuilder, actualResult),
                AddInteger(actualBuilder, 10)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(actualBuilder, 0),
                    symbol: actualResult),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualLeft,
                    symbol: conditionTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(
                        actualBuilder,
                        conditionTransport),
                    children: [branch]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(
                        actualBuilder,
                        conditionTransport),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Expression,
                                expression: actualUpdate))
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(
                        actualBuilder,
                        actualResult))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Splits_a_generated_register_reused_after_a_short_circuit()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddDynamicName(expectedBuilder, "left"),
                AddDynamicName(expectedBuilder, "right")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: expectedCondition,
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        AddAssignmentStatement(
                            expectedBuilder,
                            AddDynamicName(expectedBuilder, "result"),
                            AddDynamicName(expectedBuilder, "value")))
                ])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var transport = AddGeneratedLocal(actualBuilder, "transport");
        var updateCondition = AddBlock(
            actualBuilder,
            AddAssignmentStatement(
                actualBuilder,
                AddSymbolReference(actualBuilder, transport),
                AddDynamicName(actualBuilder, "right")));
        var body = AddBlock(
            actualBuilder,
            AddAssignmentStatement(
                actualBuilder,
                AddSymbolReference(actualBuilder, transport),
                AddDynamicName(actualBuilder, "value")),
            AddAssignmentStatement(
                actualBuilder,
                AddDynamicName(actualBuilder, "result"),
                AddSymbolReference(actualBuilder, transport)));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "left"),
                    symbol: transport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, transport),
                    children: [updateCondition]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, transport),
                    children: [body])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_a_stable_generated_alias_across_a_short_circuit()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedParameter = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedLogical = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children:
            [
                AddDynamicName(expectedBuilder, "left"),
                AddDynamicName(expectedBuilder, "right")
            ]);
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(expectedBuilder, "invoke"),
                AddSymbolReference(expectedBuilder, expectedParameter),
                expectedLogical
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expectedCall)],
            [expectedParameter]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualParameter = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var parameterTransport = AddGeneratedLocal(
            actualBuilder,
            "parameterTransport");
        var logicalTransport = AddGeneratedLocal(
            actualBuilder,
            "logicalTransport");
        var argumentTransport = AddGeneratedLocal(
            actualBuilder,
            "argumentTransport");
        var negatedLogical = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [AddSymbolReference(actualBuilder, logicalTransport)]);
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(actualBuilder, "invoke"),
                AddSymbolReference(actualBuilder, argumentTransport),
                AddSymbolReference(actualBuilder, logicalTransport)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(
                        actualBuilder,
                        actualParameter),
                    symbol: parameterTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "left"),
                    symbol: logicalTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(
                        actualBuilder,
                        parameterTransport),
                    symbol: argumentTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: negatedLogical,
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            AddAssignmentStatement(
                                actualBuilder,
                                AddSymbolReference(
                                    actualBuilder,
                                    logicalTransport),
                                AddDynamicName(actualBuilder, "right")))
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: actualCall)
            ],
            [actualParameter]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Splits_a_generated_receiver_before_its_next_definition()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedScratch = AddGeneratedLocal(expectedBuilder, "scratch");
        var expectedResult = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedLookup = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "Selection"),
                    "lookup"),
                AddDynamicName(expectedBuilder, "index")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: expectedLookup,
                    symbol: expectedResult),
                AddAssignmentStatement(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedScratch),
                    AddDynamicName(expectedBuilder, "flag")),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: expectedBuilder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Add,
                        children:
                        [
                            AddSymbolReference(expectedBuilder, expectedResult),
                            AddSymbolReference(expectedBuilder, expectedScratch)
                        ]))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualScratch = AddGeneratedLocal(actualBuilder, "scratch");
        var actualResult = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualLookup = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualScratch),
                    "lookup"),
                AddDynamicName(actualBuilder, "index")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "Selection"),
                    symbol: actualScratch),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualLookup,
                    symbol: actualResult),
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualScratch),
                    AddDynamicName(actualBuilder, "flag")),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Add,
                        children:
                        [
                            AddSymbolReference(actualBuilder, actualResult),
                            AddSymbolReference(actualBuilder, actualScratch)
                        ]))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_a_generated_receiver_into_a_for_initializer()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedIterator = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "iterator",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedInitializer = expectedBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: expectedBuilder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children:
                [
                    AddMember(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "Mouse"),
                        "getTopMostEntity")
                ]),
            symbol: expectedIterator);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.For,
                expression: AddSymbolReference(expectedBuilder, expectedIterator),
                initializers: [expectedInitializer])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualIterator = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "iterator",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var receiver = AddGeneratedLocal(actualBuilder, "receiver");
        var actualInitializer = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: actualBuilder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children:
                [
                    AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, receiver),
                        "getTopMostEntity")
                ]),
            symbol: actualIterator);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "Mouse"),
                    symbol: receiver),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.For,
                    expression: AddSymbolReference(actualBuilder, actualIterator),
                    initializers: [actualInitializer])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_logical_result_assigned_from_a_source_local()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedValue = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedLogical = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddSymbolReference(expectedBuilder, expectedValue),
                AddDynamicName(expectedBuilder, "right")
            ]);
        var expectedNegated = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [expectedLogical]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                AddAssignmentStatement(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedValue),
                    AddDynamicName(expectedBuilder, "left")),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: expectedNegated,
                    children:
                    [
                        AddBlock(
                            expectedBuilder,
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualValue = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var resultTransport = AddGeneratedLocal(actualBuilder, "result");
        var actual = CreateMethod(
            actualBuilder,
            [
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualValue),
                    AddDynamicName(actualBuilder, "left")),
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, resultTransport),
                    AddSymbolReference(actualBuilder, actualValue)),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, resultTransport),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            AddAssignmentStatement(
                                actualBuilder,
                                AddSymbolReference(actualBuilder, resultTransport),
                                AddDynamicName(actualBuilder, "right")))
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children:
                        [
                            AddSymbolReference(
                                actualBuilder,
                                resultTransport)
                        ]),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_logical_update_of_a_source_local()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedValue = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedLogical = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddSymbolReference(expectedBuilder, expectedValue),
                AddDynamicName(expectedBuilder, "right")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                AddAssignmentStatement(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedValue),
                    AddDynamicName(expectedBuilder, "left")),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: expectedBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children: [expectedLogical]),
                    children:
                    [
                        AddBlock(
                            expectedBuilder,
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualValue = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "__reg4",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualValue),
                    AddDynamicName(actualBuilder, "left")),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddSymbolReference(actualBuilder, actualValue),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            AddAssignmentStatement(
                                actualBuilder,
                                AddSymbolReference(actualBuilder, actualValue),
                                AddDynamicName(actualBuilder, "right")))
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children: [AddSymbolReference(actualBuilder, actualValue)]),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ])
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_short_circuit_after_reversed_call_transports()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedParameter = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedThis = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Object));
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedThis),
                    "invoke"),
                AddSymbolReference(expectedBuilder, expectedParameter),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.LogicalOr,
                    children:
                    [
                        AddMember(
                            expectedBuilder,
                            AddSymbolReference(expectedBuilder, expectedThis),
                            "height"),
                        AddMember(
                            expectedBuilder,
                            AddSymbolReference(expectedBuilder, expectedThis),
                            "fallbackHeight")
                    ])
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expectedCall)],
            [expectedParameter]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualParameter = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualThis = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Object));
        var parameterTransport = AddGeneratedLocal(actualBuilder, "parameter");
        var logicalValue = AddGeneratedLocal(actualBuilder, "logicalValue");
        var conditionTransport = AddGeneratedLocal(actualBuilder, "condition");
        var argumentTransport = AddGeneratedLocal(actualBuilder, "argument");
        var receiverTransport = AddGeneratedLocal(actualBuilder, "receiver");
        var logicalResult = AddGeneratedLocal(actualBuilder, "logicalResult");
        var argumentResult = AddGeneratedLocal(actualBuilder, "argumentResult");
        var receiverResult = AddGeneratedLocal(actualBuilder, "receiverResult");
        var negatedCondition = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [AddSymbolReference(actualBuilder, conditionTransport)]);
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, receiverResult),
                    "invoke"),
                AddSymbolReference(actualBuilder, argumentResult),
                AddSymbolReference(actualBuilder, logicalResult)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, actualParameter),
                    symbol: parameterTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, actualThis),
                        "height"),
                    symbol: logicalValue),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, logicalValue),
                    symbol: conditionTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, parameterTransport),
                    symbol: argumentTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, actualThis),
                    symbol: receiverTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: negatedCondition,
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            AddAssignmentStatement(
                                actualBuilder,
                                AddSymbolReference(actualBuilder, logicalValue),
                                AddMember(
                                    actualBuilder,
                                    AddSymbolReference(actualBuilder, actualThis),
                                    "fallbackHeight")))
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, logicalValue),
                    symbol: logicalResult),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, argumentTransport),
                    symbol: argumentResult),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, receiverTransport),
                    symbol: receiverResult),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: actualCall)
            ],
            [actualParameter]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_a_trailing_loop_if_as_a_continue_guard()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedBody = AddBlock(
            expectedBuilder,
            expectedBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: AddDynamicName(expectedBuilder, "condition"),
                children:
                [
                    AddBlock(
                        expectedBuilder,
                        AddAssignmentStatement(
                            expectedBuilder,
                            AddDynamicName(expectedBuilder, "result"),
                            AddInteger(expectedBuilder, 1)))
                ]));
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.While,
                expression: AddDynamicName(expectedBuilder, "running"),
                children: [expectedBody])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var negated = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [AddDynamicName(actualBuilder, "condition")]);
        var actualBody = AddBlock(
            actualBuilder,
            actualBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: negated,
                children:
                [
                    AddBlock(
                        actualBuilder,
                        actualBuilder.AddStatement(
                            Avm1SourceStatementKind.Continue))
                ]),
            AddAssignmentStatement(
                actualBuilder,
                AddDynamicName(actualBuilder, "result"),
                AddInteger(actualBuilder, 1)));
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.While,
                expression: AddDynamicName(actualBuilder, "running"),
                children: [actualBody])]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_a_trailing_loop_else_if_continue_guard()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedNested = expectedBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(expectedBuilder, "secondary"),
            children:
            [
                AddBlock(
                    expectedBuilder,
                    AddAssignmentStatement(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "second"),
                        AddInteger(expectedBuilder, 2))),
                AddBlock(
                    expectedBuilder,
                    AddAssignmentStatement(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "fallback"),
                        AddInteger(expectedBuilder, 3)))
            ]);
        var expectedConditional = expectedBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(expectedBuilder, "primary"),
            children:
            [
                AddBlock(
                    expectedBuilder,
                    AddAssignmentStatement(
                        expectedBuilder,
                        AddDynamicName(expectedBuilder, "first"),
                        AddInteger(expectedBuilder, 1))),
                AddBlock(expectedBuilder, expectedNested)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.While,
                expression: AddDynamicName(expectedBuilder, "running"),
                children: [AddBlock(expectedBuilder, expectedConditional)])]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var guardedSecondary = actualBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(actualBuilder, "secondary"),
            children:
            [
                AddBlock(
                    actualBuilder,
                    AddAssignmentStatement(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "second"),
                        AddInteger(actualBuilder, 2)),
                    actualBuilder.AddStatement(
                        Avm1SourceStatementKind.Continue))
            ]);
        var actualConditional = actualBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(actualBuilder, "primary"),
            children:
            [
                AddBlock(
                    actualBuilder,
                    AddAssignmentStatement(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "first"),
                        AddInteger(actualBuilder, 1))),
                AddBlock(
                    actualBuilder,
                    guardedSecondary,
                    AddAssignmentStatement(
                        actualBuilder,
                        AddDynamicName(actualBuilder, "fallback"),
                        AddInteger(actualBuilder, 3)))
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.While,
                expression: AddDynamicName(actualBuilder, "running"),
                children: [AddBlock(actualBuilder, actualConditional)])]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_conditional_phi_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var conditionalExpression = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(expectedBuilder, "condition"),
                AddInteger(expectedBuilder, 1),
                AddInteger(expectedBuilder, 2)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: conditionalExpression)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var temporary = AddGeneratedLocal(actualBuilder, "v4");
        var declaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            symbol: temporary);
        var whenTrue = actualBuilder.AddStatement(
            Avm1SourceStatementKind.Block,
            children:
            [
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, temporary),
                    AddInteger(actualBuilder, 1))
            ]);
        var whenFalse = actualBuilder.AddStatement(
            Avm1SourceStatementKind.Block,
            children:
            [
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, temporary),
                    AddInteger(actualBuilder, 2))
            ]);
        var conditional = actualBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(actualBuilder, "condition"),
            children: [whenTrue, whenFalse]);
        var actual = CreateMethod(
            actualBuilder,
            [
                declaration,
                conditional,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, temporary))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_conditional_phi_in_an_adobe_member_call_argument()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedValue = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(expectedBuilder, "condition"),
                AddString(expectedBuilder, "show"),
                AddString(expectedBuilder, "hide")
            ]);
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "indicator"),
                    "gotoAndPlay"),
                expectedValue
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expectedCall)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var transport = AddGeneratedLocal(actualBuilder, "state");
        var whenTrue = AddBlock(
            actualBuilder,
            actualBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: AddString(actualBuilder, "show"),
                symbol: transport));
        var whenFalse = AddBlock(
            actualBuilder,
            AddAssignmentStatement(
                actualBuilder,
                AddSymbolReference(actualBuilder, transport),
                AddString(actualBuilder, "hide")));
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "indicator"),
                    "gotoAndPlay"),
                AddSymbolReference(actualBuilder, transport)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddDynamicName(actualBuilder, "condition"),
                    children: [whenTrue, whenFalse]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: actualCall)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_conditional_phi_in_a_late_adobe_call_argument()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedValue = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(expectedBuilder, "condition"),
                AddString(expectedBuilder, "show"),
                AddString(expectedBuilder, "hide")
            ]);
        var expectedCall = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "manager"),
                    "setOverride"),
                AddString(expectedBuilder, "section"),
                AddInteger(expectedBuilder, 1),
                expectedValue
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expectedCall)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var transport = AddGeneratedLocal(actualBuilder, "state");
        var whenTrue = AddBlock(
            actualBuilder,
            actualBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: AddString(actualBuilder, "show"),
                symbol: transport));
        var whenFalse = AddBlock(
            actualBuilder,
            AddAssignmentStatement(
                actualBuilder,
                AddSymbolReference(actualBuilder, transport),
                AddString(actualBuilder, "hide")));
        var actualCall = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "manager"),
                    "setOverride"),
                AddString(actualBuilder, "section"),
                AddInteger(actualBuilder, 1),
                AddSymbolReference(actualBuilder, transport)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddDynamicName(actualBuilder, "condition"),
                    children: [whenTrue, whenFalse]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: actualCall)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_a_stable_assignment_to_compound_assignment()
    {
        var expected = CreateCompoundAssignmentMethod(compound: true);
        var actual = CreateCompoundAssignmentMethod(compound: false);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_a_static_this_member_path_to_compound_assignment()
    {
        static SourceSymbolIndex AddThis(Avm1SourceArena.Builder builder) =>
            builder.AddSymbol(
                Avm1SourceSymbolKind.This,
                "this",
                SourceTypeIndex.Invalid,
                builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
                Avm1SourceSymbolFlags.DeclarationProvided);

        static SourceExpressionIndex AddTarget(
            Avm1SourceArena.Builder builder,
            SourceSymbolIndex @this) =>
            AddMember(
                builder,
                AddMember(
                    builder,
                    AddSymbolReference(builder, @this),
                    "child"),
                "x");

        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedThis = AddThis(expectedBuilder);
        var expectedAssignment = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.SubtractAssign,
            children:
            [
                AddTarget(expectedBuilder, expectedThis),
                AddDynamicName(expectedBuilder, "offset")
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expectedAssignment)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualThis = AddThis(actualBuilder);
        var difference = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Subtract,
            children:
            [
                AddTarget(actualBuilder, actualThis),
                AddDynamicName(actualBuilder, "offset")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [AddAssignmentStatement(
                actualBuilder,
                AddTarget(actualBuilder, actualThis),
                difference)]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_a_prefix_increment_result_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedOuter = AddParameter(expectedBuilder, "outer");
        var expectedInner = AddParameter(expectedBuilder, "inner");
        var expectedTarget = AddMember(
            expectedBuilder,
            AddSymbolReference(expectedBuilder, expectedOuter),
            "value");
        var expectedSource = AddMember(
            expectedBuilder,
            AddSymbolReference(expectedBuilder, expectedInner),
            "value");
        var expectedPrefix = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PrefixIncrement,
            children: [expectedSource]);
        var expected = CreateMethod(
            expectedBuilder,
            [AddAssignmentStatement(
                expectedBuilder,
                expectedTarget,
                expectedPrefix)],
            [expectedOuter, expectedInner]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualOuter = AddParameter(actualBuilder, "outer");
        var actualInner = AddParameter(actualBuilder, "inner");
        var transport = AddGeneratedLocal(actualBuilder, "newValue");
        var actualSource = AddMember(
            actualBuilder,
            AddSymbolReference(actualBuilder, actualInner),
            "value");
        var steppedValue = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children: [actualSource, AddInteger(actualBuilder, 1)]);
        var declaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: steppedValue,
            symbol: transport);
        var update = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children: [actualSource, AddInteger(actualBuilder, 1)]);
        var actualTarget = AddMember(
            actualBuilder,
            AddSymbolReference(actualBuilder, actualOuter),
            "value");
        var actual = CreateMethod(
            actualBuilder,
            [
                declaration,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: update),
                AddAssignmentStatement(
                    actualBuilder,
                    actualTarget,
                    AddSymbolReference(actualBuilder, transport))
            ],
            [actualOuter, actualInner]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Keeps_clause_confined_authored_and_recovered_locals_equivalent()
    {
        static Avm1SourceMethod CreateSwitchMethod(
            Avm1SourceSymbolFlags flags)
        {
            var builder = new Avm1SourceArena.Builder();
            var local = builder.AddSymbol(
                Avm1SourceSymbolKind.Local,
                "value",
                SourceTypeIndex.Invalid,
                builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
                flags);
            var declaration = builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: AddDynamicName(builder, "input"),
                symbol: local);
            var sum = builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                Avm1SourceOperator.Add,
                children:
                [
                    AddSymbolReference(builder, local),
                    AddSymbolReference(builder, local)
                ]);
            var assignment = AddAssignmentStatement(
                builder,
                AddDynamicName(builder, "output"),
                sum);
            var switchCase = builder.AddStatement(
                Avm1SourceStatementKind.SwitchCase,
                expression: AddInteger(builder, 1),
                children:
                [
                    declaration,
                    assignment,
                    builder.AddStatement(Avm1SourceStatementKind.Break)
                ]);
            var @switch = builder.AddStatement(
                Avm1SourceStatementKind.Switch,
                expression: AddDynamicName(builder, "kind"),
                children: [switchCase]);
            return CreateMethod(builder, [@switch]);
        }

        var recovered = CreateSwitchMethod(
            Avm1SourceSymbolFlags.CompilerGenerated);
        var authored = CreateSwitchMethod(Avm1SourceSymbolFlags.None);

        var result = Avm1SourceEquivalence.Compare(recovered, authored);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
    }

    [Fact]
    public void Folds_numeric_binary_constants()
    {
        var expected = CreateLiteralReturn(
            Avm1SourceLiteralKind.Number,
            numberValue: 3);
        var actualBuilder = new Avm1SourceArena.Builder();
        var sum = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddInteger(actualBuilder, 1),
                AddInteger(actualBuilder, 2)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: sum)]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Reaches_a_fixed_point_for_deeply_nested_constant_expressions()
    {
        var expected = CreateLiteralReturn(
            Avm1SourceLiteralKind.Number,
            numberValue: 8);
        var actualBuilder = new Avm1SourceArena.Builder();
        var value = AddInteger(actualBuilder, 1);
        for (var i = 1; i < 8; i++)
        {
            value = actualBuilder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                Avm1SourceOperator.Add,
                children: [value, AddInteger(actualBuilder, 1)]);
        }
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: value)]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Normalizes_an_inverted_if_with_swapped_arms()
    {
        var expected = CreateConditionalStatementMethod(inverted: false);
        var actual = CreateConditionalStatementMethod(inverted: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Puts_the_more_complex_terminal_branch_first()
    {
        var expected = CreateTerminalBranchOrderingMethod(inverted: false);
        var actual = CreateTerminalBranchOrderingMethod(inverted: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_value_and_valueless_terminal_returns()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddDynamicName(expectedBuilder, "condition"),
                    children:
                    [
                        AddBlock(
                            expectedBuilder,
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Return))
                    ]),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(expectedBuilder, 1))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Unary,
                        Avm1SourceOperator.LogicalNot,
                        children:
                        [
                            AddDynamicName(actualBuilder, "condition")
                        ]),
                    children:
                    [
                        AddBlock(
                            actualBuilder,
                            actualBuilder.AddStatement(
                                Avm1SourceStatementKind.Return,
                                expression: AddInteger(actualBuilder, 1)))
                    ]),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
    }

    [Fact]
    public void Canonicalizes_a_terminal_if_else_as_a_guard_and_tail()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: AddDynamicName(expectedBuilder, "condition"),
                    children:
                    [
                        AddBlock(
                            expectedBuilder,
                            expectedBuilder.AddStatement(
                                Avm1SourceStatementKind.Return,
                                expression: AddInteger(expectedBuilder, 1)))
                    ]),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(expectedBuilder, 2))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: AddDynamicName(actualBuilder, "condition"),
                children:
                [
                    AddBlock(
                        actualBuilder,
                        actualBuilder.AddStatement(
                            Avm1SourceStatementKind.Return,
                            expression: AddInteger(actualBuilder, 1))),
                    AddBlock(
                        actualBuilder,
                        actualBuilder.AddStatement(
                            Avm1SourceStatementKind.Return,
                            expression: AddInteger(actualBuilder, 2)))
                ])]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_a_terminal_guard_and_switch_default_tail()
    {
        var expected = CreateTerminalGuardSwitchMethod(
            switchIsGuarded: false);
        var actual = CreateTerminalGuardSwitchMethod(
            switchIsGuarded: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result) + Environment.NewLine +
            "EXPECTED:" + Environment.NewLine +
            Avm1SourceNormalizer.Normalize(
                expected,
                CancellationToken.None).Method.GetAs2Text() +
            Environment.NewLine + "ACTUAL:" + Environment.NewLine +
            Avm1SourceNormalizer.Normalize(
                actual,
                CancellationToken.None).Method.GetAs2Text());
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removes_an_unused_empty_generated_declaration()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddInteger(expectedBuilder, 1))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var unused = AddGeneratedLocal(actualBuilder, "unused");
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    symbol: unused),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(actualBuilder, 1))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removes_an_unused_pure_generated_declaration()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddInteger(expectedBuilder, 1))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var unused = AddGeneratedTemporary(actualBuilder, "unused");
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(actualBuilder, 7),
                    symbol: unused),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(actualBuilder, 1))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removes_an_unused_pure_recovery_local_declaration()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddInteger(expectedBuilder, 1))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var unused = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "v13",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(actualBuilder, 7),
                    symbol: unused),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(actualBuilder, 1))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Preserves_effects_when_removing_an_unused_recovery_local_declaration()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedRead = AddMember(
            expectedBuilder,
            AddDynamicName(expectedBuilder, "ClassObject"),
            "prototype");
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: expectedRead),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(expectedBuilder, 1))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var unused = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "v42",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actualRead = AddMember(
            actualBuilder,
            AddDynamicName(actualBuilder, "ClassObject"),
            "prototype");
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualRead,
                    symbol: unused),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(actualBuilder, 1))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_unused_temporary_and_recovery_local_transport_kinds()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedUnused = AddGeneratedTemporary(expectedBuilder, "v7");
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: AddMember(
                    expectedBuilder,
                    AddDynamicName(expectedBuilder, "ClassObject"),
                    "prototype"),
                symbol: expectedUnused)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualUnused = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "v7",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: AddMember(
                    actualBuilder,
                    AddDynamicName(actualBuilder, "ClassObject"),
                    "prototype"),
                symbol: actualUnused)]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ExpectedRewriteCount.ShouldBeGreaterThan(0);
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removes_a_redundant_symbol_self_assignment()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedSymbol = AddGeneratedLocal(expectedBuilder, "result");
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddSymbolReference(expectedBuilder, expectedSymbol))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualSymbol = AddGeneratedLocal(actualBuilder, "result");
        var selfAssignment = AddAssignment(
            actualBuilder,
            AddSymbolReference(actualBuilder, actualSymbol),
            AddSymbolReference(actualBuilder, actualSymbol));
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: selfAssignment),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, actualSymbol))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Normalizes_logical_not_over_a_comparison()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedCondition = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Equal,
            children:
            [
                AddDynamicName(expectedBuilder, "value"),
                AddInteger(expectedBuilder, 1)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedCondition)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var notEqual = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.NotEqual,
            children:
            [
                AddDynamicName(actualBuilder, "value"),
                AddInteger(actualBuilder, 1)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [actualBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Unary,
                    Avm1SourceOperator.LogicalNot,
                    children: [notEqual]))]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Normalizes_a_standalone_logical_not_to_a_fixed_point()
    {
        var builder = new Avm1SourceArena.Builder();
        var method = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: builder.AddExpression(
                    Avm1SourceExpressionKind.Unary,
                    Avm1SourceOperator.LogicalNot,
                    children: [AddDynamicName(builder, "value")]))]);

        var result = Avm1SourceEquivalence.Compare(method, method);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
    }

    [Fact]
    public void Normalization_preserves_computed_member_access_with_a_dynamic_key()
    {
        var builder = new Avm1SourceArena.Builder();
        var values = AddParameter(builder, "values");
        var index = AddParameter(builder, "index");
        var key = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddSymbolReference(builder, index)]);
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [AddSymbolReference(builder, values), key]);
        var method = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: member)],
            [values, index]);

        var normalized = Avm1SourceNormalizer.Normalize(
            method,
            CancellationToken.None);
        var text = normalized.Method.GetAs2Text();

        normalized.Diagnostics.ShouldBeEmpty();
        text.ShouldContain("parameter0[parameter1++]");
        text.ShouldNotContain("parameter0.parameter1++");
    }

    [Fact]
    public void Restores_a_branch_local_phi_with_a_condition_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedConditional = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(expectedBuilder, "condition"),
                AddInteger(expectedBuilder, 1),
                AddInteger(expectedBuilder, 2)
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedConditional)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var condition = AddGeneratedLocal(actualBuilder, "conditionValue");
        var resultValue = AddGeneratedLocal(actualBuilder, "resultValue");
        var conditionDeclaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddDynamicName(actualBuilder, "condition"),
            symbol: condition);
        var negatedCondition = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [AddSymbolReference(actualBuilder, condition)]);
        var falseValueDeclaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddInteger(actualBuilder, 2),
            symbol: resultValue);
        var trueValueAssignment = AddAssignmentStatement(
            actualBuilder,
            AddSymbolReference(actualBuilder, resultValue),
            AddInteger(actualBuilder, 1));
        var conditional = actualBuilder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: negatedCondition,
            children:
            [
                AddBlock(actualBuilder, falseValueDeclaration),
                AddBlock(actualBuilder, trueValueAssignment)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                conditionDeclaration,
                conditional,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, resultValue))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Restores_an_assignment_chain_from_a_shared_value_transport()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedReceiver = AddParameter(expectedBuilder, "receiver");
        var expectedKey = AddParameter(expectedBuilder, "key");
        var expectedResult = expectedBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            expectedBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var expectedMember = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children:
            [
                AddMember(
                    expectedBuilder,
                    AddSymbolReference(expectedBuilder, expectedReceiver),
                    "owner"),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.Add,
                    children:
                    [
                        AddString(expectedBuilder, "prefix_"),
                        AddSymbolReference(expectedBuilder, expectedKey)
                    ])
            ]);
        var expectedInner = AddAssignment(
            expectedBuilder,
            AddSymbolReference(expectedBuilder, expectedResult),
            expectedBuilder.AddExpression(Avm1SourceExpressionKind.ArrayLiteral));
        var expected = CreateMethod(
            expectedBuilder,
            [AddAssignmentStatement(expectedBuilder, expectedMember, expectedInner)],
            [expectedReceiver, expectedKey]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualReceiver = AddParameter(actualBuilder, "receiver");
        var actualKey = AddParameter(actualBuilder, "key");
        var actualResult = actualBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            actualBuilder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var receiverTransport = AddGeneratedTemporary(actualBuilder, "v36");
        var valueTransport = AddGeneratedTemporary(actualBuilder, "v40");
        var actualMember = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children:
            [
                AddSymbolReference(actualBuilder, receiverTransport),
                actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.Binary,
                    Avm1SourceOperator.Add,
                    children:
                    [
                        AddString(actualBuilder, "prefix_"),
                        AddSymbolReference(actualBuilder, actualKey)
                    ])
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, actualReceiver),
                        "owner"),
                    symbol: receiverTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.ArrayLiteral),
                    symbol: valueTransport),
                AddAssignmentStatement(
                    actualBuilder,
                    AddSymbolReference(actualBuilder, actualResult),
                    AddSymbolReference(actualBuilder, valueTransport)),
                AddAssignmentStatement(
                    actualBuilder,
                    actualMember,
                    AddSymbolReference(actualBuilder, valueTransport))
            ],
            [actualReceiver, actualKey]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        var normalizedActual = Avm1SourceNormalizer.Normalize(
            actual,
            CancellationToken.None).Method.GetAs2Text();
        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result) + Environment.NewLine + normalizedActual);
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Collapses_generated_transport_aliases_before_inlining()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedMember = AddMember(
            expectedBuilder,
            AddDynamicName(expectedBuilder, "object"),
            "value");
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expectedMember)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var first = AddGeneratedLocal(actualBuilder, "v1");
        var second = AddGeneratedLocal(actualBuilder, "v2");
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "object"),
                    symbol: first),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(actualBuilder, first),
                    symbol: second),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, second),
                        "value"))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Keeps_a_transport_alias_read_after_the_selected_consumer()
    {
        var builder = new Avm1SourceArena.Builder();
        var source = AddGeneratedLocal(builder, "source");
        var alias = AddGeneratedLocal(builder, "alias");
        var method = CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(builder, "object"),
                    symbol: source),
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddSymbolReference(builder, source),
                    symbol: alias),
                AddAssignmentStatement(
                    builder,
                    AddDynamicName(builder, "unrelated"),
                    AddInteger(builder, 1)),
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddMember(
                        builder,
                        AddSymbolReference(builder, alias),
                        "value"))
            ]);

        var normalized = Avm1SourceNormalizer.Normalize(
            method,
            CancellationToken.None).Method.GetAs2Text();

        normalized.ShouldContain("var local0 = object;");
        normalized.ShouldContain("return local0.value;");
        normalized.ShouldNotContain("local1");
    }

    [Fact]
    public void Restores_unique_object_pairs_from_reversed_compiler_transports()
    {
        var expected = CreateObjectTransportMethod(
            withTransport: false,
            duplicateKeys: false);
        var actual = CreateObjectTransportMethod(
            withTransport: true,
            duplicateKeys: false);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_direct_transports_in_a_nested_object_literal()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedResult = AddGeneratedLocal(expectedBuilder, "result");
        var expectedMetrics = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(expectedBuilder, "left"),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(expectedBuilder, "readLeft")]),
                AddString(expectedBuilder, "bottom"),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(expectedBuilder, "readBottom")]),
                AddString(expectedBuilder, "xscale"),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(expectedBuilder, "readScale")])
            ]);
        var expectedObject = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(expectedBuilder, "clip"),
                AddDynamicName(expectedBuilder, "clip"),
                AddString(expectedBuilder, "metrics"),
                expectedMetrics
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: expectedObject,
                    symbol: expectedResult),
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(expectedBuilder, expectedResult))
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var left = AddGeneratedLocal(actualBuilder, "left");
        var scale = AddGeneratedLocal(actualBuilder, "scale");
        var bottom = AddGeneratedLocal(actualBuilder, "bottom");
        var actualResult = AddGeneratedLocal(actualBuilder, "result");
        var actualMetrics = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(actualBuilder, "left"),
                AddSymbolReference(actualBuilder, left),
                AddString(actualBuilder, "bottom"),
                AddSymbolReference(actualBuilder, bottom),
                AddString(actualBuilder, "xscale"),
                AddSymbolReference(actualBuilder, scale)
            ]);
        var actualObject = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(actualBuilder, "clip"),
                AddDynamicName(actualBuilder, "clip"),
                AddString(actualBuilder, "metrics"),
                actualMetrics
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children: [AddDynamicName(actualBuilder, "readLeft")]),
                    symbol: left),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children: [AddDynamicName(actualBuilder, "readScale")]),
                    symbol: scale),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children: [AddDynamicName(actualBuilder, "readBottom")]),
                    symbol: bottom),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualObject,
                    symbol: actualResult),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(actualBuilder, actualResult))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_a_transport_into_the_first_evaluated_nested_array_object()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedReceiver = AddGeneratedLocal(expectedBuilder, "receiver");
        var expectedArray = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children:
            [
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.ObjectLiteral,
                    children:
                    [
                        AddString(expectedBuilder, "name"),
                        AddString(expectedBuilder, "Left"),
                        AddString(expectedBuilder, "context"),
                        AddDynamicName(expectedBuilder, "contextValue")
                    ]),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.ObjectLiteral,
                    children:
                    [
                        AddString(expectedBuilder, "name"),
                        AddString(expectedBuilder, "Right"),
                        AddString(expectedBuilder, "context"),
                        AddDynamicName(expectedBuilder, "contextValue")
                    ])
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(expectedBuilder, "target"),
                    symbol: expectedReceiver),
                AddAssignmentStatement(
                    expectedBuilder,
                    AddMember(
                        expectedBuilder,
                        AddSymbolReference(expectedBuilder, expectedReceiver),
                        "values"),
                    expectedArray)
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualReceiver = AddGeneratedLocal(actualBuilder, "receiver");
        var transport = AddGeneratedLocal(actualBuilder, "transport");
        var actualArray = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children:
            [
                actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.ObjectLiteral,
                    children:
                    [
                        AddString(actualBuilder, "name"),
                        AddString(actualBuilder, "Left"),
                        AddString(actualBuilder, "context"),
                        AddDynamicName(actualBuilder, "contextValue")
                    ]),
                actualBuilder.AddExpression(
                    Avm1SourceExpressionKind.ObjectLiteral,
                    children:
                    [
                        AddString(actualBuilder, "name"),
                        AddString(actualBuilder, "Right"),
                        AddString(actualBuilder, "context"),
                        AddSymbolReference(actualBuilder, transport)
                    ])
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "target"),
                    symbol: actualReceiver),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "contextValue"),
                    symbol: transport),
                AddAssignmentStatement(
                    actualBuilder,
                    AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, actualReceiver),
                        "values"),
                    actualArray)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Inlines_aggregate_transports_into_an_assigned_object_literal()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedReceiver = AddGeneratedLocal(expectedBuilder, "receiver");
        var expectedMap = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(expectedBuilder, "up"),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.ArrayLiteral,
                    children: [AddString(expectedBuilder, "up")]),
                AddString(expectedBuilder, "down"),
                expectedBuilder.AddExpression(
                    Avm1SourceExpressionKind.ArrayLiteral,
                    children: [AddString(expectedBuilder, "down")])
            ]);
        var expected = CreateMethod(
            expectedBuilder,
            [
                expectedBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(expectedBuilder, "target"),
                    symbol: expectedReceiver),
                AddAssignmentStatement(
                    expectedBuilder,
                    AddMember(
                        expectedBuilder,
                        AddSymbolReference(expectedBuilder, expectedReceiver),
                        "stateMap"),
                    expectedMap)
            ]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var actualReceiver = AddGeneratedLocal(actualBuilder, "receiver");
        var up = AddGeneratedTemporary(actualBuilder, "up");
        var down = AddGeneratedLocal(actualBuilder, "down");
        var actualMap = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(actualBuilder, "up"),
                AddSymbolReference(actualBuilder, up),
                AddString(actualBuilder, "down"),
                AddSymbolReference(actualBuilder, down)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(actualBuilder, "target"),
                    symbol: actualReceiver),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.ArrayLiteral,
                        children: [AddString(actualBuilder, "up")]),
                    symbol: up),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: actualBuilder.AddExpression(
                        Avm1SourceExpressionKind.ArrayLiteral,
                        children: [AddString(actualBuilder, "down")]),
                    symbol: down),
                AddAssignmentStatement(
                    actualBuilder,
                    AddMember(
                        actualBuilder,
                        AddSymbolReference(actualBuilder, actualReceiver),
                        "stateMap"),
                    actualMap)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Elides_a_materialized_receiver_already_represented_by_its_origin()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedTarget = AddMember(
            expectedBuilder,
            AddMember(
                expectedBuilder,
                AddDynamicName(expectedBuilder, "object"),
                "child"),
            "field");
        var expected = CreateMethod(
            expectedBuilder,
            [AddAssignmentStatement(
                expectedBuilder,
                expectedTarget,
                AddInteger(expectedBuilder, 1))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var materializedOrigin = actualBuilder.AddOrigin(
            recoveryUnit: 3,
            recoveryNode: 10,
            startOffset: 24,
            endOffset: 25);
        var receiverOrigin = actualBuilder.AddOrigin(
            recoveryUnit: 3,
            recoveryNode: 11,
            startOffset: 24,
            endOffset: 25);
        var transport = AddGeneratedTemporary(actualBuilder, "receiver");
        var materializedReceiver = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            origin: materializedOrigin,
            children:
            [
                AddDynamicName(actualBuilder, "object"),
                AddString(actualBuilder, "child")
            ]);
        var repeatedReceiver = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            origin: receiverOrigin,
            children:
            [
                AddDynamicName(actualBuilder, "object"),
                AddString(actualBuilder, "child")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: materializedReceiver,
                    symbol: transport),
                AddAssignmentStatement(
                    actualBuilder,
                    AddMember(actualBuilder, repeatedReceiver, "field"),
                    AddInteger(actualBuilder, 1))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Elides_a_materialized_receiver_across_generated_transports()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedTarget = AddMember(
            expectedBuilder,
            AddMember(
                expectedBuilder,
                AddDynamicName(expectedBuilder, "object"),
                "child"),
            "field");
        var expected = CreateMethod(
            expectedBuilder,
            [AddAssignmentStatement(
                expectedBuilder,
                expectedTarget,
                AddInteger(expectedBuilder, 1))]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var materializedOrigin = actualBuilder.AddOrigin(
            recoveryUnit: 3,
            recoveryNode: 10,
            startOffset: 24,
            endOffset: 25);
        var receiverOrigin = actualBuilder.AddOrigin(
            recoveryUnit: 3,
            recoveryNode: 39,
            startOffset: 24,
            endOffset: 25);
        var receiverTransport = AddGeneratedTemporary(actualBuilder, "receiver");
        var valueTransport = AddGeneratedTemporary(actualBuilder, "value");
        var materializedReceiver = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            origin: materializedOrigin,
            children:
            [
                AddDynamicName(actualBuilder, "object"),
                AddString(actualBuilder, "child")
            ]);
        var repeatedReceiver = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            origin: receiverOrigin,
            children:
            [
                AddDynamicName(actualBuilder, "object"),
                AddString(actualBuilder, "child")
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: materializedReceiver,
                    symbol: receiverTransport),
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddInteger(actualBuilder, 1),
                    symbol: valueTransport),
                AddAssignmentStatement(
                    actualBuilder,
                    AddMember(actualBuilder, repeatedReceiver, "field"),
                    AddSymbolReference(actualBuilder, valueTransport))
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Does_not_reorder_duplicate_object_keys()
    {
        var expected = CreateObjectTransportMethod(
            withTransport: false,
            duplicateKeys: true);
        var actual = CreateObjectTransportMethod(
            withTransport: true,
            duplicateKeys: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Different);
    }

    [Fact]
    public void Restores_a_for_in_key_transport()
    {
        var expected = CreateForInTransportMethod(withTransport: false);
        var actual = CreateForInTransportMethod(withTransport: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Canonicalizes_a_discarded_increment_result()
    {
        var expected = CreateDiscardedIncrementMethod(useIncrement: false);
        var actual = CreateDiscardedIncrementMethod(useIncrement: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removes_only_the_final_unlabeled_switch_break()
    {
        var expected = CreateSwitchMethod(finalBreak: false);
        var actual = CreateSwitchMethod(finalBreak: true);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Equivalent, Describe(result));
        result.ActualRewriteCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Does_not_duplicate_a_reused_compiler_transport_initializer()
    {
        var expectedBuilder = new Avm1SourceArena.Builder();
        var expectedObject = AddDynamicName(expectedBuilder, "object");
        var duplicated = expectedBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children: [expectedObject, expectedObject]);
        var expected = CreateMethod(
            expectedBuilder,
            [expectedBuilder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: duplicated)]);

        var actualBuilder = new Avm1SourceArena.Builder();
        var temporary = AddGeneratedLocal(actualBuilder, "v5");
        var declaration = actualBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddDynamicName(actualBuilder, "object"),
            symbol: temporary);
        var sum = actualBuilder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddSymbolReference(actualBuilder, temporary),
                AddSymbolReference(actualBuilder, temporary)
            ]);
        var actual = CreateMethod(
            actualBuilder,
            [
                declaration,
                actualBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: sum)
            ]);

        var result = Avm1SourceEquivalence.Compare(expected, actual);

        result.Status.ShouldBe(Avm1SourceEquivalenceStatus.Different);
        result.ActualRewriteCount.ShouldBe(0);
    }

    private static Avm1SourceMethod CreateIdentityLoopMethod(
        string parameterName,
        string labelName,
        bool typed)
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var declaredType = typed
            ? builder.GetBuiltInType(Avm1SourceTypeKind.Number)
            : SourceTypeIndex.Invalid;
        var inferredType = typed
            ? builder.GetBuiltInType(Avm1SourceTypeKind.String)
            : unknown;
        var parameter = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            parameterName,
            declaredType,
            inferredType);
        var label = builder.AddLabel(labelName);
        var origin = builder.AddOrigin(5, 10, 20, 30);
        var @break = builder.AddStatement(
            Avm1SourceStatementKind.Break,
            label: label,
            origin: origin);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddSymbolReference(builder, parameter, origin),
            label: label,
            origin: origin,
            children: [@break]);
        return CreateMethod(builder, [loop], [parameter]);
    }

    private static Avm1SourceMethod CreateLiteralReturn(
        Avm1SourceLiteralKind kind,
        int integerValue = 0,
        double numberValue = 0)
    {
        var builder = new Avm1SourceArena.Builder();
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                kind,
                integerValue: integerValue,
                numberValue: numberValue));
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: literal)]);
    }

    private static Avm1SourceMethod CreateQualifiedMemberReturn(
        string qualifiedName,
        bool computed)
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = builder.AddExpression(
            Avm1SourceExpressionKind.QualifiedName,
            name: builder.InternString(qualifiedName));
        var member = AddMember(builder, receiver, "staticValue", computed);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: member)]);
    }

    private static Avm1SourceMethod CreateDeclaredSymbolReturn(Avm1SourceSymbolKind kind)
    {
        var builder = new Avm1SourceArena.Builder();
        var symbol = builder.AddSymbol(
            kind,
            "value",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var declaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddInteger(builder, 1),
            symbol: symbol);
        var sum = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddSymbolReference(builder, symbol),
                AddSymbolReference(builder, symbol)
            ]);
        return CreateMethod(
            builder,
            [
                declaration,
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: sum)
            ]);
    }

    private static Avm1SourceMethod CreateOpaqueReturn()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 0);
        var opaque = builder.AddOpaque(
            Avm1SourceOpaqueKind.RecoveryExpression,
            "unsupported expression",
            origin,
            [0x00]);
        var expression = builder.AddExpression(
            Avm1SourceExpressionKind.Opaque,
            opaque: opaque,
            origin: origin);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: expression)]);
    }

    private static Avm1SourceMethod CreateCompoundAssignmentMethod(bool compound)
    {
        var builder = new Avm1SourceArena.Builder();
        var target = AddDynamicName(builder, "counter");
        SourceExpressionIndex assignment;
        if (compound)
        {
            assignment = builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.AddAssign,
                children: [target, AddInteger(builder, 1)]);
        }
        else
        {
            var sum = builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                Avm1SourceOperator.Add,
                children:
                [
                    AddDynamicName(builder, "counter"),
                    AddInteger(builder, 1)
                ]);
            assignment = AddAssignment(builder, target, sum);
        }
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: assignment)]);
    }

    private static Avm1SourceMethod CreateConditionalStatementMethod(bool inverted)
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = AddDynamicName(builder, "condition");
        if (inverted)
        {
            condition = builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                Avm1SourceOperator.LogicalNot,
                children: [condition]);
        }
        var whenTrue = AddBlock(
            builder,
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddInteger(builder, inverted ? 2 : 1)));
        var whenFalse = AddBlock(
            builder,
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddInteger(builder, inverted ? 1 : 2)));
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: condition,
                children: [whenTrue, whenFalse])]);
    }

    private static Avm1SourceMethod CreateTerminalBranchOrderingMethod(bool inverted)
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            inverted
                ? Avm1SourceOperator.Equal
                : Avm1SourceOperator.NotEqual,
            children:
            [
                AddDynamicName(builder, "left"),
                AddDynamicName(builder, "right")
            ]);
        var complex = AddBlock(
            builder,
            AddAssignmentStatement(
                builder,
                AddDynamicName(builder, "result"),
                AddInteger(builder, 1)),
            builder.AddStatement(Avm1SourceStatementKind.Return));
        var simple = AddBlock(
            builder,
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddDynamicName(builder, "value")));
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.If,
                expression: condition,
                children: inverted
                    ? [simple, complex]
                    : [complex, simple])]);
    }

    private static Avm1SourceMethod CreateTerminalGuardSwitchMethod(
        bool switchIsGuarded)
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            switchIsGuarded
                ? Avm1SourceOperator.StrictNotEqual
                : Avm1SourceOperator.StrictEqual,
            children:
            [
                AddDynamicName(builder, "nav"),
                AddDynamicName(builder, "up")
            ]);
        var simpleIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "multiline"),
            children:
            [
                AddBlock(
                    builder,
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddInteger(builder, 1)))
            ]);
        var fallback = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: AddInteger(builder, 0));
        var switchCase = builder.AddStatement(
            Avm1SourceStatementKind.SwitchCase,
            expression: AddInteger(builder, 1),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(builder, 2))
            ]);
        var secondCase = builder.AddStatement(
            Avm1SourceStatementKind.SwitchCase,
            expression: AddInteger(builder, 2),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(builder, 3))
            ]);
        var thirdCase = builder.AddStatement(
            Avm1SourceStatementKind.SwitchCase,
            expression: AddInteger(builder, 3),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(builder, 4))
            ]);
        var clauses = new List<SourceStatementIndex>
        {
            switchCase,
            secondCase,
            thirdCase
        };
        if (!switchIsGuarded)
        {
            clauses.Add(builder.AddStatement(
                Avm1SourceStatementKind.SwitchDefault,
                children: [fallback]));
        }
        var switchStatement = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "nav"),
            children: clauses);

        if (!switchIsGuarded)
        {
            var simpleBody = AddBlock(builder, simpleIf, fallback);
            return CreateMethod(
                builder,
                [
                    builder.AddStatement(
                        Avm1SourceStatementKind.If,
                        expression: condition,
                        children: [simpleBody]),
                    switchStatement
                ]);
        }

        var switchBody = AddBlock(builder, switchStatement, fallback);
        return CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.If,
                    expression: condition,
                    children: [switchBody]),
                simpleIf,
                fallback
            ]);
    }

    private static Avm1SourceMethod CreateForInTransportMethod(bool withTransport)
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var key = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "key",
            SourceTypeIndex.Invalid,
            unknown);
        var bodyUse = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: AddSymbolReference(builder, key));
        if (!withTransport)
        {
            var loop = builder.AddStatement(
                Avm1SourceStatementKind.ForIn,
                expression: AddSymbolReference(builder, key),
                secondaryExpression: AddDynamicName(builder, "collection"),
                flags: Avm1SourceStatementFlags.ForInDeclaresKey,
                children: [bodyUse]);
            return CreateMethod(builder, [loop]);
        }

        var transport = AddGeneratedLocal(builder, "keyTransport");
        var declaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            symbol: key);
        var assignment = AddAssignmentStatement(
            builder,
            AddSymbolReference(builder, key),
            AddSymbolReference(builder, transport));
        var transportedLoop = builder.AddStatement(
            Avm1SourceStatementKind.ForIn,
            expression: AddSymbolReference(builder, transport),
            secondaryExpression: AddDynamicName(builder, "collection"),
            flags: Avm1SourceStatementFlags.ForInDeclaresKey,
            children: [assignment, bodyUse]);
        return CreateMethod(builder, [declaration, transportedLoop]);
    }

    private static Avm1SourceMethod CreateObjectTransportMethod(
        bool withTransport,
        bool duplicateKeys)
    {
        var builder = new Avm1SourceArena.Builder();
        var firstKey = duplicateKeys ? "same" : "first";
        var secondKey = duplicateKeys ? "same" : "second";
        if (!withTransport)
        {
            var literal = builder.AddExpression(
                Avm1SourceExpressionKind.ObjectLiteral,
                children:
                [
                    AddString(builder, firstKey),
                    AddDynamicName(builder, "firstValue"),
                    AddString(builder, secondKey),
                    AddDynamicName(builder, "secondValue")
                ]);
            return CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: literal)]);
        }

        var firstValue = AddGeneratedLocal(builder, "firstTransport");
        var secondValue = AddGeneratedLocal(builder, "secondTransport");
        var reversed = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(builder, secondKey),
                AddSymbolReference(builder, secondValue),
                AddString(builder, firstKey),
                AddSymbolReference(builder, firstValue)
            ]);
        return CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(builder, "firstValue"),
                    symbol: firstValue),
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddDynamicName(builder, "secondValue"),
                    symbol: secondValue),
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: reversed)
            ]);
    }

    private static Avm1SourceMethod CreateDiscardedIncrementMethod(bool useIncrement)
    {
        var builder = new Avm1SourceArena.Builder();
        var target = AddDynamicName(builder, "counter");
        var expression = useIncrement
            ? builder.AddExpression(
                Avm1SourceExpressionKind.Unary,
                Avm1SourceOperator.PostfixIncrement,
                children: [target])
            : builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.AddAssign,
                children: [target, AddInteger(builder, 1)]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression)]);
    }

    private static Avm1SourceMethod CreateTerminalIfMethod(bool inverted)
    {
        var builder = new Avm1SourceArena.Builder();
        var value = AddDynamicName(builder, "value");
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            inverted
                ? Avm1SourceOperator.GreaterOrEqual
                : Avm1SourceOperator.Less,
            children: [value, AddInteger(builder, 0)]);
        var @throw = AddBlock(
            builder,
            builder.AddStatement(
                Avm1SourceStatementKind.Throw,
                expression: AddDynamicName(builder, "value")));
        var @return = AddBlock(
            builder,
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: AddDynamicName(builder, "value")));
        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: condition,
            children: inverted
                ? [@return, @throw]
                : [@throw, @return]);
        return CreateMethod(builder, [conditional]);
    }

    private static Avm1SourceMethod CreateFunctionMethod(
        string parameterName,
        int? returnValue)
    {
        var builder = new Avm1SourceArena.Builder();
        var parameter = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            parameterName,
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var returnExpression = returnValue.HasValue
            ? AddInteger(builder, returnValue.Value)
            : AddSymbolReference(builder, parameter);
        var functionBody = AddBlock(
            builder,
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: returnExpression));
        var function = builder.AddFunction(
            SourceSymbolIndex.Invalid,
            functionBody,
            Avm1SourceFunctionFlags.None,
            SourceOriginIndex.Invalid,
            [parameter]);
        var expression = builder.AddExpression(
            Avm1SourceExpressionKind.FunctionLiteral,
            function: function);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: expression)]);
    }

    private static Avm1SourceMethod CreateSwitchMethod(bool finalBreak)
    {
        var builder = new Avm1SourceArena.Builder();
        var firstAssignment = AddAssignmentStatement(
            builder,
            AddDynamicName(builder, "result"),
            AddInteger(builder, 10));
        var firstBreak = builder.AddStatement(Avm1SourceStatementKind.Break);
        var firstCase = builder.AddStatement(
            Avm1SourceStatementKind.SwitchCase,
            expression: AddInteger(builder, 1),
            children: [firstAssignment, firstBreak]);
        var defaultAssignment = AddAssignmentStatement(
            builder,
            AddDynamicName(builder, "result"),
            AddInteger(builder, 20));
        var defaultChildren = finalBreak
            ? new[]
            {
                defaultAssignment,
                builder.AddStatement(Avm1SourceStatementKind.Break)
            }
            : [defaultAssignment];
        var defaultClause = builder.AddStatement(
            Avm1SourceStatementKind.SwitchDefault,
            children: defaultChildren);
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "kind"),
            children: [firstCase, defaultClause]);
        return CreateMethod(builder, [@switch]);
    }

    private static SourceSymbolIndex AddGeneratedLocal(
        Avm1SourceArena.Builder builder,
        string name) =>
        builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            name,
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.CompilerGenerated);

    private static Avm1SourceMethod CreateOrderedCallTransportMethod(
        bool withTransports)
    {
        var builder = new Avm1SourceArena.Builder();
        var left = AddDynamicName(builder, "left");
        var right = AddDynamicName(builder, "right");
        var objectLiteral = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddString(builder, "base"),
                AddInteger(builder, 10)
            ]);
        var statements = new List<SourceStatementIndex>();
        if (withTransports)
        {
            var leftTransport = AddGeneratedTemporary(builder, "leftTransport");
            var rightTransport = AddGeneratedTemporary(builder, "rightTransport");
            var objectTransport = AddGeneratedLocal(builder, "objectTransport");
            statements.Add(builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: left,
                symbol: leftTransport));
            statements.Add(builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: right,
                symbol: rightTransport));
            statements.Add(builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: objectLiteral,
                symbol: objectTransport));
            left = AddSymbolReference(builder, leftTransport);
            right = AddSymbolReference(builder, rightTransport);
            objectLiteral = AddSymbolReference(builder, objectTransport);
        }

        var leftCall = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddMember(builder, left, "run")]);
        var rightCall = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddMember(builder, right, "call"),
                objectLiteral,
                AddInteger(builder, 2)
            ]);
        statements.Add(builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: builder.AddExpression(
                Avm1SourceExpressionKind.Binary,
                Avm1SourceOperator.Add,
                children: [leftCall, rightCall])));
        return CreateMethod(builder, statements);
    }

    private static Avm1SourceMethod CreateBranchSplitDeclarationMethod(
        bool declareInTrueBranch)
    {
        var builder = new Avm1SourceArena.Builder();
        var local = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "__reg7",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));

        SourceStatementIndex CreateDefinition(int value, bool declaration)
        {
            var initializer = AddInteger(builder, value);
            return declaration
                ? builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: initializer,
                    symbol: local)
                : AddAssignmentStatement(
                    builder,
                    AddSymbolReference(builder, local),
                    initializer);
        }

        SourceStatementIndex CreateUse() =>
            builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: builder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children:
                    [
                        AddDynamicName(builder, "consume"),
                        AddSymbolReference(builder, local)
                    ]));

        var whenTrue = AddBlock(
            builder,
            CreateDefinition(1, declareInTrueBranch),
            CreateUse());
        var whenFalse = AddBlock(
            builder,
            CreateDefinition(2, !declareInTrueBranch),
            CreateUse());
        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children: [whenTrue, whenFalse]);
        return CreateMethod(builder, [conditional]);
    }

    private static SourceSymbolIndex AddGeneratedTemporary(
        Avm1SourceArena.Builder builder,
        string name) =>
        builder.AddSymbol(
            Avm1SourceSymbolKind.Temporary,
            name,
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.CompilerGenerated);

    private static SourceSymbolIndex AddParameter(
        Avm1SourceArena.Builder builder,
        string name) =>
        builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            name,
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.DeclarationProvided);

    private static SourceExpressionIndex AddInteger(
        Avm1SourceArena.Builder builder,
        int value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: value));

    private static SourceExpressionIndex AddDynamicName(
        Avm1SourceArena.Builder builder,
        string name) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString(name));

    private static SourceExpressionIndex AddString(
        Avm1SourceArena.Builder builder,
        string value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString(value)));

    private static SourceExpressionIndex AddSymbolReference(
        Avm1SourceArena.Builder builder,
        SourceSymbolIndex symbol,
        SourceOriginIndex? origin = null) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: symbol,
            origin: origin);

    private static SourceExpressionIndex AddMember(
        Avm1SourceArena.Builder builder,
        SourceExpressionIndex receiver,
        string name,
        bool computed = false)
    {
        var memberName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString(name)));
        return builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: computed
                ? Avm1SourceExpressionFlags.ComputedMember
                : Avm1SourceExpressionFlags.None,
            children: [receiver, memberName]);
    }

    private static SourceExpressionIndex AddAssignment(
        Avm1SourceArena.Builder builder,
        SourceExpressionIndex target,
        SourceExpressionIndex value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [target, value]);

    private static SourceStatementIndex AddAssignmentStatement(
        Avm1SourceArena.Builder builder,
        SourceExpressionIndex target,
        SourceExpressionIndex value) =>
        builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: AddAssignment(builder, target, value));

    private static SourceStatementIndex AddBlock(
        Avm1SourceArena.Builder builder,
        params SourceStatementIndex[] statements) =>
        builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: statements);

    private static Avm1SourceMethod CreateMethod(
        Avm1SourceArena.Builder builder,
        IReadOnlyList<SourceStatementIndex> statements,
        IReadOnlyList<SourceSymbolIndex>? parameters = null)
    {
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: statements);
        return new Avm1SourceMethod(
            builder.ToArena(),
            body,
            parameters?.ToArray() ?? [],
            []);
    }

    private static string Describe(Avm1SourceEquivalenceResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}"));

}
